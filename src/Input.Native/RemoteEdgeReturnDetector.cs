using System;
using System.Drawing;

namespace LanMouseShare.Input
{
    /// <summary>
    /// Detects the user pushing the pointer through the entry edge on the target screen so
    /// control can be handed back to the controller.
    ///
    /// The detector is deliberately stateful:
    ///  * a single injected step at the edge never releases (the pointer is clamped by Windows
    ///    there, so one step is normal and used to force control back to the controller by accident);
    ///  * only a sustained outward push (accumulated inside a short window) counts;
    ///  * after a release the detector stays blocked for a moment so the next injected step cannot
    ///    immediately fire again.
    /// </summary>
    public sealed class RemoteEdgeReturnDetector
    {
        /// <summary>Outward distance in pixels that must be pushed through the edge.</summary>
        public const int PushThreshold = 30;

        /// <summary>
        /// The pointer must have travelled this far away from the entry edge at least once before a
        /// push back through it counts. Without arming, the injected cursor starts a few pixels from
        /// the edge and the first small leftward move immediately "returned", so the user had to
        /// cross twice to keep control.
        /// </summary>
        public const int ArmDistance = 40;

        /// <summary>Only pushes that arrive within this window are accumulated.</summary>
        public const int PushWindowMs = 250;

        /// <summary>A single sample never counts as a deliberate push; at least two are required.</summary>
        public const int MinPushSamples = 2;

        /// <summary>After a release or a reset this long is ignored, which prevents ping-pong.</summary>
        public const int CooldownMs = 500;

        private int _push;
        private int _pushSamples;
        private long _lastPushTicks;
        private long _blockedUntilTicks;
        private bool _armed;

        public RemoteEdgeReturnResult Update(Point point, Size screenSize, string direction, int deltaX, int deltaY, bool blocked, bool blockCorners, int cornerBlockPixels)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
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

            if (nowTicks < _blockedUntilTicks)
            {
                _push = 0;
                return new RemoteEdgeReturnResult(false, false, outwardDelta, edgeDistance, "Cooldown");
            }

            var atEdge = EdgePushSwitchDetector.IsAtPhysicalEdge(point, screenSize, direction);
            if (!atEdge)
            {
                _push = 0;
                if (edgeDistance >= ArmDistance)
                {
                    _armed = true;
                }

                return new RemoteEdgeReturnResult(false, false, outwardDelta, edgeDistance, "AwayFromEdge");
            }

            if (!_armed)
            {
                _push = 0;
                return new RemoteEdgeReturnResult(false, true, outwardDelta, edgeDistance, "Disarmed");
            }

            if (outwardDelta <= 0)
            {
                _push = 0;
                return new RemoteEdgeReturnResult(false, true, outwardDelta, edgeDistance, "Armed");
            }

            if ((nowTicks - _lastPushTicks) / TimeSpan.TicksPerMillisecond > PushWindowMs)
            {
                _push = 0;
                _pushSamples = 0;
            }

            _push += outwardDelta;
            _pushSamples++;
            _lastPushTicks = nowTicks;
            if (_push < PushThreshold || _pushSamples < MinPushSamples)
            {
                return new RemoteEdgeReturnResult(false, true, outwardDelta, edgeDistance, "Armed");
            }

            BlockForCooldown(nowTicks);
            return new RemoteEdgeReturnResult(true, true, outwardDelta, edgeDistance, "EdgePush");
        }

        /// <summary>Clears the accumulated push and keeps the detector quiet for the cooldown.</summary>
        public void Reset()
        {
            BlockForCooldown(DateTime.UtcNow.Ticks);
        }

        private void BlockForCooldown(long nowTicks)
        {
            _push = 0;
            _pushSamples = 0;
            _lastPushTicks = 0;
            _armed = false;
            _blockedUntilTicks = nowTicks + (CooldownMs * TimeSpan.TicksPerMillisecond);
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
