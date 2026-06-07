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
                using (var runtime = new ServiceRuntime())
                {
                    runtime.Start();
                    AppLog.Info("Agent-managed service runtime started.");
                    ServiceStopSignal.Wait();
                    runtime.Stop();
                    AppLog.Info("Agent-managed service runtime stopped.");
                }

                return;
            }

            ServiceBase.Run(new LanMouseShareService());
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
