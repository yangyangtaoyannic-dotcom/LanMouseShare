using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class TransportFrame
    {
        [DataMember(Order = 1)]
        public string SourceDeviceId { get; set; }

        [DataMember(Order = 2)]
        public bool IsProtected { get; set; }

        [DataMember(Order = 3)]
        public string EnvelopeJson { get; set; }

        [DataMember(Order = 4)]
        public byte[] ProtectedEnvelopeBytes { get; set; }
    }
}
