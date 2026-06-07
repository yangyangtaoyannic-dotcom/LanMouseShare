using System;
using System.Runtime.Serialization;

namespace LanMouseShare.Core.Protocol
{
    [DataContract]
    public sealed class ProtocolEnvelope
    {
        [DataMember(Order = 1)]
        public string ProtocolVersion { get; set; }

        [DataMember(Order = 2)]
        public string MessageId { get; set; }

        [DataMember(Order = 3)]
        public string SourceDeviceId { get; set; }

        [DataMember(Order = 4)]
        public string TargetDeviceId { get; set; }

        [DataMember(Order = 5)]
        public string MessageType { get; set; }

        [DataMember(Order = 6)]
        public long CreatedUtcTicks { get; set; }

        [DataMember(Order = 7)]
        public string PayloadJson { get; set; }

        public static ProtocolEnvelope Create(string sourceDeviceId, string targetDeviceId, string messageType, string payloadJson)
        {
            return new ProtocolEnvelope
            {
                ProtocolVersion = "1",
                MessageId = Guid.NewGuid().ToString("N"),
                SourceDeviceId = sourceDeviceId,
                TargetDeviceId = targetDeviceId,
                MessageType = messageType,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                PayloadJson = payloadJson
            };
        }
    }

    public static class ProtocolMessageTypes
    {
        public const string PairingRequest = "PairingRequest";
        public const string PairingConfirm = "PairingConfirm";
        public const string ControlAcquire = "ControlAcquire";
        public const string ControlRelease = "ControlRelease";
        public const string ControlTargetChanged = "ControlTargetChanged";
        public const string ControlPause = "ControlPause";
        public const string ControlResume = "ControlResume";
        public const string LayoutUpdate = "LayoutUpdate";
        public const string ScreenInfoUpdate = "ScreenInfoUpdate";
        public const string InputEvent = "InputEvent";
        public const string InputBatch = "InputBatch";
        public const string ClipboardUpdate = "ClipboardUpdate";
        public const string FileOffer = "FileOffer";
        public const string FileStreamStart = "FileStreamStart";
        public const string FileComplete = "FileComplete";
        public const string Heartbeat = "Heartbeat";
    }
}
