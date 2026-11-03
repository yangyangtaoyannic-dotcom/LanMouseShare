using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Networking;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class DirectInputChannel : IDisposable
    {
        public const int Port = 47534;
        private const int Magic = ControlChannelCodec.Magic;
        private const byte FrameVersion = ControlChannelCodec.Version;
        private const byte FrameInputBatch = ControlChannelCodec.FrameInputBatch;
        private const byte FrameLease = ControlChannelCodec.FrameLease;
        private const byte FrameAck = ControlChannelCodec.FrameAck;
        private const byte FrameClipboardFiles = ControlChannelCodec.FrameClipboardFiles;

        // Compact input frames (0.1.50): the payload of a plain relative mouse move is
        // 28 bytes in the legacy frame and 2 bytes here, which is most of the wire cost
        // while the pointer moves. Version 4 is only used for these frame types.
        private const byte VersionCompact = 4;
        private const byte VersionSequenced = 5;
        private const byte FrameMouseRelativeCompact = 3;
        private const byte FrameMouseAbsoluteCompact = 4;
        private const int MaxCompactEvents = 200;

        private readonly UdpClient _listener;
        private readonly UdpClient _sender;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private IPEndPoint _peerEndpoint;
        private long _framesSent;
        private long _bytesSent;
        private int _lastFrameBytes;
        private ushort _inputSeq;
        private ushort _lastInputSeq;
        private bool _hasInputSeq;
        private bool _qosMarked;
        private DateTime _lastFrameLogUtc = DateTime.MinValue;

        public event EventHandler<InputBatchReceivedEventArgs> InputBatchReceived;
        public event EventHandler<ControlLeaseReceivedEventArgs> ControlLeaseReceived;
        public event EventHandler<ControlAckReceivedEventArgs> ControlAckReceived;
        public event EventHandler<ClipboardFilesReceivedEventArgs> ClipboardFilesReceived;

        public DirectInputChannel()
        {
            _listener = new UdpClient(Port);
            _sender = new UdpClient();
            _thread = new Thread(ListenLoop);
            _thread.IsBackground = true;
            _thread.Name = "LanMouseShare direct input listener";
            _thread.Start();
            _qosMarked = TrafficPriority.TryMarkInteractive(_sender.Client);
            AppLog.Info("DirectInputChannel listening on UDP " + Port + " qos=" + (_qosMarked ? "dscp46" : "none"));
        }

        public long FramesSent
        {
            get { return Interlocked.Read(ref _framesSent); }
        }

        public long BytesSent
        {
            get { return Interlocked.Read(ref _bytesSent); }
        }

        public int LastFrameBytes
        {
            get { return _lastFrameBytes; }
        }

        public bool HasPeer
        {
            get { return _peerEndpoint != null; }
        }

        public void SetPeer(string address)
        {
            IPAddress ip;
            if (IPAddress.TryParse(address ?? string.Empty, out ip))
            {
                _peerEndpoint = new IPEndPoint(ip, Port);
            }
            else
            {
                _peerEndpoint = null;
            }
        }

        public bool Send(InputPayload[] payloads)
        {
            if (payloads == null || payloads.Length == 0)
            {
                return false;
            }

            return SendRaw(EncodeInput(payloads));
        }

        public bool SendControlLease(ControlLease lease)
        {
            var bytes = ControlChannelCodec.EncodeLease(lease);
            return bytes != null && SendRaw(bytes);
        }

        public bool SendControlAck(long epoch)
        {
            return SendRaw(ControlChannelCodec.EncodeAck(epoch));
        }

        /// <summary>
        /// Announces the copied file list over the low-latency channel. The TCP clipboard
        /// envelope still follows, but it can stall for seconds on Wi-Fi; this frame is what
        /// makes a paste right after the copy see the files.
        /// </summary>
        public void SendClipboardFiles(string[] paths)
        {
            var bytes = ControlChannelCodec.EncodeClipboardFiles(paths);
            if (bytes == null)
            {
                return;
            }

            if (!SendRaw(bytes))
            {
                AppLog.Debug("clipboard.announce.fail", "count=" + (paths == null ? 0 : paths.Length));
            }
        }
        private bool SendRaw(byte[] bytes)
        {
            var endpoint = _peerEndpoint;
            if (endpoint == null)
            {
                return false;
            }

            try
            {
                _sender.Send(bytes, bytes.Length, endpoint);
                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, bytes.Length);
                _lastFrameBytes = bytes.Length;
                if (AppLog.IsVerbose && (DateTime.UtcNow - _lastFrameLogUtc).TotalMilliseconds >= 250)
                {
                    _lastFrameLogUtc = DateTime.UtcNow;
                    AppLog.Debug("input.frame", "bytes=" + bytes.Length + " header=" + (bytes.Length > 4 && bytes[4] == VersionCompact ? "v4" : "v3") + " type=" + (bytes.Length > 5 ? bytes[5].ToString() : "?") + " framesTotal=" + _framesSent);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Direct input send failed.", ex);
                return false;
            }
        }

        private static void WriteHeader(BinaryWriter writer, byte frameType)
        {
            ControlChannelCodec.WriteHeader(writer, frameType);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = null;
                    var bytes = _listener.Receive(ref remote);
                    DispatchFrame(bytes);
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        AppLog.Error("Direct input receive failed.", ex);
                        Thread.Sleep(25);
                    }
                }
            }
        }

        private void DispatchFrame(byte[] bytes)
        {
            if (bytes.Length > 6 && bytes[4] >= ControlChannelCodec.Version && bytes[5] == FrameClipboardFiles)
            {
                string[] announced;
                if (ControlChannelCodec.TryDecodeClipboardFiles(bytes, out announced))
                {
                    var announceHandler = ClipboardFilesReceived;
                    if (announceHandler != null)
                    {
                        announceHandler(this, new ClipboardFilesReceivedEventArgs(announced));
                    }
                }

                return;
            }

            InputPayload[] payloads;
            ControlLease lease;
            long ackEpoch;
            byte frameType;
            ushort sequence;
            bool hasSequence;
            if (!DecodeFrame(bytes, out frameType, out sequence, out hasSequence, out payloads, out lease, out ackEpoch))
            {
                return;
            }

            if (frameType == FrameInputBatch)
            {
                if (hasSequence && !AcceptSequence(sequence))
                {
                    return;
                }

                var handler = InputBatchReceived;
                if (handler != null && payloads.Length > 0)
                {
                    handler(this, new InputBatchReceivedEventArgs(payloads));
                }

                return;
            }

            if (frameType == FrameLease)
            {
                var handler = ControlLeaseReceived;
                if (handler != null)
                {
                    handler(this, new ControlLeaseReceivedEventArgs(lease));
                }

                return;
            }

            if (frameType == FrameAck)
            {
                var handler = ControlAckReceived;
                if (handler != null)
                {
                    handler(this, new ControlAckReceivedEventArgs(ackEpoch));
                }
            }
        }

        private bool AcceptSequence(ushort sequence)
        {
            if (_hasInputSeq)
            {
                var delta = unchecked((short)(sequence - _lastInputSeq));
                if (delta <= 0)
                {
                    // UDP reorders easily on Wi-Fi. Applying an old frame makes the pointer
                    // jump backwards, so stale frames are dropped instead of injected.
                    AppLog.Debug("input.stale", "seq=" + sequence + " last=" + _lastInputSeq + " delta=" + delta);
                    return false;
                }
            }

            _hasInputSeq = true;
            _lastInputSeq = sequence;
            return true;
        }

        private static bool DecodeFrame(byte[] bytes, out byte frameType, out ushort sequence, out bool hasSequence, out InputPayload[] payloads, out ControlLease lease, out long ackEpoch)
        {
            frameType = FrameInputBatch;
            sequence = 0;
            hasSequence = false;
            payloads = new InputPayload[0];
            lease = null;
            ackEpoch = 0;

            if (bytes == null || bytes.Length < 5)
            {
                return false;
            }

            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic)
                {
                    return false;
                }

                var version = reader.ReadByte();
                if (version >= 3)
                {
                    frameType = reader.ReadByte();
                    if (version >= VersionSequenced &&
                        (frameType == FrameInputBatch || frameType == FrameMouseRelativeCompact || frameType == FrameMouseAbsoluteCompact))
                    {
                        sequence = reader.ReadUInt16();
                        hasSequence = true;
                    }

                    if (frameType == FrameLease)
                    {
                        return ControlChannelCodec.TryDecodeLease(bytes, out lease);
                    }

                    if (frameType == FrameAck)
                    {
                        return ControlChannelCodec.TryDecodeAck(bytes, out ackEpoch);
                    }

                    if (version >= VersionCompact && frameType == FrameMouseRelativeCompact)
                    {
                        payloads = ReadCompactRelative(reader, stream);
                        frameType = FrameInputBatch;
                        return true;
                    }

                    if (version >= VersionCompact && frameType == FrameMouseAbsoluteCompact)
                    {
                        payloads = ReadCompactAbsolute(reader, stream);
                        frameType = FrameInputBatch;
                        return true;
                    }
                }

                frameType = FrameInputBatch;
                payloads = ReadInputBatch(reader, version, stream);
                return true;
            }
        }

        private static InputPayload[] ReadInputBatch(BinaryReader reader, byte version, MemoryStream stream)
        {
            var payloads = new List<InputPayload>();
            var count = reader.ReadUInt16();
            for (int i = 0; i < count && stream.Position < stream.Length; i++)
            {
                var payload = new InputPayload
                {
                    Kind = FromKind(reader.ReadByte()),
                    X = reader.ReadInt32(),
                    Y = reader.ReadInt32(),
                    Delta = reader.ReadInt32(),
                    VirtualKey = reader.ReadInt32(),
                    Button = reader.ReadInt32(),
                    IsDown = reader.ReadBoolean()
                };
                if (version >= 2 && stream.Position + 10 <= stream.Length)
                {
                    payload.ScanCode = reader.ReadInt32();
                    payload.Flags = reader.ReadInt32();
                    payload.IsExtendedKey = reader.ReadBoolean();
                    payload.IsSystemKey = reader.ReadBoolean();
                }

                payloads.Add(payload);
            }

            return payloads.ToArray();
        }

        private byte[] EncodeInput(InputPayload[] payloads)
        {
            if (payloads == null || payloads.Length == 0)
            {
                return null;
            }

            var count = Math.Min(payloads.Length, 128);
            var sequence = unchecked(_inputSeq++);
            byte[] compact;
            if (count <= MaxCompactEvents)
            {
                if (TryEncodeRelativeCompact(payloads, count, sequence, out compact))
                {
                    return compact;
                }

                if (TryEncodeAbsoluteCompact(payloads, count, sequence, out compact))
                {
                    return compact;
                }
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteInputHeader(writer, FrameInputBatch, sequence);
                writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    var payload = payloads[i];
                    writer.Write(ToKind(payload.Kind));
                    writer.Write(payload.X);
                    writer.Write(payload.Y);
                    writer.Write(payload.Delta);
                    writer.Write(payload.VirtualKey);
                    writer.Write(payload.Button);
                    writer.Write(payload.IsDown);
                    writer.Write(payload.ScanCode);
                    writer.Write(payload.Flags);
                    writer.Write(payload.IsExtendedKey);
                    writer.Write(payload.IsSystemKey);
                }

                return stream.ToArray();
            }
        }

        /// <summary>
        /// Relative mouse moves dominate the traffic while the pointer moves: encode the
        /// whole batch as 2 signed bytes per event when everything fits.
        /// </summary>
        private static bool TryEncodeRelativeCompact(InputPayload[] payloads, int count, ushort sequence, out byte[] bytes)
        {
            bytes = null;
            for (int i = 0; i < count; i++)
            {
                var payload = payloads[i];
                if (payload.Kind != InputKinds.MouseMoveRelative)
                {
                    return false;
                }

                if (payload.X < -128 || payload.X > 127 || payload.Y < -128 || payload.Y > 127)
                {
                    return false;
                }
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteInputHeader(writer, FrameMouseRelativeCompact, sequence);
                writer.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write((sbyte)payloads[i].X);
                    writer.Write((sbyte)payloads[i].Y);
                }

                writer.Flush();
                bytes = stream.ToArray();
                return true;
            }
        }

        private static bool TryEncodeAbsoluteCompact(InputPayload[] payloads, int count, ushort sequence, out byte[] bytes)
        {
            bytes = null;
            for (int i = 0; i < count; i++)
            {
                var payload = payloads[i];
                if (payload.Kind != InputKinds.MouseMove)
                {
                    return false;
                }

                if (payload.X < 0 || payload.X > 65535 || payload.Y < 0 || payload.Y > 65535)
                {
                    return false;
                }
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteInputHeader(writer, FrameMouseAbsoluteCompact, sequence);
                writer.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write((ushort)payloads[i].X);
                    writer.Write((ushort)payloads[i].Y);
                }

                writer.Flush();
                bytes = stream.ToArray();
                return true;
            }
        }

        private static void WriteInputHeader(BinaryWriter writer, byte frameType, ushort sequence)
        {
            WriteHeaderVersion(writer, VersionSequenced, frameType);
            writer.Write(sequence);
        }

        private static void WriteHeaderVersion(BinaryWriter writer, byte version, byte frameType)
        {
            writer.Write(Magic);
            writer.Write(version);
            writer.Write(frameType);
        }

        private static InputPayload[] ReadCompactRelative(BinaryReader reader, MemoryStream stream)
        {
            var count = reader.ReadByte();
            var payloads = new List<InputPayload>(count);
            for (int i = 0; i < count && stream.Position + 2 <= stream.Length; i++)
            {
                payloads.Add(new InputPayload
                {
                    Kind = InputKinds.MouseMoveRelative,
                    X = reader.ReadSByte(),
                    Y = reader.ReadSByte()
                });
            }

            return payloads.ToArray();
        }

        private static InputPayload[] ReadCompactAbsolute(BinaryReader reader, MemoryStream stream)
        {
            var count = reader.ReadByte();
            var payloads = new List<InputPayload>(count);
            for (int i = 0; i < count && stream.Position + 4 <= stream.Length; i++)
            {
                payloads.Add(new InputPayload
                {
                    Kind = InputKinds.MouseMove,
                    X = reader.ReadUInt16(),
                    Y = reader.ReadUInt16()
                });
            }

            return payloads.ToArray();
        }

        private static byte ToKind(string kind)
        {
            if (kind == InputKinds.MouseMoveRelative) return 1;
            if (kind == InputKinds.MouseMove) return 2;
            if (kind == InputKinds.MouseButton) return 3;
            if (kind == InputKinds.MouseWheel) return 4;
            if (kind == InputKinds.Key) return 5;
            return 0;
        }

        private static string FromKind(byte kind)
        {
            if (kind == 1) return InputKinds.MouseMoveRelative;
            if (kind == 2) return InputKinds.MouseMove;
            if (kind == 3) return InputKinds.MouseButton;
            if (kind == 4) return InputKinds.MouseWheel;
            if (kind == 5) return InputKinds.Key;
            return string.Empty;
        }

        public void Dispose()
        {
            _running = false;
            _listener.Close();
            _sender.Close();
        }
    }

    internal sealed class InputBatchReceivedEventArgs : EventArgs
    {
        public InputBatchReceivedEventArgs(InputPayload[] payloads)
        {
            Payloads = payloads;
        }

        public InputPayload[] Payloads { get; private set; }
    }

    internal sealed class ControlLeaseReceivedEventArgs : EventArgs
    {
        public ControlLeaseReceivedEventArgs(ControlLease lease)
        {
            Lease = lease;
        }

        public ControlLease Lease { get; private set; }
    }

    internal sealed class ControlAckReceivedEventArgs : EventArgs
    {
        public ControlAckReceivedEventArgs(long epoch)
        {
            Epoch = epoch;
        }

        public long Epoch { get; private set; }
    }

    internal sealed class ClipboardFilesReceivedEventArgs : EventArgs
    {
        public ClipboardFilesReceivedEventArgs(string[] paths)
        {
            Paths = paths ?? new string[0];
        }

        public string[] Paths { get; private set; }
    }
}
