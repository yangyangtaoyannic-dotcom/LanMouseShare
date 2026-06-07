using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanMouseShare.Setup
{
    internal static class Program
    {
        private const string AppName = "LanMouseShare";
        private const string DisplayName = "LAN Mouse Share";
        private const string AgentRunName = "LanMouseShare.Agent";
        private const string SetupVersion = "0.1.41";

        private static int Main(string[] args)
        {
            try
            {
                if (HasArg(args, "/uninstall"))
                {
                    if (!IsAdministrator())
                    {
                        return 1;
                    }

                    if (!HasArg(args, "/final") && IsRunningFromInstallDirectory())
                    {
                        RelaunchUninstallFromTemp();
                        return 0;
                    }

                    Uninstall();
                    return 0;
                }

                if (!HasArg(args, "/install") && IsInstalledCurrentOrNewer())
                {
                    StartInstalledAgent();
                    return 0;
                }

                if (!IsAdministrator())
                {
                    return 1;
                }

                Install();
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanMouseShareSetup.log"),
                        DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
                }
                catch
                {
                }

                return 1;
            }
        }

        private static void Install()
        {
            var installDir = InstallDirectory();
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(Path.Combine(installDir, "Agent"));
            Directory.CreateDirectory(Path.Combine(installDir, "Service"));
            Directory.CreateDirectory(Path.Combine(installDir, "Scripts"));
            Directory.CreateDirectory(Path.Combine(installDir, "Docs"));
            EnsureLogDirectoryAccess();

            StopAgent();
            StopService();
            ExtractPayload(installDir);
            InstallService(installDir);
            ConfigureFirewall(true);
            ConfigureStartup(false, Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"));
            CreateShortcuts(installDir);
            RegisterUninstall(installDir);
            StartAgent(installDir);
        }

        private static void Uninstall()
        {
            var installDir = InstallDirectory();
            StopAgent();
            StopService();
            UninstallService(installDir);
            ConfigureFirewall(false);
            ConfigureStartup(false, null);
            RemoveShortcuts();
            UnregisterUninstall();

            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, true);
            }
        }

        private static void ExtractPayload(string installDir)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames();
            if (names.Length == 0)
            {
                throw new InvalidOperationException("安装包缺少内置文件资源。");
            }

            for (int i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (!name.StartsWith("Agent.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Service.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Scripts.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Docs.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Root.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = name.Replace('.', Path.DirectorySeparatorChar);
                var first = relative.IndexOf(Path.DirectorySeparatorChar);
                var folder = relative.Substring(0, first);
                var fileName = relative.Substring(first + 1).Replace(Path.DirectorySeparatorChar, '.');
                string target;
                if (folder == "Root")
                {
                    target = Path.Combine(installDir, fileName);
                }
                else
                {
                    target = Path.Combine(installDir, folder, fileName);
                }

                using (var input = assembly.GetManifestResourceStream(name))
                using (var output = File.Create(target))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void InstallService(string installDir)
        {
            var serviceExe = Path.Combine(installDir, "Service", "LanMouseShare.Service.exe");
            CleanupLanMouseShareServices();
            Run("sc.exe", "delete LanMouseShare", false);
            Thread.Sleep(500);
            Run("sc.exe", "create LanMouseShare binPath= \"" + serviceExe + "\" start= demand DisplayName= \"LAN Mouse Share\"", true);
            Run("sc.exe", "description LanMouseShare \"Provides LAN discovery, pairing, transport, and file transfer for LanMouseShare.\"", false);
        }

        private static void UninstallService(string installDir)
        {
            Run("sc.exe", "delete LanMouseShare", false);
        }

        private static void StartService()
        {
            Run("net.exe", "start LanMouseShare", false);
        }

        private static void StopService()
        {
            Run("net.exe", "stop LanMouseShare", false);
        }

        private static void CleanupLanMouseShareServices()
        {
            Run("sc.exe", "config LanMouseShare start= demand", false);
            Run("net.exe", "stop LanMouseShare", false);
            Run("sc.exe", "delete LanMouseShare", false);
        }

        private static void StopAgent()
        {
            foreach (var process in Process.GetProcessesByName("LanMouseShare.Agent"))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
            }
        }

        private static void StartAgent(string installDir)
        {
            var agent = Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe");
            if (File.Exists(agent))
            {
                StartAgentAsShellUser(agent);
            }
        }

        private static void StartAgentAsShellUser(string agent)
        {
            try
            {
                var startInfo = new ProcessStartInfo("explorer.exe", "\"" + agent + "\"");
                startInfo.UseShellExecute = false;
                Process.Start(startInfo);
                return;
            }
            catch
            {
            }

            Process.Start(agent);
        }

        private static void ConfigureFirewall(bool add)
        {
            if (add)
            {
                Run("netsh.exe", "advfirewall firewall delete rule name=\"LAN Mouse Share\"", false);
                Run("netsh.exe", "advfirewall firewall add rule name=\"LAN Mouse Share\" dir=in action=allow protocol=UDP localport=47531,47533,47534", false);
                Run("netsh.exe", "advfirewall firewall add rule name=\"LAN Mouse Share\" dir=in action=allow protocol=TCP localport=47532", false);
            }
            else
            {
                Run("netsh.exe", "advfirewall firewall delete rule name=\"LAN Mouse Share\"", false);
            }
        }

        private static void ConfigureStartup(bool enabled, string agentPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled)
                {
                    key.SetValue(AgentRunName, "\"" + agentPath + "\"");
                }
                else
                {
                    key.DeleteValue(AgentRunName, false);
                }
            }
        }

        private static void RegisterUninstall(string installDir)
        {
            using (var key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare"))
            {
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", SetupVersion);
                key.SetValue("Publisher", "LanMouseShare");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "LanMouseShareSetup.exe") + "\" /uninstall");
                key.SetValue("DisplayIcon", Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"));
            }

            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(installDir, "LanMouseShareSetup.exe"), true);
        }

        private static void UnregisterUninstall()
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare", false);
        }

        private static void CreateShortcuts(string installDir)
        {
            var folder = StartMenuFolder();
            Directory.CreateDirectory(folder);
            CreateShortcut(Path.Combine(folder, "LAN Mouse Share.lnk"), Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"), string.Empty);
            CreateShortcut(Path.Combine(folder, "打开日志.lnk"), "explorer.exe", LogDirectory());
            CreateShortcut(Path.Combine(folder, "卸载 LAN Mouse Share.lnk"), Path.Combine(installDir, "LanMouseShareSetup.exe"), "/uninstall");
            CreateShortcut(DesktopShortcutPath(), Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"), string.Empty);
        }

        private static void RemoveShortcuts()
        {
            var folder = StartMenuFolder();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            var desktopShortcut = DesktopShortcutPath();
            if (File.Exists(desktopShortcut))
            {
                File.Delete(desktopShortcut);
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string arguments)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            if (File.Exists(targetPath))
            {
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.IconLocation = targetPath;
            }
            shortcut.Save();
        }

        private static string InstallDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        }

        private static string StartMenuFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LAN Mouse Share");
        }

        private static string DesktopShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LAN Mouse Share.lnk");
        }

        private static string LogDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName, "Logs");
        }

        private static void EnsureLogDirectoryAccess()
        {
            var directory = LogDirectory();
            Directory.CreateDirectory(directory);
            var security = Directory.GetAccessControl(directory);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var rule = new FileSystemAccessRule(
                users,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);
            bool modified;
            security.ModifyAccessRule(AccessControlModification.Add, rule, out modified);
            Directory.SetAccessControl(directory, security);
        }

        private static bool IsInstalledCurrentOrNewer()
        {
            var agent = Path.Combine(InstallDirectory(), "Agent", "LanMouseShare.Agent.exe");
            if (!File.Exists(agent))
            {
                return false;
            }

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare"))
                {
                    var installedText = key != null ? key.GetValue("DisplayVersion") as string : null;
                    Version installed;
                    Version current;
                    if (Version.TryParse(installedText, out installed) && Version.TryParse(SetupVersion, out current))
                    {
                        if (installed == current && !InstalledSetupMatchesCurrentPackage())
                        {
                            return false;
                        }

                        return installed >= current;
                    }
                }
            }
            catch
            {
            }

            return true;
        }

        private static bool InstalledSetupMatchesCurrentPackage()
        {
            try
            {
                var installedSetup = Path.Combine(InstallDirectory(), "LanMouseShareSetup.exe");
                var currentSetup = Assembly.GetExecutingAssembly().Location;
                if (!File.Exists(installedSetup) || !File.Exists(currentSetup))
                {
                    return false;
                }

                var installedInfo = new FileInfo(installedSetup);
                var currentInfo = new FileInfo(currentSetup);
                if (installedInfo.Length != currentInfo.Length)
                {
                    return false;
                }

                using (var installed = File.OpenRead(installedSetup))
                using (var current = File.OpenRead(currentSetup))
                {
                    var installedBuffer = new byte[64 * 1024];
                    var currentBuffer = new byte[64 * 1024];
                    while (true)
                    {
                        var installedRead = installed.Read(installedBuffer, 0, installedBuffer.Length);
                        var currentRead = current.Read(currentBuffer, 0, currentBuffer.Length);
                        if (installedRead != currentRead)
                        {
                            return false;
                        }

                        if (installedRead == 0)
                        {
                            return true;
                        }

                        for (int i = 0; i < installedRead; i++)
                        {
                            if (installedBuffer[i] != currentBuffer[i])
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void StartInstalledAgent()
        {
            if (Process.GetProcessesByName("LanMouseShare.Agent").Length > 0)
            {
                return;
            }

            StartAgent(InstallDirectory());
        }

        private static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool HasArg(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRunningFromInstallDirectory()
        {
            var current = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            var installDir = Path.GetFullPath(InstallDirectory()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return current.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);
        }

        private static void RelaunchUninstallFromTemp()
        {
            var temp = Path.Combine(Path.GetTempPath(), "LanMouseShareSetup-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, temp, true);
            var startInfo = new ProcessStartInfo(temp, "/uninstall /final");
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            Process.Start(startInfo);
        }

        private static void Run(string fileName, string arguments, bool throwOnFailure)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments);
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            using (var process = Process.Start(startInfo))
            {
                process.WaitForExit();
                if (throwOnFailure && process.ExitCode != 0)
                {
                    throw new InvalidOperationException(fileName + " failed. ExitCode=" + process.ExitCode);
                }
            }
        }
    }
}
