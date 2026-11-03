using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LanMouseShare.Core.Configuration;
using Microsoft.Win32;

namespace LanMouseShare.Core.Diagnostics
{
    /// <summary>
    /// Writes a plain text report with the machine facts needed to diagnose cross-machine
    /// input problems: OS edition/build, screen layout and DPI, network adapters, service
    /// state and the effective LanMouseShare settings. The report is written next to the
    /// log files so it travels with them, and the log forwarder streams it to the
    /// controller which stores it under the remote device folder.
    /// </summary>
    public static class SystemInfoReport
    {
        public const string FileName = "SystemInfo.txt";
        public const string FileNamePrefix = "SystemInfo-";

        private static readonly object Sync = new object();

        /// <summary>
        /// The agent and the service run on the same machine and share one log directory, so
        /// each component writes its own report instead of overwriting the other one.
        /// </summary>
        public static string FileNameFor(string component)
        {
            var safe = string.IsNullOrWhiteSpace(component) ? "App" : component.Trim();
            var builder = new StringBuilder(safe.Length);
            for (int i = 0; i < safe.Length; i++)
            {
                var ch = safe[i];
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return FileNamePrefix + builder + ".txt";
        }

        public static string WriteFile(string directory, string component)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            lock (Sync)
            {
                Directory.CreateDirectory(directory);
                var fileName = FileNameFor(component);
                var path = Path.Combine(directory, fileName);
                var text = Build(component);
                File.WriteAllText(path, text, new UTF8Encoding(true));
                TryCopyToLiveDirectory(text, fileName);
                AppLog.Info("System info report written. Path=" + path);
                return path;
            }
        }

        /// <summary>
        /// The "open local logs" button opens the live folder, so keep a copy of the report
        /// there as well; it makes the file reachable without digging through the log tree.
        /// </summary>
        private static void TryCopyToLiveDirectory(string text, string fileName)
        {
            try
            {
                var live = AppLog.LiveDirectory;
                if (string.IsNullOrWhiteSpace(live))
                {
                    return;
                }

                Directory.CreateDirectory(live);
                var target = Path.Combine(live, fileName);
                File.WriteAllText(target, text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                AppLog.Debug("sysinfo.live-copy.skip", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static string Build(string component)
        {
            var builder = new StringBuilder();
            builder.AppendLine("LanMouseShare \u7CFB\u7EDF\u4FE1\u606F / system information");
            builder.AppendLine("=================================================");
            builder.AppendLine("\u751F\u6210\u65F6\u95F4 / generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("\u7EC4\u4EF6 / component: " + (component ?? "?"));
            builder.AppendLine("\u7A0B\u5E8F\u7248\u672C / version: " + AppLog.Version);
            builder.AppendLine();

            AppendOperatingSystem(builder);
            AppendHardware(builder);
            AppendDisplays(builder);
            AppendNetwork(builder);
            AppendProcess(builder);
            AppendConfiguration(builder);

            return builder.ToString();
        }

        private static void AppendOperatingSystem(StringBuilder builder)
        {
            builder.AppendLine("[\u64CD\u4F5C\u7CFB\u7EDF / operating system]");
            var os = Environment.OSVersion;
            builder.AppendLine("  OSVersion: " + os.VersionString + " Platform=" + os.Platform);
            builder.AppendLine("  Is64BitOS: " + Environment.Is64BitOperatingSystem + " Is64BitProcess: " + Environment.Is64BitProcess);
            builder.AppendLine("  CLR: " + Environment.Version);
            builder.AppendLine("  Machine: " + Safe(Environment.MachineName) + " User: " + Safe(Environment.UserName) +
                               " Domain: " + Safe(Environment.UserDomainName));
            builder.AppendLine("  Culture: " + CultureInfo.CurrentCulture.Name + " UICulture: " + CultureInfo.CurrentUICulture.Name);

            foreach (var key in new[]
                     {
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "ProductName" },
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "DisplayVersion" },
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ReleaseId", "ReleaseId" },
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "CurrentBuild" },
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", "UBR" },
                         new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "EditionID" }
                     })
            {
                builder.AppendLine("  " + key[2] + ": " + Safe(ReadRegistry(key[0], key[1])));
            }

            builder.AppendLine("  DotNet48: " + Safe(ReadRegistry(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Version")));
            builder.AppendLine("  UptimeMs: " + Environment.TickCount);
            builder.AppendLine();
        }

        private static void AppendHardware(StringBuilder builder)
        {
            builder.AppendLine("[\u786C\u4EF6 / hardware]");
            builder.AppendLine("  CPU: " + Safe(ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")));
            builder.AppendLine("  PhysicalMemoryMB: " + GetTotalMemoryMb());
            builder.AppendLine("  SystemDrive: " + Safe(Environment.GetEnvironmentVariable("SystemDrive")));

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    builder.AppendLine("  Drive " + drive.Name + ": " +
                                       (drive.TotalSize / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB total, " +
                                       (drive.AvailableFreeSpace / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB free");
                }
                catch
                {
                }
            }

            builder.AppendLine();
        }

        private static void AppendDisplays(StringBuilder builder)
        {
            builder.AppendLine("[\u663E\u793A\u5668 / displays]");
            var monitors = EnumerateMonitors();
            if (monitors.Count == 0)
            {
                builder.AppendLine("  monitor enumeration failed; using screen bounds only");
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                builder.AppendLine("  Monitor" + (i + 1) + ": bounds=" + monitor.Left + "," + monitor.Top + "," +
                                   monitor.Width + "x" + monitor.Height +
                                   " primary=" + monitor.Primary +
                                   " dpi=" + monitor.DpiX + "x" + monitor.DpiY);
            }

            builder.AppendLine();
        }

        private static void AppendNetwork(StringBuilder builder)
        {
            builder.AppendLine("[\u7F51\u7EDC / network]");
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < adapters.Length; i++)
                {
                    var adapter = adapters[i];
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    builder.AppendLine("  Adapter " + (i + 1) + ": " + Safe(adapter.Name) +
                                       " | " + Safe(adapter.Description) +
                                       " | type=" + adapter.NetworkInterfaceType +
                                       " status=" + adapter.OperationalStatus +
                                       " speed=" + FormatSpeed(adapter.Speed) +
                                       " mac=" + Safe(adapter.GetPhysicalAddress().ToString()));
                    var properties = adapter.GetIPProperties();
                    var addresses = new List<string>();
                    foreach (var address in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork ||
                            address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            addresses.Add(address.Address + "/" + address.PrefixLength);
                        }
                    }

                    if (addresses.Count > 0)
                    {
                        builder.AppendLine("    addresses: " + string.Join(", ", addresses.ToArray()));
                    }

                    var gateways = new List<string>();
                    foreach (var gateway in properties.GatewayAddresses)
                    {
                        if (gateway.Address != null && !gateway.Address.Equals(IPAddress.Any))
                        {
                            gateways.Add(gateway.Address.ToString());
                        }
                    }

                    if (gateways.Count > 0)
                    {
                        builder.AppendLine("    gateways: " + string.Join(", ", gateways.ToArray()) +
                                           " dhcp=" + (properties.DhcpServerAddresses.Count > 0));
                    }
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine("  network enumeration failed: " + ex.Message);
            }

            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                var ports = new List<string>();
                foreach (var endpoint in listeners)
                {
                    if (endpoint.Port == 47532 || endpoint.Port == 47540)
                    {
                        ports.Add(endpoint.ToString());
                    }
                }

                builder.AppendLine("  Listening (47532/47540): " + (ports.Count == 0 ? "-" : string.Join(", ", ports.ToArray())));
            }
            catch (Exception ex)
            {
                builder.AppendLine("  listening ports failed: " + ex.Message);
            }

            builder.AppendLine();
        }

        private static void AppendProcess(StringBuilder builder)
        {
            builder.AppendLine("[\u8FDB\u7A0B / process]");
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                builder.AppendLine("  " + Safe(process.ProcessName) + " pid=" + process.Id +
                                   " started=" + process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                   " workingSetMB=" + (process.WorkingSet64 / 1024 / 1024));
                builder.AppendLine("  path: " + Safe(process.MainModule != null ? process.MainModule.FileName : "-"));
                builder.AppendLine("  sessionId: " + process.SessionId +
                                   " isAdmin: " + IsAdministrator() +
                                   " interactive: " + Environment.UserInteractive);
            }
            catch (Exception ex)
            {
                builder.AppendLine("  process info failed: " + ex.Message);
            }

            builder.AppendLine("  logs root: " + Safe(AppLog.LogsRoot));
            builder.AppendLine("  live logs: " + Safe(AppLog.LiveDirectory));
            builder.AppendLine("  remote logs: " + Safe(AppLog.RemoteDirectory));
            builder.AppendLine();
        }

        private static void AppendConfiguration(StringBuilder builder)
        {
            builder.AppendLine("[\u914D\u7F6E / configuration]");
            try
            {
                var store = new ConfigStore(ConfigStore.DefaultPath());
                var config = store.LoadOrCreate();
                builder.AppendLine("  DeviceId: " + Safe(config.DeviceId));
                builder.AppendLine("  DeviceName: " + Safe(config.DeviceName));
                builder.AppendLine("  ShareRole: " + Safe(config.ShareRole) + " (" + ShareRoleValues.DisplayName(config.ShareRole) + ")");
                builder.AppendLine("  ControllerAddress: " + Safe(config.ControllerAddress));
                builder.AppendLine("  VerboseLogging: " + config.VerboseLogging + " LogRetentionDays: " + config.LogRetentionDays);
                builder.AppendLine("  Layout." + Safe(config.Layout != null ? config.Layout.PeerDirection : "-") +
                                   " Screen=" + config.ScreenWidth + "x" + config.ScreenHeight);
                builder.AppendLine("  MouseSpeedMultiplier: " + config.MouseSpeedMultiplier.ToString(CultureInfo.InvariantCulture) +
                                   " MouseUpdateRateHz: " + config.MouseUpdateRateHz);
                builder.AppendLine("  Hotkeys: pause=" + Safe(config.PauseHotkey) +
                                   " stop=" + Safe(config.StopControlHotkey) +
                                   " exit=" + Safe(config.ExitHotkey));
                builder.AppendLine("  ClipboardEnabled: " + config.ClipboardEnabled +
                                   " BlockScreenCorners: " + config.BlockScreenCorners +
                                   " StartWithWindows: " + config.StartWithWindows);
                builder.AppendLine("  ConfigPath: " + Safe(ConfigStore.DefaultPath()));
            }
            catch (Exception ex)
            {
                builder.AppendLine("  configuration read failed: " + ex.Message);
            }
        }

        private static string ReadRegistry(string keyPath, string valueName)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    var value = key.GetValue(valueName);
                    return value == null ? null : value.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FormatSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0)
            {
                return "-";
            }

            return (bitsPerSecond / 1000000).ToString(CultureInfo.InvariantCulture) + "Mbps";
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static bool IsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static long GetTotalMemoryMb()
        {
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (GlobalMemoryStatusEx(ref status))
                {
                    return (long)(status.TotalPhys / 1024 / 1024);
                }
            }
            catch
            {
            }

            return 0;
        }

        private static List<MonitorInfo> EnumerateMonitors()
        {
            var monitors = new List<MonitorInfo>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data)
                {
                    var info = new MonitorInfoEx();
                    info.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                    var ok = GetMonitorInfo(monitor, ref info);
                    if (!ok)
                    {
                        return true;
                    }

                    int dpiX;
                    int dpiY;
                    GetMonitorDpi(monitor, out dpiX, out dpiY);
                    monitors.Add(new MonitorInfo
                    {
                        Left = info.Monitor.Left,
                        Top = info.Monitor.Top,
                        Width = info.Monitor.Right - info.Monitor.Left,
                        Height = info.Monitor.Bottom - info.Monitor.Top,
                        Primary = (info.Flags & 1) != 0,
                        DpiX = dpiX,
                        DpiY = dpiY
                    });
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            return monitors;
        }

        private static void GetMonitorDpi(IntPtr monitor, out int dpiX, out int dpiY)
        {
            dpiX = 96;
            dpiY = 96;
            try
            {
                uint x;
                uint y;
                if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0 && y > 0)
                {
                    dpiX = (int)x;
                    dpiY = (int)y;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        private sealed class MonitorInfo
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public bool Primary;
            public int DpiX;
            public int DpiY;
        }
    }
}
