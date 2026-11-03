using System;
using System.ServiceProcess;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Service
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            AppLog.Initialize("Service", true);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                AppLog.Error("Unhandled service exception. IsTerminating=" + e.IsTerminating, e.ExceptionObject as Exception);
            };
            AppLog.Info("Service process entry. Args=" + string.Join(" ", args));
            try
            {
                SystemInfoReport.WriteFile(AppLog.LogsRoot, "Service");
            }
            catch (Exception ex)
            {
                AppLog.Warn("System info report failed. " + ex.GetType().Name + ": " + ex.Message);
            }
            if (args.Length > 0 && string.Equals(args[0], "--console", StringComparison.OrdinalIgnoreCase))
            {
                using (var runtime = new ServiceRuntime())
                {
                    runtime.Start();
                    Console.WriteLine("LanMouseShare service runtime started. Press Enter to stop.");
                    Console.ReadLine();
                    runtime.Stop();
                }

                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "--agent", StringComparison.OrdinalIgnoreCase))
            {
                var ownerPid = 0;
                if (args.Length > 1)
                {
                    int.TryParse(args[1], out ownerPid);
                }

                using (var runtime = new ServiceRuntime())
                {
                    runtime.Start();
                    AppLog.Info("Agent-managed service runtime started. OwnerPid=" + ownerPid);

                    // Agent 被强杀 / 崩溃时不会有 ShutdownService 指令，
                    // 这个看门狗保证服务跟着 Agent 一起退出，不留孤儿进程。
                    if (ownerPid > 0)
                    {
                        StartOwnerWatchdog(ownerPid);
                    }

                    ServiceStopSignal.Wait();
                    runtime.Stop();
                    AppLog.Info("Agent-managed service runtime stopped.");
                }

                return;
            }

            ServiceBase.Run(new LanMouseShareService());
        }

        private static void StartOwnerWatchdog(int ownerPid)
        {
            var thread = new System.Threading.Thread(delegate()
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(2000);

                    try
                    {
                        using (var owner = System.Diagnostics.Process.GetProcessById(ownerPid))
                        {
                            if (!owner.HasExited)
                            {
                                continue;
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 进程已不存在。
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Service owner watchdog check failed. " + ex.GetType().Name + ": " + ex.Message);
                        continue;
                    }

                    AppLog.Warn("Agent process exited; stopping agent-managed service. OwnerPid=" + ownerPid);
                    ServiceStopSignal.RequestStop();
                    return;
                }
            });
            thread.IsBackground = true;
            thread.Name = "service-owner-watchdog";
            thread.Start();
        }
    }

    internal static class ServiceStopSignal
    {
        private static readonly System.Threading.ManualResetEvent StopEvent = new System.Threading.ManualResetEvent(false);

        public static void RequestStop()
        {
            StopEvent.Set();
        }

        public static void Wait()
        {
            StopEvent.WaitOne();
        }
    }
}
