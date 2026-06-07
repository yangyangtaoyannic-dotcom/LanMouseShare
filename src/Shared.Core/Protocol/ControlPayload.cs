using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class ControlReleasePayload
    {
        [DataMember(Order = 1)]
        public int RemoteX { get; set; }

        [DataMember(Order = 2)]
        public int RemoteY { get; set; }

        [DataMember(Order = 3)]
        public int RemoteScreenWidth { get; set; }

        [DataMember(Order = 4)]
        public int RemoteScreenHeight { get; set; }

        [DataMember(Order = 5)]
        public string Direction { get; set; }

        [DataMember(Order = 6)]
        public string Reason { get; set; }
    }

    [DataContract]
    public sealed class ControlPausePayload
    {
        [DataMember(Order = 1)]
        public bool StopAll { get; set; }

        [DataMember(Order = 2)]
        public string Reason { get; set; }
    }

    [DataContract]
    public sealed class ControlTargetPayload
    {
        [DataMember(Order = 1)]
        public string ActiveTargetDeviceId { get; set; }

        [DataMember(Order = 2)]
        public string Reason { get; set; }
    }
}
