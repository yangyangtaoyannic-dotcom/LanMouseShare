using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class PairingRequestPayload
    {
        [DataMember(Order = 1)]
        public string DeviceId { get; set; }

        [DataMember(Order = 2)]
        public string DeviceName { get; set; }

        [DataMember(Order = 3)]
        public string PairingCode { get; set; }
    }

    [DataContract]
    public sealed class PairingConfirmPayload
    {
        [DataMember(Order = 1)]
        public string DeviceId { get; set; }

        [DataMember(Order = 2)]
        public string DeviceName { get; set; }

        [DataMember(Order = 3)]
        public byte[] SharedSecret { get; set; }
    }
}
