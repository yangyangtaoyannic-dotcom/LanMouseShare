using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Networking;

namespace LanMouseShare.Service
{
    /// <summary>
    /// Runs on the agent (被控端) machine: tails the local live log directory and
    /// ships every new line to the controller (主控端) over TCP.
    /// </summary>
    internal sealed class LogForwarder : IDisposable
    {
        private const int MaxQueuedLines = 20000;
        private const int ConnectTimeoutMs = 3000;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

        private readonly object _sync = new object();
        private readonly Func<string> _peerAddressProvider;
        private readonly Func<AppConfig> _configProvider;
        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemoryStream> _partials = new Dictionary<string, MemoryStream>(StringComparer.OrdinalIgnoreCase);
        private Thread _tailThread;
        private Thread _sendThread;
        private volatile bool _running;
        private volatile string _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u672A\u542F\u52A8";
        private long _dropped;
        private long _sent;
        private long _bytesSent;
        private long _inputActiveTicks;
        private string _lastError;
        private DateTime _lastErrorLogUtc = DateTime.MinValue;

        public LogForwarder(Func<string> peerAddressProvider, Func<AppConfig> configProvider)
        {
            _peerAddressProvider = peerAddressProvider;
            _configProvider = configProvider;
        }

        public string StatusText
        {
            get { return _status; }
        }

        /// <summary>Called whenever input was just forwarded: logs yield to input.</summary>
        public void NotifyInputActivity()
        {
            Interlocked.Exchange(ref _inputActiveTicks, DateTime.UtcNow.Ticks);
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_running)
                {
                    return;
                }

                _running = true;
                // Seed offsets with current file sizes: do not re-ship history on every restart.
                try
                {
                    var directories = AppLog.GetForwardedLiveDirectories();
                    for (int d = 0; d < directories.Length; d++)
                    {
                        if (!Directory.Exists(directories[d]))
                        {
                            continue;
                        }

                        var files = Directory.GetFiles(directories[d], "*.log");
                        for (int i = 0; i < files.Length; i++)
                        {
                            _offsets[files[i]] = new FileInfo(files[i]).Length;
                        }
                    }
                }
                catch
                {
                }

                _tailThread = new Thread(TailLoop);
                _tailThread.IsBackground = true;
                _tailThread.Name = "LogTail";
                _tailThread.Start();

                _sendThread = new Thread(SendLoop);
                _sendThread.IsBackground = true;
                _sendThread.Name = "LogSend";
                _sendThread.Start();

                _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u542F\u52A8\u4E2D\u2026";
                AppLog.Event("INFO", "log.forwarder.start", "live=" + AppLog.LiveDirectory);
            }
        }

        public void Stop()
        {
            Thread tail;
            Thread send;
            lock (_sync)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                tail = _tailThread;
                send = _sendThread;
                _tailThread = null;
                _sendThread = null;
                _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u5DF2\u505C\u6B62";
            }

            try
            {
                if (tail != null)
                {
                    tail.Join(1000);
                }
            }
            catch
            {
            }

            try
            {
                if (send != null)
                {
                    send.Join(1500);
                }
            }
            catch
            {
            }

            AppLog.Event("INFO", "log.forwarder.stop", "sent=" + _sent + " dropped=" + _dropped);
        }

        public void Dispose()
        {
            Stop();
        }

        private void TailLoop()
        {
            while (_running)
            {
                try
                {
                    TailOnce();
                }
                catch (Exception ex)
                {
                    LogErrorThrottled("log.forwarder.tail.failed", ex);
                }

                Thread.Sleep(300);
            }
        }

        private void TailOnce()
        {
            var directories = AppLog.GetForwardedLiveDirectories();
            for (int d = 0; d < directories.Length; d++)
            {
                var directory = directories[d];
                if (!_running)
                {
                    return;
                }

                TailDirectory(directory);
            }
        }

        private void TailDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory.GetFiles(directory, "*.log");
            for (int i = 0; i < files.Length; i++)
            {
                if (!_running)
                {
                    return;
                }

                var path = files[i];
                long offset;
                if (!_offsets.TryGetValue(path, out offset))
                {
                    offset = 0;
                    _offsets[path] = 0;
                }

                var length = new FileInfo(path).Length;
                if (length <= offset)
                {
                    if (length < offset)
                    {
                        _offsets[path] = 0;
                    }

                    continue;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var bytes = new byte[read];
                        Buffer.BlockCopy(buffer, 0, bytes, 0, read);
                        AppendTail(path, bytes);
                        offset += read;
                    }
                }

                _offsets[path] = offset;
            }
        }

        private void AppendTail(string path, byte[] bytes)
        {
            MemoryStream partial;
            if (!_partials.TryGetValue(path, out partial))
            {
                partial = new MemoryStream();
                _partials[path] = partial;
            }

            partial.Write(bytes, 0, bytes.Length);
            var content = partial.ToArray();
            var start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != (byte)'\n')
                {
                    continue;
                }

                var lineLength = i - start;
                if (lineLength > 0)
                {
                    var line = Encoding.UTF8.GetString(content, start, lineLength).TrimEnd('\r');
                    if (line.Length > LogProtocol.MaxLineLength)
                    {
                        line = line.Substring(0, LogProtocol.MaxLineLength);
                    }

                    Enqueue(line);
                }

                start = i + 1;
            }

            var remaining = content.Length - start;
            partial.SetLength(0);
            if (remaining > 0)
            {
                partial.Write(content, start, remaining);
            }
        }

        private void Enqueue(string line)
        {
            _queue.Enqueue(line);
            while (_queue.Count > MaxQueuedLines)
            {
                string dropped;
                if (_queue.TryDequeue(out dropped))
                {
                    _dropped++;
                }
                else
                {
                    break;
                }
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    var endpoint = ResolveEndpoint();
                    if (string.IsNullOrEmpty(endpoint))
                    {
                        _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u7B49\u5F85\u914D\u5BF9\u6216\u586B\u5199\u4E3B\u63A7\u7AEF\u5730\u5740";
                        Thread.Sleep(1000);
                        continue;
                    }

                    string host;
                    int port;
                    LogProtocol.SplitEndpoint(endpoint, out host, out port);
                    _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u6B63\u5728\u8FDE\u63A5 " + host + ":" + port;

                    client = new TcpClient();
                    if (!TryConnect(client, host, port, ConnectTimeoutMs))
                    {
                        _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u65E0\u6CD5\u8FDE\u63A5 " + host + ":" + port;
                        client.Close();
                        Thread.Sleep(3000);
                        continue;
                    }

                    client.NoDelay = true;
                    TrafficPriority.TryMarkBulk(client.Client);
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 8192, true))
                    {
                        stream.ReadTimeout = 15000;
                        stream.WriteTimeout = 15000;

                        var config = _configProvider == null ? null : _configProvider();
                        var deviceId = config != null ? config.DeviceId : "-";
                        var deviceName = config != null ? config.DeviceName : "-";
                        var token = config != null && config.DebugToken != null ? config.DebugToken : string.Empty;

                        writer.WriteLine(LogProtocol.BuildHello(deviceId, deviceName, ShareRoleValues.Agent, token, AppLog.Version));
                        writer.Flush();

                        var reply = reader.ReadLine();
                        if (reply == null || !reply.StartsWith(LogProtocol.OkPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u88AB\u4E3B\u63A7\u7AEF\u62D2\u7EDD " + (reply ?? "<no reply>");
                            AppLog.Warn("log.forwarder.denied reply=" + (reply ?? "<no reply>") + " endpoint=" + endpoint);
                            Thread.Sleep(5000);
                            continue;
                        }

                        _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u5DF2\u8FDE\u63A5 " + host + ":" + port + "\uFF08\u5DF2\u53D1\u9001 " + _sent + " \u884C\uFF09";
                        AppLog.Event("INFO", "log.forwarder.connected", "endpoint=" + endpoint);
                        SendSystemInfoBlock(writer);

                        var lastPingUtc = DateTime.UtcNow;
                        while (_running)
                        {
                            var inputActive = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _inputActiveTicks)) <
                                              TimeSpan.FromMilliseconds(250).Ticks;
                            var maxBatch = inputActive ? 20 : 200;
                            var wrote = 0;
                            string line;
                            while (wrote < maxBatch && _queue.TryDequeue(out line))
                            {
                                writer.WriteLine(line);
                                wrote++;
                                _sent++;
                                _bytesSent += line.Length + 2;
                            }

                            if (wrote > 0)
                            {
                                writer.Flush();
                                _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u5DF2\u8FDE\u63A5 " + host + ":" + port +
                                          "\uFF08\u5DF2\u53D1\u9001 " + _sent + " \u884C\uFF09";
                                continue;
                            }

                            if (DateTime.UtcNow - lastPingUtc > PingInterval)
                            {
                                writer.WriteLine(LogProtocol.PingLine);
                                writer.Flush();
                                lastPingUtc = DateTime.UtcNow;
                            }

                            Thread.Sleep(inputActive ? 60 : 120);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogErrorThrottled("log.forwarder.failed", ex);
                    _status = "\u65E5\u5FD7\u63A8\u9001\uFF1A\u8FDE\u63A5\u4E2D\u65AD\uFF0C\u91CD\u8BD5\u4E2D";
                    Thread.Sleep(2000);
                }
                finally
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void SendSystemInfoBlock(StreamWriter writer)
        {
            try
            {
                var files = Directory.GetFiles(AppLog.LogsRoot, SystemInfoReport.FileNamePrefix + "*.txt");
                if (files.Length == 0)
                {
                    AppLog.Debug("log.forwarder.sysinfo.skip", "reason=missing root=" + AppLog.LogsRoot);
                    return;
                }

                for (int f = 0; f < files.Length; f++)
                {
                    var lines = File.ReadAllLines(files[f], Encoding.UTF8);
                    writer.WriteLine(LogProtocol.SysInfoBegin + " file=" + Path.GetFileName(files[f]));
                    for (int i = 0; i < lines.Length; i++)
                    {
                        writer.WriteLine(LogProtocol.SysInfoLinePrefix +
                                         LogProtocol.SafeLogLine(lines[i], LogProtocol.MaxSysInfoLineLength));
                    }

                    writer.WriteLine(LogProtocol.SysInfoEnd);
                    AppLog.Event("INFO", "log.forwarder.sysinfo", "file=" + Path.GetFileName(files[f]) +
                                                                   " lines=" + lines.Length);
                }

                writer.Flush();
            }
            catch (Exception ex)
            {
                AppLog.Warn("log.forwarder.sysinfo.fail " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryConnect(TcpClient client, string host, int port, int timeoutMs)
        {
            try
            {
                var result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    return false;
                }

                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string ResolveEndpoint()
        {
            try
            {
                var config = _configProvider == null ? null : _configProvider();
                if (config != null && !string.IsNullOrWhiteSpace(config.ControllerAddress))
                {
                    return config.ControllerAddress.Trim();
                }
            }
            catch
            {
            }

            try
            {
                if (_peerAddressProvider != null)
                {
                    var address = _peerAddressProvider();
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        return address.Trim();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private void LogErrorThrottled(string ev, Exception ex)
        {
            var now = DateTime.UtcNow;
            var text = ex == null ? "unknown" : ex.GetType().Name + ": " + ex.Message;
            if (text == _lastError && now - _lastErrorLogUtc < TimeSpan.FromSeconds(60))
            {
                return;
            }

            _lastError = text;
            _lastErrorLogUtc = now;
            AppLog.Error(ev + " error=" + text, ex);
        }
    }
}
