using System;
using System.IO;
using System.Security.Cryptography;
using LanMouseShare.Core.Crypto;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Core.Configuration
{
    public sealed class ConfigStore
    {
        private static readonly object SaveLock = new object();
        private readonly string _configPath;

        public ConfigStore(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new ArgumentException("Config path is required.", "configPath");
            }

            _configPath = configPath;
        }

        public AppConfig LoadOrCreate()
        {
            if (File.Exists(_configPath))
            {
                var existingConfig = ProtocolSerializer.FromJson<AppConfig>(File.ReadAllText(_configPath));
                ApplyDefaults(existingConfig);
                return existingConfig;
            }

            var identity = DeviceIdentity.CreateLocal();
            var config = AppConfig.CreateDefault(identity.DeviceId, DeviceIdentity.FriendlyName(), Protect(identity.SharedSecret));
            Save(config);
            return config;
        }

        public static void ApplyDefaults(AppConfig config)
        {
            if (config == null)
            {
                return;
            }

            if (config.TrustedDevices == null)
            {
                config.TrustedDevices = new System.Collections.Generic.List<TrustedDevice>();
            }

            if (config.Layout == null)
            {
                config.Layout = new LayoutConfig { PeerDirection = "Right" };
            }

            if (string.IsNullOrWhiteSpace(config.ReleaseHotkey))
            {
                config.ReleaseHotkey = "Ctrl+Alt+Pause";
            }

            if (string.IsNullOrWhiteSpace(config.PauseHotkey))
            {
                config.PauseHotkey = "Ctrl+F11";
            }

            if (string.IsNullOrWhiteSpace(config.StopControlHotkey))
            {
                config.StopControlHotkey = "Ctrl+F10";
            }

            if (string.IsNullOrWhiteSpace(config.ExitHotkey))
            {
                config.ExitHotkey = "Ctrl+Alt+End";
            }

            if (config.MouseSpeedMultiplier <= 0)
            {
                config.MouseSpeedMultiplier = 1.0;
            }

            if (config.MouseUpdateRateHz <= 0)
            {
                config.MouseUpdateRateHz = 200;
            }

            config.ShareRole = ShareRoleValues.Normalize(config.ShareRole);
            if (config.ControllerAddress == null)
            {
                config.ControllerAddress = string.Empty;
            }

            if (config.DebugToken == null)
            {
                config.DebugToken = string.Empty;
            }

            if (config.LogRetentionDays <= 0)
            {
                config.LogRetentionDays = 3;
            }

            if (config.ConfigRevision < ConfigStoreSettingsRevision)
            {
                // Configs written before 0.1.50 have no such field; they default to shipping logs.
                config.LogTransferEnabled = true;
                config.ConfigRevision = ConfigStoreSettingsRevision;
            }
        }

        private const int ConfigStoreSettingsRevision = 50;

        public void Save(AppConfig config)
        {
            lock (SaveLock)
            {
                ApplyDefaults(config);
                var directory = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = ProtocolSerializer.ToJson(config);
                IOException lastIoException = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.WriteAllText(_configPath, json);
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastIoException = ex;
                        System.Threading.Thread.Sleep(50);
                    }
                }

                throw lastIoException;
            }
        }

        public byte[] UnprotectLocalSecret(AppConfig config)
        {
            return Unprotect(config.ProtectedLocalSecret);
        }

        public byte[] Protect(byte[] bytes)
        {
            return ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        }

        public byte[] Unprotect(byte[] bytes)
        {
            return ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
        }

        public static string DefaultPath()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(root, "LanMouseShare", "config.json");
        }
    }
}
