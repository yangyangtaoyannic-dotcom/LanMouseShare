using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class DirectInputChannel : IDisposable
    {
        public const int Port = 47534;
        private const int Magic = 0x49534D4C;

        private readonly UdpClient _listener;
        private readonly UdpClient _sender;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private IPEndPoint _peerEndpoint;

        public event EventHandler<InputBatchReceivedEventArgs> InputBatchReceived;

        public DirectInputChannel()
        {
            _listener = new UdpClient(Port);
            _sender = new UdpClient();
            _thread = new Thread(ListenLoop);
            _thread.IsBackground = true;
            _thread.Name = "LanMouseShare direct input listener";
            _thread.Start();
            AppLog.Info("DirectInputChannel listening on UDP " + Port);
        }

        public void SetPeer(string address)
        {
            IPAddress ip;
            if (IPAddress.TryParse(address ?? string.Empty, out ip))
            {
                _peerEndpoint = new IPEndPoint(ip, Port);
            }
        }

        public bool Send(InputPayload[] payloads)
        {
            var endpoint = _peerEndpoint;
            if (endpoint == null || payloads == null || payloads.Length == 0)
            {
                return false;
            }

            try
            {
                var bytes = Encode(payloads);
                _sender.Send(bytes, bytes.Length, endpoint);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Direct input send failed.", ex);
                return false;
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = null;
                    var bytes = _listener.Receive(ref remote);
                    var payloads = Decode(bytes);
                    var handler = InputBatchReceived;
                    if (handler != null && payloads.Length > 0)
                    {
                        handler(this, new InputBatchReceivedEventArgs(payloads));
                    }
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

        private static byte[] Encode(InputPayload[] payloads)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write((byte)2);
                writer.Write((ushort)Math.Min(payloads.Length, 128));
                for (int i = 0; i < payloads.Length && i < 128; i++)
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

        private static InputPayload[] Decode(byte[] bytes)
        {
            var payloads = new List<InputPayload>();
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic)
                {
                    return payloads.ToArray();
                }

                var version = reader.ReadByte();
                int count = reader.ReadUInt16();
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
}
