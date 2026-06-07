using System.Drawing;

namespace LanMouseShare.Input
{
    public sealed class EdgeSwitchDetector
    {
        private readonly int _thresholdPixels;

        public EdgeSwitchDetector(int thresholdPixels)
        {
            _thresholdPixels = thresholdPixels < 0 ? 0 : thresholdPixels;
        }

        public bool ShouldSwitch(Point point, Size screenSize, string peerDirection)
        {
            return IsAtEdge(point, screenSize, peerDirection);
        }

        public bool ShouldSwitch(Point point, Point previousPoint, Size screenSize, string peerDirection)
        {
            return IsAtEdge(point, screenSize, peerDirection) && IsMovingOutward(point, previousPoint, peerDirection);
        }

        public bool ShouldSwitch(Point point, Point previousPoint, Size screenSize, string peerDirection, bool blockCorners, int cornerBlockPixels)
        {
            if (blockCorners && IsInBlockedCorner(point, screenSize, peerDirection, cornerBlockPixels))
            {
                return false;
            }

            return ShouldSwitch(point, previousPoint, screenSize, peerDirection);
        }

        private bool IsAtEdge(Point point, Size screenSize, string peerDirection)
        {
            if (peerDirection == "Left")
            {
                return point.X <= _thresholdPixels;
            }

            if (peerDirection == "Right")
            {
                return point.X >= screenSize.Width - 1 - _thresholdPixels;
            }

            if (peerDirection == "Up")
            {
                return point.Y <= _thresholdPixels;
            }

            if (peerDirection == "Down")
            {
                return point.Y >= screenSize.Height - 1 - _thresholdPixels;
            }

            return false;
        }

        private static bool IsMovingOutward(Point point, Point previousPoint, string peerDirection)
        {
            if (peerDirection == "Left")
            {
                return point.X < previousPoint.X;
            }

            if (peerDirection == "Right")
            {
                return point.X > previousPoint.X;
            }

            if (peerDirection == "Up")
            {
                return point.Y < previousPoint.Y;
            }

            if (peerDirection == "Down")
            {
                return point.Y > previousPoint.Y;
            }

            return false;
        }

        public static bool IsInBlockedCorner(Point point, Size screenSize, string peerDirection, int cornerBlockPixels)
        {
            var size = cornerBlockPixels < 0 ? 0 : cornerBlockPixels;
            if (size == 0 || screenSize.Width <= 0 || screenSize.Height <= 0)
            {
                return false;
            }

            if (peerDirection == "Left" || peerDirection == "Right")
            {
                var atHorizontalEdge = peerDirection == "Left"
                    ? point.X <= 0
                    : point.X >= screenSize.Width - 1;
                return atHorizontalEdge && (point.Y < size || point.Y >= screenSize.Height - size);
            }

            if (peerDirection == "Up" || peerDirection == "Down")
            {
                var atVerticalEdge = peerDirection == "Up"
                    ? point.Y <= 0
                    : point.Y >= screenSize.Height - 1;
                return atVerticalEdge && (point.X < size || point.X >= screenSize.Width - size);
            }

            return false;
        }

        public Point TranslateToPeer(Point point, Size localScreen, Size remoteScreen, string peerDirection)
        {
            if (peerDirection == "Left")
            {
                return new Point(remoteScreen.Width - 4, Scale(point.Y, localScreen.Height, remoteScreen.Height));
            }

            if (peerDirection == "Right")
            {
                return new Point(4, Scale(point.Y, localScreen.Height, remoteScreen.Height));
            }

            if (peerDirection == "Up")
            {
                return new Point(Scale(point.X, localScreen.Width, remoteScreen.Width), remoteScreen.Height - 4);
            }

            if (peerDirection == "Down")
            {
                return new Point(Scale(point.X, localScreen.Width, remoteScreen.Width), 4);
            }

            return point;
        }

        private static int Scale(int value, int source, int target)
        {
            if (source <= 0)
            {
                return 0;
            }

            return value * target / source;
        }
    }
}
