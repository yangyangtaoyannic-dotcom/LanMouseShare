using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent.FileLink
{
    /// <summary>
    /// fix8 数据通道的发起端。每个公开方法都是一次"一条连接、一个操作"的完整会话：
    /// 连接 → 握手 → 请求 → 逐文件确认 → 关闭。失败只影响本次调用，不会污染后续传输。
    /// </summary>
    internal sealed class FileLinkClient
    {
        private readonly Func<string> _peerAddress;
        private readonly Func<byte[]> _peerSecret;
        private readonly string _localDeviceId;
        private readonly string _localDeviceName;

        public FileLinkClient(Func<string> peerAddress, Func<byte[]> peerSecret, string localDeviceId, string localDeviceName)
            : this(peerAddress, peerSecret, localDeviceId, localDeviceName, FileLinkProtocol.DefaultPort)
        {
        }

        public FileLinkClient(Func<string> peerAddress, Func<byte[]> peerSecret, string localDeviceId, string localDeviceName, int port)
        {
            _peerAddress = peerAddress;
            _peerSecret = peerSecret;
            _localDeviceId = localDeviceId;
            _localDeviceName = localDeviceName;
            _port = port > 0 ? port : FileLinkProtocol.DefaultPort;
            LastError = string.Empty;
        }

        private readonly int _port;

        public string LastError { get; private set; }

        // fix9：真正的取消——把当前连接直接掐断，卡在读写上的调用会立刻抛异常退出，
        // 不用等 30 秒 IO 超时。界面上的“取消传输”按钮就是调 Cancel()。
        private volatile bool _cancelled;
        private Session _active;

        public void Cancel()
        {
            _cancelled = true;
            var session = _active;
            if (session != null)
            {
                try { session.Dispose(); } catch (Exception) { }
            }
        }

        private void ThrowIfCancelled()
        {
            if (_cancelled)
            {
                throw new IOException("已取消传输");
            }
        }

        /// <summary>推文件：先逐个问"收不收"，只传被接受的；每个文件等 done 落定。</summary>
        public bool PushFiles(IList<string> paths, Action<FileLinkProgress> progress, out int okCount, out int failCount)
        {
            okCount = 0;
            failCount = 0;
            LastError = string.Empty;

            if (paths == null || paths.Count == 0)
            {
                LastError = "没有要发送的文件。";
                return false;
            }

            var files = new List<FileLinkFile>();
            var sources = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    AppLog.Warn("filelink.push.skip path=" + path + " reason=missing");
                    failCount++;
                    continue;
                }

                var info = new FileInfo(path);
                files.Add(new FileLinkFile { Name = info.Name, Size = info.Length });
                sources.Add(path);
            }

            if (files.Count == 0)
            {
                LastError = "要发送的文件都不存在了。";
                return false;
            }

            using (var session = Open())
            {
                if (session == null)
                {
                    // 连不上（对端没装新版 / 不在线 / 端口被挡）：每个文件都算失败，
                    // 这样界面不会显示"发送成功 0 个文件"这种误导性结果。
                    failCount += files.Count;
                    return false;
                }

                var stream = session.Stream;
                Action<FileLinkProgress> safeProgress = progress == null
                    ? null
                    : new Action<FileLinkProgress>(delegate(FileLinkProgress p)
                    {
                        ThrowIfCancelled();
                        progress(p);
                    });
                var push = new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpPush,
                    TransferId = Guid.NewGuid().ToString("N"),
                    Files = files
                };
                FileLinkFrames.Write(stream, push);

                for (var i = 0; i < files.Count; i++)
                {
                    ThrowIfCancelled();
                    var reply = FileLinkFrames.Read(stream);
                    if (reply == null)
                    {
                        LastError = "对端在中途断开了连接。";
                        failCount += files.Count - i;
                        return false;
                    }

                    if (reply.Op == FileLinkProtocol.OpError)
                    {
                        LastError = reply.Reason ?? "对端拒绝接收。";
                        failCount += files.Count - i;
                        return false;
                    }

                    if (reply.Op != FileLinkProtocol.ReplyAccept || !reply.Ok)
                    {
                        AppLog.Info("filelink.push.rejected index=" + i + " name=" + files[i].Name + " reason=" + (reply.Reason ?? "unknown"));
                        failCount++;
                        continue;
                    }

                    try
                    {
                        using (var input = new FileStream(sources[i], FileMode.Open, FileAccess.Read, FileShare.Read, FileLinkProtocol.FileBufferSize, FileOptions.SequentialScan))
                        {
                            var length = input.Length;
                            files[i].Size = length;
                            FileLinkStreamer.Send(stream, input, length, files[i].Name, false, safeProgress);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_cancelled)
                        {
                            // 用户点了“取消传输”，底层是 socket 被掐断引发的 ObjectDisposed/IO 异常，
                            // 界面上要显示人能看懂的话，而不是 .NET 的原文。
                            AppLog.Info("filelink.push.cancelled name=" + files[i].Name);
                            LastError = "已取消传输";
                            failCount++;
                            return false;
                        }

                        AppLog.Error("filelink.push.stream.fail name=" + files[i].Name, ex);
                        LastError = "发送 " + files[i].Name + " 失败：" + ex.Message;
                        failCount++;
                        return false;
                    }

                    var done = FileLinkFrames.Read(stream);
                    if (done == null)
                    {
                        LastError = "对端在确认前断开了连接。";
                        failCount++;
                        return false;
                    }

                    if (done.Op == FileLinkProtocol.ReplyDone && done.Ok)
                    {
                        okCount++;
                    }
                    else
                    {
                        failCount++;
                        AppLog.Warn("filelink.push.not-saved name=" + files[i].Name + " reason=" + (done.Reason ?? "unknown"));
                    }
                }

                AppLog.Info("filelink.push.done ok=" + okCount + " fail=" + failCount + " total=" + files.Count);
                TryReadSummary(stream);
            }

            return okCount > 0;
        }

        /// <summary>只公告文件清单（不传字节），对端据此发布"可粘贴"的虚拟剪贴板。</summary>
        public bool OfferFiles(IList<string> paths)
        {
            LastError = string.Empty;
            if (paths == null || paths.Count == 0)
            {
                LastError = "没有要公告的文件。";
                return false;
            }

            var files = new List<FileLinkFile>();
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                var info = new FileInfo(path);
                files.Add(new FileLinkFile { Name = info.Name, Size = info.Length, SourcePath = path });
            }

            if (files.Count == 0)
            {
                LastError = "要公告的文件都不存在了。";
                return false;
            }

            using (var session = Open())
            {
                if (session == null)
                {
                    return false;
                }

                var offer = new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpOffer,
                    TransferId = Guid.NewGuid().ToString("N"),
                    Files = files
                };
                FileLinkFrames.Write(session.Stream, offer);

                var reply = FileLinkFrames.Read(session.Stream);
                if (reply == null)
                {
                    LastError = "对端没有回应文件公告。";
                    return false;
                }

                if (reply.Op == FileLinkProtocol.OpError)
                {
                    LastError = reply.Reason ?? "对端拒绝了文件公告。";
                    return false;
                }

                AppLog.Info("filelink.offer.sent count=" + files.Count);
                return true;
            }
        }

        /// <summary>
        /// 从对端拉一个文件到本机。destProvider 决定落盘路径（本方法负责 .part 临时文件与改名）。
        /// 返回落盘路径，失败返回 null。大文件不受旧链路的 25 秒限制，只受真实传输速度限制。
        /// </summary>
        public string PullFile(string name, string remotePath, long expectedSize, Func<string> destProvider, Action<FileLinkProgress> progress)
        {
            LastError = string.Empty;

            using (var session = Open())
            {
                if (session == null)
                {
                    return null;
                }

                var stream = session.Stream;
                var request = new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpPull,
                    TransferId = Guid.NewGuid().ToString("N"),
                    Files = new List<FileLinkFile>
                    {
                        new FileLinkFile { Name = name, SourcePath = remotePath, Size = expectedSize }
                    }
                };
                FileLinkFrames.Write(stream, request);

                var reply = FileLinkFrames.Read(stream);
                if (reply == null)
                {
                    LastError = "对端断开了连接。";
                    return null;
                }

                if (reply.Op == FileLinkProtocol.OpError)
                {
                    LastError = reply.Reason ?? "对端拒绝了拉取请求。";
                    return null;
                }

                if (reply.Op != FileLinkProtocol.ReplyFile)
                {
                    LastError = reply.Reason ?? "对端没有这个文件。";
                    return null;
                }

                var entry = reply.Files != null && reply.Files.Count > 0 ? reply.Files[0] : null;
                var size = entry != null && entry.Size > 0 ? entry.Size : expectedSize;
                var expectedSha = entry != null ? entry.Sha256 : null;

                var destination = destProvider != null ? destProvider() : null;
                if (string.IsNullOrEmpty(destination))
                {
                    LastError = "拿不到接收目录。";
                    return null;
                }

                // 对端可能改过文件名（例如文件夹是按需打包成 lms-copy-*.zip 送过来的），
                // 一律以对端给的名字落盘，这样 ExpandIfArchive 才能把 zip 解回原目录名。
                var servedName = entry != null && !string.IsNullOrEmpty(entry.Name) ? entry.Name : name;
                if (!string.IsNullOrEmpty(servedName)
                    && !string.Equals(Path.GetFileName(destination), servedName, StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(destination);
                    destination = string.IsNullOrEmpty(dir) ? servedName : Path.Combine(dir, servedName);
                }

                destination = UniquePath(destination);
                var tempPath = destination + ".part";

                try
                {
                    string actualSha;
                    Action<FileLinkProgress> safeProgress = progress == null
                        ? null
                        : new Action<FileLinkProgress>(delegate(FileLinkProgress p)
                        {
                            ThrowIfCancelled();
                            progress(p);
                        });
                    using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileLinkProtocol.FileBufferSize, FileOptions.SequentialScan))
                    {
                        actualSha = ReceiveToStream(stream, output, size, name, safeProgress);
                    }

                    if (!string.IsNullOrEmpty(expectedSha) && !string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tempPath);
                        LastError = "校验失败：" + name;
                        AppLog.Warn("filelink.pull.hash.mismatch name=" + name);
                        return null;
                    }

                    FileLinkFrames.Write(stream, new FileLinkMessage { Op = FileLinkProtocol.ReplyAck, Ok = true });
                    File.Move(tempPath, destination);
                    AppLog.Info("filelink.pull.saved name=" + name + " size=" + size + " path=" + destination);
                    TryReadSummary(stream);
                    return destination;
                }
                catch (Exception ex)
                {
                    TryDelete(tempPath);
                    if (_cancelled)
                    {
                        AppLog.Info("filelink.pull.cancelled name=" + name);
                        LastError = "已取消传输";
                        return null;
                    }

                    AppLog.Error("filelink.pull.fail name=" + name, ex);
                    LastError = ex.Message;
                    return null;
                }
            }
        }

        public bool SendClipboard(string kind, string text, byte[] data)
        {
            LastError = string.Empty;

            using (var session = Open())
            {
                if (session == null)
                {
                    return false;
                }

                var message = new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpClipboard,
                    TransferId = Guid.NewGuid().ToString("N"),
                    Kind = kind,
                    Text = text,
                    DataBase64 = data == null ? null : Convert.ToBase64String(data)
                };
                FileLinkFrames.Write(session.Stream, message);

                var reply = FileLinkFrames.Read(session.Stream);
                if (reply == null)
                {
                    LastError = "对端没有回应剪贴板。";
                    return false;
                }

                if (reply.Op == FileLinkProtocol.OpError)
                {
                    LastError = reply.Reason ?? "对端拒绝了剪贴板。";
                    return false;
                }

                return reply.Ok;
            }
        }

        /// <summary>从网络读满 size 字节写进 output，同时算 sha256；读不满就抛异常（不静默截断）。</summary>
        internal static string ReceiveToStream(Stream input, Stream output, long size, string name, Action<FileLinkProgress> progress)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[FileLinkProtocol.BufferSize];
                var got = 0L;
                var lastReport = Environment.TickCount;

                while (got < size)
                {
                    var want = (int)Math.Min(buffer.Length, size - got);
                    var read = input.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        throw new IOException("对端提前断流：" + name + " (" + got + "/" + size + ")");
                    }

                    if (output != null)
                    {
                        output.Write(buffer, 0, read);
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    got += read;

                    var now = Environment.TickCount;
                    if (progress != null && (now - lastReport >= 250 || got == size))
                    {
                        lastReport = now;
                        progress(new FileLinkProgress { Name = name, TotalBytes = size, DoneBytes = got, Receiving = true });
                    }
                }

                if (output != null)
                {
                    output.Flush();
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return FileLinkAuth.ToHex(sha.Hash);
            }
        }

        private sealed class Session : IDisposable
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public FileLinkClient Owner;

            public void Dispose()
            {
                try { if (Owner != null) Owner._active = null; } catch (Exception) { }
                try { if (Stream != null) Stream.Dispose(); } catch (Exception) { }
                try { if (Client != null) Client.Close(); } catch (Exception) { }
            }
        }

        private Session Open()
        {
            if (_cancelled)
            {
                LastError = "已取消传输";
                return null;
            }

            var address = _peerAddress != null ? _peerAddress() : null;
            if (string.IsNullOrEmpty(address))
            {
                LastError = "还不知道对端地址（未发现或未配对）。";
                return null;
            }

            var client = new TcpClient();
            try
            {
                client.NoDelay = true;
                try
                {
                    // fix10：显式放大 socket 缓冲，避免大块写入时被当作慢速应用而降窗。
                    client.SendBufferSize = FileLinkProtocol.SocketBufferSize;
                    client.ReceiveBufferSize = FileLinkProtocol.SocketBufferSize;
                }
                catch (Exception)
                {
                }

                var async = client.BeginConnect(address, _port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(FileLinkProtocol.ConnectTimeoutMs))
                {
                    try { client.Close(); } catch (Exception) { }
                    LastError = "连接对端 " + address + ":" + _port + " 超时。";
                    AppLog.Warn("filelink.connect.timeout address=" + address);
                    return null;
                }

                client.EndConnect(async);
                client.SendTimeout = FileLinkProtocol.IoTimeoutMs;
                client.ReceiveTimeout = FileLinkProtocol.IoTimeoutMs;

                var stream = client.GetStream();
                var session = new Session { Client = client, Stream = stream, Owner = this };
                _active = session;

                var hello = new FileLinkMessage
                {
                    Op = FileLinkProtocol.OpHello,
                    Version = FileLinkProtocol.Version,
                    DeviceId = _localDeviceId,
                    DeviceName = _localDeviceName,
                    Nonce = FileLinkAuth.NewNonce()
                };
                hello.Proof = FileLinkAuth.CreateProof(_peerSecret != null ? _peerSecret() : null, hello.Nonce, _localDeviceId);
                FileLinkFrames.Write(stream, hello);

                var welcome = FileLinkFrames.Read(stream);
                if (welcome == null || welcome.Op != FileLinkProtocol.OpWelcome || !welcome.Ok)
                {
                    LastError = welcome != null && !string.IsNullOrEmpty(welcome.Reason) ? welcome.Reason : "握手失败。";
                    session.Dispose();
                    return null;
                }

                return session;
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.connect.fail address=" + address, ex);
                LastError = ex.Message;
                try { client.Close(); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>尽力读掉对端的 summary 帧：不读会让对端写帧时撞上"连接已被关闭"的假错误。</summary>
        private static void TryReadSummary(Stream stream)
        {
            try
            {
                var tail = FileLinkFrames.Read(stream);
                if (tail != null && tail.Op != FileLinkProtocol.ReplySummary && tail.Op != FileLinkProtocol.ReplyDone)
                {
                    AppLog.Debug("filelink.tail.unexpected", "op=" + tail.Op);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("filelink.tail.skip", ex.Message);
            }
        }

        private static void TryDelete(string path)        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        internal static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 1; i < 999; i++)
            {
                var candidate = Path.Combine(dir ?? string.Empty, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(dir ?? string.Empty, stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + ext);
        }
    }
}
