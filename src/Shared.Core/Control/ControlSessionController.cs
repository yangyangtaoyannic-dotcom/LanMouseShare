using System;
using LanMouseShare.Core.Ipc;

namespace LanMouseShare.Core.Control
{
    public sealed class ControlSessionController
    {
        private string _localDeviceId;
        private string _peerDeviceId;
        private string _activeTargetDeviceId;
        private bool _paused;

        public void ApplyState(PeerStateDto state)
        {
            if (state == null)
            {
                _localDeviceId = null;
                _peerDeviceId = null;
                _activeTargetDeviceId = null;
                return;
            }

            _localDeviceId = state.LocalDeviceId;
            _peerDeviceId = state.PeerDeviceId;
            _activeTargetDeviceId = string.IsNullOrWhiteSpace(state.ActiveTargetDeviceId)
                ? state.LocalDeviceId
                : state.ActiveTargetDeviceId;
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                _activeTargetDeviceId = _localDeviceId;
            }
        }

        public void SetLocalTarget()
        {
            _activeTargetDeviceId = _localDeviceId;
        }

        public void SetPeerTarget()
        {
            if (!string.IsNullOrWhiteSpace(_peerDeviceId))
            {
                _activeTargetDeviceId = _peerDeviceId;
            }
        }

        public void SetTarget(string deviceId)
        {
            _activeTargetDeviceId = string.IsNullOrWhiteSpace(deviceId) ? _localDeviceId : deviceId;
        }

        public bool IsPaused
        {
            get { return _paused; }
        }

        public string LocalDeviceId
        {
            get { return _localDeviceId; }
        }

        public string PeerDeviceId
        {
            get { return _peerDeviceId; }
        }

        public string ActiveTargetDeviceId
        {
            get { return string.IsNullOrWhiteSpace(_activeTargetDeviceId) ? _localDeviceId : _activeTargetDeviceId; }
        }

        public bool IsTargetLocal
        {
            get
            {
                return string.IsNullOrWhiteSpace(ActiveTargetDeviceId) ||
                       string.Equals(ActiveTargetDeviceId, _localDeviceId, StringComparison.Ordinal);
            }
        }

        public bool IsTargetPeer
        {
            get
            {
                return !string.IsNullOrWhiteSpace(_peerDeviceId) &&
                       string.Equals(ActiveTargetDeviceId, _peerDeviceId, StringComparison.Ordinal);
            }
        }

        public ControlSessionSnapshot Snapshot(PeerStateDto state)
        {
            return new ControlSessionSnapshot
            {
                IsPaused = _paused,
                IsConnected = state != null && state.IsConnected && state.IsTrusted && !state.IsPairingMismatch,
                LocalDeviceId = _localDeviceId,
                PeerDeviceId = _peerDeviceId,
                ActiveTargetDeviceId = ActiveTargetDeviceId
            };
        }
    }

    public sealed class ControlSessionSnapshot
    {
        public bool IsPaused { get; set; }
        public bool IsConnected { get; set; }
        public string LocalDeviceId { get; set; }
        public string PeerDeviceId { get; set; }
        public string ActiveTargetDeviceId { get; set; }

        public bool IsTargetLocal
        {
            get
            {
                return string.IsNullOrWhiteSpace(ActiveTargetDeviceId) ||
                       string.Equals(ActiveTargetDeviceId, LocalDeviceId, StringComparison.Ordinal);
            }
        }

        public bool IsTargetPeer
        {
            get
            {
                return !string.IsNullOrWhiteSpace(PeerDeviceId) &&
                       string.Equals(ActiveTargetDeviceId, PeerDeviceId, StringComparison.Ordinal);
            }
        }
    }
}
