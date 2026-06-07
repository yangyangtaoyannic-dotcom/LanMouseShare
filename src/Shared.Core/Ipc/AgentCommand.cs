using System.Runtime.Serialization;

namespace LanMouseShare.Core.Ipc
{
    [DataContract]
    public sealed class AgentCommand
    {
        [DataMember(Order = 1)]
        public string Command { get; set; }

        [DataMember(Order = 2)]
        public string PayloadJson { get; set; }
    }

    public static class AgentCommands
    {
        public const string GetPeerState = "GetPeerState";
        public const string PairDevice = "PairDevice";
        public const string ApprovePairing = "ApprovePairing";
        public const string SetManualPeer = "SetManualPeer";
        public const string UpdateLayout = "UpdateLayout";
        public const string UpdateScreenInfo = "UpdateScreenInfo";
        public const string UpdateSettings = "UpdateSettings";
        public const string AcquireControl = "AcquireControl";
        public const string ReleaseControl = "ReleaseControl";
        public const string SetControlTarget = "SetControlTarget";
        public const string PauseControl = "PauseControl";
        public const string ResumeControl = "ResumeControl";
        public const string SendInput = "SendInput";
        public const string SendInputBatch = "SendInputBatch";
        public const string SendClipboard = "SendClipboard";
        public const string SendFiles = "SendFiles";
        public const string PollEvents = "PollEvents";
        public const string ShutdownService = "ShutdownService";
    }
}
