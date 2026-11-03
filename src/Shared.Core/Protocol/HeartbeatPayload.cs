using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    /// <summary>
    /// Round trip probe. A controller sends Reply=false with the local tick count; the peer
    /// echoes the same ticks back with Reply=true so the sender can compute the round trip
    /// time without keeping any per-message state.
    /// </summary>
    [DataContract]
    public sealed class HeartbeatPayload
    {
        [DataMember(Order = 1)]
        public long Ticks { get; set; }

        [DataMember(Order = 2)]
        public bool Reply { get; set; }

        /// <summary>
        /// 发送方当前是否处于"暂停共享"。带上它，对端就能在每个心跳周期里校准暂停状态：
        /// 之前只在暂停/恢复的那一刻各发一次，一方重启后另一方永远等不到恢复信号（fix7）。
        /// 旧版本不发送这个字段（反序列化为 null），因此保持向前兼容。
        /// </summary>
        [DataMember(Order = 3, EmitDefaultValue = false)]
        public bool? Paused { get; set; }
    }
}
