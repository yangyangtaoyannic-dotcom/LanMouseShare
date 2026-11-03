using System;
using System.IO;

namespace LanMouseShare.Core.Control
{
    /// <summary>
    /// Wire format of the agent-to-agent control channel (UDP, see docs/CONTROL-STATE-MODEL.md 4).
    /// Only the control frames live here so both ends and the tests share one definition.
    /// </summary>
    public static class ControlChannelCodec
    {
        public const int Magic = 0x49534D4C;
        public const byte Version = 3;
        public const byte FrameInputBatch = 0;
        public const byte FrameLease = 1;
        public const byte FrameAck = 2;
        public const byte FrameClipboardFiles = 6;
        public const int MaxClipboardFiles = 32;

        public static byte[] EncodeLease(ControlLease lease)
        {
            if (lease == null)
            {
                return null;
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, FrameLease);
                writer.Write(lease.Epoch);
                writer.Write(lease.OwnerDeviceId ?? string.Empty);
                writer.Write(lease.TtlMs);
                writer.Write(lease.Sequence);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static byte[] EncodeAck(long epoch)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, FrameAck);
                writer.Write(epoch);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Fast clipboard announcement (0.1.55): the file list travels over the low-latency
        /// control channel so a paste right after the copy already sees the files, without
        /// waiting for the TCP clipboard envelope (which can stall for seconds on Wi-Fi).
        /// </summary>
        public static byte[] EncodeClipboardFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return null;
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, FrameClipboardFiles);
                var count = Math.Min(paths.Length, MaxClipboardFiles);
                writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write(paths[i] ?? string.Empty);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecodeClipboardFiles(byte[] bytes, out string[] paths)
        {
            paths = null;
            using (var reader = OpenPayload(bytes, FrameClipboardFiles))
            {
                if (reader == null)
                {
                    return false;
                }

                try
                {
                    var count = reader.ReadUInt16();
                    if (count == 0 || count > MaxClipboardFiles)
                    {
                        return false;
                    }

                    var list = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        list[i] = reader.ReadString();
                    }

                    paths = list;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void WriteHeader(BinaryWriter writer, byte frameType)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(frameType);
        }

        public static bool TryDecodeLease(byte[] bytes, out ControlLease lease)
        {
            lease = null;
            using (var reader = OpenPayload(bytes, FrameLease))
            {
                if (reader == null)
                {
                    return false;
                }

                lease = new ControlLease
                {
                    Epoch = reader.ReadInt64(),
                    OwnerDeviceId = reader.ReadString(),
                    TtlMs = reader.ReadInt32(),
                    Sequence = reader.ReadInt64()
                };
                return true;
            }
        }

        public static bool TryDecodeAck(byte[] bytes, out long epoch)
        {
            epoch = 0;
            using (var reader = OpenPayload(bytes, FrameAck))
            {
                if (reader == null)
                {
                    return false;
                }

                epoch = reader.ReadInt64();
                return true;
            }
        }

        private static BinaryReader OpenPayload(byte[] bytes, byte expectedFrame)
        {
            if (bytes == null || bytes.Length < 6)
            {
                return null;
            }

            var stream = new MemoryStream(bytes);
            var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic)
            {
                reader.Dispose();
                return null;
            }

            var version = reader.ReadByte();
            if (version < 3)
            {
                reader.Dispose();
                return null;
            }

            if (reader.ReadByte() != expectedFrame)
            {
                reader.Dispose();
                return null;
            }

            return reader;
        }
    }
}
