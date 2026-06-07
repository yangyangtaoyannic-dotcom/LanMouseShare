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
            if (runAsAdministratorByDefault)
            {
                ConfigureRegistryStartup(false, agentPath);
                ConfigureAdminStartupTask(startWithWindows, agentPath);
            }
            else
            {
                DeleteAdminStartupTask();
                ConfigureRegistryStartup(startWithWindows, agentPath);
            }
        }

        public static void ConfigureRegistryStartup(bool enabled, string agentPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled)
                {
                    key.SetValue(RunValueName, "\"" + agentPath + "\"");
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
                return "/Create /TN \"" + AdminTaskName + "\" " +
                       "/TR \"\\\"" + agentPath + "\\\"\" " +
                       "/SC ONLOGON /RL HIGHEST /F /RU \"" + user + "\"";
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
