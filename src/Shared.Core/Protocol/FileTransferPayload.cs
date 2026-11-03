using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class FileOfferPayload
    {
        [DataMember(Order = 1)]
        public string TransferId { get; set; }

        [DataMember(Order = 2)]
        public string FileName { get; set; }

        [DataMember(Order = 3)]
        public long Length { get; set; }

        [DataMember(Order = 4)]
        public string Sha256Hex { get; set; }

        /// <summary>Original path on the sending machine (used to match fetched files).</summary>
        [DataMember(Order = 5)]
        public string SourcePath { get; set; }
    }

    [DataContract]
    public sealed class FileTransferRequestPayload
    {
        [DataMember(Order = 1)]
        public string[] FilePaths { get; set; }
    }

    [DataContract]
    public sealed class FileStreamStartPayload
    {
        [DataMember(Order = 1)]
        public string TransferId { get; set; }

        [DataMember(Order = 2)]
        public long Length { get; set; }
    }

    [DataContract]
    public sealed class FileCompletePayload
    {
        [DataMember(Order = 1)]
        public string TransferId { get; set; }

        [DataMember(Order = 2)]
        public bool Success { get; set; }

        [DataMember(Order = 3)]
        public string Message { get; set; }

        /// <summary>Where the receiving side stored the file (set by the receiver).</summary>
        [DataMember(Order = 4)]
        public string SavedPath { get; set; }
    }

    /// <summary>
    /// Streaming progress for one file. Sent by both sides (sender and receiver) so the UI can
    /// show a progress bar and so the paste pipeline can tell "still moving" from "stalled".
    /// </summary>
    [DataContract]
    public sealed class FileProgressPayload
    {
        [DataMember(Order = 1)]
        public string TransferId { get; set; }

        [DataMember(Order = 2)]
        public string FileName { get; set; }

        [DataMember(Order = 3)]
        public long Transferred { get; set; }

        [DataMember(Order = 4)]
        public long Total { get; set; }

        /// <summary>"send" or "receive".</summary>
        [DataMember(Order = 5)]
        public string Direction { get; set; }

        [DataMember(Order = 6)]
        public long SpeedBytesPerSecond { get; set; }

        [DataMember(Order = 7)]
        public bool Done { get; set; }
    }
}
