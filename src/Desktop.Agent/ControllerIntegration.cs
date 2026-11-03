using System;
using System.Drawing;
using System.Windows;
using LanMouseShare.Agent.Controllers;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    /// <summary>
    /// MainWindow 的控制器集成包装器
    /// 负责在新旧实现之间切换
    /// </summary>
    public class ControllerIntegration : IDisposable
    {
        private readonly AppConfig _config;
        private readonly bool _useNewControllers;

        // 新控制器
        private ControlFlowController _controlFlow;
        private FileTransferManager _fileTransferManager;

        // 旧组件的引用（用于新实现）
        private readonly ControlLinkMachine _linkMachine;
        private readonly ControlSessionController _session;
        private readonly InputRoutingController _inputRouter;

        public event EventHandler<ControlStateChangedEventArgs> StateChanged;
        public event EventHandler<string> StatusMessageChanged;

        public ControllerIntegration(
            AppConfig config,
            string localDeviceId,
            ControlLinkMachine linkMachine,
            ControlSessionController session,
            InputRoutingController inputRouter)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _useNewControllers = config.UseNewControllers;
            _linkMachine = linkMachine;
            _session = session;
            _inputRouter = inputRouter;

            if (_useNewControllers)
            {
                InitializeNewControllers(localDeviceId);
                AppLog.Info("controller.integration.mode", "mode=new_controllers");
            }
            else
            {
                AppLog.Info("controller.integration.mode", "mode=legacy");
            }
        }

        public bool UseNewControllers => _useNewControllers;

        #region 新控制器初始化

        private void InitializeNewControllers(string localDeviceId)
        {
            _controlFlow = new ControlFlowController(localDeviceId);
            _fileTransferManager = new FileTransferManager();

            // 连接事件
            _controlFlow.StateChanged += OnNewControllerStateChanged;
            _controlFlow.ControlBegin += OnNewControllerBegin;
            _controlFlow.ControlReleased += OnNewControllerReleased;

            _fileTransferManager.TransferProgress += OnFileTransferProgress;
            _fileTransferManager.TransferCompleted += OnFileTransferCompleted;
            _fileTransferManager.TransferError += OnFileTransferError;
        }

        #endregion

        #region 统一接口 - 鼠标移动处理

        public void HandleMouseMove(System.Drawing.Point position, Rectangle screenBounds)
        {
            if (_useNewControllers)
            {
                _controlFlow?.OnLocalMouseMove(position, screenBounds);
            }
            else
            {
                // 旧实现会在 MainWindow 中直接处理
                // 这里不做任何操作
            }
        }

        #endregion

        #region 统一接口 - 远程输入处理

        public void HandleRemoteInput(object[] rawEvents)
        {
            if (_useNewControllers)
            {
                var events = ConvertToInputEvents(rawEvents);
                _controlFlow?.OnRemoteInputReceived(events);
            }
            else
            {
                // 旧实现会在 MainWindow 中直接处理
            }
        }

        private InputEvent[] ConvertToInputEvents(object[] rawEvents)
        {
            // TODO: 根据实际的事件格式转换
            // 这里需要根据 DirectInputBatchEventArgs 的实际结构来实现
            return Array.Empty<InputEvent>();
        }

        #endregion

        #region 统一接口 - 控制权管理

        public void SetPeerDeviceId(string deviceId)
        {
            if (_useNewControllers)
            {
                _controlFlow?.SetPeerDeviceId(deviceId);
            }
            else
            {
                _linkMachine?.SetPeerDeviceId(deviceId);
            }
        }

        public void SetPaused(bool paused)
        {
            if (_useNewControllers)
            {
                _controlFlow?.SetPaused(paused);
            }
            else
            {
                _linkMachine?.SetPaused(paused, DateTime.UtcNow.Ticks);
            }
        }

        public void RequestReleaseControl(ControlLinkReleaseReason reason)
        {
            if (_useNewControllers)
            {
                _controlFlow?.RequestReleaseControl(reason);
            }
            else
            {
                _linkMachine?.Release(reason, DateTime.UtcNow.Ticks);
            }
        }

        public bool ShouldSuppressLocalInput
        {
            get
            {
                if (_useNewControllers)
                {
                    return _controlFlow?.ShouldSuppressLocalInput ?? false;
                }
                else
                {
                    var snapshot = _linkMachine?.Snapshot(DateTime.UtcNow.Ticks);
                    return snapshot?.ShouldSuppressLocalInput ?? false;
                }
            }
        }

        public bool ShouldInjectRemoteInput
        {
            get
            {
                if (_useNewControllers)
                {
                    return _controlFlow?.ShouldInjectRemoteInput ?? false;
                }
                else
                {
                    var snapshot = _linkMachine?.Snapshot(DateTime.UtcNow.Ticks);
                    return snapshot?.ShouldInjectRemoteInput ?? false;
                }
            }
        }

        public ControlLinkRole CurrentRole
        {
            get
            {
                if (_useNewControllers)
                {
                    return _controlFlow?.CurrentRole ?? ControlLinkRole.Idle;
                }
                else
                {
                    return _linkMachine?.Role ?? ControlLinkRole.Idle;
                }
            }
        }

        #endregion

        #region 统一接口 - 文件传输

        public async System.Threading.Tasks.Task<string> BeginFileTransferAsync(string[] filePaths, string peerDeviceId)
        {
            if (_useNewControllers)
            {
                return await _fileTransferManager.BeginTransferAsync(filePaths, peerDeviceId);
            }
            else
            {
                // 旧实现使用 FileDropSender
                // 返回一个假的 ID
                return Guid.NewGuid().ToString("N");
            }
        }

        public void CancelAllTransfers()
        {
            if (_useNewControllers)
            {
                _fileTransferManager?.CancelAllTransfers();
            }
        }

        #endregion

        #region 事件处理

        private void OnNewControllerStateChanged(object sender, ControlStateChangedEventArgs e)
        {
            AppLog.Info("controller.integration.state_changed",
                $"role={e.Role} paused={e.IsPaused}");

            StateChanged?.Invoke(this, e);

            // 更新状态消息
            var statusMsg = e.IsPaused ? "已暂停" : GetRoleStatusMessage(e.Role);
            StatusMessageChanged?.Invoke(this, statusMsg);
        }

        private void OnNewControllerBegin(object sender, ControlBeginEventArgs e)
        {
            AppLog.Info("controller.integration.begin",
                $"epoch={e.Epoch} direction={e.Direction}");

            StatusMessageChanged?.Invoke(this, $"正在控制远端 (方向: {e.Direction})");
        }

        private void OnNewControllerReleased(object sender, ControlReleaseEventArgs e)
        {
            AppLog.Info("controller.integration.released",
                $"reason={e.Reason} epoch={e.Epoch}");

            StatusMessageChanged?.Invoke(this, "控制已释放");
        }

        private void OnFileTransferProgress(object sender, FileTransferProgressEventArgs e)
        {
            var percentage = e.FileTotalBytes > 0
                ? (double)e.FileBytes / e.FileTotalBytes * 100
                : 0;

            AppLog.Debug("controller.integration.file_progress",
                $"file={e.FileName} progress={percentage:F1}%");

            StatusMessageChanged?.Invoke(this,
                $"传输中: {e.FileName} ({percentage:F0}%)");
        }

        private void OnFileTransferCompleted(object sender, FileTransferCompletedEventArgs e)
        {
            AppLog.Info("controller.integration.file_completed",
                $"id={e.TransferId} files={e.FilesCount} bytes={e.TotalBytes}");

            StatusMessageChanged?.Invoke(this,
                $"传输完成: {e.FilesCount} 个文件");
        }

        private void OnFileTransferError(object sender, FileTransferErrorEventArgs e)
        {
            AppLog.Error("controller.integration.file_error",
                $"id={e.TransferId} error={e.Error}");

            StatusMessageChanged?.Invoke(this, $"传输失败: {e.Error}");
        }

        private string GetRoleStatusMessage(ControlLinkRole role)
        {
            switch (role)
            {
                case ControlLinkRole.Idle:
                    return "空闲";
                case ControlLinkRole.Owner:
                    return "正在控制远端";
                case ControlLinkRole.Target:
                    return "被远端控制";
                default:
                    return "未知状态";
            }
        }

        #endregion

        public void Dispose()
        {
            if (_useNewControllers)
            {
                _controlFlow?.Dispose();
                _fileTransferManager?.Dispose();
            }
        }
    }
}
