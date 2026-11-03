using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Service
{
    /// <summary>
    /// Runs on the controller (主控端) machine: accepts log streams pushed by paired
    /// agent machines and writes them under Logs\remote\&lt;device&gt;\.
    /// </summary>
    internal sealed class LogCollector : IDisposable
    {
        private const long MaxFileBytes = 20L * 1024 * 1024;
        private readonly object _sync = new object();
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly string _localDeviceName;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private string _token = string.Empty;
        private int _port = LogProtocol.DefaultPort;
        private long _totalLines;
        private long _totalBytes;

        public LogCollector(string localDeviceName)
        {
            _localDeviceName = string.IsNullOrWhiteSpace(localDeviceName) ? "-" : localDeviceName;
        }

        public string StatusText()
        {
            int active;
            long lines;
            long bytes;
            lock (_sync)
            {
                active = 0;
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Active)
                    {
                        active++;
                    }
                }

                lines = _totalLines;
                bytes = _totalBytes;
            }

            return "\u65E5\u5FD7\u6536\u96C6\uFF1A" +
                   (active > 0 ? active + " \u53F0\u88AB\u63A7\u7AEF\u5DF2\u8FDE\u63A5" : "\u7B49\u5F85\u88AB\u63A7\u7AEF") +
                   " \u00B7 \u5171 " + lines + " \u884C / " + (bytes / 1024) + " KB";
        }

        public void Start(int port, string token)
        {
            lock (_sync)
            {
                if (_running)
                {
                    return;
                }

                _port = port <= 0 || port > 65535 ? LogProtocol.DefaultPort : port;
                _token = token == null ? string.Empty : token;
                try
                {
                    Directory.CreateDirectory(AppLog.RemoteDirectory);
                }
                catch
                {
                }

                try
                {
                    _listener = new TcpListener(IPAddress.Any, _port);
                    _listener.Start();
                }
                catch (Exception ex)
                {
                    _listener = null;
                    AppLog.Error("log.collector.listen.failed port=" + _port, ex);
                    return;
                }

                _running = true;
                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "LogAccept";
                _acceptThread.Start();
                AppLog.Event("INFO", "log.collector.start", "port=" + _port +
                                                            " token=" + (string.IsNullOrEmpty(_token) ? "off" : "on") +
                                                            " dir=" + AppLog.RemoteDirectory);
            }
        }

        public void Stop()
        {
            Thread accept;
            lock (_sync)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                accept = _acceptThread;
                _acceptThread = null;
                try
                {
                    if (_listener != null)
                    {
                        _listener.Stop();
                        _listener = null;
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (accept != null)
                {
                    accept.Join(1500);
                }
            }
            catch
            {
            }

            AppLog.Event("INFO", "log.collector.stop", "lines=" + _totalLines + " bytes=" + _totalBytes);
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_running)
                    {
                        return;
                    }

                    Thread.Sleep(200);
                    continue;
                }

                var thread = new Thread(new ThreadStart(delegate { HandleClient(client); }));
                thread.IsBackground = true;
                thread.Name = "LogClient";
                thread.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            var session = new ClientSession();
            var remote = "-";
            try
            {
                remote = client.Client.RemoteEndPoint.ToString();
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true))
                using (var handshake = new StreamWriter(stream, Encoding.UTF8, 8192, true))
                {
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;

                    var hello = reader.ReadLine();
                    if (hello == null || !hello.StartsWith(LogProtocol.HelloPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        TryWrite(handshake, LogProtocol.BuildDeny("bad-hello"));
                        AppLog.Warn("log.collector.reject bad-hello remote=" + remote);
                        return;
                    }

                    var values = LogProtocol.ParseKeyValues(hello, 1);
                    session.DeviceId = Get(values, "id");
                    session.DeviceName = Get(values, "name");
                    session.Role = Get(values, "role");
                    session.Version = Get(values, "version");
                    session.Address = remote;
                    var token = Get(values, "token");
                    if (string.IsNullOrEmpty(token) || token == "-")
                    {
                        token = string.Empty;
                    }

                    if (!string.IsNullOrEmpty(_token) && !string.Equals(token, _token, StringComparison.Ordinal))
                    {
                        TryWrite(handshake, LogProtocol.BuildDeny("bad-token"));
                        AppLog.Warn("log.collector.reject bad-token remote=" + remote + " device=" + session.DeviceName);
                        return;
                    }

                    var directory = Path.Combine(AppLog.RemoteDirectory, SafeFolder(session.DeviceName, session.DeviceId));
                    Directory.CreateDirectory(directory);
                    session.FilePath = NewFilePath(directory, session.Role);
                    session.Active = true;
                    lock (_sync)
                    {
                        _sessions.Add(session);
                    }

                    TryWrite(handshake, LogProtocol.BuildOk(_localDeviceName));
                    AppLog.Event("INFO", "log.collector.accept", "remote=" + remote +
                                                                   " device=" + session.DeviceName +
                                                                   " id=" + session.DeviceId +
                                                                   " version=" + session.Version +
                                                                   " file=" + session.FilePath);

                    ReadLoop(reader, session, directory);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("log.collector.client.failed remote=" + remote + " device=" + session.DeviceName, ex);
            }
            finally
            {
                session.Active = false;
                lock (_sync)
                {
                    _sessions.Remove(session);
                }

                AppLog.Event("INFO", "log.collector.bye", "remote=" + remote +
                                                          " device=" + session.DeviceName +
                                                          " lines=" + session.Lines +
                                                          " file=" + session.FilePath);
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }

        private void ReadLoop(StreamReader reader, ClientSession session, string directory)
        {
            var writer = OpenLogWriter(session.FilePath, session);
            List<string> sysInfo = null;
            var sysInfoName = SystemInfoReport.FileName;
            try
            {
                while (_running)
                {
                    string line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (line == null)
                    {
                        break;
                    }

                    if (line.StartsWith(LogProtocol.SysInfoBegin, StringComparison.Ordinal))
                    {
                        sysInfo = new List<string>();
                        sysInfoName = ParseSysInfoName(line);
                        continue;
                    }

                    if (sysInfo != null && line.StartsWith(LogProtocol.SysInfoLinePrefix, StringComparison.Ordinal))
                    {
                        sysInfo.Add(line.Substring(LogProtocol.SysInfoLinePrefix.Length));
                        continue;
                    }

                    if (line == LogProtocol.SysInfoEnd)
                    {
                        if (sysInfo != null)
                        {
                            WriteSystemInfo(directory, session, sysInfoName, sysInfo);
                        }

                        sysInfo = null;
                        sysInfoName = SystemInfoReport.FileName;
                        continue;
                    }

                    if (line.Length > 0 && !line.StartsWith(LogProtocol.MarkerPrefix, StringComparison.Ordinal))
                    {
                        if (line.Length > LogProtocol.MaxLineLength)
                        {
                            line = line.Substring(0, LogProtocol.MaxLineLength);
                        }

                        writer.WriteLine(line);
                        session.Lines++;
                        session.LastActivityUtc = DateTime.UtcNow;
                        lock (_sync)
                        {
                            _totalLines++;
                            _totalBytes += line.Length + 2;
                        }
                    }

                    if (writer.BaseStream.Length > MaxFileBytes)
                    {
                        writer.Flush();
                        writer.Dispose();
                        session.FilePath = NewFilePath(directory, session.Role);
                        writer = OpenLogWriter(session.FilePath, session);
                    }
                }
            }
            finally
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                }
            }
        }

        private static void WriteSystemInfo(string directory, ClientSession session, string fileName, List<string> lines)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = SystemInfoReport.FileName;
                }

                fileName = Path.GetFileName(fileName).Replace("..", "_");
                var path = Path.Combine(directory, fileName);
                var builder = new StringBuilder();
                builder.AppendLine("# SystemInfo from " + session.DeviceName + " (" + session.DeviceId + ")");
                builder.AppendLine("# received at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                for (int i = 0; i < lines.Count; i++)
                {
                    builder.AppendLine(lines[i]);
                }

                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
                AppLog.Event("INFO", "log.collector.sysinfo", "device=" + session.DeviceName + " file=" + fileName +
                                                             " lines=" + lines.Count + " path=" + path);
            }
            catch (Exception ex)
            {
                AppLog.Warn("log.collector.sysinfo.fail device=" + session.DeviceName + " error=" +
                            ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string ParseSysInfoName(string line)
        {
            var rest = line.Substring(LogProtocol.SysInfoBegin.Length).Trim();
            var index = rest.IndexOf("file=", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return SystemInfoReport.FileName;
            }

            var name = rest.Substring(index + 5).Trim().Trim('"');
            return string.IsNullOrWhiteSpace(name) ? SystemInfoReport.FileName : name;
        }

        private static StreamWriter OpenLogWriter(string path, ClientSession session)
        {
            var exists = File.Exists(path);
            var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
            // The controller UI reads these files live; never leave lines sitting in the buffer.
            writer.AutoFlush = true;
            if (!exists)
            {
                writer.WriteLine(LogProtocol.BuildOrigin(session.DeviceId, session.DeviceName, session.Role, session.Version, session.Address));
            }

            writer.Flush();
            return writer;
        }

        private static void TryWrite(StreamWriter writer, string text)
        {
            try
            {
                writer.WriteLine(text);
                writer.Flush();
            }
            catch
            {
            }
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            if (values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            return "-";
        }

        private static string NewFilePath(string directory, string role)
        {
            var prefix = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + LogProtocol.Safe(role, 12);
            var path = Path.Combine(directory, prefix + ".log");
            var index = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(directory, prefix + "-" + index + ".log");
                index++;
            }

            return path;
        }

        /// <summary>Builds a filesystem-safe folder name: device name plus a short device id.</summary>
        public static string SafeFolder(string deviceName, string deviceId)
        {
            var name = LogProtocol.Safe(deviceName, 24);
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                var bad = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (invalid[j] == ch)
                    {
                        bad = true;
                        break;
                    }
                }

                builder.Append(bad ? '_' : ch);
            }

            var id = LogProtocol.Safe(deviceId, 8);
            if (id == "-")
            {
                id = "unknown";
            }

            return builder.ToString() + "_" + id;
        }

        private sealed class ClientSession
        {
            public string DeviceId;
            public string DeviceName;
            public string Role;
            public string Version;
            public string Address;
            public string FilePath;
            public bool Active;
            public int Lines;
            public DateTime LastActivityUtc = DateTime.UtcNow;
        }
    }
}
