using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Core.Control
{
    public enum InputRouteAction
    {
        AllowLocal,
        ForwardToPeer,
        CheckEdgeSwitch,
        SuppressLocal,
        Ignore
    }

    public sealed class InputRoutingController
    {
        public InputRouteAction Route(InputPayload payload, ControlSessionSnapshot session)
        {
            if (payload == null || session == null || session.IsPaused || !session.IsConnected)
            {
                return InputRouteAction.Ignore;
            }

            if (session.IsTargetPeer)
            {
                if (payload.Kind == InputKinds.MouseMove)
                {
                    return InputRouteAction.SuppressLocal;
                }

                return InputRouteAction.ForwardToPeer;
            }

            if (payload.Kind == InputKinds.MouseMove)
            {
                return InputRouteAction.CheckEdgeSwitch;
            }

            return InputRouteAction.AllowLocal;
        }

        public bool ShouldInjectRemoteInput(ControlSessionSnapshot session)
        {
            return session != null && !session.IsPaused && session.IsConnected && session.IsTargetLocal;
        }
    }
}
