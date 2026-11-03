using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace LanMouseShare.Core.Diagnostics
{
    public static class AppLog
    {
        private static readonly object Sync = new object();
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;
        private static string _component = "App";
        private static string _path;
        private static string _root;
        private static string _liveDirectory;
        private static string _remoteDirectory;
        private static string _role = "Unknown";
        private static volatile bool _verbose;
        private static int _retentionDays = 3;
        private static Timer _pruneTimer;

        public static void Initialize(string component, bool commonAppData)
        {
            _component = string.IsNullOrWhiteSpace(component) ? "App" : component;
            _root = ResolveRoot(commonAppData);
            _liveDirectory = Path.Combine(_root, "live");
            _remoteDirectory = Path.Combine(_root, "remote");

            try
            {
                Directory.CreateDirectory(_liveDirectory);
                Directory.CreateDirectory(_remoteDirectory);
            }
            catch
            {
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _path = Path.Combine(_liveDirectory, _component + "_" + timestamp + "_" + ProcessId + ".log");
            CleanupOldLogs(_retentionDays);

            var startPruneTimer = false;
            lock (Sync)
            {
                if (_pruneTimer == null)
                {
                    _pruneTimer = new Timer(delegate { CleanupOldLogs(_retentionDays); }, null, 6 * 60 * 60 * 1000, 6 * 60 * 60 * 1000);
                    startPruneTimer = true;
                }
            }

            if (startPruneTimer)
            {
                AppLog.Info("Logger initialized. Version=" + Version + " Process=" + Process.GetCurrentProcess().ProcessName +
                            " File=" + _path + " RetentionDays=" + _retentionDays);
            }
            else
            {
                Info("Logger re-initialized. Version=" + Version + " Process=" + Process.GetCurrentProcess().ProcessName + " File=" + _path);
            }
        }

        public static string Version
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(LanMouseShare.Core.BuildInfo.Version))
                    {
                        return LanMouseShare.Core.BuildInfo.Version;
                    }

                    var version = typeof(AppLog).Assembly.GetName().Version;
                    return version == null ? "0.0.0" : version.ToString(3);
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }

        public static string LogsRoot
        {
            get
            {
                EnsureInitialized();
                return _root;
            }
        }

        /// <summary>Kept for UI compatibility: the folder that holds this machine's log tree.</summary>
        public static string LogDirectory
        {
            get
            {
                return LogsRoot;
            }
        }

        public static string LiveDirectory
        {
            get
            {
                EnsureInitialized();
                return _liveDirectory;
            }
        }

        public static string RemoteDirectory
        {
            get
            {
                EnsureInitialized();
                return _remoteDirectory;
            }
        }

        public static string CurrentLogFile
        {
            get
            {
                EnsureInitialized();
                return _path;
            }
        }

        public static void SetRole(string role)
        {
            _role = string.IsNullOrWhiteSpace(role) ? "Unknown" : role;
        }

        public static void SetVerbose(bool verbose)
        {
            _verbose = verbose;
        }

        public static bool IsVerbose
        {
            get { return _verbose; }
        }

        public static void SetRetentionDays(int days)
        {
            _retentionDays = days <= 0 ? 3 : days;
        }

        public static void Info(string message)
        {
            Write("INFO", "info", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", "warn", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", "error", message, exception);
        }

        /// <summary>Debug event written only while verbose logging is on.</summary>
        public static void Debug(string ev, string details)
        {
            if (!_verbose)
            {
                return;
            }

            Write("DEBUG", ev, details, null);
        }

        /// <summary>Structured event: level + short event name + key=value detail string.</summary>
        public static void Event(string level, string ev, string details)
        {
            Write(level, ev, details, null);
        }

        public static void Prune()
        {
            CleanupOldLogs(_retentionDays);
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_pruneTimer != null)
                {
                    try
                    {
                        _pruneTimer.Dispose();
                    }
                    catch
                    {
                    }

                    _pruneTimer = null;
                }
            }
        }

        private static void EnsureInitialized()
        {
            if (string.IsNullOrEmpty(_path))
            {
                Initialize("App", true);
            }
        }

        private static string ResolveRoot(bool commonAppData)
        {
            var candidates = new List<string>();
            try
            {
                var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(common))
                {
                    candidates.Add(Path.Combine(common, "LanMouseShare", "Logs"));
                }
            }
            catch
            {
            }

            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    candidates.Add(Path.Combine(local, "LanMouseShare", "Logs"));
                }
            }
            catch
            {
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (CanWriteTo(candidates[i]))
                {
                    return candidates[i];
                }
            }

            if (candidates.Count > 0)
            {
                return candidates[candidates.Count - 1];
            }

            return Path.Combine(Path.GetTempPath(), "LanMouseShare", "Logs");
        }

        private static bool CanWriteTo(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Grants the interactive users write access to the shared log folder so the UI process can
        /// write logs next to the service. Requires administrator rights; silently ignored otherwise.
        /// </summary>
        public static void GrantSharedWriteAccess()
        {
            try
            {
                var root = LogsRoot;
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }

                var info = new DirectoryInfo(root);
                var security = info.GetAccessControl();
                var rule = new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify | System.Security.AccessControl.FileSystemRights.Synchronize,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow);
                security.AddAccessRule(rule);
                info.SetAccessControl(security);
                Info("Log folder shared writable. Root=" + root);
            }
            catch (Exception ex)
            {
                Warn("GrantSharedWriteAccess ignored. " + ex.Message);
            }
        }

        /// <summary>
        /// All live log folders this machine should ship from: the shared folder plus per-user folders
        /// created when a user started the UI before the service granted shared access.
        /// </summary>
        public static string[] GetForwardedLiveDirectories()
        {
            EnsureInitialized();
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(_liveDirectory) && Directory.Exists(_liveDirectory))
            {
                directories.Add(_liveDirectory);
            }

            try
            {
                var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var profileParent = !string.IsNullOrEmpty(profileRoot) ? Directory.GetParent(profileRoot) : null;
                var usersDirectory = profileParent != null ? profileParent.FullName : null;
                if (!string.IsNullOrEmpty(usersDirectory) && Directory.Exists(usersDirectory))
                {
                    var profiles = Directory.GetDirectories(usersDirectory);
                    for (int i = 0; i < profiles.Length; i++)
                    {
                        var candidate = Path.Combine(profiles[i], "AppData", "Local", "LanMouseShare", "Logs", "live");
                        try
                        {
                            if (Directory.Exists(candidate) && !directories.Contains(candidate))
                            {
                                directories.Add(candidate);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            return directories.ToArray();
        }

        private static void Write(string level, string ev, string details, Exception exception)
        {
            try
            {
                if (string.IsNullOrEmpty(_path))
                {
                    Initialize("App", true);
                }

                var line = FormatLine(level, ev, details, exception);

                lock (Sync)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private static string FormatLine(string level, string ev, string details, Exception exception)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                       " [" + PadLevel(level) + "] [" + _component + "] [pid:" + ProcessId +
                       " t:" + Thread.CurrentThread.ManagedThreadId + "] role=" + _role +
                       " ev=" + ev;
            if (!string.IsNullOrEmpty(details))
            {
                line += " " + details;
            }

            if (exception != null)
            {
                line += " | exception=" + exception.GetType().Name + ": " + exception.Message;
                if (!string.IsNullOrEmpty(exception.StackTrace))
                {
                    line += Environment.NewLine + exception.StackTrace;
                }
            }

            return line;
        }

        private static string PadLevel(string level)
        {
            if (level == null)
            {
                return "INFO ";
            }

            return level.Length >= 5 ? level : level + new string(' ', 5 - level.Length);
        }

        private static void CleanupOldLogs(int retentionDays)
        {
            var days = retentionDays <= 0 ? 3 : retentionDays;
            try
            {
                if (!string.IsNullOrEmpty(_liveDirectory))
                {
                    PruneDirectory(_liveDirectory, days, 200);
                }

                if (!string.IsNullOrEmpty(_remoteDirectory))
                {
                    PruneDirectory(_remoteDirectory, days, 400);
                }
            }
            catch
            {
            }
        }

        /// <summary>Deletes log files older than retentionDays and caps the file count. Returns deleted count.</summary>
        public static int PruneDirectory(string directory, int retentionDays, int maxFiles)
        {
            var deleted = 0;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return deleted;
            }

            var days = retentionDays <= 0 ? 3 : retentionDays;
            var cutoffUtc = DateTime.UtcNow.AddDays(-days);
            var files = Directory.GetFiles(directory, "*.log", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                try
                {
                    if (RetryDelete(file, cutoffUtc, i >= maxFiles))
                    {
                        deleted++;
                    }
                }
                catch
                {
                }
            }

            try
            {
                var directories = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length)
                    .ToArray();
                for (int i = 0; i < directories.Length; i++)
                {
                    try
                    {
                        if (Directory.GetFiles(directories[i]).Length == 0 &&
                            Directory.GetDirectories(directories[i]).Length == 0)
                        {
                            Directory.Delete(directories[i]);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return deleted;
        }

        private static bool RetryDelete(FileInfo file, DateTime cutoffUtc, bool overCap)
        {
            var tooOld = file.LastWriteTimeUtc < cutoffUtc;
            if (!tooOld && !overCap)
            {
                return false;
            }

            try
            {
                file.Delete();
                return true;
            }
            catch (IOException)
            {
                // Still open by another process; skip and retry on the next prune.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
