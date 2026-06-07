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
    }
}
