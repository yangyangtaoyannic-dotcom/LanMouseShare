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

        /// <summary>True while the physical key is held (used to detect Ctrl+V on the owner side).</summary>
        public static bool IsKeyDown(int virtualKey)
        {
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }
    }
}
