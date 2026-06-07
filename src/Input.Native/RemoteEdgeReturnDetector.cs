using System.Drawing;

namespace LanMouseShare.Input
{
    public sealed class RemoteEdgeReturnDetector
    {
        public RemoteEdgeReturnResult Update(Point point, Size screenSize, string direction, int deltaX, int deltaY, bool blocked, bool blockCorners, int cornerBlockPixels)
        {
            var edgeDistance = EdgePushSwitchDetector.DistanceToPhysicalEdge(point, screenSize, direction);
            var outwardDelta = GetOutwardDelta(deltaX, deltaY, direction);
            if (blocked)
            {
                return new RemoteEdgeReturnResult(false, false, outwardDelta, edgeDistance, "Blocked");
            }

            if (blockCorners && EdgeSwitchDetector.IsInBlockedCorner(point, screenSize, direction, cornerBlockPixels))
            {
                return new RemoteEdgeReturnResult(false, false, outwardDelta, edgeDistance, "BlockedCorner");
            }

            var atEdge = EdgePushSwitchDetector.IsAtPhysicalEdge(point, screenSize, direction);
            if (!atEdge)
            {
                return new RemoteEdgeReturnResult(false, false, outwardDelta, edgeDistance, "AwayFromEdge");
            }

            if (outwardDelta > 0)
            {
                return new RemoteEdgeReturnResult(true, true, outwardDelta, edgeDistance, "RealEdgeOutward");
            }

            if (outwardDelta <= 0)
            {
                return new RemoteEdgeReturnResult(false, true, outwardDelta, edgeDistance, "Armed");
            }

            return new RemoteEdgeReturnResult(false, true, outwardDelta, edgeDistance, "Armed");
        }

        public void Reset()
        {
        }

        private static int GetOutwardDelta(int deltaX, int deltaY, string direction)
        {
            if (direction == "Left")
            {
                return -deltaX;
            }

            if (direction == "Right")
            {
                return deltaX;
            }

            if (direction == "Up")
            {
                return -deltaY;
            }

            if (direction == "Down")
            {
                return deltaY;
            }

            return 0;
        }
    }

    public sealed class RemoteEdgeReturnResult
    {
        public RemoteEdgeReturnResult(bool shouldRelease, bool wasAtEdge, int outwardDelta, int edgeDistance, string reason)
        {
            ShouldRelease = shouldRelease;
            WasAtEdge = wasAtEdge;
            OutwardDelta = outwardDelta;
            EdgeDistance = edgeDistance;
            Reason = reason;
        }

        public bool ShouldRelease { get; private set; }
        public bool WasAtEdge { get; private set; }
        public int OutwardDelta { get; private set; }
        public int EdgeDistance { get; private set; }
        public string Reason { get; private set; }
    }
}
