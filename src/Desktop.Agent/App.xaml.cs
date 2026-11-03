using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Input;

namespace LanMouseShare.Agent
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\LanMouseShare.Agent.Instance";
        private const string ActivateEventName = @"Local\LanMouseShare.Agent.Activate";

        private Mutex _instanceMutex;
        private EventWaitHandle _activateEvent;

        public bool StartMinimized { get; private set; }

        /// <summary>
        /// fix10：右键菜单"传输到对端"会以 `--send "路径"` 启动本程序。如果抢到了单实例，
        /// 就由主窗口在这里取走这些路径直接发送；抢不到则转交给正在运行的实例后退出。
        /// </summary>
        public string[] PendingSendPaths { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            DesktopEnvironment.EnableDpiAwareness();
            AppLog.Initialize("Agent", false);
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 检查命令行参数
            if (e.Args != null)
            {
                var sendPaths = new System.Collections.Generic.List<string>();
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (e.Args[i].Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
                        e.Args[i].Equals("-minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        StartMinimized = true;
                        AppLog.Event("INFO", "app.startup", "mode=minimized");
                    }
                    else if (e.Args[i].Equals("--send", StringComparison.OrdinalIgnoreCase))
                    {
                        // 资源管理器右键只传一个 "%1"，这里仍按"可重复"解析，方便脚本批量调用。
                        if (i + 1 < e.Args.Length && !string.IsNullOrEmpty(e.Args[i + 1]))
                        {
                            sendPaths.Add(e.Args[i + 1]);
                            i++;
                        }
                    }
                }

                if (sendPaths.Count > 0)
                {
                    PendingSendPaths = sendPaths.ToArray();
                    AppLog.Event("INFO", "app.startup", "mode=send count=" + sendPaths.Count);
                }
            }

            base.OnStartup(e);

            // 单实例：第二次双击快捷方式时不再启动第二个 Agent（会重复挂钩子、重复托盘图标、
            // 抢同一个 UDP 端口、互抢 config.json），而是把已经在运行的那个主界面叫到前台。
            if (!AcquireSingleInstance())
            {
                // fix10：右键"传输到对端"启动的那一份进程——把路径转交给在跑的实例再退出。
                if (PendingSendPaths != null && PendingSendPaths.Length > 0)
                {
                    SendRequestChannel.TrySend(PendingSendPaths);
                    AppLog.Event("INFO", "app.single_instance", "action=forward_send count=" + PendingSendPaths.Length);
                    Shutdown();
                    return;
                }

                AppLog.Event("INFO", "app.single_instance", "action=activate_existing");
                SignalExistingInstance();
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private bool AcquireSingleInstance()
        {
            bool createdNew;
            try
            {
                _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            }
            catch (Exception ex)
            {
                // 拿不到互斥体时宁可继续运行，也不要让程序打不开。
                AppLog.Error("Single instance mutex failed; continuing.", ex);
                return true;
            }

            if (!createdNew)
            {
                try
                {
                    _instanceMutex.Dispose();
                }
                catch
                {
                }

                _instanceMutex = null;
                return false;
            }

            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                var watcher = new Thread(WatchActivationRequests);
                watcher.IsBackground = true;
                watcher.Name = "agent-activate-watch";
                watcher.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("Activation watcher failed.", ex);
            }

            return true;
        }

        private void WatchActivationRequests()
        {
            while (true)
            {
                try
                {
                    _activateEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("activate.watch.fail", "error=" + ex.GetType().Name);
                    return;
                }

                try
                {
                    Dispatcher.BeginInvoke(new Action(ShowMainWindowForActivation));
                }
                catch (Exception ex)
                {
                    AppLog.Debug("activate.dispatch.fail", "error=" + ex.GetType().Name);
                    return;
                }
            }
        }

        private void ShowMainWindowForActivation()
        {
            try
            {
                AppLog.Event("INFO", "app.activate", "reason=second_launch");
                var window = MainWindow;
                if (window == null)
                {
                    return;
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();

                // 被其它窗口挡住时，快速顶一下置顶再取消，确保真的出现在最前面。
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Show main window from activation failed.", ex);
            }
        }

        private static void SignalExistingInstance()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (var handle = EventWaitHandle.OpenExisting(ActivateEventName))
                    {
                        handle.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // 已有实例正在启动，稍等再试。
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("activate.signal.fail", "error=" + ex.GetType().Name);
                    Thread.Sleep(100);
                }
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Unhandled agent UI exception.", e.Exception);
            e.Handled = true;
        }
    }
}
