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

        [DataMember(Order = 6)]
        public string[] FilePaths { get; set; }

        /// <summary>True when the file list is an announcement only: the bytes are pulled on paste.</summary>
        [DataMember(Order = 7)]
        public bool IsLazy { get; set; }

        [DataMember(Order = 8)]
        public long[] FileSizes { get; set; }
    }

    /// <summary>Sent by the pasting side to ask the copying side to stream the announced files.</summary>
    [DataContract]
    public sealed class ClipboardPullRequestPayload
    {
        [DataMember(Order = 1)]
        public string[] FilePaths { get; set; }

        [DataMember(Order = 2)]
        public string RequesterDeviceId { get; set; }
    }

    /// <summary>
    /// Sent by the controlling side when the user presses Ctrl+V while its clipboard holds files.
    /// The peer never sees injected key events, so it cannot detect the paste by itself.
    /// </summary>
    [DataContract]
    public sealed class ClipboardFetchResultPayload
    {
        [DataMember(Order = 1)]
        public string[] Paths { get; set; }

        [DataMember(Order = 2)]
        public string[] Missing { get; set; }

        [DataMember(Order = 3)]
        public bool TimedOut { get; set; }
    }

    [DataContract]
    public sealed class ClipboardPasteRequestPayload
    {
        [DataMember(Order = 1)]
        public string RequesterDeviceId { get; set; }

        [DataMember(Order = 2)]
        public int FileCount { get; set; }

        /// <summary>
        /// The copied paths, carried in the request itself. A lazily announced list may still be in
        /// flight when the user hits Ctrl+V, and the paste must not depend on that race.
        /// </summary>
        [DataMember(Order = 3)]
        public string[] FilePaths { get; set; }
    }

    public static class ClipboardFormats
    {
        public const string Text = "Text";
        public const string ImagePng = "ImagePng";

        /// <summary>CF_HDROP: a list of files copied in Explorer, transferred as real files.</summary>
        public const string Files = "Files";
    }
}
