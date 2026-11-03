using System.Drawing;

namespace LanMouseShare.Input
{
    public static class EdgeTransitionMapper
    {
        private const int EdgeInset = 8;
        // fix24：进场落点从 8px 挪到 40px。fix19 时"右键落错端"的根因就是落点贴着返回边，
        // 手指抖几像素就被判成"要回去"；把落点推离返回边之后，返回手势就可以做成
        // "越边即回"而不必再靠 260ms 驻留/140px 推力去拖时间。
        private const int RemoteInset = 40;

        public static Point MapEntryToRemote(Point entryPoint, Size localScreen, Size remoteScreen, string direction)
        {
            if (direction == "Left")
            {
                return new Point(Clamp(remoteScreen.Width - RemoteInset, 0, remoteScreen.Width - 1), Scale(entryPoint.Y, localScreen.Height, remoteScreen.Height));
            }

            if (direction == "Up")
            {
                return new Point(Scale(entryPoint.X, localScreen.Width, remoteScreen.Width), Clamp(remoteScreen.Height - RemoteInset, 0, remoteScreen.Height - 1));
            }

            if (direction == "Down")
            {
                return new Point(Scale(entryPoint.X, localScreen.Width, remoteScreen.Width), Clamp(RemoteInset, 0, remoteScreen.Height - 1));
            }

            return new Point(Clamp(RemoteInset, 0, remoteScreen.Width - 1), Scale(entryPoint.Y, localScreen.Height, remoteScreen.Height));
        }

        public static Point MapReturnToLocal(Point remotePoint, Size remoteScreen, Size localScreen, string direction)
        {
            if (direction == "Left")
            {
                return new Point(EdgeInset, Scale(remotePoint.Y, remoteScreen.Height, localScreen.Height));
            }

            if (direction == "Up")
            {
                return new Point(Scale(remotePoint.X, remoteScreen.Width, localScreen.Width), EdgeInset);
            }

            if (direction == "Down")
            {
                return new Point(Scale(remotePoint.X, remoteScreen.Width, localScreen.Width), Clamp(localScreen.Height - EdgeInset, 0, localScreen.Height - 1));
            }

            return new Point(Clamp(localScreen.Width - EdgeInset, 0, localScreen.Width - 1), Scale(remotePoint.Y, remoteScreen.Height, localScreen.Height));
        }

        private static int Scale(int value, int source, int target)
        {
            if (source <= 0 || target <= 0)
            {
                return 0;
            }

            return Clamp(value * target / source, 0, target - 1);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
            {
                return min;
            }

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
