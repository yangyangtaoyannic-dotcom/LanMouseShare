using System;
using System.Drawing;

namespace LanMouseShare.Input
{
    public struct DpiScale
    {
        private readonly uint _dpiX;
        private readonly uint _dpiY;

        public DpiScale(uint dpiX, uint dpiY)
        {
            _dpiX = dpiX == 0 ? 96u : dpiX;
            _dpiY = dpiY == 0 ? 96u : dpiY;
        }

        public uint DpiX { get { return _dpiX == 0 ? 96u : _dpiX; } }
        public uint DpiY { get { return _dpiY == 0 ? 96u : _dpiY; } }
    }

    public static class DpiCoordinateMapper
    {
        private const uint DefaultDpi = 96;
        private const int MdtEffectiveDpi = 0;
        private const int LogPixelsX = 88;
        private const int LogPixelsY = 90;

        public static DpiScale GetScaleForPoint(int x, int y)
        {
            return GetScaleForMonitor(x, y);
        }

        public static RectangleF PixelRectToDip(Rectangle pixelRect, DpiScale scale)
        {
            var left = PixelsToDip(pixelRect.Left, scale.DpiX);
            var top = PixelsToDip(pixelRect.Top, scale.DpiY);
            var right = PixelsToDip(pixelRect.Right, scale.DpiX);
            var bottom = PixelsToDip(pixelRect.Bottom, scale.DpiY);
            return RectangleF.FromLTRB((float)left, (float)top, (float)right, (float)bottom);
        }

        public static PointF PixelPointToDip(Point pixelPoint, DpiScale scale)
        {
            return new PointF(
                (float)PixelsToDip(pixelPoint.X, scale.DpiX),
                (float)PixelsToDip(pixelPoint.Y, scale.DpiY));
        }

        public static double PixelsToDip(int pixels, uint dpi)
        {
            var normalizedDpi = dpi == 0 ? DefaultDpi : dpi;
            return pixels * 96.0 / normalizedDpi;
        }

        private static DpiScale GetScaleForMonitor(int x, int y)
        {
            try
            {
                var point = new NativeMethods.POINT { X = x, Y = y };
                var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    uint dpiX;
                    uint dpiY;
                    var hr = NativeMethods.GetDpiForMonitor(monitor, MdtEffectiveDpi, out dpiX, out dpiY);
                    if (hr == 0 && dpiX > 0 && dpiY > 0)
                    {
                        return new DpiScale(dpiX, dpiY);
                    }
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch
            {
            }

            return GetScaleFromDeviceContext();
        }

        private static DpiScale GetScaleFromDeviceContext()
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = NativeMethods.GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    var dpiX = NativeMethods.GetDeviceCaps(hdc, LogPixelsX);
                    var dpiY = NativeMethods.GetDeviceCaps(hdc, LogPixelsY);
                    if (dpiX > 0 && dpiY > 0)
                    {
                        return new DpiScale((uint)dpiX, (uint)dpiY);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
                }
            }

            return new DpiScale(DefaultDpi, DefaultDpi);
        }
    }
}
