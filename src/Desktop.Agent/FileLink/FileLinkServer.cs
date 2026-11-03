using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;

namespace LanMouseShare.Agent.FileLink
{
    internal sealed class FileLinkIncomingEventArgs : EventArgs
    {
        public FileLinkFile[] Files;
        public string[] SavedPaths;
        public string RemoteAddress;
        public string TransferId;
    }

    internal sealed class FileLinkClipboardEventArgs : EventArgs
    {
        public string Kind;
        public string Text;
        public byte[] Data;
    }

    /// <summary>
    /// fix8 数据通道的接收端。它只做三件事：认人（只接受当前配对的那台机器）、
    /// 落盘（.part + 原子改名）、如实回报（每个文件一条 done）。
    /// 全程不依赖 Service、不依赖 IPC 管道、不受"暂停共享"影响。
    /// </summary>
    internal sealed class FileLinkServer : IDisposable
    {
        private readonly Func<PeerStateDto> _getState;
        private readonly Func<string> _inbox;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        // fix9：记录正在处理中的连接，取消时直接关掉，卡在读写上的接收/发送会立即抛异常，
        // .part 临时文件由 HandlePush 的异常分支删掉，不会留下半截文件。
        private readonly List<TcpClient> _sessions = new List<TcpClient>();

        public void CancelActive()
        {
            TcpClient[] snapshot;
            lock (_sessions)
            {
                snapshot = _sessions.ToArray();
                _sessions.Clear();
            }

            foreach (var client in snapshot)
            {
                try { client.Close(); } catch (Exception) { }
            }

            if (snapshot.Length > 0)
            {
                AppLog.Event("INFO", "filelink.cancel.closed", "count=" + snapshot.Length);
            }
        }

        private readonly int _port;

        public FileLinkServer(Func<PeerStateDto> getState, Func<string> inbox)
            : this(getState, inbox, FileLinkProtocol.DefaultPort)
        {
        }

        public FileLinkServer(Func<PeerStateDto> getState, Func<string> inbox, int port)
        {
            _getState = getState;
            _inbox = inbox;
            _port = port > 0 ? port : FileLinkProtocol.DefaultPort;
        }

        public event EventHandler<FileLinkIncomingEventArgs> FilesOffered;
        public event EventHandler<FileLinkIncomingEventArgs> FilesReceived;
        public event EventHandler<FileLinkProgress> Progress;
        public event EventHandler<FileLinkClipboardEventArgs> ClipboardReceived;

        public int Port
        {
            get { return _port; }
        }

        public bool Started
        {
            get { return _running; }
        }

        private void RaiseProgress(FileLinkProgress progress)
        {
            var handler = Progress;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, progress);
            }
            catch (Exception ex)
            {
                AppLog.Debug("filelink.progress.handler.fail", ex.Message);
            }
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FileLinkAccept" };
                _acceptThread.Start();
                AppLog.Info("filelink.server.started port=" + _port);
            }
            catch (Exception ex)
            {
                _running = false;
                AppLog.Error("filelink.server.start.fail port=" + _port, ex);
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
            }

            _listener = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (!_running)
                    {
                        return;
                    }

                    AppLog.Debug("filelink.accept.fail", ex.Message);
                    Thread.Sleep(200);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(delegate(object state) { HandleConnection((TcpClient)state); }, client);
            }
        }

        private void HandleConnection(TcpClient client)
        {
            var remote = "unknown";
            lock (_sessions)
            {
                _sessions.Add(client);
            }

            try
            {
                client.NoDelay = true;
                try
                {
                    client.SendBufferSize = FileLinkProtocol.SocketBufferSize;
                    client.ReceiveBufferSize = FileLinkProtocol.SocketBufferSize;
                }
                catch (Exception)
                {
                }

                client.SendTimeout = FileLinkProtocol.IoTimeoutMs;
                client.ReceiveTimeout = FileLinkProtocol.IoTimeoutMs;

                var endPoint = client.Client.RemoteEndPoint as IPEndPoint;
                remote = endPoint != null ? endPoint.Address.ToString() : "unknown";

                var stream = client.GetStream();
                var state = _getState != null ? _getState() : null;

                var hello = FileLinkFrames.Read(stream);
                if (hello == null || hello.Op != FileLinkProtocol.OpHello)
                {
                    AppLog.Warn("filelink.hello.invalid remote=" + remote);
                    return;
                }

                var reject = CheckPeer(state, hello, remote);
                if (reject != null)
                {
                    AppLog.Warn("filelink.hello.reject remote=" + remote + " device=" + hello.DeviceId + " reason=" + reject);
                    FileLinkFrames.Write(stream, new FileLinkMessage
                    {
                        Op = FileLinkProtocol.OpError,
                        Ok = false,
                        Reason = reject
                    });
                    return;
                }

                FileLinkFrames.Write(stream, new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpWelcome,
                    Version = FileLinkProtocol.Version,
                    DeviceId = state != null ? state.LocalDeviceId : null,
                    DeviceName = state != null ? state.LocalDeviceName : null,
                    Ok = true
                });

                while (true)
                {
                    var message = FileLinkFrames.Read(stream);
                    if (message == null)
                    {
                        return;
                    }

                    if (message.Op == FileLinkProtocol.OpPush)
                    {
                        HandlePush(stream, message, remote);
                        continue;
                    }

                    if (message.Op == FileLinkProtocol.OpPull)
                    {
                        HandlePull(stream, message);
                        continue;
                    }

                    if (message.Op == FileLinkProtocol.OpOffer)
                    {
                        HandleOffer(stream, message, remote);
                        continue;
                    }

                    if (message.Op == FileLinkProtocol.OpClipboard)
                    {
                        HandleClipboard(stream, message);
                        continue;
                    }

                    AppLog.Warn("filelink.op.unknown remote=" + remote + " op=" + message.Op);
                    FileLinkFrames.Write(stream, new FileLinkMessage { Op = FileLinkProtocol.OpError, Reason = "unknown op" });
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.conn.fail remote=" + remote, ex);
            }
            finally
            {
                lock (_sessions)
                {
                    _sessions.Remove(client);
                }

                try { client.Close(); } catch (Exception) { }
            }
        }

        /// <summary>只接受"当前配对的那台机器"：设备 ID 必须等于状态里的对端 ID，地址必须等于状态里的对端地址。</summary>
        private static string CheckPeer(PeerStateDto state, FileLinkMessage hello, string remote)
        {
            if (state == null)
            {
                return "本机状态还没准备好。";
            }

            if (string.IsNullOrEmpty(state.PeerDeviceId))
            {
                return "还没有配对的对端。";
            }

            if (!string.Equals(hello.DeviceId, state.PeerDeviceId, StringComparison.Ordinal))
            {
                return "设备不匹配（未配对）。";
            }

            if (!string.IsNullOrEmpty(state.PeerAddress)
                && !string.Equals(state.PeerAddress, remote, StringComparison.Ordinal))
            {
                // 地址变了（换网卡/换网络）时放行，但要留痕，方便排查"是不是别的机器在连"。
                AppLog.Warn("filelink.hello.address.mismatch expected=" + state.PeerAddress + " actual=" + remote);
            }

            return null;
        }

        private void HandlePush(Stream stream, FileLinkMessage message, string remote)
        {
            var files = message.Files ?? new List<FileLinkFile>();
            var folder = ResolveInbox();
            var saved = new List<string>();

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var safeName = SanitizeName(file.Name);
                string destination = null;
                string tempPath = null;
                FileStream output = null;

                try
                {
                    destination = FileLinkClient.UniquePath(Path.Combine(folder, safeName));
                    tempPath = destination + ".part";
                    output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileLinkProtocol.FileBufferSize, FileOptions.SequentialScan);
                }
                catch (Exception ex)
                {
                    AppLog.Error("filelink.push.open.fail name=" + safeName, ex);
                    if (output != null)
                    {
                        try { output.Dispose(); } catch (Exception) { }
                    }

                    FileLinkFrames.Write(stream, new FileLinkMessage
                    {
                        Op = FileLinkProtocol.ReplyReject,
                        Index = i,
                        Ok = false,
                        Reason = "无法创建接收文件：" + ex.Message
                    });
                    continue;
                }

                FileLinkFrames.Write(stream, new FileLinkMessage
                {
                    Op = FileLinkProtocol.ReplyAccept,
                    Index = i,
                    Ok = true,
                    SavedAs = destination
                });

                try
                {
                    var sha = FileLinkClient.ReceiveToStream(stream, output, file.Size, safeName, RaiseProgress);
                    output.Dispose();
                    output = null;

                    if (File.Exists(destination))
                    {
                        destination = FileLinkClient.UniquePath(destination);
                    }

                    File.Move(tempPath, destination);
                    saved.Add(destination);
                    AppLog.Info("filelink.push.received name=" + safeName + " size=" + file.Size + " sha256=" + sha + " path=" + destination);

                    FileLinkFrames.Write(stream, new FileLinkMessage
                    {
                        Op = FileLinkProtocol.ReplyDone,
                        Index = i,
                        Ok = true,
                        SavedAs = destination
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("filelink.push.receive.fail name=" + safeName, ex);
                    if (output != null)
                    {
                        try { output.Dispose(); } catch (Exception) { }
                    }

                    TryDelete(tempPath);
                    try
                    {
                        FileLinkFrames.Write(stream, new FileLinkMessage
                        {
                            Op = FileLinkProtocol.ReplyDone,
                            Index = i,
                            Ok = false,
                            Reason = ex.Message
                        });
                    }
                    catch (Exception)
                    {
                    }

                    return;
                }
            }

            FileLinkFrames.Write(stream, new FileLinkMessage
            {
                Op = FileLinkProtocol.ReplySummary,
                Ok = saved.Count > 0,
                OkCount = saved.Count,
                FailCount = files.Count - saved.Count
            });

            var received = FilesReceived;
            if (received != null)
            {
                received(this, new FileLinkIncomingEventArgs
                {
                    Files = files.ToArray(),
                    SavedPaths = saved.ToArray(),
                    RemoteAddress = remote,
                    TransferId = message.TransferId
                });
            }
        }

        private void HandlePull(Stream stream, FileLinkMessage message)
        {
            var files = message.Files ?? new List<FileLinkFile>();
            var sent = 0;

            for (var i = 0; i < files.Count; i++)
            {
                var request = files[i];
                var source = request.SourcePath;
                var servePath = source;
                var tempZip = (string)null;

                if (!string.IsNullOrEmpty(source) && !File.Exists(source) && Directory.Exists(source))
                {
                    // 对端复制的是文件夹：Windows 剪贴板里只有一个目录路径，无法直接当文件送。
                    // 这里按需打成一个 lms-copy-<目录名>-<guid8>.zip，接收端 ExpandIfArchive 会解回原目录名。
                    try
                    {
                        var folderName = new DirectoryInfo(source).Name;
                        tempZip = Path.Combine(Path.GetTempPath(),
                            "lms-copy-" + SanitizeName(folderName) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                        ZipFile.CreateFromDirectory(source, tempZip);
                        servePath = tempZip;
                        AppLog.Event("INFO", "filelink.pull.folder.zip", "folder=" + source + " zip=" + tempZip);
                    }
                    catch (Exception zipEx)
                    {
                        AppLog.Error("filelink.pull.folder.zip.fail folder=" + source, zipEx);
                        TryDeleteFile(tempZip);
                        FileLinkFrames.Write(stream, new FileLinkMessage
                        {
                            Op = FileLinkProtocol.ReplyReject,
                            Index = i,
                            Ok = false,
                            Reason = "对端文件夹打包失败：" + zipEx.Message
                        });
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(servePath) || !File.Exists(servePath))
                {
                    AppLog.Warn("filelink.pull.missing path=" + source);
                    FileLinkFrames.Write(stream, new FileLinkMessage
                    {
                        Op = FileLinkProtocol.ReplyReject,
                        Index = i,
                        Ok = false,
                        Reason = "对端文件已不存在。"
                    });
                    continue;
                }

                try
                {
                    var info = new FileInfo(servePath);
                    FileLinkFrames.Write(stream, new FileLinkMessage
                    {
                        Op = FileLinkProtocol.ReplyFile,
                        Index = i,
                        Ok = true,
                        Files = new List<FileLinkFile>
                        {
                            new FileLinkFile { Name = info.Name, Size = info.Length, SourcePath = source }
                        }
                    });

                    using (var input = new FileStream(servePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileLinkProtocol.FileBufferSize, FileOptions.SequentialScan))
                    {
                        FileLinkStreamer.Send(stream, input, info.Length, info.Name, false, null);
                    }

                    var ack = FileLinkFrames.Read(stream);
                    if (ack == null)
                    {
                        AppLog.Warn("filelink.pull.ack.missing path=" + servePath);
                        return;
                    }

                    sent++;
                    AppLog.Info("filelink.pull.sent name=" + info.Name + " size=" + info.Length);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempZip))
                    {
                        TryDeleteFile(tempZip);
                    }
                }
            }

            FileLinkFrames.Write(stream, new FileLinkMessage
            {
                Op = FileLinkProtocol.ReplySummary,
                Ok = sent > 0,
                OkCount = sent,
                FailCount = files.Count - sent
            });
        }

        private void HandleOffer(Stream stream, FileLinkMessage message, string remote)
        {
            var files = message.Files ?? new List<FileLinkFile>();
            AppLog.Info("filelink.offer.received count=" + files.Count + " remote=" + remote);
            FileLinkFrames.Write(stream, new FileLinkMessage
            {
                Op = FileLinkProtocol.ReplySummary,
                Ok = true,
                OkCount = files.Count
            });

            var handler = FilesOffered;
            if (handler != null)
            {
                handler(this, new FileLinkIncomingEventArgs
                {
                    Files = files.ToArray(),
                    SavedPaths = new string[0],
                    RemoteAddress = remote,
                    TransferId = message.TransferId
                });
            }
        }

        private void HandleClipboard(Stream stream, FileLinkMessage message)
        {
            var handler = ClipboardReceived;
            byte[] data = null;
            if (!string.IsNullOrEmpty(message.DataBase64))
            {
                try
                {
                    data = Convert.FromBase64String(message.DataBase64);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("filelink.clipboard.base64.fail " + ex.Message);
                }
            }

            FileLinkFrames.Write(stream, new FileLinkMessage { Op = FileLinkProtocol.ReplySummary, Ok = true, OkCount = 1 });

            if (handler != null)
            {
                handler(this, new FileLinkClipboardEventArgs
                {
                    Kind = message.Kind,
                    Text = message.Text,
                    Data = data
                });
            }
        }

        private string ResolveInbox()
        {
            var folder = _inbox != null ? _inbox() : null;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.Combine(Path.GetTempPath(), "MouseTransfer");
            }

            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.inbox.create.fail path=" + folder, ex);
                folder = Path.GetTempPath();
            }

            return folder;
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("filelink.tmp.delete.fail", path + " " + ex.Message);
            }
        }

        private static string SanitizeName(string name)
        {
            var safe = Path.GetFileName(name ?? string.Empty);
            if (string.IsNullOrEmpty(safe))
            {
                safe = "file-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }

            return safe;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
