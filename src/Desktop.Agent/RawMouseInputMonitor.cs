using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    internal sealed class RawMouseInputMonitor : IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const int RIDEV_INPUTSINK = 0x00000100;
        private const int RID_INPUT = 0x10000003;
        private const int RIM_TYPEMOUSE = 0;

        private HwndSource _source;

        public event EventHandler<RawMouseMovedEventArgs> MouseMoved;

        public void Attach(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _source = HwndSource.FromHwnd(helper.Handle);
            if (_source != null)
            {
                _source.AddHook(WndProc);
                Register(helper.Handle);
                AppLog.Info("Raw mouse input attached.");
            }
        }

        public void Dispose()
        {
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT)
            {
                ProcessRawInput(lParam);
            }

            return IntPtr.Zero;
        }

        private void ProcessRawInput(IntPtr lParam)
        {
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size == 0)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) != size)
                {
                    return;
                }

                var raw = (RAWINPUT)Marshal.PtrToStructure(buffer, typeof(RAWINPUT));
                if (raw.header.dwType == RIM_TYPEMOUSE && (raw.mouse.lLastX != 0 || raw.mouse.lLastY != 0))
                {
                    var handler = MouseMoved;
                    if (handler != null)
                    {
                        handler(this, new RawMouseMovedEventArgs(raw.mouse.lLastX, raw.mouse.lLastY));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Register(IntPtr hwnd)
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                }
            };

            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                AppLog.Warn("RegisterRawInputDevices failed.");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public int dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public int dwType;
            public int dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public ushort padding;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }
    }

    internal sealed class RawMouseMovedEventArgs : EventArgs
    {
        public RawMouseMovedEventArgs(int dx, int dy)
        {
            DeltaX = dx;
            DeltaY = dy;
        }

        public int DeltaX { get; private set; }
        public int DeltaY { get; private set; }
    }
}
