using System.Collections.Generic;
using System.Runtime.Serialization;

namespace LanMouseShare.Core.Configuration
{
    [DataContract]
    public sealed class AppConfig
    {
        [DataMember(Order = 1)]
        public string DeviceId { get; set; }

        [DataMember(Order = 2)]
        public string DeviceName { get; set; }

        [DataMember(Order = 3)]
        public byte[] ProtectedLocalSecret { get; set; }

        [DataMember(Order = 4)]
        public List<TrustedDevice> TrustedDevices { get; set; }

        [DataMember(Order = 5)]
        public LayoutConfig Layout { get; set; }

        [DataMember(Order = 6)]
        public bool ClipboardEnabled { get; set; }

        [DataMember(Order = 7)]
        public string ReleaseHotkey { get; set; }

        [DataMember(Order = 8)]
        public string PauseHotkey { get; set; }

        [DataMember(Order = 9)]
        public string StopControlHotkey { get; set; }

        [DataMember(Order = 10)]
        public string ExitHotkey { get; set; }

        [DataMember(Order = 11)]
        public bool StartWithWindows { get; set; }

        [DataMember(Order = 12)]
        public bool MinimizeToTrayOnClose { get; set; }

        [DataMember(Order = 13)]
        public bool BlockScreenCorners { get; set; }

        [DataMember(Order = 14)]
        public bool RunAsAdministratorByDefault { get; set; }

        [DataMember(Order = 15)]
        public int ScreenWidth { get; set; }

        [DataMember(Order = 16)]
        public int ScreenHeight { get; set; }

        [DataMember(Order = 17)]
        public double MouseSpeedMultiplier { get; set; }

        [DataMember(Order = 18)]
        public int MouseUpdateRateHz { get; set; }

        [DataMember(Order = 19)]
        public string ShareRole { get; set; }

        [DataMember(Order = 20)]
        public string ControllerAddress { get; set; }

        [DataMember(Order = 21)]
        public string DebugToken { get; set; }

        [DataMember(Order = 22)]
        public bool VerboseLogging { get; set; }

        [DataMember(Order = 23)]
        public int LogRetentionDays { get; set; }

        [DataMember(Order = 24)]
        public bool LogTransferEnabled { get; set; }

        [DataMember(Order = 25)]
        public int ConfigRevision { get; set; }

        [DataMember(Order = 26)]
        public bool UseNewControllers { get; set; }

        public static AppConfig CreateDefault(string deviceId, string deviceName, byte[] protectedLocalSecret)
        {
            return new AppConfig
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                ProtectedLocalSecret = protectedLocalSecret,
                TrustedDevices = new List<TrustedDevice>(),
                Layout = new LayoutConfig { PeerDirection = "Right" },
                ClipboardEnabled = true,
                ReleaseHotkey = "Ctrl+Alt+Pause",
                PauseHotkey = "Ctrl+F11",
                StopControlHotkey = "Ctrl+F10",
                ExitHotkey = "Ctrl+Alt+End",
                StartWithWindows = false,
                MinimizeToTrayOnClose = false,
                BlockScreenCorners = false,
                RunAsAdministratorByDefault = false,
                MouseSpeedMultiplier = 1.0,
                MouseUpdateRateHz = 200,
                ShareRole = ShareRoleValues.Controller,
                ControllerAddress = string.Empty,
                DebugToken = string.Empty,
                VerboseLogging = false,
                LogRetentionDays = 3,
                LogTransferEnabled = true,
                ConfigRevision = 50,
                UseNewControllers = true
            };
        }
    }

    public static class ShareRoleValues
    {
        public const string Controller = "Controller";
        public const string Agent = "Agent";

        public static string Normalize(string role)
        {
            if (string.Equals(role, Agent, System.StringComparison.OrdinalIgnoreCase))
            {
                return Agent;
            }

            return Controller;
        }

        public static string DisplayName(string role)
        {
            return Normalize(role) == Agent ? "\u88AB\u63A7\u7AEF" : "\u4E3B\u63A7\u7AEF";
        }
    }

    [DataContract]
    public sealed class TrustedDevice
    {
        [DataMember(Order = 1)]
        public string DeviceId { get; set; }

        [DataMember(Order = 2)]
        public string DeviceName { get; set; }

        [DataMember(Order = 3)]
        public string LastKnownAddress { get; set; }

        [DataMember(Order = 4)]
        public byte[] ProtectedSharedSecret { get; set; }

        [DataMember(Order = 5)]
        public int ScreenWidth { get; set; }

        [DataMember(Order = 6)]
        public int ScreenHeight { get; set; }
    }

    [DataContract]
    public sealed class LayoutConfig
    {
        [DataMember(Order = 1)]
        public string PeerDirection { get; set; }

        /// <summary>边角阻挡开关随布局同步（问题③修复）：null 表示本次不携带该字段。</summary>
        [DataMember(Order = 2)]
        public bool? BlockScreenCorners { get; set; }
    }
}
