using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Core.Networking
{
    public sealed class UdpDiscoveryService : IDisposable
    {
        public const int DiscoveryPort = 47531;

        private readonly DiscoveryPacket _localPacket;
        private UdpClient _listener;
        private Thread _thread;
        private Thread _broadcastThread;
        private volatile bool _running;

        public event EventHandler<DiscoveryReceivedEventArgs> DeviceDiscovered;

        public UdpDiscoveryService(DiscoveryPacket localPacket)
        {
            _localPacket = localPacket;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _listener = new UdpClient(DiscoveryPort);
            _listener.EnableBroadcast = true;
            _thread = new Thread(ListenLoop);
            _thread.IsBackground = true;
            _thread.Start();
            _broadcastThread = new Thread(BroadcastLoop);
            _broadcastThread.IsBackground = true;
            _broadcastThread.Start();
            Broadcast();
        }

        public void Broadcast()
        {
            var bytes = ProtocolSerializer.ToUtf8JsonBytes(_localPacket);
            using (var sender = new UdpClient())
            {
                sender.EnableBroadcast = true;
                sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                foreach (var address in GetSubnetBroadcastAddresses())
                {
                    try
                    {
                        sender.Send(bytes, bytes.Length, new IPEndPoint(address, DiscoveryPort));
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void BroadcastLoop()
        {
            while (_running)
            {
                try
                {
                    Broadcast();
                }
                catch
                {
                }

                Thread.Sleep(3000);
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint endpoint = null;
                    var bytes = _listener.Receive(ref endpoint);
                    var packet = ProtocolSerializer.FromUtf8JsonBytes<DiscoveryPacket>(bytes);
                    if (packet != null && packet.DeviceId != _localPacket.DeviceId)
                    {
                        var handler = DeviceDiscovered;
                        if (handler != null)
                        {
                            handler(this, new DiscoveryReceivedEventArgs(packet, endpoint.Address));
                        }
                    }
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        Thread.Sleep(250);
                    }
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_listener != null)
            {
                _listener.Close();
            }
        }

        private static IPAddress[] GetSubnetBroadcastAddresses()
        {
            var addresses = new System.Collections.Generic.List<IPAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }

                    addresses.Add(new IPAddress(broadcast));
                }
            }

            return addresses.ToArray();
        }
    }

    public sealed class DiscoveryReceivedEventArgs : EventArgs
    {
        public DiscoveryReceivedEventArgs(DiscoveryPacket packet, IPAddress address)
        {
            Packet = packet;
            Address = address;
        }

        public DiscoveryPacket Packet { get; private set; }

        public IPAddress Address { get; private set; }
    }
}
