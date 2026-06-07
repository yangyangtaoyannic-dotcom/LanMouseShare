using System.Drawing;

namespace LanMouseShare.Input
{
    public static class CursorController
    {
        public static void SetPosition(int x, int y)
        {
            NativeMethods.SetCursorPos(x, y);
        }

        public static Point GetPosition()
        {
            NativeMethods.POINT point;
            return NativeMethods.GetCursorPos(out point) ? new Point(point.X, point.Y) : Point.Empty;
        }
    }
}
