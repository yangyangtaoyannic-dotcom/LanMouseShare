using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    /// <summary>
    /// fix10：在资源管理器的右键菜单里加"传输到对端"。
    ///
    /// 用户要求放弃"复制文件 → 对端粘贴"那条链路，改成选中文件/文件夹 → 右键 →
    /// 传输到对端 → 直接传到对端桌面。菜单项写在 HKCU 下（不需要管理员权限，也不需要
    /// 每次开机重建），命令是 `LanMouseShare.Agent.exe --send "%1"`；Agent 是单实例，
    /// 第二个进程会把路径通过 SendRequestChannel 转交给正在跑的实例。
    /// 注册是幂等的：值没变就不写，失败只记日志不影响主流程。
    /// </summary>
    internal static class ShellContextMenu
    {
        private const string FileMenuKey = @"Software\Classes\*\shell\LanMouseShareSend";
        private const string FolderMenuKey = @"Software\Classes\Directory\shell\LanMouseShareSend";

        // fix10：快捷方式必须单独注册。只挂 `*` 的话，Windows 会把 .lnk 解析成它的目标再套用
        // 文件动词，于是"传一个快捷方式"结果把快捷方式指向的程序本体传了过去（用户实测：
        // 传 LAN Mouse Share 快捷方式，对面收到的是 LanMouseShare.Agent.exe）。
        // 挂到 lnkfile 上以后，右键拿到的是 .lnk 文件本身，传过去也还是快捷方式。
        private const string ShortcutMenuKey = @"Software\Classes\lnkfile\shell\LanMouseShareSend";

        private const string MenuText = "传输到对端";

        /// <summary>注册（或更新）右键菜单项。重复调用安全。</summary>
        public static void Register()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    AppLog.Warn("Shell menu skipped: agent path unknown.");
                    return;
                }

                var command = "\"" + exePath + "\" --send \"%1\"";

                var wroteFile = WriteMenu(FileMenuKey, exePath, command);
                var wroteFolder = WriteMenu(FolderMenuKey, exePath, command);
                var wroteShortcut = WriteMenu(ShortcutMenuKey, exePath, command);

                if (wroteFile || wroteFolder || wroteShortcut)
                {
                    AppLog.Event("INFO", "shell.menu.registered",
                        "exe=" + exePath + " file=" + wroteFile + " folder=" + wroteFolder + " shortcut=" + wroteShortcut);
                }
            }
            catch (SecurityException ex)
            {
                AppLog.Warn("Shell menu registration denied: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Warn("Shell menu registration denied: " + ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.Error("Shell menu registration failed.", ex);
            }
        }

        /// <summary>移除菜单项（卸载或调试用）。</summary>
        public static void Unregister()
        {
            TryDeleteKey(FileMenuKey);
            TryDeleteKey(FolderMenuKey);
            TryDeleteKey(ShortcutMenuKey);
        }

        private static bool WriteMenu(string menuKey, string exePath, string command)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(menuKey))
            {
                if (key == null)
                {
                    return false;
                }

                var changed = false;

                var currentText = key.GetValue(string.Empty) as string;
                if (!string.Equals(currentText, MenuText, StringComparison.Ordinal))
                {
                    key.SetValue(string.Empty, MenuText, RegistryValueKind.String);
                    changed = true;
                }

                var currentIcon = key.GetValue("Icon") as string;
                if (!string.Equals(currentIcon, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("Icon", exePath, RegistryValueKind.String);
                    changed = true;
                }

                var currentFlags = key.GetValue("MultiSelectModel") as string;
                if (!string.Equals(currentFlags, "Player", StringComparison.OrdinalIgnoreCase))
                {
                    // Player 表示多选时每个文件各起一次进程，交给 SendRequestChannel 聚合。
                    key.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
                    changed = true;
                }

                using (var commandKey = key.CreateSubKey("command"))
                {
                    if (commandKey == null)
                    {
                        return changed;
                    }

                    var currentCommand = commandKey.GetValue(string.Empty) as string;
                    if (!string.Equals(currentCommand, command, StringComparison.Ordinal))
                    {
                        commandKey.SetValue(string.Empty, command, RegistryValueKind.String);
                        changed = true;
                    }
                }

                return changed;
            }
        }

        private static void TryDeleteKey(string menuKey)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(menuKey, false);
            }
            catch (Exception ex)
            {
                AppLog.Debug("shell.menu.unregister.fail", ex.Message);
            }
        }
    }
}
