using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        /// <summary>安装器自身的数字版本，只用来判断"是否已装同等或更新的包"，必须单调递增。</summary>
        private const string SetupVersion = "0.2.7";
        /// <summary>对外展示的产品版本（设置 → 应用里看到的那个），跟 Agent 版本保持一致。</summary>
        private const string AppVersion = "0.2.0-beta-fix24";
        private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare";
        private const string ShellMenuKeyFile = @"Software\Classes\*\shell\LanMouseShareSend";
        private const string ShellMenuKeyDirectory = @"Software\Classes\Directory\shell\LanMouseShareSend";
        private const string ShellMenuKeyShortcut = @"Software\Classes\lnkfile\shell\LanMouseShareSend";

        private static int Main(string[] args)
        {
            try
            {
                if (HasArg(args, "/uninstall") || HasArg(args, "--uninstall") || HasArg(args, "-uninstall") || HasArg(args, "uninstall"))
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

                if (!HasArg(args, "/install") && !HasArg(args, "--install") && IsInstalledCurrentOrNewer())
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
            KillProcess("LanMouseShare.Service");
            ExtractPayload(installDir);
            CleanupLegacyArtifacts(installDir);
            InstallService(installDir);
            ConfigureFirewall(true);
            ConfigureStartup(false, Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"));
            CreateShortcuts(installDir);
            RegisterUninstall(installDir);
            StartAgent(installDir);
        }

        /// <summary>
        /// 完整卸载：摘掉开机自启 → 问用户要不要连配置日志一起删 → 结束进程与服务 →
        /// 删防火墙/右键菜单/快捷方式/卸载项 → 可选删数据目录 → 删安装目录。
        /// 每一步都单独 try，重复卸载不会抛异常。
        /// </summary>
        private static void Uninstall()
        {
            var installDir = InstallDirectory();

            // 先摘开机自启，否则刚杀掉 Agent 就被 Run 项重新拉起来。
            TryRun(delegate { ConfigureStartup(false, null); });

            var removeUserData = AskAboutUserData();
            if (!removeUserData.HasValue)
            {
                return;
            }

            TryRun(delegate { StopAgent(); });
            TryRun(delegate { KillProcess("LanMouseShare.Agent"); });
            TryRun(delegate { StopService(); });
            TryRun(delegate { KillProcess("LanMouseShare.Service"); });
            TryRun(delegate { UninstallService(installDir); });
            TryRun(delegate { ConfigureFirewall(false); });
            TryRun(delegate { RemoveShellIntegration(); });
            TryRun(delegate { RemoveShortcuts(); });
            TryRun(delegate { UnregisterUninstall(); });

            if (removeUserData.Value)
            {
                TryRun(delegate { DeleteDirectoryWithRetry(UserDataDirectory()); });
            }

            var installRemoved = TryRun(delegate { DeleteDirectoryWithRetry(installDir); });

            MessageBox.Show(
                BuildUninstallSummary(installDir, removeUserData.Value, installRemoved),
                DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /// <summary>
        /// 问用户是否连 C:\ProgramData\LanMouseShare（配置、日志、配对记录）一起删。
        /// 返回 null 表示用户点了"取消"——那就整个卸载都不做。
        /// </summary>
        private static bool? AskAboutUserData()
        {
            var text =
                "要卸载 " + DisplayName + " 吗？" + Environment.NewLine + Environment.NewLine +
                "卸载会结束正在运行的 Agent 与 Service，并删除程序文件、防火墙规则、" +
                "开机自启、开始菜单/桌面快捷方式、资源管理器右键菜单。" + Environment.NewLine + Environment.NewLine +
                "是否同时删除配置、日志与配对记录？" + Environment.NewLine +
                UserDataDirectory() + Environment.NewLine + Environment.NewLine +
                "是 = 连配置和日志一起删（重装后需要重新配对）" + Environment.NewLine +
                "否 = 只删程序，保留配置和日志" + Environment.NewLine +
                "取消 = 什么都不做";
            var result = MessageBox.Show(text, DisplayName, MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                return true;
            }

            if (result == DialogResult.No)
            {
                return false;
            }

            return null;
        }

        private static string BuildUninstallSummary(string installDir, bool removedUserData, bool installRemoved)
        {
            var lines = new System.Collections.Generic.List<string>();
            lines.Add(installRemoved ? "程序文件已删除。" : "程序文件没能全部删除（可能仍被占用），请重启后手动删除：" + installDir);
            lines.Add(removedUserData
                ? "配置、日志与配对记录已删除。"
                : "配置、日志与配对记录已保留：" + UserDataDirectory());
            lines.Add("防火墙规则、开机自启、快捷方式、右键菜单、卸载项均已清理。");
            lines.Add("桌面上的传输文件没有动。");
            return string.Join(Environment.NewLine + Environment.NewLine, lines.ToArray());
        }

        private static string UserDataDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);
        }

        /// <summary>安装目录里被占用的文件会短暂锁住，重试几次再判定失败。</summary>
        private static bool DeleteDirectoryWithRetry(string directory)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        return true;
                    }

                    Directory.Delete(directory, true);
                    if (!Directory.Exists(directory))
                    {
                        return true;
                    }
                }
                catch
                {
                }

                Thread.Sleep(400);
            }

            return !Directory.Exists(directory);
        }

        /// <summary>删掉资源管理器右键菜单注册项，并通知外壳刷新，否则要重启 explorer 才消失。</summary>
        private static void RemoveShellIntegration()
        {
            DeleteUserKeyTree(ShellMenuKeyFile);
            DeleteUserKeyTree(ShellMenuKeyDirectory);
            DeleteUserKeyTree(ShellMenuKeyShortcut);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DeleteUserKeyTree(string keyName)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyName, false);
            }
            catch
            {
            }
        }

        private static bool TryRun(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        private const string PayloadResourceName = "Root.payload.zip";

        private static void ExtractPayload(string installDir)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(PayloadResourceName))
            {
                if (stream != null)
                {
                    ExtractZipPayload(stream, installDir);
                    return;
                }
            }

            ExtractLegacyResources(assembly, installDir);
        }

        private static void ExtractZipPayload(Stream stream, string installDir)
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(installDir, relative);
                    var folder = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static void ExtractLegacyResources(Assembly assembly, string installDir)
        {
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
            KillProcess("LanMouseShare.Agent");
        }

        /// <summary>强制结束同名进程，避免“关掉软件后还在运行”的孤儿进程。</summary>
        private static void KillProcess(string processName)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < processes.Length; i++)
            {
                var process = processes[i];
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                        process.WaitForExit(1500);
                    }
                }
                catch
                {
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// 清掉历史版本留下的多余文件：旧的回滚备份目录、旧版 (x86) 安装目录、
        /// 以及运行时用不到的 pdb 调试符号。
        /// </summary>
        private static void CleanupLegacyArtifacts(string installDir)
        {
            try
            {
                var parent = Path.GetDirectoryName(installDir);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    var backups = Directory.GetDirectories(parent, AppName + ".rollback-backup-*");
                    Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
                    // 保留最新的一份备份，其余删除。
                    for (int i = 0; i < backups.Length - 1; i++)
                    {
                        TryDeleteDirectory(backups[i]);
                    }
                }

                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppName);
                if (!string.IsNullOrEmpty(legacy) &&
                    Directory.Exists(legacy) &&
                    !string.Equals(Path.GetFullPath(legacy).TrimEnd('\\'), Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(legacy);
                }

                if (Directory.Exists(installDir))
                {
                    foreach (var pdb in Directory.GetFiles(installDir, "*.pdb", SearchOption.AllDirectories))
                    {
                        try { File.Delete(pdb); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
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
                Run("netsh.exe", "advfirewall firewall add rule name=\"LAN Mouse Share\" dir=in action=allow protocol=TCP localport=47532,47540,47541", false);
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
            using (var key = Registry.LocalMachine.CreateSubKey(UninstallRegistryKey))
            {
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", "LanMouseShare");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "LanMouseShareSetup.exe") + "\" --uninstall");
                key.SetValue("DisplayIcon", Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"));
                key.SetValue("HelpLink", Path.Combine(installDir, "使用说明-先看我.md"));
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                // 升级判断用这个数字版本，DisplayVersion 只负责好看。
                key.SetValue("SetupVersion", SetupVersion);
            }

            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(installDir, "LanMouseShareSetup.exe"), true);
        }

        private static void UnregisterUninstall()
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryKey, false);
        }

        private static void CreateShortcuts(string installDir)
        {
            var folder = StartMenuFolder();
            Directory.CreateDirectory(folder);
            CreateShortcut(Path.Combine(folder, "LAN Mouse Share.lnk"), Path.Combine(installDir, "Agent", "LanMouseShare.Agent.exe"), string.Empty);
            CreateShortcut(Path.Combine(folder, "打开日志.lnk"), "explorer.exe", LogDirectory());
            CreateShortcut(Path.Combine(folder, "卸载 LAN Mouse Share.lnk"), Path.Combine(installDir, "LanMouseShareSetup.exe"), "--uninstall");
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
                using (var key = Registry.LocalMachine.OpenSubKey(UninstallRegistryKey))
                {
                    var installedText = key != null ? key.GetValue("SetupVersion") as string : null;
                    if (string.IsNullOrEmpty(installedText))
                    {
                        installedText = key != null ? key.GetValue("DisplayVersion") as string : null;
                    }

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
