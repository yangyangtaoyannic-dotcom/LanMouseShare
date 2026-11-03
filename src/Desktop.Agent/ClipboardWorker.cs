using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    /// <summary>
    /// 剪贴板 I/O 专用线程（0.2.0-beta-fix16）。
    ///
    /// 为什么必须有它：WH_MOUSE_LL / WH_KEYBOARD_LL 低级钩子装在 WPF UI 线程上
    /// （<c>MainWindow</c> 的 <c>_hook.Start()</c>），钩子回调要靠这条线程泵消息才能跑。
    /// 而 <see cref="System.Windows.Clipboard"/> 的每次调用都是一次跨进程 OLE 往返，
    /// 内部还有 10 次 × 100ms 的 COMException 睡眠重试。一旦在 UI 线程上调用，
    /// 钩子回调就排不上队，Windows 会把整机鼠标输入一起卡住 —— 这就是
    /// “跨屏复制必卡鼠标、写入端卡好几秒”的直接原因。
    ///
    /// 所以这里起一条常驻的后台 STA 线程：剪贴板只能在这条线程上读写，调用方只投递任务，
    /// 永不阻塞。线程带 WPF Dispatcher（完整 Win32 消息泵），粘贴时别的进程按需取数据
    /// 才有线程响应；线程常驻不退出，写完剪贴板后数据也不会因为线程结束而失效。
    /// </summary>
    internal static class ClipboardWorker
    {
        private static readonly object Sync = new object();

        // 用 volatile 引用赋值代替锁：其他线程只读一次，读到 null 才进锁启动。
        private static volatile Dispatcher _dispatcher;
        private static volatile int _threadId;
        private static int _fallbackLogged;

        /// <summary>调用方是否已经在这条剪贴板线程上（此时可以直接读写，不用再投递）。</summary>
        public static bool IsOnWorkerThread
        {
            get
            {
                var id = _threadId;
                return id != 0 && Thread.CurrentThread.ManagedThreadId == id;
            }
        }

        /// <summary>投递一个剪贴板任务。调用方立刻返回，绝不在自己线程上执行。</summary>
        public static void Post(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = EnsureStarted();
            if (dispatcher == null)
            {
                RunOnFallback(action);
                return;
            }

            try
            {
                // fix16.1：任务体必须再包一层 SafeRun。BeginInvoke 里抛出的异常会进
                // Dispatcher.UnhandledException，默认直接终结整个进程 —— 剪贴板任务
                // 出点小错不能让用户的整套键鼠共享跟着崩。
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate { SafeRun(action); }));
            }
            catch (Exception ex)
            {
                // Dispatcher 已关闭（退出过程）：退到线程池，至少不阻塞调用方。
                AppLog.Warn("clipboard.worker.post.failed " + ex.Message);
                RunOnFallback(action);
            }
        }

        /// <summary>延迟投递（重试、去抖都用它）。计时器建在剪贴板线程上，与本线程共存亡。</summary>
        public static void PostDelayed(int delayMs, Action action)
        {
            if (action == null)
            {
                return;
            }

            if (delayMs <= 0)
            {
                Post(action);
                return;
            }

            var dispatcher = EnsureStarted();
            if (dispatcher == null)
            {
                RunOnFallback(action);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                {
                    var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(delayMs);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        SafeRun(action);
                    };
                    timer.Start();
                }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("clipboard.worker.delay.failed " + ex.Message);
                RunOnFallback(action);
            }
        }

        /// <summary>已经在线程上就直接执行，否则投递。用于“可能被 worker 自己调用”的代码路径。</summary>
        public static void RunInline(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (IsOnWorkerThread)
            {
                SafeRun(action);
                return;
            }

            Post(action);
        }

        private static void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Warn("clipboard.worker.job.failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RunOnFallback(Action action)
        {
            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
            {
                AppLog.Warn("clipboard.worker.unavailable; clipboard jobs fall back to thread pool.");
            }

            ThreadPool.QueueUserWorkItem(delegate { SafeRun(action); });
        }

        private static Dispatcher EnsureStarted()
        {
            var existing = _dispatcher;
            if (existing != null)
            {
                return existing;
            }

            lock (Sync)
            {
                existing = _dispatcher;
                if (existing != null)
                {
                    return existing;
                }

                var ready = new ManualResetEventSlim(false);
                var thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        // OLE 剪贴板要求 STA + OleInitialize；WPF 的 Clipboard 也会自己补，
                        // 这里显式初始化一次，避免第一次调用落在没初始化的线程上。
                        TryOleInitialize();
                        var dispatcher = Dispatcher.CurrentDispatcher;

                        // fix16.1：兜底。这条线程上的任何漏网异常都不能变成进程退出。
                        dispatcher.UnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs args)
                        {
                            args.Handled = true;
                            AppLog.Warn("clipboard.worker.unhandled " +
                                (args.Exception != null ? args.Exception.GetType().Name + ": " + args.Exception.Message : "-"));
                        };

                        _threadId = Thread.CurrentThread.ManagedThreadId;
                        _dispatcher = dispatcher;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("clipboard.worker.start.failed " + ex.Message);
                    }
                    finally
                    {
                        ready.Set();
                    }

                    // 常驻消息泵：STA 线程必须泵消息，OLE 才能把别的进程的取数据请求
                    // 派回这条线程。线程是后台线程，退出应用时随之结束。
                    Dispatcher.Run();
                }));

                thread.IsBackground = true;
                thread.Name = "LanMouseShare clipboard IO";
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait(5000);
                ready.Dispose();

                return _dispatcher;
            }
        }

        private static void TryOleInitialize()
        {
            try
            {
                OleInitialize(IntPtr.Zero);
            }
            catch (Exception)
            {
                // ole32 必然存在；失败也不阻断，WPF Clipboard 自己还会再初始化一次。
            }
        }

        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);
    }
}
