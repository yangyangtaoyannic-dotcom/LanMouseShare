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

    /// <summary>
    /// Relay copy of the control lease. The lease normally travels agent-to-agent over UDP,
    /// but that hop is not always allowed by host firewalls; the relay keeps the model working
    /// over the paired TCP/UDP service link as well (see docs/CONTROL-STATE-MODEL.md 4).
    /// </summary>
    [DataContract]
    public sealed class ControlLeasePayload
    {
        [DataMember(Order = 1)]
        public long Epoch { get; set; }

        [DataMember(Order = 2)]
        public string OwnerDeviceId { get; set; }

        [DataMember(Order = 3)]
        public int TtlMs { get; set; }

        [DataMember(Order = 4)]
        public long Sequence { get; set; }
    }

    [DataContract]
    public sealed class ControlLeaseAckPayload
    {
        [DataMember(Order = 1)]
        public long Epoch { get; set; }
    }
}
