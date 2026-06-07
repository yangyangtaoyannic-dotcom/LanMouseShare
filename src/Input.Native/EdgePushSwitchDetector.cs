using System;
using System.Drawing;

namespace LanMouseShare.Input
{
    public sealed class EdgePushSwitchDetector
    {
        private const int EdgeContactTolerancePixels = 2;

        private bool _armed;
        private string _lastDirection;
        private int _accumulatedPush;

        public EdgePushSwitchDetector(int requiredPushPixels)
        {
            RequiredPushPixels = requiredPushPixels <= 0 ? 12 : requiredPushPixels;
        }

        public int RequiredPushPixels { get; private set; }

        public bool IsArmed
        {
            get { return _armed; }
        }

        public EdgePushResult Update(Point point, Point previousPoint, Size screenSize, string direction, bool blocked)
        {
            if (blocked)
            {
                return Reset("Blocked");
            }

            if (_lastDirection != direction)
            {
                _armed = false;
                _accumulatedPush = 0;
                _lastDirection = direction;
            }

            var previousDistance = DistanceToPhysicalEdge(previousPoint, screenSize, direction);
            var currentDistance = DistanceToPhysicalEdge(point, screenSize, direction);

            if (!IsAtPhysicalEdge(point, screenSize, direction))
            {
                if (_armed)
                {
                    _armed = false;
                    _accumulatedPush = 0;
                    return new EdgePushResult(false, false, 0, 0, "LeftEdge", previousDistance, currentDistance, false);
                }
                return new EdgePushResult(false, false, 0, 0, "AwayFromEdge", previousDistance, currentDistance, false);
            }

            var delta = GetOutwardDelta(point, previousPoint, direction);

            if (!_armed)
            {
                if (previousDistance > EdgeContactTolerancePixels)
                {
                    _accumulatedPush = 0;
                    return new EdgePushResult(false, false, delta, 0, "SkippedToEdge", previousDistance, currentDistance, true);
                }

                _armed = true;
                _accumulatedPush = Math.Max(0, delta + Math.Min(Math.Max(previousDistance, 0), EdgeContactTolerancePixels));
                if (_accumulatedPush >= RequiredPushPixels)
                {
                    int pushed = _accumulatedPush;
                    _armed = false;
                    _accumulatedPush = 0;
                    return new EdgePushResult(true, true, delta, pushed, "EdgeContactConfirmed", previousDistance, currentDistance, false);
                }
                return new EdgePushResult(false, true, delta, _accumulatedPush, "Armed", previousDistance, currentDistance, previousDistance > EdgeContactTolerancePixels);
            }

            if (delta > 0)
            {
                _accumulatedPush += delta;
                if (_accumulatedPush >= RequiredPushPixels)
                {
                    int pushed = _accumulatedPush;
                    _armed = false;
                    _accumulatedPush = 0;
                    return new EdgePushResult(true, true, delta, pushed, "EdgePushConfirmed", previousDistance, currentDistance, false);
                }
                return new EdgePushResult(false, true, delta, _accumulatedPush, "Pushing", previousDistance, currentDistance, false);
            }

            if (delta < 0)
            {
                _accumulatedPush = 0;
                return new EdgePushResult(false, true, delta, 0, "Reversed", previousDistance, currentDistance, false);
            }

            return new EdgePushResult(false, true, 0, _accumulatedPush, "Armed", previousDistance, currentDistance, false);
        }

        public int AccumulateRawDelta(int rawDeltaX, int rawDeltaY, string direction)
        {
            if (!_armed)
            {
                return 0;
            }

            var outward = GetOutwardRawDelta(rawDeltaX, rawDeltaY, direction);
            if (outward > 0)
            {
                _accumulatedPush += outward;
            }
            else if (outward < 0)
            {
                _accumulatedPush = 0;
            }

            return _accumulatedPush;
        }

        public EdgePushResult Reset(string reason)
        {
            _armed = false;
            _accumulatedPush = 0;
            return new EdgePushResult(false, false, 0, 0, reason);
        }

        private static int GetOutwardRawDelta(int deltaX, int deltaY, string direction)
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

        public static bool IsAtPhysicalEdge(Point point, Size screenSize, string direction)
        {
            if (direction == "Left")
            {
                return point.X <= 0;
            }

            if (direction == "Right")
            {
                return point.X >= screenSize.Width - 1;
            }

            if (direction == "Up")
            {
                return point.Y <= 0;
            }

            if (direction == "Down")
            {
                return point.Y >= screenSize.Height - 1;
            }

            return false;
        }

        public static int DistanceToPhysicalEdge(Point point, Size screenSize, string direction)
        {
            if (direction == "Left")
            {
                return point.X;
            }

            if (direction == "Right")
            {
                return screenSize.Width - 1 - point.X;
            }

            if (direction == "Up")
            {
                return point.Y;
            }

            if (direction == "Down")
            {
                return screenSize.Height - 1 - point.Y;
            }

            return int.MaxValue;
        }

        private static int GetOutwardDelta(Point point, Point previousPoint, string direction)
        {
            if (direction == "Left")
            {
                return previousPoint.X - point.X;
            }

            if (direction == "Right")
            {
                return point.X - previousPoint.X;
            }

            if (direction == "Up")
            {
                return previousPoint.Y - point.Y;
            }

            if (direction == "Down")
            {
                return point.Y - previousPoint.Y;
            }

            return -1;
        }
    }

    public sealed class EdgePushResult
    {
        public EdgePushResult(
            bool shouldSwitch,
            bool wasArmed,
            int delta,
            int accumulatedPushPixels,
            string reason,
            int previousEdgeDistance = int.MaxValue,
            int currentEdgeDistance = int.MaxValue,
            bool skippedToEdge = false)
        {
            ShouldSwitch = shouldSwitch;
            WasArmed = wasArmed;
            Delta = delta;
            AccumulatedPushPixels = accumulatedPushPixels;
            Reason = reason;
            PreviousEdgeDistance = previousEdgeDistance;
            CurrentEdgeDistance = currentEdgeDistance;
            SkippedToEdge = skippedToEdge;
        }

        public bool ShouldSwitch { get; private set; }
        public bool WasArmed { get; private set; }
        public int Delta { get; private set; }
        public int AccumulatedPushPixels { get; private set; }
        public string Reason { get; private set; }
        public int PreviousEdgeDistance { get; private set; }
        public int CurrentEdgeDistance { get; private set; }
        public bool SkippedToEdge { get; private set; }
    }
}
