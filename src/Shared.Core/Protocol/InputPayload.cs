using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class InputPayload
    {
        [DataMember(Order = 1)]
        public string Kind { get; set; }

        [DataMember(Order = 2)]
        public int X { get; set; }

        [DataMember(Order = 3)]
        public int Y { get; set; }

        [DataMember(Order = 4)]
        public int Delta { get; set; }

        [DataMember(Order = 5)]
        public int VirtualKey { get; set; }

        [DataMember(Order = 6)]
        public bool IsDown { get; set; }

        [DataMember(Order = 7)]
        public int Button { get; set; }

        [DataMember(Order = 8)]
        public int ScanCode { get; set; }

        [DataMember(Order = 9)]
        public int Flags { get; set; }

        [DataMember(Order = 10)]
        public bool IsExtendedKey { get; set; }

        [DataMember(Order = 11)]
        public bool IsSystemKey { get; set; }
    }

    public static class InputKinds
    {
        public const string MouseMove = "MouseMove";
        public const string MouseMoveRelative = "MouseMoveRelative";
        public const string MouseButton = "MouseButton";
        public const string MouseWheel = "MouseWheel";
        public const string Key = "Key";
    }

    [DataContract]
    public sealed class InputBatchPayload
    {
        [DataMember(Order = 1)]
        public InputPayload[] Events { get; set; }
    }
}
