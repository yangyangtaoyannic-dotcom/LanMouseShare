using System;
using System.Drawing;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 远程控制控制器 - 管理远程控制会话的生命周期
    /// 职责：发送租约、处理确认、维护控制权状态
    /// </summary>
    public sealed class RemoteControlController : IDisposable
    {
        private ControlLease _currentLease;
        private string _currentDirection;
        private Size _remoteScreenSize;
        private DateTime _sessionStartTime;
        private bool _isActive;

        public event EventHandler ControlAcquired;
        public event EventHandler<ControlLostEventArgs> ControlLost;

        /// <summary>
        /// 当前是否处于活跃控制状态
        /// </summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// 当前控制的方向
        /// </summary>
        public string CurrentDirection => _currentDirection;

        /// <summary>
        /// 远程屏幕尺寸
        /// </summary>
        public Size RemoteScreenSize => _remoteScreenSize;

        /// <summary>
        /// 开始控制（作为 Owner）
        /// </summary>
        public void BeginControl(ControlLease lease, string direction, Size remoteScreenSize)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            _currentLease = lease;
            _currentDirection = direction;
            _remoteScreenSize = remoteScreenSize;
            _sessionStartTime = DateTime.UtcNow;
            _isActive = true;

            AppLog.Info("remote.control.begin",
                $"epoch={lease.Epoch} direction={direction} remoteSize={remoteScreenSize.Width}x{remoteScreenSize.Height}");

            ControlAcquired?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 成为被控端（Target）
        /// </summary>
        public void BecomeTarget(ControlLease lease)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            _currentLease = lease;
            _sessionStartTime = DateTime.UtcNow;
            _isActive = true;

            AppLog.Info("remote.control.target",
                $"epoch={lease.Epoch} owner={lease.OwnerDeviceId}");
        }

        /// <summary>
        /// 发送控制租约
        /// </summary>
        public void SendLease(ControlLease lease)
        {
            if (lease == null) return;

            // 实际发送逻辑由外部组件处理（DirectInputChannel 和 PipeClient）
            // 这里只记录日志
            AppLog.Debug("remote.control.lease.send",
                $"epoch={lease.Epoch} seq={lease.Sequence} ttl={lease.TtlMs}");
        }

        /// <summary>
        /// 发送控制确认
        /// </summary>
        public void SendAck(long epoch)
        {
            // 实际发送逻辑由外部组件处理
            AppLog.Debug("remote.control.ack.send", $"epoch={epoch}");
        }

        /// <summary>
        /// 释放控制
        /// </summary>
        public void ReleaseControl(ControlLinkReleaseReason reason)
        {
            if (!_isActive) return;

            var duration = DateTime.UtcNow - _sessionStartTime;

            AppLog.Info("remote.control.release",
                $"reason={reason} duration={duration.TotalMilliseconds:F0}ms epoch={_currentLease?.Epoch ?? 0}");

            _isActive = false;
            _currentLease = null;
            _currentDirection = null;

            ControlLost?.Invoke(this, new ControlLostEventArgs(reason));
        }

        /// <summary>
        /// 获取会话持续时间
        /// </summary>
        public TimeSpan GetSessionDuration()
        {
            if (!_isActive)
                return TimeSpan.Zero;

            return DateTime.UtcNow - _sessionStartTime;
        }

        public void Dispose()
        {
            if (_isActive)
            {
                ReleaseControl(ControlLinkReleaseReason.Disconnected);
            }
        }
    }

    public class ControlLostEventArgs : EventArgs
    {
        public ControlLinkReleaseReason Reason { get; }

        public ControlLostEventArgs(ControlLinkReleaseReason reason)
        {
            Reason = reason;
        }
    }
}
