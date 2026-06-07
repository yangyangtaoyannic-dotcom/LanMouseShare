using System.Windows;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Input;

namespace LanMouseShare.Agent
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DesktopEnvironment.EnableDpiAwareness();
            AppLog.Initialize("Agent", false);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Unhandled agent UI exception.", e.Exception);
            e.Handled = true;
        }
    }
}
