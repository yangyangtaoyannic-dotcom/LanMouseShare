using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    /// <summary>
    /// fix21：跨屏后提示"指针现在在这里"的跟随式水波纹。
    /// 窗口无边框、全透明、置顶、不抢焦点、不拦点击；出现即全亮，
    /// 之后每 16ms 重新贴到光标位置跟随移动，同时从指针中心一圈圈向外扩散淡出（水波纹），
    /// 整条提示 1 秒后自关。
    /// 只在 UI 线程使用：由 MainWindow 在"刚变成 Target"或"控制权返回本机"时调用。
    /// </summary>
    internal sealed class ScreenEntryRippleWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const double WindowSize = 200.0;

        // 水波纹参数：4 圈错开出生，各自从 MinRadius 扩散到 MaxRadius 后淡没。
        private const int RippleCount = 4;
        private const int RippleLifeMs = 620;
        private const int RippleSpawnMs = 190;
        private const double RippleMinRadius = 10.0;
        private const double RippleMaxRadius = 82.0;
        private const double RippleThickness = 3.0;

        private const int TotalMs = 1000;
        private const int ExitFadeMs = 180;
        private const int MoveIntervalMs = 16;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private static ScreenEntryRippleWindow _current;

        private readonly double _scaleX;
        private readonly double _scaleY;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Ellipse[] _rings = new Ellipse[RippleCount];
        private readonly int[] _birthMs = new int[RippleCount];
        private DispatcherTimer _followTimer;

        private ScreenEntryRippleWindow(double dpiScaleX, double dpiScaleY)
        {
            _scaleX = dpiScaleX <= 0 ? 1.0 : dpiScaleX;
            _scaleY = dpiScaleY <= 0 ? 1.0 : dpiScaleY;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = WindowSize;
            Height = WindowSize;
            Opacity = 1.0;

            var canvas = new Canvas { Width = WindowSize, Height = WindowSize, IsHitTestVisible = false };
            Content = canvas;

            for (var i = 0; i < RippleCount; i++)
            {
                var ring = new Ellipse
                {
                    Width = 0,
                    Height = 0,
                    StrokeThickness = RippleThickness,
                    Stroke = new SolidColorBrush(RippleColor(i)),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Hidden
                };
                Canvas.SetLeft(ring, WindowSize / 2);
                Canvas.SetTop(ring, WindowSize / 2);
                canvas.Children.Add(ring);

                _rings[i] = ring;
                _birthMs[i] = i * RippleSpawnMs;
            }

            SizeChanged += OnSizeChanged;
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>在鼠标当前位置开始一次跟随提示；重复调用会先关掉上一次。返回是否真的画出来了。</summary>
        public static bool ShowAtCursor(double dpiScaleX, double dpiScaleY)
        {
            NativePoint point;
            if (!GetCursorPos(out point))
            {
                return false;
            }

            CloseCurrent();
            var window = new ScreenEntryRippleWindow(dpiScaleX, dpiScaleY);
            window.MoveTo(point.X, point.Y);
            _current = window;
            window.Show();
            return true;
        }

        private static void CloseCurrent()
        {
            var previous = _current;
            if (previous == null)
            {
                return;
            }

            _current = null;
            try
            {
                previous.Close();
            }
            catch (Exception ex)
            {
                AppLog.Debug("ripple.close.failed", ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>水波纹用偏青白的冷色，交替两档让扩散有层次。</summary>
        private static Color RippleColor(int index)
        {
            return (index % 2) == 0
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 158, 226, 255);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 画布尺寸固定，跟着窗口尺寸走一遍，避免 0 尺寸时定位偏移。
            var canvas = Content as Canvas;
            if (canvas == null)
            {
                return;
            }

            canvas.Width = ActualWidth > 0 ? ActualWidth : WindowSize;
            canvas.Height = ActualHeight > 0 ? ActualHeight : WindowSize;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                var style = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
            catch (Exception ex)
            {
                AppLog.Debug("ripple.style.failed", ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _clock.Restart();

            _followTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(MoveIntervalMs)
            };
            _followTimer.Tick += OnFollowTick;
            _followTimer.Start();
        }

        private void OnFollowTick(object sender, EventArgs e)
        {
            try
            {
                var elapsed = _clock.ElapsedMilliseconds;

                NativePoint point;
                if (GetCursorPos(out point))
                {
                    MoveTo(point.X, point.Y);
                }

                UpdateRipples(elapsed);

                if (elapsed >= TotalMs)
                {
                    StopFollowTimer();
                    CloseCurrent();
                    return;
                }

                var fadeStart = TotalMs - ExitFadeMs;
                if (elapsed >= fadeStart)
                {
                    var progress = (elapsed - fadeStart) / (double)ExitFadeMs;
                    Opacity = progress >= 1.0 ? 0.0 : 1.0 - progress;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("ripple.follow.failed", ex.GetType().Name + ": " + ex.Message);
                StopFollowTimer();
                CloseCurrent();
            }
        }

        /// <summary>按各自的出生时间推进每一圈：半径向外扩、透明度随扩散衰减。</summary>
        private void UpdateRipples(long elapsedMs)
        {
            var center = WindowSize / 2;

            for (var i = 0; i < RippleCount; i++)
            {
                var ring = _rings[i];
                if (ring == null)
                {
                    continue;
                }

                var local = elapsedMs - _birthMs[i];
                if (local < 0 || local > RippleLifeMs)
                {
                    ring.Visibility = Visibility.Hidden;
                    continue;
                }

                var progress = local / (double)RippleLifeMs;
                var eased = 1.0 - Math.Pow(1.0 - progress, 2.2);
                var radius = RippleMinRadius + (RippleMaxRadius - RippleMinRadius) * eased;
                var alpha = Math.Pow(1.0 - progress, 1.5);

                ring.Visibility = Visibility.Visible;
                ring.Width = radius * 2;
                ring.Height = radius * 2;
                ring.StrokeThickness = RippleThickness * (1.0 - 0.6 * progress);
                ring.Opacity = alpha;

                Canvas.SetLeft(ring, center - radius);
                Canvas.SetTop(ring, center - radius);
            }
        }

        private void MoveTo(int cursorX, int cursorY)
        {
            Left = cursorX / _scaleX - (WindowSize / 2);
            Top = cursorY / _scaleY - (WindowSize / 2);
        }

        private void StopFollowTimer()
        {
            var timer = _followTimer;
            if (timer == null)
            {
                return;
            }

            _followTimer = null;
            timer.Stop();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            StopFollowTimer();
        }
    }
}
