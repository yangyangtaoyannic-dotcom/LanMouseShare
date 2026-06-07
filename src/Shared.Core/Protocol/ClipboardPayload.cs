using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class ClipboardPayload
    {
        [DataMember(Order = 1)]
        public string OriginDeviceId { get; set; }

        [DataMember(Order = 2)]
        public long Version { get; set; }

        [DataMember(Order = 3)]
        public string Format { get; set; }

        [DataMember(Order = 4)]
        public string Text { get; set; }

        [DataMember(Order = 5)]
        public byte[] ImagePngBytes { get; set; }
    }

    public static class ClipboardFormats
    {
        public const string Text = "Text";
        public const string ImagePng = "ImagePng";
    }
}
