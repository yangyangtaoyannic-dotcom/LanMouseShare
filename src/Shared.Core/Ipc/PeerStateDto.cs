using System.Runtime.Serialization;

namespace LanMouseShare.Core.Ipc
{
    [DataContract]
    public sealed class PeerStateDto
    {
        [DataMember(Order = 1)]
        public string LocalDeviceId { get; set; }

        [DataMember(Order = 2)]
        public string LocalDeviceName { get; set; }

        [DataMember(Order = 3)]
        public string PeerDeviceId { get; set; }

        [DataMember(Order = 4)]
        public string PeerDeviceName { get; set; }

        [DataMember(Order = 5)]
        public string PeerAddress { get; set; }

        [DataMember(Order = 6)]
        public bool IsConnected { get; set; }

        [DataMember(Order = 7)]
        public bool IsTrusted { get; set; }

        [DataMember(Order = 8)]
        public string LayoutDirection { get; set; }

        [DataMember(Order = 9)]
        public int LocalScreenWidth { get; set; }

        [DataMember(Order = 10)]
        public int LocalScreenHeight { get; set; }

        [DataMember(Order = 11)]
        public int PeerScreenWidth { get; set; }

        [DataMember(Order = 12)]
        public int PeerScreenHeight { get; set; }

        [DataMember(Order = 13)]
        public double MouseSpeedMultiplier { get; set; }

        [DataMember(Order = 14)]
        public int MouseUpdateRateHz { get; set; }

        [DataMember(Order = 15)]
        public bool ClipboardEnabled { get; set; }

        [DataMember(Order = 16)]
        public string PairingStatus { get; set; }

        [DataMember(Order = 17)]
        public bool IsPairingMismatch { get; set; }

        [DataMember(Order = 18)]
        public string PauseHotkey { get; set; }

        [DataMember(Order = 19)]
        public string StopControlHotkey { get; set; }

        [DataMember(Order = 20)]
        public string ExitHotkey { get; set; }

        [DataMember(Order = 21)]
        public bool StartWithWindows { get; set; }

        [DataMember(Order = 22)]
        public bool MinimizeToTrayOnClose { get; set; }

        [DataMember(Order = 23)]
        public bool BlockScreenCorners { get; set; }

        [DataMember(Order = 24)]
        public string ActiveTargetDeviceId { get; set; }

        [DataMember(Order = 25)]
        public bool RunAsAdministratorByDefault { get; set; }

        [DataMember(Order = 26)]
        public string ShareRole { get; set; }

        [DataMember(Order = 27)]
        public string ControllerAddress { get; set; }

        [DataMember(Order = 28)]
        public string DebugToken { get; set; }

        [DataMember(Order = 29)]
        public bool VerboseLogging { get; set; }

        [DataMember(Order = 30)]
        public int LogRetentionDays { get; set; }

        [DataMember(Order = 31)]
        public string LogTransportStatus { get; set; }

        [DataMember(Order = 32)]
        public string LogDirectory { get; set; }

        [DataMember(Order = 33)]
        public string RemoteLogDirectory { get; set; }

        [DataMember(Order = 34)]
        public string ShareRoleText { get; set; }

        [DataMember(Order = 35)]
        public int NetworkRttMs { get; set; }

        [DataMember(Order = 36)]
        public bool LogTransferEnabled { get; set; }
    }

    public static class PairingStatuses
    {
        public const string NotDiscovered = "NotDiscovered";
        public const string DiscoveredUnpaired = "DiscoveredUnpaired";
        public const string WaitingForApproval = "WaitingForApproval";
        public const string PairedDisconnected = "PairedDisconnected";
        public const string PairedConnected = "PairedConnected";
        public const string PairingMismatch = "PairingMismatch";
    }
}
