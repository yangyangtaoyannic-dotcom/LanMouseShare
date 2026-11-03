using System;
using System.Diagnostics;
using System.Threading;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 控制流程协调器 - 统一管理跨屏控制的整个生命周期
    /// 职责：协调边缘检测、会话管理、输入转发等子控制器
    /// </summary>
    public sealed class ControlFlowController : IDisposable
    {
        private readonly ControlLinkMachine _linkMachine;
        private readonly EdgeDetectionController _edgeDetection;
        private readonly RemoteControlController _remoteControl;
        private readonly InputForwardingController _inputForwarding;
        private readonly Timer _heartbeatTimer;

        private string _localDeviceId;
        private string _peerDeviceId;
        private bool _paused;
        private bool _disposed;

        public event EventHandler<ControlStateChangedEventArgs> StateChanged;
        public event EventHandler<ControlBeginEventArgs> ControlBegin;
        public event EventHandler<ControlReleaseEventArgs> ControlReleased;
        public event EventHandler<EdgeSwitchEventArgs> EdgeSwitched;

        public ControlFlowController(string localDeviceId)
        {
            _localDeviceId = localDeviceId ?? throw new ArgumentNullException(nameof(localDeviceId));
            _linkMachine = new ControlLinkMachine(localDeviceId);
            _edgeDetection = new EdgeDetectionController();
            _remoteControl = new RemoteControlController();
            _inputForwarding = new InputForwardingController();

            // 连接子控制器事件
            _edgeDetection.EdgeReached += OnEdgeReached;
            _remoteControl.ControlAcquired += OnControlAcquired;
            _remoteControl.ControlLost += OnControlLost;

            // 心跳定时器 - 处理租约发送和超时检测
            _heartbeatTimer = new Timer(HeartbeatTick, null, 50, 50);
        }

        #region 公共接口

        /// <summary>
        /// 当前控制角色
        /// </summary>
        public ControlLinkRole CurrentRole => _linkMachine.Role;

        /// <summary>
        /// 当前世代号
        /// </summary>
        public long CurrentEpoch => _linkMachine.Epoch;

        /// <summary>
        /// 是否暂停
        /// </summary>
        public bool IsPaused => _paused;

        /// <summary>
        /// 是否应该抑制本地输入
        /// </summary>
        public bool ShouldSuppressLocalInput
        {
            get
            {
                var snapshot = _linkMachine.Snapshot(DateTime.UtcNow.Ticks);
                return snapshot.ShouldSuppressLocalInput;
            }
        }

        /// <summary>
        /// 是否应该注入远程输入
        /// </summary>
        public bool ShouldInjectRemoteInput
        {
            get
            {
                var snapshot = _linkMachine.Snapshot(DateTime.UtcNow.Ticks);
                return snapshot.ShouldInjectRemoteInput;
            }
        }

        /// <summary>
        /// 设置对端设备 ID
        /// </summary>
        public void SetPeerDeviceId(string deviceId)
        {
            _peerDeviceId = deviceId;
            _linkMachine.SetPeerDeviceId(deviceId);
            AppLog.Info("control.flow.peer", $"peer={deviceId}");
        }

        /// <summary>
        /// 设置暂停状态
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;

            _paused = paused;
            _linkMachine.SetPaused(paused, DateTime.UtcNow.Ticks);

            if (paused)
            {
                _remoteControl.ReleaseControl(ControlLinkReleaseReason.Paused);
                _inputForwarding.Stop();
            }

            AppLog.Info("control.flow.paused", $"paused={paused}");
            StateChanged?.Invoke(this, new ControlStateChangedEventArgs(CurrentRole, paused));
        }

        /// <summary>
        /// 处理本地鼠标移动（用于边缘检测）
        /// </summary>
        public void OnLocalMouseMove(DrawingPoint position, System.Drawing.Rectangle screenBounds)
        {
            if (_paused || CurrentRole != ControlLinkRole.Idle)
                return;

            // 委托给边缘检测控制器
            _edgeDetection.OnMouseMove(position, screenBounds);
        }

        /// <summary>
        /// 处理收到的远程输入
        /// </summary>
        public void OnRemoteInputReceived(InputEvent[] events)
        {
            if (!ShouldInjectRemoteInput)
            {
                AppLog.Debug("control.flow.input.ignored", $"role={CurrentRole} paused={_paused}");
                return;
            }

            // 通知状态机有远程输入到达（证明租约有效）
            _linkMachine.NoteRemoteInput(DateTime.UtcNow.Ticks);

            // 委托给输入转发控制器注入
            _inputForwarding.InjectRemoteInput(events);
        }

        /// <summary>
        /// 处理收到的控制租约
        /// </summary>
        public void OnLeaseReceived(ControlLease lease)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var roleChanged = _linkMachine.OnLease(lease, nowTicks);

            if (roleChanged)
            {
                AppLog.Info("control.flow.role.changed",
                    $"role={CurrentRole} epoch={lease.Epoch} owner={lease.OwnerDeviceId}");

                // 角色变为 Target，开始接收远程控制
                if (CurrentRole == ControlLinkRole.Target)
                {
                    _remoteControl.BecomeTarget(lease);
                }

                StateChanged?.Invoke(this, new ControlStateChangedEventArgs(CurrentRole, _paused));
            }
        }

        /// <summary>
        /// 处理收到的控制确认
        /// </summary>
        public void OnAckReceived(long epoch)
        {
            _linkMachine.OnAck(epoch, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// 请求释放控制
        /// </summary>
        public void RequestReleaseControl(ControlLinkReleaseReason reason)
        {
            _linkMachine.Release(reason, DateTime.UtcNow.Ticks);
            _remoteControl.ReleaseControl(reason);
            _inputForwarding.Stop();

            AppLog.Info("control.flow.released", $"reason={reason} epoch={CurrentEpoch}");
            ControlReleased?.Invoke(this, new ControlReleaseEventArgs(reason, CurrentEpoch));
        }

        /// <summary>
        /// 获取当前状态快照
        /// </summary>
        public ControlLinkSnapshot GetSnapshot()
        {
            return _linkMachine.Snapshot(DateTime.UtcNow.Ticks);
        }

        #endregion

        #region 私有方法

        private void OnEdgeReached(object sender, EdgeReachedEventArgs e)
        {
            // 边缘检测器报告到达边缘，尝试开始控制
            var nowTicks = DateTime.UtcNow.Ticks;

            if (_linkMachine.TryBeginControl(nowTicks, out var lease))
            {
                AppLog.Info("control.flow.begin",
                    $"direction={e.Direction} epoch={lease.Epoch} entry={e.EntryPoint}");

                // 通知远程控制器开始控制
                _remoteControl.BeginControl(lease, e.Direction, e.RemoteScreenSize);

                // 启动输入转发
                _inputForwarding.Start(e.Direction, e.RemoteScreenSize);

                ControlBegin?.Invoke(this, new ControlBeginEventArgs(
                    lease.Epoch, e.Direction, e.EntryPoint, e.RemoteScreenSize));
            }
            else
            {
                AppLog.Warn("control.flow.begin.denied",
                    $"role={CurrentRole} paused={_paused}");
            }
        }

        private void OnControlAcquired(object sender, EventArgs e)
        {
            EdgeSwitched?.Invoke(this, new EdgeSwitchEventArgs(true));
        }

        private void OnControlLost(object sender, ControlLostEventArgs e)
        {
            _linkMachine.Release(e.Reason, DateTime.UtcNow.Ticks);
            _inputForwarding.Stop();

            EdgeSwitched?.Invoke(this, new EdgeSwitchEventArgs(false));
        }

        private void HeartbeatTick(object state)
        {
            if (_disposed || _paused) return;

            var nowTicks = DateTime.UtcNow.Ticks;

            // 检查超时
            if (_linkMachine.Tick(nowTicks, out var reason))
            {
                AppLog.Warn("control.flow.timeout", $"reason={reason} role={CurrentRole}");
                RequestReleaseControl(reason);
            }

            // 发送租约（如果是 Owner）
            if (_linkMachine.ShouldSendLease(nowTicks))
            {
                var lease = _linkMachine.BuildLease(nowTicks);
                if (lease != null)
                {
                    _remoteControl.SendLease(lease);
                }
            }

            // 发送确认（如果是 Target）
            if (_linkMachine.ShouldSendAck(nowTicks))
            {
                _remoteControl.SendAck(CurrentEpoch);
                _linkMachine.MarkAckSent(nowTicks);
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _heartbeatTimer?.Dispose();
            _edgeDetection?.Dispose();
            _remoteControl?.Dispose();
            _inputForwarding?.Dispose();
        }
    }

    #region 事件参数类

    public class ControlStateChangedEventArgs : EventArgs
    {
        public ControlLinkRole Role { get; }
        public bool IsPaused { get; }

        public ControlStateChangedEventArgs(ControlLinkRole role, bool isPaused)
        {
            Role = role;
            IsPaused = isPaused;
        }
    }

    public class ControlBeginEventArgs : EventArgs
    {
        public long Epoch { get; }
        public string Direction { get; }
        public DrawingPoint EntryPoint { get; }
        public DrawingSize RemoteScreenSize { get; }

        public ControlBeginEventArgs(long epoch, string direction,
            DrawingPoint entryPoint, DrawingSize remoteScreenSize)
        {
            Epoch = epoch;
            Direction = direction;
            EntryPoint = entryPoint;
            RemoteScreenSize = remoteScreenSize;
        }
    }

    public class ControlReleaseEventArgs : EventArgs
    {
        public ControlLinkReleaseReason Reason { get; }
        public long Epoch { get; }

        public ControlReleaseEventArgs(ControlLinkReleaseReason reason, long epoch)
        {
            Reason = reason;
            Epoch = epoch;
        }
    }

    public class EdgeSwitchEventArgs : EventArgs
    {
        public bool SwitchedOut { get; }

        public EdgeSwitchEventArgs(bool switchedOut)
        {
            SwitchedOut = switchedOut;
        }
    }

    #endregion
}
