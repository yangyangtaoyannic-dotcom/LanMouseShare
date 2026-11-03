using System;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Service
{
    /// <summary>
    /// Owns the debug-log pipeline for this machine. A controller runs the collector,
    /// an agent runs the forwarder. Both roles keep writing their own live log files.
    /// </summary>
    internal sealed class LogHub : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Func<string> _peerAddressProvider;
        private readonly Func<AppConfig> _configProvider;
        private LogCollector _collector;
        private LogForwarder _forwarder;
        private string _role = ShareRoleValues.Controller;
        private string _token = string.Empty;
        private string _address = string.Empty;
        private bool _transferEnabled = true;
        private bool _running;

        public LogHub(Func<string> peerAddressProvider, Func<AppConfig> configProvider)
        {
            _peerAddressProvider = peerAddressProvider;
            _configProvider = configProvider;
        }

        public void Start(AppConfig config)
        {
            lock (_sync)
            {
                _running = true;
                // The UI process writes its logs next to the service logs; share the folder first.
                AppLog.GrantSharedWriteAccess();
                ApplyLocked(config, true);
            }
        }

        public void Apply(AppConfig config)
        {
            lock (_sync)
            {
                if (!_running)
                {
                    return;
                }

                ApplyLocked(config, false);
            }
        }

        public void Stop()
        {
            LogCollector collector;
            LogForwarder forwarder;
            lock (_sync)
            {
                _running = false;
                collector = _collector;
                forwarder = _forwarder;
                _collector = null;
                _forwarder = null;
            }

            if (forwarder != null)
            {
                TryStop(delegate { forwarder.Stop(); });
            }

            if (collector != null)
            {
                TryStop(delegate { collector.Stop(); });
            }

            AppLog.Event("INFO", "log.hub.stop", "role=" + ShareRoleValues.DisplayName(_role));
        }

        public void Dispose()
        {
            Stop();
        }

        private void ApplyLocked(AppConfig config, bool force)
        {
            if (config == null)
            {
                return;
            }

            var role = ShareRoleValues.Normalize(config.ShareRole);
            var token = config.DebugToken == null ? string.Empty : config.DebugToken;
            var address = config.ControllerAddress == null ? string.Empty : config.ControllerAddress.Trim();
            var verbose = config.VerboseLogging;
            var retention = config.LogRetentionDays <= 0 ? 3 : config.LogRetentionDays;
            var transferEnabled = config.LogTransferEnabled;

            AppLog.SetRole(ShareRoleValues.DisplayName(role));
            AppLog.SetVerbose(verbose);
            AppLog.SetRetentionDays(retention);

            var roleChanged = force || role != _role || token != _token || address != _address ||
                              transferEnabled != _transferEnabled;
            _role = role;
            _token = token;
            _address = address;
            _transferEnabled = transferEnabled;

            if (!roleChanged)
            {
                return;
            }

            AppLog.Event("INFO", "log.hub.apply",
                "role=" + ShareRoleValues.DisplayName(role) +
                " controller=" + (string.IsNullOrEmpty(address) ? "auto" : address) +
                " token=" + (string.IsNullOrEmpty(token) ? "off" : "on") +
                " verbose=" + verbose +
                " retentionDays=" + retention +
                " transfer=" + (transferEnabled ? "on" : "off"));

            if (_forwarder != null)
            {
                var old = _forwarder;
                _forwarder = null;
                TryStop(delegate { old.Stop(); });
            }

            if (_collector != null)
            {
                var old = _collector;
                _collector = null;
                TryStop(delegate { old.Stop(); });
            }

            if (role == ShareRoleValues.Agent && !transferEnabled)
            {
                AppLog.Event("INFO", "log.hub.transfer.off", "reason=disabled-by-user");
            }
            else if (role == ShareRoleValues.Agent)
            {
                var forwarder = new LogForwarder(_peerAddressProvider, _configProvider);
                _forwarder = forwarder;
                forwarder.Start();
                AppLog.Event("INFO", "log.hub.role.agent", "target=" + (string.IsNullOrEmpty(address) ? "auto-peer" : address));
            }
            else
            {
                var collector = new LogCollector(config.DeviceName);
                _collector = collector;
                collector.Start(LogProtocol.DefaultPort, token);
                AppLog.Event("INFO", "log.hub.role.controller", "port=" + LogProtocol.DefaultPort);
            }

            AppLog.Prune();
        }

        public string StatusText()
        {
            lock (_sync)
            {
                var roleName = ShareRoleValues.DisplayName(_role);
                if (_role == ShareRoleValues.Agent)
                {
                    if (_forwarder == null)
                    {
                        if (!_transferEnabled)
                        {
                            return "\u8FD0\u884C\u89D2\u8272\uFF1A" + roleName +
                                   " \u00B7 \u65E5\u5FD7\u63A8\u9001\u5DF2\u5173\u95ED\uFF08\u4EC5\u4FDD\u5B58\u5728\u672C\u673A\uFF09";
                        }

                        return "\u8FD0\u884C\u89D2\u8272\uFF1A" + roleName + " \u00B7 \u65E5\u5FD7\u63A8\u9001\u672A\u542F\u52A8";
                    }

                    return "\u8FD0\u884C\u89D2\u8272\uFF1A" + roleName + " \u00B7 " + _forwarder.StatusText;
                }

                if (_collector == null)
                {
                    return "\u8FD0\u884C\u89D2\u8272\uFF1A" + roleName + " \u00B7 \u65E5\u5FD7\u6536\u96C6\u672A\u542F\u52A8";
                }

                return "\u8FD0\u884C\u89D2\u8272\uFF1A" + roleName + " \u00B7 " + _collector.StatusText();
            }
        }

        public void NotifyInputActivity()
        {
            LogForwarder forwarder;
            lock (_sync)
            {
                forwarder = _forwarder;
            }

            if (forwarder != null)
            {
                forwarder.NotifyInputActivity();
            }
        }

        private static void TryStop(Action stop)
        {
            try
            {
                stop();
            }
            catch (Exception ex)
            {
                AppLog.Error("log.hub.stop.failed", ex);
            }
        }
    }
}
