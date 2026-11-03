using System;

namespace LanMouseShare.Core.Control
{
    public enum ControlLinkRole
    {
        Idle = 0,
        Owner = 1,
        Target = 2
    }

    public enum ControlLinkReleaseReason
    {
        None = 0,
        AckTimeout = 1,
        LeaseExpired = 2,
        PeerReleased = 3,
        Arbitrated = 4,
        StopAll = 5,
        Disconnected = 6,
        Paused = 7,
        InjectionBlocked = 8,
        LocalRequest = 9,
        TargetRequestedRelease = 10
    }

    public sealed class ControlLease
    {
        public long Epoch { get; set; }
        public string OwnerDeviceId { get; set; }
        public int TtlMs { get; set; }
        public long Sequence { get; set; }
    }

    public sealed class ControlLinkSnapshot
    {
        public ControlLinkRole Role { get; set; }
        public long Epoch { get; set; }
        public string OwnerDeviceId { get; set; }
        public bool IsPaused { get; set; }
        public bool ShouldSuppressLocalInput { get; set; }
        public bool ShouldInjectRemoteInput { get; set; }
        public bool CanBeginControl { get; set; }
        public int LeaseAgeMs { get; set; }
        public int AckAgeMs { get; set; }

        public string Text()
        {
            return "role=" + Role +
                   " epoch=" + Epoch +
                   " owner=" + (string.IsNullOrEmpty(OwnerDeviceId) ? "-" : OwnerDeviceId) +
                   " leaseAge=" + LeaseAgeMs +
                   " ackAge=" + AckAgeMs +
                   " paused=" + (IsPaused ? "1" : "0");
        }
    }

    /// <summary>
    /// Single source of truth for the controller/agent relationship.
    /// See docs/CONTROL-STATE-MODEL.md for the normative specification.
    /// </summary>
    public sealed class ControlLinkMachine
    {
        public const int LeaseIntervalMs = 100;
        public const int AckIntervalMs = 200;

        // These budgets are deliberately generous. They are not the input deadline (input flows on
        // its own path); they only decide when a control session is considered dead. A short budget
        // turned every UI hiccup or Wi-Fi burst into a visible 700 ms freeze, so a session now
        // survives a couple of lost seconds and only dies when the peer is really gone.
        public const int LeaseTimeoutMs = 2500;
        public const int AckTimeoutMs = 2500;
        public const int LeaseTtlMs = 4000;

        private readonly object _sync = new object();
        private string _localDeviceId;
        private string _peerDeviceId;
        private ControlLinkRole _role = ControlLinkRole.Idle;
        private long _epoch;
        private long _lastSeenEpoch;
        private long _peerLeaseEpoch;
        private string _ownerDeviceId;
        private long _lastLeaseSentTicks;
        private long _lastLeaseSeenTicks;
        private long _lastAckSeenTicks;
        private long _claimStartedTicks;
        private long _sequence;
        private bool _paused;
        private long _staleMessages;

        public ControlLinkMachine(string localDeviceId)
        {
            _localDeviceId = localDeviceId ?? string.Empty;
        }

        public void SetLocalDeviceId(string deviceId)
        {
            lock (_sync)
            {
                _localDeviceId = deviceId ?? string.Empty;
            }
        }

        public void SetPeerDeviceId(string deviceId)
        {
            lock (_sync)
            {
                _peerDeviceId = deviceId ?? string.Empty;
            }
        }

        public void SetPaused(bool paused, long nowTicks)
        {
            lock (_sync)
            {
                if (_paused == paused)
                {
                    return;
                }

                _paused = paused;
                if (paused && _role != ControlLinkRole.Idle)
                {
                    ResetLocked(ControlLinkReleaseReason.Paused);
                }
            }
        }

        public bool TryBeginControl(long nowTicks, out ControlLease lease)
        {
            lock (_sync)
            {
                lease = null;
                if (!CanBeginControlLocked())
                {
                    return false;
                }

                // Epochs must be comparable across both PCs. A per-device counter is not: the PC that
                // just went idle keeps a low counter and can never win the arbitration against a peer
                // that accumulated a higher one, which made "switch back" impossible. UTC ticks give
                // every claim a shared, monotonically increasing stamp; the +1 keeps it strictly
                // increasing even if the local clock does not advance between calls.
                _epoch = Math.Max(Math.Max(Math.Max(_epoch, _lastSeenEpoch), _peerLeaseEpoch) + 1, nowTicks);
                _role = ControlLinkRole.Owner;
                _ownerDeviceId = _localDeviceId;
                _claimStartedTicks = nowTicks;
                _lastAckSeenTicks = 0;
                _lastLeaseSentTicks = 0;
                _sequence = 0;
                lease = new ControlLease
                {
                    Epoch = _epoch,
                    OwnerDeviceId = _ownerDeviceId,
                    TtlMs = LeaseTtlMs,
                    Sequence = ++_sequence
                };
                _lastLeaseSentTicks = nowTicks;
                return true;
            }
        }

        public ControlLease BuildLease(long nowTicks)
        {
            lock (_sync)
            {
                if (_role != ControlLinkRole.Owner || _paused)
                {
                    return null;
                }

                _sequence++;
                _lastLeaseSentTicks = nowTicks;
                return new ControlLease
                {
                    Epoch = _epoch,
                    OwnerDeviceId = _ownerDeviceId,
                    TtlMs = LeaseTtlMs,
                    Sequence = _sequence
                };
            }
        }

        /// <summary>
        /// Applies a lease received from the peer. Returns true when the local role changed.
        /// </summary>
        public bool OnLease(ControlLease lease, long nowTicks)
        {
            if (lease == null || string.IsNullOrEmpty(lease.OwnerDeviceId))
            {
                return false;
            }

            lock (_sync)
            {
                if (_paused)
                {
                    return false;
                }

                if (string.Equals(lease.OwnerDeviceId, _localDeviceId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (lease.Epoch < _peerLeaseEpoch)
                {
                    _staleMessages++;
                    return false;
                }

                if (_role == ControlLinkRole.Owner)
                {
                    if (!IsLeaseStronger(lease.Epoch, lease.OwnerDeviceId, _epoch, _ownerDeviceId))
                    {
                        return false;
                    }

                    ResetLocked(ControlLinkReleaseReason.Arbitrated);
                }

                var roleChanged = _role != ControlLinkRole.Target;
                _role = ControlLinkRole.Target;
                _epoch = Math.Max(_epoch, lease.Epoch);
                _lastSeenEpoch = Math.Max(_lastSeenEpoch, lease.Epoch);
                _peerLeaseEpoch = Math.Max(_peerLeaseEpoch, lease.Epoch);
                _ownerDeviceId = lease.OwnerDeviceId;
                _lastLeaseSeenTicks = nowTicks;
                return roleChanged;
            }
        }

        public void OnAck(long epoch, long nowTicks)
        {
            lock (_sync)
            {
                if (_role == ControlLinkRole.Owner && epoch == _epoch)
                {
                    _lastAckSeenTicks = nowTicks;
                }
            }
        }

        /// <summary>
        /// Data-plane liveness. An accepted input frame proves the owner is still driving this
        /// machine, so a briefly missing lease must never freeze injection.
        /// </summary>
        public void NoteRemoteInput(long nowTicks)
        {
            lock (_sync)
            {
                if (_role == ControlLinkRole.Target)
                {
                    _lastLeaseSeenTicks = nowTicks;
                }
            }
        }

        public void Release(ControlLinkReleaseReason reason, long nowTicks)
        {
            lock (_sync)
            {
                ResetLocked(reason);
            }
        }

        /// <summary>
        /// Timed housekeeping. Returns true when the role changed because of an expiry.
        /// </summary>
        public bool Tick(long nowTicks, out ControlLinkReleaseReason reason)
        {
            lock (_sync)
            {
                reason = ControlLinkReleaseReason.None;
                if (_paused || _role == ControlLinkRole.Idle)
                {
                    return false;
                }

                if (_role == ControlLinkRole.Owner)
                {
                    var baseline = _lastAckSeenTicks > 0 ? _lastAckSeenTicks : _claimStartedTicks;
                    if (nowTicks - baseline > MsToTicks(AckTimeoutMs))
                    {
                        reason = ControlLinkReleaseReason.AckTimeout;
                        ResetLocked(reason);
                        return true;
                    }

                    return false;
                }

                if (nowTicks - _lastLeaseSeenTicks > MsToTicks(LeaseTimeoutMs))
                {
                    reason = ControlLinkReleaseReason.LeaseExpired;
                    ResetLocked(reason);
                    return true;
                }

                return false;
            }
        }

        public bool ShouldSendLease(long nowTicks)
        {
            lock (_sync)
            {
                return _role == ControlLinkRole.Owner &&
                       !_paused &&
                       nowTicks - _lastLeaseSentTicks >= MsToTicks(LeaseIntervalMs);
            }
        }

        public bool ShouldSendAck(long nowTicks)
        {
            lock (_sync)
            {
                return _role == ControlLinkRole.Target &&
                       !_paused &&
                       nowTicks - _lastLeaseSeenTicks <= MsToTicks(LeaseTimeoutMs) &&
                       nowTicks - _lastAckSentTicks >= MsToTicks(AckIntervalMs);
            }
        }

        public void MarkAckSent(long nowTicks)
        {
            lock (_sync)
            {
                _lastAckSentTicks = nowTicks;
            }
        }

        public ControlLinkSnapshot Snapshot(long nowTicks)
        {
            lock (_sync)
            {
                var leaseAge = _lastLeaseSeenTicks > 0 ? (int)TicksToMs(nowTicks - _lastLeaseSeenTicks) : -1;
                var ackAge = _lastAckSeenTicks > 0 ? (int)TicksToMs(nowTicks - _lastAckSeenTicks) : -1;
                var leaseFresh = _lastLeaseSeenTicks > 0 && nowTicks - _lastLeaseSeenTicks <= MsToTicks(LeaseTimeoutMs);
                return new ControlLinkSnapshot
                {
                    Role = _role,
                    Epoch = _epoch,
                    OwnerDeviceId = _ownerDeviceId,
                    IsPaused = _paused,
                    ShouldSuppressLocalInput = _role == ControlLinkRole.Owner && !_paused,
                    ShouldInjectRemoteInput = _role == ControlLinkRole.Target && !_paused && leaseFresh,
                    CanBeginControl = CanBeginControlLocked(),
                    LeaseAgeMs = leaseAge,
                    AckAgeMs = ackAge
                };
            }
        }

        public ControlLinkRole Role
        {
            get { lock (_sync) { return _role; } }
        }

        public long Epoch
        {
            get { lock (_sync) { return _epoch; } }
        }

        public long StaleMessages
        {
            get { lock (_sync) { return _staleMessages; } }
        }

        public bool IsPaused
        {
            get { lock (_sync) { return _paused; } }
        }

        /// <summary>
        /// Deterministic arbitration: higher epoch wins; equal epoch falls back to the
        /// ordinal string comparison of the owner device id (larger wins).
        /// </summary>
        public static bool IsLeaseStronger(long theirEpoch, string theirOwner, long myEpoch, string myOwner)
        {
            if (theirEpoch != myEpoch)
            {
                return theirEpoch > myEpoch;
            }

            return string.CompareOrdinal(theirOwner ?? string.Empty, myOwner ?? string.Empty) > 0;
        }

        private bool CanBeginControlLocked()
        {
            return _role == ControlLinkRole.Idle &&
                   !_paused &&
                   !string.IsNullOrEmpty(_localDeviceId) &&
                   !string.IsNullOrEmpty(_peerDeviceId);
        }

        private void ResetLocked(ControlLinkReleaseReason reason)
        {
            _role = ControlLinkRole.Idle;
            _ownerDeviceId = null;
            _lastSeenEpoch = Math.Max(_lastSeenEpoch, _epoch);
            _lastLeaseSeenTicks = 0;
            _lastAckSeenTicks = 0;
            _claimStartedTicks = 0;
            _lastReleaseReason = reason;
        }

        private ControlLinkReleaseReason _lastReleaseReason = ControlLinkReleaseReason.None;

        public ControlLinkReleaseReason LastReleaseReason
        {
            get { lock (_sync) { return _lastReleaseReason; } }
        }

        private long _lastAckSentTicks;

        private static long MsToTicks(int ms)
        {
            return ms * TimeSpan.TicksPerMillisecond;
        }

        private static long TicksToMs(long ticks)
        {
            return ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
