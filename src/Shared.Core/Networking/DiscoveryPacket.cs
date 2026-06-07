using System.Runtime.Serialization;

namespace LanMouseShare.Core.Networking
{
    [DataContract]
    public sealed class DiscoveryPacket
    {
        [DataMember(Order = 1)]
        public string ProtocolVersion { get; set; }

        [DataMember(Order = 2)]
        public string DeviceId { get; set; }

        [DataMember(Order = 3)]
        public string DeviceName { get; set; }

        [DataMember(Order = 4)]
        public int TcpPort { get; set; }

        public static DiscoveryPacket Create(string deviceId, string deviceName, int tcpPort)
        {
            return new DiscoveryPacket
            {
                ProtocolVersion = "1",
                DeviceId = deviceId,
                DeviceName = deviceName,
                TcpPort = tcpPort
            };
        }
    }
}
