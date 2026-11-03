using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent.FileLink
{
    /// <summary>
    /// fix8 全新数据通道的线上格式。
    ///
    /// 设计原则（针对旧链路的失效模式逐条对症）：
    ///  1. 一条连接只做一件事（推文件 / 拉文件 / 公告 / 剪贴板），做完立刻断开，不复用流，
    ///     因此不可能出现"上一帧没读干净、下一帧错位"的问题。
    ///  2. 每个文件都是"先问后传"：接收方先回 accept/reject，发送方只传被接受的文件。
    ///     被拒绝的文件一个字节都不进网络，接收方也就没有需要丢弃的残留字节。
    ///  3. 每个文件传完，接收方回一条 done（含校验结果），发送方因此能确知结果，
    ///     不会出现"日志说发了、对端其实没收到"的静默失败。
    ///  4. 通道只依赖 Agent 进程自己的 TCP 监听与对端 IP，不经过 Service、不经过 IPC 管道、
    ///     也不受"暂停共享/控制租约"影响。
    /// </summary>
    internal static class FileLinkProtocol
    {
        public const int Version = 1;

        /// <summary>fix8 数据通道端口（安装器需要放行该 TCP 端口）。</summary>
        public const int DefaultPort = 47541;

        private const int MaxFrameBytes = 4 * 1024 * 1024;

        /// <summary>单帧 JSON 的上限，供帧读写共用。</summary>
        internal const int MaxPayloadForFrame = MaxFrameBytes;

        // fix10：单次读写块从 1MB 提到 4MB，配合下面的 socket 缓冲区与 1MB 文件流缓冲，
        // 千兆网下大文件的吞吐明显更稳（每 4MB 才一次 syscall/进度回调）。
        public const int BufferSize = 4 * 1024 * 1024;

        /// <summary>TCP 收发缓冲区，配合 BufferSize 减少等待窗口造成的抖动。</summary>
        public const int SocketBufferSize = 1024 * 1024;

        /// <summary>落盘用的 FileStream 缓冲区，顺序写避免频繁小 I/O。</summary>
        public const int FileBufferSize = 1024 * 1024;

        public const int HandshakeTimeoutMs = 4000;
        public const int IoTimeoutMs = 30000;
        public const int ConnectTimeoutMs = 4000;

        // 操作类型
        public const string OpHello = "hello";
        public const string OpWelcome = "welcome";
        public const string OpError = "error";
        public const string OpPush = "push";
        public const string OpPull = "pull";
        public const string OpOffer = "offer";
        public const string OpClipboard = "clipboard";

        // 会话内的应答类型
        public const string ReplyAccept = "accept";
        public const string ReplyReject = "reject";
        public const string ReplyDone = "done";
        public const string ReplyFile = "file";
        public const string ReplyAck = "ack";
        public const string ReplySummary = "summary";

        /// <summary>剪贴板分类。</summary>
        public const string ClipboardText = "text";
        public const string ClipboardImage = "image";
    }

    /// <summary>通道里传输的单个文件描述。Name 是文件名（不含目录），SourcePath 只在 offer/pull 里用。</summary>
    [DataContract]
    internal sealed class FileLinkFile
    {
        [DataMember(Order = 1)]
        public string Name { get; set; }

        [DataMember(Order = 2)]
        public long Size { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string Sha256 { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string SourcePath { get; set; }
    }

    /// <summary>
    /// 通道上的唯一消息类型。用"一个扁平 DTO + Op 字段"而不是多态，是因为
    /// DataContractJsonSerializer 不擅长多态，而扁平结构在日志里也更好读。
    /// </summary>
    [DataContract]
    internal sealed class FileLinkMessage
    {
        [DataMember(Order = 1)]
        public string Op { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public int Version { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string DeviceId { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string DeviceName { get; set; }

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public string TransferId { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public List<FileLinkFile> Files { get; set; }

        [DataMember(Order = 7, EmitDefaultValue = false)]
        public int Index { get; set; }

        [DataMember(Order = 8, EmitDefaultValue = false)]
        public bool Ok { get; set; }

        [DataMember(Order = 9, EmitDefaultValue = false)]
        public string Reason { get; set; }

        [DataMember(Order = 10, EmitDefaultValue = false)]
        public string SavedAs { get; set; }

        [DataMember(Order = 11, EmitDefaultValue = false)]
        public string Kind { get; set; }

        [DataMember(Order = 12, EmitDefaultValue = false)]
        public string Text { get; set; }

        [DataMember(Order = 13, EmitDefaultValue = false)]
        public string DataBase64 { get; set; }

        [DataMember(Order = 14, EmitDefaultValue = false)]
        public string Nonce { get; set; }

        [DataMember(Order = 15, EmitDefaultValue = false)]
        public string Proof { get; set; }

        [DataMember(Order = 16, EmitDefaultValue = false)]
        public int OkCount { get; set; }

        [DataMember(Order = 17, EmitDefaultValue = false)]
        public int FailCount { get; set; }
    }

    internal sealed class FileLinkProgress
    {
        public string Name;
        public long TotalBytes;
        public long DoneBytes;
        public bool Receiving;
    }

    /// <summary>长度前缀（小端 uint32）+ UTF-8 JSON 的帧收发，读操作保证读满或抛异常，绝不返回半帧。</summary>
    internal static class FileLinkFrames
    {
        public static void Write(Stream stream, FileLinkMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            var json = Encoding.UTF8.GetBytes(ProtocolSerializer.ToJson(message));
            if (json.Length > FileLinkProtocol.MaxPayloadForFrame)
            {
                throw new InvalidOperationException("FileLink frame too large: " + json.Length);
            }

            var header = new byte[4];
            header[0] = (byte)(json.Length & 0xFF);
            header[1] = (byte)((json.Length >> 8) & 0xFF);
            header[2] = (byte)((json.Length >> 16) & 0xFF);
            header[3] = (byte)((json.Length >> 24) & 0xFF);
            stream.Write(header, 0, header.Length);
            stream.Write(json, 0, json.Length);
            stream.Flush();
        }

        /// <summary>读到干净 EOF 时返回 null；读到半帧或长度非法时抛 InvalidDataException。</summary>
        public static FileLinkMessage Read(Stream stream)
        {
            var header = new byte[4];
            var got = ReadFull(stream, header, 0, header.Length, true);
            if (got == 0)
            {
                return null;
            }

            if (got != header.Length)
            {
                throw new InvalidDataException("FileLink frame header truncated.");
            }

            var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length <= 0 || length > FileLinkProtocol.MaxPayloadForFrame)
            {
                throw new InvalidDataException("FileLink frame length invalid: " + length);
            }

            var payload = new byte[length];
            if (ReadFull(stream, payload, 0, length, false) != length)
            {
                throw new InvalidDataException("FileLink frame payload truncated.");
            }

            return ProtocolSerializer.FromJson<FileLinkMessage>(Encoding.UTF8.GetString(payload));
        }

        /// <summary>读满 count 字节。allowEof=true 时允许第一字节就 EOF（返回 0）。</summary>
        public static int ReadFull(Stream stream, byte[] buffer, int offset, int count, bool allowEof)
        {
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0)
                {
                    if (allowEof && read == 0)
                    {
                        return 0;
                    }

                    return read;
                }

                read += n;
            }

            return read;
        }
    }

    /// <summary>握手证明：用配对时协商出的共享密钥对 (nonce|deviceId) 做 HMAC-SHA256。</summary>
    internal static class FileLinkAuth
    {
        public static string CreateProof(byte[] sharedSecret, string nonce, string deviceId)
        {
            if (sharedSecret == null || sharedSecret.Length == 0 || string.IsNullOrEmpty(nonce))
            {
                return string.Empty;
            }

            using (var hmac = new HMACSHA256(sharedSecret))
            {
                var data = Encoding.UTF8.GetBytes(nonce + "|" + (deviceId ?? string.Empty));
                return ToHex(hmac.ComputeHash(data));
            }
        }

        public static bool VerifyProof(byte[] sharedSecret, string nonce, string deviceId, string proof)
        {
            var expected = CreateProof(sharedSecret, nonce, deviceId);
            if (expected.Length == 0)
            {
                // 没有共享密钥（老配对数据）时降级为"按设备 ID 信任"，并让调用方记一条警告。
                return true;
            }

            return FixedTimeEquals(expected, proof ?? string.Empty);
        }

        public static string NewNonce()
        {
            var bytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }

            return ToHex(bytes);
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        public static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = hex[(bytes[i] >> 4) & 0xF];
                chars[i * 2 + 1] = hex[bytes[i] & 0xF];
            }

            return new string(chars);
        }
    }

    /// <summary>带进度与可取消的字节搬运。无论成功失败都保证"要么读满 length，要么把连接丢掉"，不会留下半帧。</summary>
    internal static class FileLinkStreamer
    {
        public static string Send(Stream output, Stream input, long length, string name, bool receiving, Action<FileLinkProgress> progress)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[FileLinkProtocol.BufferSize];
                var sent = 0L;
                var lastReport = Environment.TickCount;

                while (sent < length)
                {
                    var want = (int)Math.Min(buffer.Length, length - sent);
                    var read = input.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        throw new IOException("Source file ended early: " + name + " (" + sent + "/" + length + ")");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    if (output != null)
                    {
                        output.Write(buffer, 0, read);
                    }

                    sent += read;
                    var now = Environment.TickCount;
                    if (progress != null && (now - lastReport >= 250 || sent == length))
                    {
                        lastReport = now;
                        progress(new FileLinkProgress { Name = name, TotalBytes = length, DoneBytes = sent, Receiving = receiving });
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

        /// <summary>只读丢弃 length 字节——用于"对端拒绝/取消时把在途数据吃干净"，与 fix7 的 drain 思路一致。</summary>
        public static void Drain(Stream input, long length)
        {
            var buffer = new byte[64 * 1024];
            var left = length;
            while (left > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                {
                    throw new IOException("Connection closed while draining " + left + " bytes.");
                }

                left -= read;
            }
        }
    }
}
