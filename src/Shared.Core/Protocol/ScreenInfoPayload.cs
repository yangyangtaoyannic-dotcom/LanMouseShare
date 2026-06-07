using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class ScreenInfoPayload
    {
        [DataMember(Order = 1)]
        public int Width { get; set; }

        [DataMember(Order = 2)]
        public int Height { get; set; }
    }
}
