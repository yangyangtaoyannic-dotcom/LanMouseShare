using System;
using System.Net.Sockets;

namespace LanMouseShare.Core.Networking
{
    /// <summary>
    /// Best-effort DSCP marking for the sockets that carry input, log and file traffic.
    /// Windows only schedules by DSCP when a QoS policy exists, so these calls are an
    /// optimisation, never a guarantee. They never throw.
    /// </summary>
    public static class TrafficPriority
    {
        /// <summary>DSCP Expedited Forwarding (46) - the interactive input channel.</summary>
        public const int InteractiveDscp = 46;

        /// <summary>DSCP CS1 (8) - bulk logging, first to be deferred by the network.</summary>
        public const int BulkDscp = 8;

        public static bool TryMarkInteractive(Socket socket)
        {
            return TryMark(socket, InteractiveDscp);
        }

        public static bool TryMarkBulk(Socket socket)
        {
            return TryMark(socket, BulkDscp);
        }

        public static bool TryMark(Socket socket, int dscp)
        {
            if (socket == null)
            {
                return false;
            }

            if (dscp < 0)
            {
                dscp = 0;
            }

            if (dscp > 63)
            {
                dscp = 63;
            }

            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, dscp << 2);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
