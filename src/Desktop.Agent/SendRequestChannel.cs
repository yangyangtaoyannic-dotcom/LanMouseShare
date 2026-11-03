using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    /// <summary>
    /// fix10：资源管理器右键"传输到对端"的握手通道。
    ///
    /// 右键菜单执行的是 `LanMouseShare.Agent.exe --send "路径"`，而 Agent 是单实例程序
    /// （见 App.xaml.cs 的互斥体），所以第二个进程不能自己干活，只能把路径丢给已经在
    /// 跑的那个实例。这里用命名管道做这件事：
    /// - 首实例：StartServer() 起监听，收到路径后按 700ms 去抖聚合（右键多选文件时
    ///   Windows 会对每个文件各起一次进程），聚合完成回调给 MainWindow 去推文件。
    /// - 后启动的进程：TrySend(paths) 连上去、写完就退出。
    /// 不做自定义管道 ACL：同用户的两个进程默认就能互连，而自定义 ACL 反而会让
    /// 非提权进程建不出管道实例（fix7 踩过的坑）。
    /// </summary>
    internal static class SendRequestChannel
    {
        public const string PipeName = "LanMouseShare.Agent.Send";

        private const int ClientConnectTimeoutMs = 1500;
        private const int AggregateDelayMs = 700;

        private static Thread _listenThread;
        private static volatile bool _running;
        private static Timer _aggregateTimer;
        private static readonly object _sync = new object();
        private static readonly List<string> _pending = new List<string>();
        private static Action<string[]> _onBatch;

        /// <summary>首个实例调用：开始接收其它进程转交过来的路径。</summary>
        public static void StartServer(Action<string[]> onBatch)
        {
            if (onBatch == null)
            {
                return;
            }

            _onBatch = onBatch;
            _running = true;
            _listenThread = new Thread(ListenLoop);
            _listenThread.IsBackground = true;
            _listenThread.Name = "SendRequestChannel";
            _listenThread.Start();
            AppLog.Event("INFO", "send.channel.server", "pipe=" + PipeName);
        }

        /// <summary>后启动的进程调用：把路径转交给正在运行的实例。</summary>
        public static bool TrySend(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return false;
            }

            string json;
            try
            {
                json = ProtocolSerializer.ToJson(new SendRequestPayload { Paths = paths, RequestedUtcTicks = DateTime.UtcNow.Ticks });
            }
            catch (Exception ex)
            {
                AppLog.Error("send.channel.serialize.fail", ex);
                return false;
            }

            try
            {
                // 多选右键时 Windows 会同时起多个进程，服务端可能正处在"上一个连接已关闭、
                // 还没建出新实例"的空档里（这时 Connect 会报找不到管道），所以重试几次。
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                        {
                            client.Connect(ClientConnectTimeoutMs);
                            var bytes = Encoding.UTF8.GetBytes(json);
                            var header = new byte[4];
                            header[0] = (byte)(bytes.Length & 0xFF);
                            header[1] = (byte)((bytes.Length >> 8) & 0xFF);
                            header[2] = (byte)((bytes.Length >> 16) & 0xFF);
                            header[3] = (byte)((bytes.Length >> 24) & 0xFF);
                            client.Write(header, 0, header.Length);
                            client.Write(bytes, 0, bytes.Length);
                            client.Flush();
                        }

                        AppLog.Event("INFO", "send.channel.forwarded", "count=" + paths.Length + " attempt=" + attempt);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 2)
                        {
                            throw;
                        }

                        AppLog.Debug("send.channel.retry", "attempt=" + attempt + " " + ex.Message);
                        Thread.Sleep(150);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("send.channel.forward.fail", ex);
                return false;
            }
        }

        private static void ListenLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();

                    var json = ReadString(server);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var payload = ProtocolSerializer.FromJson<SendRequestPayload>(json);
                        if (payload != null && payload.Paths != null)
                        {
                            QueueForAggregation(payload.Paths);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("send.channel.listen.fail", ex.Message);
                    Thread.Sleep(200);
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Dispose(); } catch (Exception) { }
                    }
                }
            }
        }

        private static string ReadString(Stream stream)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header, 0, header.Length))
            {
                return null;
            }

            var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length <= 0 || length > 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[length];
            if (!ReadExactly(stream, bytes, 0, bytes.Length))
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }

        private static void QueueForAggregation(string[] paths)
        {
            lock (_sync)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    var path = paths[i];
                    if (!string.IsNullOrEmpty(path) && !_pending.Contains(path))
                    {
                        _pending.Add(path);
                    }
                }

                if (_aggregateTimer == null)
                {
                    _aggregateTimer = new Timer(OnAggregateTick, null, AggregateDelayMs, Timeout.Infinite);
                }
                else
                {
                    _aggregateTimer.Change(AggregateDelayMs, Timeout.Infinite);
                }
            }
        }

        private static void OnAggregateTick(object state)
        {
            string[] batch;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                batch = _pending.ToArray();
                _pending.Clear();
            }

            try
            {
                AppLog.Event("INFO", "send.channel.batch", "count=" + batch.Length);
                var handler = _onBatch;
                if (handler != null)
                {
                    handler(batch);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("send.channel.batch.fail", ex);
            }
        }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class SendRequestPayload
    {
        [System.Runtime.Serialization.DataMember(Order = 1)]
        public string[] Paths { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 2)]
        public long RequestedUtcTicks { get; set; }
    }
}
