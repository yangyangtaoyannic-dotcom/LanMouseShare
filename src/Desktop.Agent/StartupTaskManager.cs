using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    internal static class StartupTaskManager
    {
        public const string RunValueName = "LanMouseShare.Agent";
        public const string AdminTaskName = "LanMouseShare.Agent.Admin";

        public static bool IsCurrentProcessAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static void ApplyStartup(bool startWithWindows, bool runAsAdministratorByDefault, string agentPath)
        {
            // A Run key entry starts the agent with the caller token. When the agent needs to capture
            // input it must run elevated, and a Run key cannot do that without a UAC prompt at every
            // logon - which users read as "start with Windows does not work". So whenever we are
            // already elevated, prefer a highest-privilege scheduled task and drop the Run value.
            var elevated = IsCurrentProcessAdministrator();
            var useTask = startWithWindows && (runAsAdministratorByDefault || elevated);
            if (useTask)
            {
                // 先创建计划任务，成功后再删 Run 键；创建失败（例如 Win7 上旧代码
                // 无密码建任务被拒、非交互会话等）回退写 Run 键，保证开机启动始终
                // 至少有一条注册项生效（问题②修复）。
                var taskCreated = false;
                try
                {
                    ConfigureAdminStartupTask(true, agentPath);
                    taskCreated = true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("startup.task.create.failed, fallback to run key. Error=" + ex.Message);
                }

                ConfigureRegistryStartup(!taskCreated, agentPath);
            }
            else
            {
                DeleteAdminStartupTask();
                ConfigureRegistryStartup(startWithWindows, agentPath);
            }

            AppLog.Event("INFO", "startup.applied",
                "startWithWindows=" + startWithWindows +
                " runAsAdmin=" + runAsAdministratorByDefault +
                " elevated=" + elevated +
                " mode=" + (useTask ? "scheduled-task" : "run-key"));
        }

        public static void ConfigureRegistryStartup(bool enabled, string agentPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled)
                {
                    // 添加 /minimized 参数实现静默启动
                    key.SetValue(RunValueName, "\"" + agentPath + "\" /minimized");
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }

        public static void ConfigureAdminStartupTask(bool startWithWindows, string agentPath)
        {
            if (!startWithWindows)
            {
                DeleteAdminStartupTask();
                return;
            }

                var user = Environment.UserDomainName + "\\" + Environment.UserName;
                RunSchtasks(BuildCreateTaskArguments(agentPath, user), true);
            }

            public static void DeleteAdminStartupTask()
            {
                RunSchtasks(BuildDeleteTaskArguments(), false);
            }

            public static string BuildCreateTaskArguments(string agentPath, string user)
            {
                // /IT：仅用户交互登录时运行——官方支持的免密创建方式（Win7 无 /IT
                // 且不带 /RP 时 schtasks 会要求密码并失败）。/RL HIGHEST 保持提权。
                // 添加 /minimized 参数实现静默启动
                return "/Create /TN \"" + AdminTaskName + "\" " +
                       "/TR \"\\\"" + agentPath + "\\\" /minimized\" " +
                       "/SC ONLOGON /IT /RL HIGHEST /F /RU \"" + user + "\"";
            }

            public static string BuildDeleteTaskArguments()
            {
                return "/Delete /TN \"" + AdminTaskName + "\" /F";
            }

        public static bool RestartElevated()
        {
            var agentPath = System.Reflection.Assembly.GetEntryAssembly().Location;
            if (string.IsNullOrWhiteSpace(agentPath) || !File.Exists(agentPath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo(agentPath);
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            Process.Start(startInfo);
            return true;
        }

        private static void RunSchtasks(string arguments, bool throwOnFailure)
        {
            try
            {
                var startInfo = new ProcessStartInfo("schtasks.exe", arguments);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit(5000);
                    if (throwOnFailure && process.ExitCode != 0)
                    {
                        throw new InvalidOperationException("schtasks failed. ExitCode=" + process.ExitCode + " Args=" + arguments);
                    }
                }
            }
            catch (Exception ex)
            {
                if (throwOnFailure)
                {
                    throw;
                }

                AppLog.Warn("schtasks ignored. Args=" + arguments + " Error=" + ex.Message);
            }
        }
    }
}
