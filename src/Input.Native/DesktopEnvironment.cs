using System;
using System.Drawing;
using System.Windows.Forms;

namespace LanMouseShare.Input
{
    public static class DesktopEnvironment
    {
        private static bool _dpiAwarenessRequested;

        public static void EnableDpiAwareness()
        {
            if (_dpiAwarenessRequested)
            {
                return;
            }

            _dpiAwarenessRequested = true;
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static bool IsAnyMouseButtonPhysicallyDown()
        {
            return IsLeftMouseButtonPhysicallyDown() ||
                   IsRightMouseButtonPhysicallyDown() ||
                   IsMiddleMouseButtonPhysicallyDown() ||
                   IsKeyDown(NativeMethods.VK_XBUTTON1) ||
                   IsKeyDown(NativeMethods.VK_XBUTTON2);
        }

        public static bool IsLeftMouseButtonPhysicallyDown()
        {
            return IsKeyDown(NativeMethods.VK_LBUTTON);
        }

        public static bool IsRightMouseButtonPhysicallyDown()
        {
            return IsKeyDown(NativeMethods.VK_RBUTTON);
        }

        public static bool IsMiddleMouseButtonPhysicallyDown()
        {
            return IsKeyDown(NativeMethods.VK_MBUTTON);
        }

        public static Rectangle GetScreenBoundsFromPoint(int x, int y)
        {
            try
            {
                var point = new NativeMethods.POINT { X = x, Y = y };
                var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new NativeMethods.MONITORINFO();
                    info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
                    if (NativeMethods.GetMonitorInfo(monitor, ref info))
                    {
                        return Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
                    }
                }
            }
            catch
            {
            }

            return Screen.FromPoint(new Point(x, y)).Bounds;
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }
    }
}
