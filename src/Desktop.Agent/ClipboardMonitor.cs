using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LanMouseShare.Agent
{
    internal sealed class ClipboardMonitor : IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private HwndSource _source;

        public event EventHandler ClipboardChanged;

        public void Attach(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _source = HwndSource.FromHwnd(helper.Handle);
            if (_source != null)
            {
                _source.AddHook(WndProc);
                AddClipboardFormatListener(helper.Handle);
            }
        }

        public void Dispose()
        {
            if (_source != null)
            {
                var handle = _source.Handle;
                _source.RemoveHook(WndProc);
                RemoveClipboardFormatListener(handle);
                _source = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                var handler = ClipboardChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}
