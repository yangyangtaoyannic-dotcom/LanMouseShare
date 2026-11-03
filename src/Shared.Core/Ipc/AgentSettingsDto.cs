using System.Runtime.Serialization;

namespace LanMouseShare.Core.Ipc
{
    [DataContract]
    public sealed class AgentSettingsDto
    {
        [DataMember(Order = 1)]
        public double MouseSpeedMultiplier { get; set; }

        [DataMember(Order = 2)]
        public int MouseUpdateRateHz { get; set; }

        [DataMember(Order = 3)]
        public bool ClipboardEnabled { get; set; }

        [DataMember(Order = 4)]
        public string PauseHotkey { get; set; }

        [DataMember(Order = 5)]
        public string StopControlHotkey { get; set; }

        [DataMember(Order = 6)]
        public string ExitHotkey { get; set; }

        [DataMember(Order = 7)]
        public bool StartWithWindows { get; set; }

        [DataMember(Order = 8)]
        public bool MinimizeToTrayOnClose { get; set; }

        [DataMember(Order = 9)]
        public bool BlockScreenCorners { get; set; }

        [DataMember(Order = 10)]
        public bool RunAsAdministratorByDefault { get; set; }

        [DataMember(Order = 11)]
        public string ShareRole { get; set; }

        [DataMember(Order = 12)]
        public string ControllerAddress { get; set; }

        [DataMember(Order = 13)]
        public string DebugToken { get; set; }

        [DataMember(Order = 14)]
        public bool VerboseLogging { get; set; }

        [DataMember(Order = 15)]
        public int LogRetentionDays { get; set; }

        [DataMember(Order = 16)]
        public bool LogTransferEnabled { get; set; }
    }
}
