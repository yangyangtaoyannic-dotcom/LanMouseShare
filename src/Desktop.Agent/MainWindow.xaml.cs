using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Protocol;
using LanMouseShare.Input;
using Forms = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace LanMouseShare.Agent
{
    public partial class MainWindow : Window
    {
        private const string AppVersion = "0.2.0-beta-fix19";
        private const int CornerBlockPixels = 80;

        // fix15：边角阻挡带以前是固定 80 像素，但两台机器的分辨率差很多（2880x1920 对
        // 1920x1080），同一个 80px 在高分辨率那一端只占屏幕高度的 4%，看着就像根本没
        // 有阻挡；再加上返回路径上的阻挡曾被删掉，于是用户的感觉是"只有低分辨率那一端
        // 能做边角阻挡"。改成按屏幕尺寸取比例，80 像素退化成下限。
        private const double CornerBlockRatio = 0.075;

        private readonly PipeClient _pipe = new PipeClient();
        private readonly InputHook _hook = new InputHook();
        private readonly InputInjector _injector = new InputInjector();
        private readonly EdgeSwitchDetector _edgeSwitchDetector = new EdgeSwitchDetector(0);
        private readonly RemoteEdgeReturnDetector _remoteReturnDetector = new RemoteEdgeReturnDetector();
        private readonly ControlSessionController _session = new ControlSessionController();
        private readonly InputRoutingController _inputRouter = new InputRoutingController();
        private readonly InjectionFailureTracker _injectionFailures = new InjectionFailureTracker();
        private readonly DispatcherTimer _pollTimer = new DispatcherTimer();
        private readonly DispatcherTimer _stateTimer = new DispatcherTimer();
        private readonly DispatcherTimer _uiWatchdog = new DispatcherTimer();
        private readonly HashSet<string> _ignoredConflictProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private RemoteInputSender _remoteInputSender;
        private DirectInputChannel _directInputChannel;
        private RawMouseInputMonitor _rawMouseInput;
        private ClipboardSyncService _clipboardSync;

        // fix16.1：剪贴板开关的线程安全镜像。ClipboardEnabledBox.IsChecked 是 WPF 依赖属性，
        // 只有 UI 线程能读 —— 从剪贴板线程或管道线程读会抛 InvalidOperationException，
        // 而且在剪贴板线程上抛就是整个进程崩（0.2.0-beta-fix18 复制文本必崩的根因）。
        // 所以开关状态一律先镜像到这个 volatile 字段，任何线程只读它。
        private volatile bool _clipboardEnabledCache = true;
        private FileDropSender _fileDropSender;
        private LanMouseShare.Agent.FileLink.FileLinkHub _fileLink;
        private const string ServiceProcessName = "LanMouseShare.Service";
        private Process _managedServiceProcess;
        private int _pollFailures;
        private long _lastServiceRecoveryTicks;
        private int _serviceRecoveries;
        private Forms.NotifyIcon _trayIcon;
        private PeerStateDto _state;
        private bool _pollInFlight;
        private bool _stateInFlight;
        private bool _settingsLoaded;
        private bool _applyingSettingsToUi;
        private bool _exitRequested;
        private bool _shutdownStarted;
        private bool _minimizeToTrayOnClose;
        private bool _blockScreenCorners;
        private bool _startWithWindows;
        private bool _runAsAdministratorByDefault;
        private DateTime _lastEdgeTraceUtc = DateTime.MinValue;
        private System.Drawing.Rectangle _localScreenBounds;
        private DrawingPoint _lastLocalMousePoint;
        private bool _hasLastLocalMousePoint;
        private int _remoteVirtualX;
        private int _remoteVirtualY;
        private DrawingSize _remoteVirtualScreen;
        private double _mouseSpeedMultiplier = 1.0;
        private int _mouseUpdateRateHz = 200;
        private string _pauseHotkey = "Ctrl+F11";
        private string _stopControlHotkey = "Ctrl+F10";
        private string _exitHotkey = "Ctrl+Alt+End";
        private string _shareRole = ShareRoleValues.Controller;
        private string _controllerAddress = string.Empty;
        private string _debugToken = string.Empty;
        private int _logRetentionDays = 3;
        private bool _verboseLogging;
        private bool _logTransferEnabled = true;
        private bool _edgeSwitchArmed = true;
        private DateTime _edgeSwitchLockUntilUtc = DateTime.MinValue;
        private DateTime _lastMousePositionLogUtc = DateTime.MinValue;
        private DateTime _lastRemoteInjectLogUtc = DateTime.MinValue;
        private string _lastLocalScreenKey = string.Empty;
        private DateTime _lastIgnoredInputLogUtc = DateTime.MinValue;
        private int _ignoredInputCount;
        private readonly HashSet<int> _injectedHeldKeys = new HashSet<int>();
        private DateTime _lastKeyLogUtc = DateTime.MinValue;
        private DateTime _lastBoundaryLogUtc = DateTime.MinValue;
        private int _lastInjectedMouseX = -1;
        private int _lastInjectedMouseY = -1;
        private DateTime _targetEnteredUtc = DateTime.MinValue;
        private DateTime _ownerSinceUtc = DateTime.MinValue;
        private DateTime _lastUiTickUtc = DateTime.MinValue;
        private double _lastUiGapMs;
        private DateTime _uiStallLogUntilUtc = DateTime.MinValue;
        private const int EdgeSwitchCooldownMs = 400;
        /// <summary>
        /// The pointer must come back this far inside the screen before the edge switch can fire
        /// again. With a small value the cursor was re-armed as soon as it returned 12px inside the
        /// edge, so a return immediately bounced back out again (the "rubbing the edge" report).
        /// </summary>
        private const int EdgeArmDistance = 80;
        /// <summary>Ignore return-to-controller requests for this long after becoming the target.</summary>
        private const int TargetReturnGraceMs = 600;
        /// <summary>A single injected sample may contribute at most this much outward push.</summary>
        private const int MaxReturnSamplePixels = 60;

        // Owner-side return detection: pushing the physical mouse through the peer's return
        // edge is accumulated here so the return is instant instead of "rubbing for seconds".
        private const int OwnerReturnPushThreshold = 140;
        private const int MaxDeltaPerEvent = 120;
        private const int OwnerReturnArmMs = 400;
        private const int OwnerReturnPushWindowMs = 320;
        // fix19：跨屏到对端后指针落在离返回边只有 8px 的位置（EdgeTransitionMapper.RemoteInset），
        // 用户在左缘附近做一次快速左扫（实测 254px→0 只用 56ms）就会被旧逻辑判成"要回去"，
        // 控制权被立刻收回本机，随后所有点击都在本机执行——这才是"右键菜单弹在 A 上"的真正原因。
        // 现在要求：先真正进到对端 ≥40px（武装）、在返回边连续驻留 ≥260ms、累计推力 ≥140px
        // 才允许回切；回切后 700ms 冷却，避免"回去—又进来—又回去"的乒乓。
        private const int OwnerReturnArmDistance = 40;
        private const int OwnerReturnDwellMs = 260;
        private const int OwnerReturnCooldownMs = 700;
        private const int PasteIdleTimeoutSeconds = 30;
        private const int PasteMaxSeconds = 1800;
        private int _ownerReturnPush;
        private DateTime _ownerReturnPushUtc = DateTime.MinValue;
        private bool _ownerReturnArmed;
        private DateTime _ownerReturnEdgeSinceUtc = DateTime.MinValue;
        private DateTime _ownerReturnCooldownUntilUtc = DateTime.MinValue;
        private Thread _linkThread;
        private volatile bool _linkRunning;
        private ControlLinkMachine _link = new ControlLinkMachine(string.Empty);
        private ControlLinkRole _lastLinkRole = ControlLinkRole.Idle;
        private long _lastLeaseLogTicks;
        private long _lastStaleLogged; 
        private long _lastRelayLeaseTicks;
        private bool _pastingFiles;
        private bool _forwardedCtrlDown;
        private bool _swallowPasteKeyUp;
        private bool _replayPasteKey = true;
        private string _lastPrefetchSignature = string.Empty;
        private bool _startupOptionsReapplied;
        private int _pendingPasteExpected;
        private readonly List<string> _pendingPasteFiles = new List<string>();
        private DateTime _pendingPasteStartedUtc = DateTime.MinValue;
        private DateTime _pendingPasteLastProgressUtc = DateTime.MinValue;
        private DispatcherTimer _pasteTimeoutTimer;
        private int _lastRemoteFileCount;
        private string _lastTransferText = string.Empty;
        private DateTime _lastTransferProgressUtc = DateTime.MinValue;
        private string _fileLinkProgressName = string.Empty;
        private int _fileLinkProgressTick;
        private long _fileLinkProgressBytes;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            VersionText.Text = "LanMouseShare " + AppVersion;
            UpdatePauseButton();
            UpdateControlTargetText();
            AppLog.Info("Agent window loaded. Version=" + AppVersion);

            // 检查是否需要静默启动
            var app = Application.Current as App;
            if (app != null && app.StartMinimized)
            {
                Hide();
                WindowState = WindowState.Minimized;
                AppLog.Event("INFO", "window.minimized", "reason=startup_parameter");
            }

            EnsureLocalServiceAvailable();

            try
            {
                _directInputChannel = new DirectInputChannel();
                _directInputChannel.InputBatchReceived += OnDirectInputBatchReceived;
                _directInputChannel.ControlLeaseReceived += OnControlLeaseReceived;
                _directInputChannel.ControlAckReceived += OnControlAckReceived;
                _directInputChannel.ClipboardFilesReceived += OnClipboardFilesAnnounced;
            }
            catch (Exception ex)
            {
                AppLog.Error("Direct input channel unavailable; falling back to service input.", ex);
            }

            _remoteInputSender = new RemoteInputSender(_pipe, _directInputChannel);
            _remoteInputSender.SetMouseUpdateRate(_mouseUpdateRateHz);
            _remoteInputSender.SendFailed += OnRemoteInputSendFailed;

            _hook.InputCaptured += OnInputCaptured;
            _hook.Start();

            _rawMouseInput = new RawMouseInputMonitor();
            _rawMouseInput.Attach(this);
            _rawMouseInput.MouseMoved += OnRawMouseMoved;

            _clipboardSync = new ClipboardSyncService(_pipe, GetState, IsClipboardEnabled);
            _clipboardSync.Attach(this);
            _fileDropSender = new FileDropSender(_pipe, GetState, IsControlPaused, Notify);
            _clipboardSync.FileClipboardAnnounced += OnFileClipboardAnnounced;
            _clipboardSync.RemoteFilesPending += OnRemoteFilesPending;

            // fix8：文件和剪贴板改走 Agent 与 Agent 之间的独立 TCP 通道（47541）。
            // 旧链路把文件传输挂在 Service 的命令队列上，任何一处状态卡住（暂停共享、
            // 控制租约、IPC 管道）文件就静默失败；新通道一条连接只做一件事，
            // 每个文件都有明确的 accept/done 回应，失败会告诉用户。
            _fileLink = new LanMouseShare.Agent.FileLink.FileLinkHub(GetState, GetTransferDirectory);
            _fileLink.Progress += OnFileLinkProgress;
            _fileLink.FilesReceived += OnFileLinkFilesReceived;
            _fileLink.ClipboardReceived += OnFileLinkClipboardReceived;
            _fileLink.PushCompleted += OnFileLinkPushCompleted;
            _fileLink.Start();
            _clipboardSync.Transport = SendClipboardViaFileLink;

            // fix10：文件传输改成"资源管理器右键 → 传输到对端"，直接推到对端桌面。
            // 菜单项写在 HKCU（不需要管理员），点击后用 --send 起第二个 Agent 进程，
            // 由单实例逻辑把路径转交回本进程，这里聚合后统一发送。
            ShellContextMenu.Register();
            SendRequestChannel.StartServer(OnSendRequestBatch);

            var pendingSend = (Application.Current as App) != null
                ? (Application.Current as App).PendingSendPaths
                : null;
            if (pendingSend != null && pendingSend.Length > 0)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    AppLog.Event("INFO", "send.channel.preload", "count=" + pendingSend.Length);
                    SendFilesViaFileLink(pendingSend);
                }));
            }

            // 50 ms keeps the relayed lease/ack hop inside the 700 ms ack budget.
            _pollTimer.Interval = TimeSpan.FromMilliseconds(50);
            _pollTimer.Tick += OnPollEvents;
            _pollTimer.Start();

            _stateTimer.Interval = TimeSpan.FromSeconds(2);
            _stateTimer.Tick += delegate { RefreshStateAsync(); };
            _stateTimer.Start();

            _lastUiTickUtc = DateTime.UtcNow;
            _uiWatchdog.Interval = TimeSpan.FromMilliseconds(250);
            _uiWatchdog.Tick += OnUiWatchdogTick;
            _uiWatchdog.Start();

            // The lease/ack beat runs on its own thread. A DispatcherTimer stops beating whenever the
            // UI thread is busy (WPF layout, clipboard, a modal dialog), which used to drop the peer
            // into Idle mid-gesture and looked like "huge latency".
            _linkRunning = true;
            _linkThread = new Thread(new ThreadStart(LinkLoop));
            _linkThread.IsBackground = true;
            _linkThread.Name = "LanMouseShare link beat";
            _linkThread.Start();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SystemInfoReport.WriteFile(AppLog.LogsRoot, "Agent");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("SystemInfo report failed. Error=" + ex.Message);
                }
            });

            // fix7：退出时 StopControlEverywhere(true) 会给对端发一次 StopAll 暂停，
            // 但新进程不知道自己上一世暂停过谁，对端就永久停在"已暂停"，
            // 之后拖文件、粘贴全部静默失败。所以启动后主动恢复一次。
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        _pipe.Send(AgentCommands.ResumeControl, "{}", PipeConstants.DefaultTimeoutMs);
                        AppLog.Info("Startup resume control sent. attempt=" + (attempt + 1));
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 4)
                        {
                            AppLog.Warn("Startup resume control failed. " + ex.GetType().Name + ": " + ex.Message);
                            return;
                        }

                        Thread.Sleep(1000);
                    }
                }
            });

            RegisterScreenInfo();
            RefreshStateAsync();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exitRequested && _minimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            ShutdownAndDispose();
        }

        private void ShutdownAndDispose()
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            AppLog.Info("Agent closing.");
            _linkRunning = false;
            if (_linkThread != null)
            {
                try { _linkThread.Join(800); } catch (Exception) { }
                _linkThread = null;
            }
            StopControlEverywhere(true);
            RequestServiceShutdown();
            _pollTimer.Stop();
            _stateTimer.Stop();
            _hook.Dispose();
            if (_clipboardSync != null) _clipboardSync.Dispose();
            if (_fileLink != null) _fileLink.Dispose();
            if (_remoteInputSender != null) _remoteInputSender.Dispose();
            if (_rawMouseInput != null) _rawMouseInput.Dispose();
            if (_directInputChannel != null) _directInputChannel.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private PeerStateDto GetState()
        {
            return _state;
        }

        private bool IsControlPaused()
        {
            return _session.IsPaused;
        }

        private bool IsClipboardEnabled()
        {
            // fix7：剪贴板只看自己的开关。以前这里还带了 !_session.IsPaused，
            // 于是对端一进暂停（哪怕只是它关了一次程序）本机就收不到文件公告，
            // Ctrl+V 和"拉取并粘贴"都变成"对端暂无待拉取的文件"。
            // fix16.1：只读 volatile 镜像，绝不碰 WPF 控件（见 _clipboardEnabledCache 注释）。
            return _clipboardEnabledCache;
        }

        private void RefreshStateAsync()
        {
            if (_stateInFlight)
            {
                return;
            }

            _stateInFlight = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PeerStateDto state = null;
                Exception error = null;
                try
                {
                    var json = _pipe.Send(AgentCommands.GetPeerState, string.Empty, PipeConstants.StatusTimeoutMs);
                    state = ProtocolSerializer.FromJson<PeerStateDto>(json);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _stateInFlight = false;
                    if (error != null)
                    {
                        SetStatus(UiText.ServiceDisconnected);
                        AppLog.Error("Refresh state failed.", error);
                        return;
                    }

                    ApplyState(state);
                }));
            });
        }

        private void ApplyState(PeerStateDto state)
        {
            _state = state;
            _session.ApplyState(state);
            if (state == null)
            {
                _link.Release(ControlLinkReleaseReason.Disconnected, DateTime.UtcNow.Ticks);
                SyncLinkFlags("service-disconnected");
                SetStatus(UiText.ServiceDisconnected);
                return;
            }

            _link.SetLocalDeviceId(state.LocalDeviceId);
            _link.SetPeerDeviceId(state.PeerDeviceId);

            AppLog.SetRole(ShareRoleValues.DisplayName(state.ShareRole));
            AppLog.SetVerbose(state.VerboseLogging);
            AppLog.SetRetentionDays(state.LogRetentionDays);

            LocalNameText.Text = state.LocalDeviceName ?? string.Empty;
            LocalIdText.Text = state.LocalDeviceId ?? string.Empty;
            PeerNameText.Text = state.PeerDeviceName ?? UiText.NotDiscovered;
            PeerAddressText.Text = state.PeerAddress ?? string.Empty;
            TrustStateText.Text = TrustStateTextFor(state);
            LayoutText.Text = DirectionText(state.LayoutDirection);

            if (_directInputChannel != null)
            {
                _directInputChannel.SetPeer(state.IsConnected && state.IsTrusted ? state.PeerAddress : null);
            }

            ApplySettingsToUi(state);
            if (!state.IsConnected || !state.IsTrusted || state.IsPairingMismatch)
            {
                if (_link.Role != ControlLinkRole.Idle)
                {
                    ReleaseControl(false);
                }
            }

            if (state.IsPairingMismatch)
            {
                SetStatus(UiText.PairingMismatchRePair);
            }
            else if (_session.IsPaused)
            {
                SetStatus(UiText.CrossScreenPaused);
            }
            else if (state.IsConnected && state.IsTrusted)
            {
                SetStatus(UiText.Connected + (state.PeerDeviceName ?? state.PeerAddress));
            }
            else if (state.PairingStatus == PairingStatuses.PairedDisconnected)
            {
                SetStatus(UiText.PairedWaitingPeer);
            }
            else if (state.PairingStatus == PairingStatuses.WaitingForApproval)
            {
                SetStatus(UiText.WaitingApproval);
            }
            else if (state.PeerDeviceName != null || state.PeerAddress != null)
            {
                SetStatus(UiText.DeviceDiscoveredWaitingPair);
            }
            else
            {
                SetStatus(UiText.DiscoveringDevice);
            }

            UpdateControlTargetText();
        }

        private static string TrustStateTextFor(PeerStateDto state)
        {
            if (state == null)
            {
                return UiText.ServiceDisconnected;
            }

            if (state.IsPairingMismatch || state.PairingStatus == PairingStatuses.PairingMismatch)
            {
                return UiText.PairingMismatch;
            }

            if (state.PairingStatus == PairingStatuses.WaitingForApproval)
            {
                return UiText.WaitingConfirm;
            }

            if (state.IsConnected && state.IsTrusted)
            {
                return UiText.Paired;
            }

            if (state.PairingStatus == PairingStatuses.PairedDisconnected)
            {
                return UiText.PairedDisconnected;
            }

            return UiText.NotPaired;
        }

        private void ApplySettingsToUi(PeerStateDto state)
        {
            _applyingSettingsToUi = true;
            _mouseSpeedMultiplier = state.MouseSpeedMultiplier <= 0 ? 1.0 : state.MouseSpeedMultiplier;
            _mouseUpdateRateHz = NormalizeMouseRate(state.MouseUpdateRateHz);
            _pauseHotkey = NormalizeHotkeyText(state.PauseHotkey, "Ctrl+F11");
            _stopControlHotkey = NormalizeHotkeyText(state.StopControlHotkey, "Ctrl+F10");
            _exitHotkey = NormalizeHotkeyText(state.ExitHotkey, "Ctrl+Alt+End");
            _startWithWindows = state.StartWithWindows;
            _minimizeToTrayOnClose = state.MinimizeToTrayOnClose;
            _blockScreenCorners = state.BlockScreenCorners;
            _runAsAdministratorByDefault = state.RunAsAdministratorByDefault;

            MouseSpeedSlider.Value = _mouseSpeedMultiplier;
            MouseSpeedText.Text = _mouseSpeedMultiplier.ToString("0.00") + "x";
            MouseRateText.Text = _mouseUpdateRateHz + "Hz";
            CurrentRateText.Text = _mouseUpdateRateHz + "Hz";
            ClipboardEnabledBox.IsChecked = state.ClipboardEnabled;
            _clipboardEnabledCache = ClipboardEnabledBox.IsChecked == true;
            PauseHotkeyBox.Text = _pauseHotkey;
            StopHotkeyBox.Text = _stopControlHotkey;
            ExitHotkeyBox.Text = _exitHotkey;
            StartWithWindowsBox.IsChecked = _startWithWindows;
            MinimizeToTrayBox.IsChecked = _minimizeToTrayOnClose;
            BlockCornersBox.IsChecked = _blockScreenCorners;
            RunAsAdminDefaultBox.IsChecked = _runAsAdministratorByDefault;
            _shareRole = ShareRoleValues.Normalize(state.ShareRole);
            _controllerAddress = state.ControllerAddress ?? string.Empty;
            _debugToken = state.DebugToken ?? string.Empty;
            _logRetentionDays = state.LogRetentionDays <= 0 ? 3 : state.LogRetentionDays;
            _verboseLogging = state.VerboseLogging;
            _logTransferEnabled = state.LogTransferEnabled;
            ApplyLogSettingsToUi(state);
            UpdatePrivilegeStatus();
            SelectMouseRate(_mouseUpdateRateHz);
            if (_remoteInputSender != null)
            {
                _remoteInputSender.SetMouseUpdateRate(_mouseUpdateRateHz);
            }

            _settingsLoaded = true;
            _applyingSettingsToUi = false;

            // 自愈（问题②修复）：配置加载完成后按 config 重放一次开机启动注册。
            // 此前每次启动只读配置、从不重写注册表/计划任务，注册失败或过期后
            // "开机启动"会一直失效。放后台线程，避免 schtasks 阻塞 UI。
            if (!_startupOptionsReapplied)
            {
                _startupOptionsReapplied = true;
                var startWithWindows = _startWithWindows;
                var runAsAdmin = _runAsAdministratorByDefault;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    ApplyStartupOptions(startWithWindows, runAsAdmin);
                });
            }
        }

        private void ApplyLogSettingsToUi(PeerStateDto state)
        {
            SelectShareRole(_shareRole);
            if (!string.Equals(ControllerAddressBox.Text, _controllerAddress, StringComparison.Ordinal))
            {
                ControllerAddressBox.Text = _controllerAddress;
            }

            if (!string.Equals(DebugTokenBox.Text, _debugToken, StringComparison.Ordinal))
            {
                DebugTokenBox.Text = _debugToken;
            }

            var retentionText = _logRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(LogRetentionBox.Text, retentionText, StringComparison.Ordinal))
            {
                LogRetentionBox.Text = retentionText;
            }

            VerboseLoggingBox.IsChecked = _verboseLogging;
            LogTransferBox.IsChecked = _logTransferEnabled;
            var transport = string.IsNullOrWhiteSpace(state.LogTransportStatus) ? "-" : state.LogTransportStatus;
            transport += state.NetworkRttMs >= 0 ? "　网络延时 " + state.NetworkRttMs + " ms" : "　网络延时 未测";
            LogStatusText.Text = transport;
            LogPathText.Text = "本机日志：" + (state.LogDirectory ?? AppLog.LiveDirectory) +
                               "　被控端日志：" + (state.RemoteLogDirectory ?? AppLog.RemoteDirectory);
        }

        private void SelectShareRole(string role)
        {
            var normalized = ShareRoleValues.Normalize(role);
            if (ShareRoleBox == null)
            {
                return;
            }

            for (int i = 0; i < ShareRoleBox.Items.Count; i++)
            {
                var item = ShareRoleBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (ShareRoleBox.SelectedIndex != i)
                    {
                        ShareRoleBox.SelectedIndex = i;
                    }

                    return;
                }
            }
        }

        private void RegisterScreenInfo()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var bounds = Forms.SystemInformation.VirtualScreen;
                    var payload = new ScreenInfoPayload { Width = bounds.Width, Height = bounds.Height };
                    _pipe.Send(AgentCommands.UpdateScreenInfo, ProtocolSerializer.ToJson(payload));
                }
                catch (Exception ex)
                {
                    AppLog.Error("UpdateScreenInfo failed.", ex);
                }
            });
        }

        private void OnInputCaptured(object sender, InputCapturedEventArgs e)
        {
            if (IsConfiguredHotkey(e.Payload, _stopControlHotkey))
            {
                e.SuppressLocalInput = true;
                StopControlEverywhere(true);
                return;
            }

            if (IsConfiguredHotkey(e.Payload, _pauseHotkey))
            {
                e.SuppressLocalInput = true;
                TogglePauseControl(true);
                return;
            }

            if (IsConfiguredHotkey(e.Payload, _exitHotkey))
            {
                e.SuppressLocalInput = true;
                ExitApplication();
                return;
            }

            var snapshot = _session.Snapshot(_state);
            if (snapshot.IsPaused || !snapshot.IsConnected)
            {
                return;
            }

            // Pasting files that were copied on the peer has to work on BOTH keyboards:
            // the controller's physical Ctrl+V (handled in the Owner branch) and the
            // controlled machine's own keyboard. Injected keys are filtered by the hook,
            // so the local path must be detected from real input before routing.
            if (TryInterceptLocalFilePaste(e))
            {
                return;
            }

            // Single decision point for input routing (see docs/CONTROL-STATE-MODEL.md 3.3):
            // Owner swallows and forwards, Target keeps local input fully usable.
            var link = _link.Snapshot(DateTime.UtcNow.Ticks);
            if (link.Role == ControlLinkRole.Owner)
            {
                HandleRemoteControlInput(e);
                return;
            }

            if (link.Role == ControlLinkRole.Target)
            {
                HandleTargetLocalInput(e);
                return;
            }

            // fix10：Idle 状态下只有鼠标移动参与跨屏切换判断。键盘/滚轮/鼠标按键的
            // InputPayload 不带坐标（X/Y 默认为 0），旧代码把它们当成"指针跑到屏幕
            // 左上角"，于是敲一下键盘就可能触发边缘切换、把控制权交给对端——
            // 这就是"在本机打字却出现在另一台电脑、键盘像是反了"的直接原因。
            if (e.Payload.Kind != InputKinds.MouseMove)
            {
                return;
            }

            var rawPoint = new DrawingPoint(e.Payload.X, e.Payload.Y);
            var screen = GetActiveScreenBounds(rawPoint.X, rawPoint.Y);
            var point = new DrawingPoint(rawPoint.X - screen.Left, rawPoint.Y - screen.Top);
            LogLocalMousePosition(point, screen);
            if (!_hasLastLocalMousePoint)
            {
                _lastLocalMousePoint = point;
                _hasLastLocalMousePoint = true;
                return;
            }

            var previousPoint = _lastLocalMousePoint;
            _lastLocalMousePoint = point;

            var shouldSwitch = _edgeSwitchArmed && _edgeSwitchDetector.ShouldSwitch(
                point,
                previousPoint,
                new DrawingSize(screen.Width, screen.Height),
                _state.LayoutDirection,
                _blockScreenCorners,
                CornerBlockFor(_state.LayoutDirection, screen.Width, screen.Height));
            if (shouldSwitch)
            {
                ActivateRemoteControl(point, screen);
                e.SuppressLocalInput = true;
            }
            else
            {
                ArmEdgeSwitchIfMovedAway(point, screen);
                LogEdgeTrace(point, previousPoint, screen);
            }
        }

        // While the peer owns control this machine is a Target: local input stays fully
        // usable, but the Target must never promote itself (invariant I4). Pushing the
        // pointer to the edge that faces the peer only *asks* the owner to release, so a
        // user whose machine is being controlled can take control back on purpose.
        private void HandleTargetLocalInput(InputCapturedEventArgs e)
        {
            // fix10：被控端本地输入一律保持本机可用，但只有鼠标移动参与"取回控制"的
            // 边缘判断；键盘事件的坐标是 0,0，不能当指针位置用。
            if (e.Payload.Kind != InputKinds.MouseMove)
            {
                return;
            }

            var rawPoint = new DrawingPoint(e.Payload.X, e.Payload.Y);
            var screen = GetActiveScreenBounds(rawPoint.X, rawPoint.Y);
            var point = new DrawingPoint(rawPoint.X - screen.Left, rawPoint.Y - screen.Top);
            LogLocalMousePosition(point, screen);

            if (!_hasLastLocalMousePoint)
            {
                _lastLocalMousePoint = point;
                _hasLastLocalMousePoint = true;
                return;
            }

            var previousPoint = _lastLocalMousePoint;
            _lastLocalMousePoint = point;

            var shouldSwitch = _edgeSwitchArmed && _edgeSwitchDetector.ShouldSwitch(
                point,
                previousPoint,
                new DrawingSize(screen.Width, screen.Height),
                _state.LayoutDirection,
                _blockScreenCorners,
                CornerBlockFor(_state.LayoutDirection, screen.Width, screen.Height));
            if (!shouldSwitch)
            {
                ArmEdgeSwitchIfMovedAway(point, screen);
                return;
            }

            RequestReleaseFromOwner(point, screen);
        }

        private void RequestReleaseFromOwner(DrawingPoint point, System.Drawing.Rectangle screen)
        {
            var direction = _state != null && _state.LayoutDirection != null ? _state.LayoutDirection : "Right";
            var payload = new ControlReleasePayload
            {
                RemoteX = point.X,
                RemoteY = point.Y,
                RemoteScreenWidth = screen.Width,
                RemoteScreenHeight = screen.Height,
                Direction = direction,
                Reason = "ControllerEdge"
            };

            ArmEdgeSwitch("takeover request");
            AppLog.Event("INFO", "screen.takeover.request",
                "direction=" + direction +
                " point=" + point.X + "," + point.Y +
                " role=" + _link.Role +
                " epoch=" + _link.Epoch);
            _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, ProtocolSerializer.ToJson(payload));
        }

        private void LogLocalMousePosition(DrawingPoint point, System.Drawing.Rectangle screen)
        {
            var screenKey = screen.Left + "," + screen.Top + "," + screen.Width + "," + screen.Height;
            if (!string.Equals(screenKey, _lastLocalScreenKey, StringComparison.Ordinal))
            {
                AppLog.Event("INFO", "mouse.screen.change", "from=" + (_lastLocalScreenKey.Length == 0 ? "-" : _lastLocalScreenKey) +
                                                              " to=" + screenKey +
                                                              " point=" + point.X + "," + point.Y);
                _lastLocalScreenKey = screenKey;
            }

            var now = DateTime.UtcNow;
            var verbose = AppLog.IsVerbose;
            var intervalMs = verbose ? 250 : 2000;
            if ((now - _lastMousePositionLogUtc).TotalMilliseconds < intervalMs)
            {
                return;
            }

            _lastMousePositionLogUtc = now;
            var details = "x=" + point.X + " y=" + point.Y +
                          " screen=" + screen.Left + "," + screen.Top + "," + screen.Width + "," + screen.Height +
                          " target=" + (_session.IsTargetPeer ? "peer" : _session.IsTargetLocal ? "local" : "-") +
                          " role=" + _link.Role +
                          " paused=" + _session.IsPaused;
            if (verbose)
            {
                AppLog.Debug("mouse.local.position", details);
            }
            else
            {
                AppLog.Event("INFO", "mouse.local.position", details);
            }
        }

        private void OnUiWatchdogTick(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            var gapMs = (now - _lastUiTickUtc).TotalMilliseconds;
            _lastUiTickUtc = now;
            _lastUiGapMs = gapMs;
            if (gapMs >= 800 && now >= _uiStallLogUntilUtc)
            {
                _uiStallLogUntilUtc = now.AddSeconds(3);
                AppLog.Warn("UI thread stall detected. GapMs=" + (int)gapMs +
                            " LastGapMs=" + (int)_lastUiGapMs +
                            " Target=" + (_session.IsTargetPeer ? "peer" : "local") +
                            " Paused=" + _session.IsPaused);
            }
        }


        private void ActivateRemoteControl(DrawingPoint entryPoint, System.Drawing.Rectangle localScreen)
        {
            var now = DateTime.UtcNow.Ticks;
            ControlLease lease;
            if (!_link.TryBeginControl(now, out lease))
            {
                AppLog.Debug("control.link.begin.denied", _link.Snapshot(now).Text());
                return;
            }

            _session.SetPeerTarget();
            SyncLinkFlags("begin");
            SendLeaseNow(lease);
            AppLog.Event("INFO", "control.link.begin",
                "epoch=" + lease.Epoch +
                " direction=" + _state.LayoutDirection +
                " entry=" + entryPoint.X + "," + entryPoint.Y +
                " localScreen=" + localScreen.Left + "," + localScreen.Top + "," + localScreen.Width + "," + localScreen.Height);
            _localScreenBounds = localScreen;
            _remoteVirtualScreen = GetRemoteScreenSize(new DrawingSize(localScreen.Width, localScreen.Height));
            InitializeRemoteVirtualPosition(entryPoint, new DrawingSize(localScreen.Width, localScreen.Height));
            ArmEdgeSwitch("activate remote");
            AppLog.Event("INFO", "screen.switch.out",
                "direction=" + _state.LayoutDirection +
                " entry=" + entryPoint.X + "," + entryPoint.Y +
                " localScreen=" + localScreen.Left + "," + localScreen.Top + "," + localScreen.Width + "," + localScreen.Height +
                " remoteScreen=" + _remoteVirtualScreen.Width + "x" + _remoteVirtualScreen.Height +
                " remotePoint=" + _remoteVirtualX + "," + _remoteVirtualY);
            SendControlTarget(_session.ActiveTargetDeviceId, "EdgeSwitch");
            _remoteInputSender.SendCommand(AgentCommands.AcquireControl, "{}");
            _remoteInputSender.Enqueue(new InputPayload { Kind = InputKinds.MouseMove, X = _remoteVirtualX, Y = _remoteVirtualY });
            Notify(UiText.SwitchedControl, UiText.Controlling + (_state.PeerDeviceName ?? UiText.Peer));
            UpdateControlTargetText();
        }

        private void HandleRemoteControlInput(InputCapturedEventArgs e)
        {
            if (_link.Role != ControlLinkRole.Owner)
            {
                return;
            }

            if (e.Payload.Kind == InputKinds.MouseMove)
            {
                e.SuppressLocalInput = true;
                return;
            }

            if (e.Payload.Kind == InputKinds.Key)
            {
                var key = e.Payload.VirtualKey;
                if (key == 0x11)
                {
                    _forwardedCtrlDown = e.Payload.IsDown;
                }

                if (key == 0x56 && !e.Payload.IsDown && _swallowPasteKeyUp)
                {
                    _swallowPasteKeyUp = false;
                    e.SuppressLocalInput = true;
                    return;
                }

                if (key == 0x56 && e.Payload.IsDown && (_forwardedCtrlDown || IsPhysicalKeyDown(0x11)) && _clipboardSync.RefreshLocalHasFiles())
                {
                    // The peer filters injected input, so it never sees this Ctrl+V. Ask it to pull
                    // the announced files and paste locally instead of forwarding the key.
                    _swallowPasteKeyUp = true;
                    e.SuppressLocalInput = true;
                    AppLog.Event("INFO", "clipboard.paste.intercept",
                        "ctrl=1" +
                        " localFiles=" + _clipboardSync.LocalFilePaths.Length +
                        " role=" + _link.Role);
                    RequestRemoteFilePaste();
                    return;
                }

                if (key == 0x56 && e.Payload.IsDown)
                {
                    AppLog.Event("INFO", "clipboard.paste.skip",
                        "ctrl=" + ((_forwardedCtrlDown || IsPhysicalKeyDown(0x11)) ? "1" : "0") +
                        " localFiles=" + (_clipboardSync.LocalHasFiles ? "1" : "0") +
                        " role=" + _link.Role);
                }
            }

            if (_remoteInputSender.Enqueue(e.Payload))
            {
                e.SuppressLocalInput = true;
                if (e.Payload.Kind == InputKinds.Key)
                {
                    LogKeyForward(e.Payload);
                }

                return;
            }

            AppLog.Warn("Remote input enqueue failed; releasing local control.");
            ReleaseControl(false);
        }

        private void OnRawMouseMoved(object sender, RawMouseMovedEventArgs e)
        {
            if (_link.Role != ControlLinkRole.Owner || _link.IsPaused)
            {
                return;
            }

            var dx = ScaleDelta(e.DeltaX);
            var dy = ScaleDelta(e.DeltaY);
            // A single raw report can carry a huge count on fast flicks; without a cap the virtual
            // position jumps across the whole remote screen in one event and can hit the return edge
            // instantly, which looked like "control randomly dropped".
            dx = Math.Max(-MaxDeltaPerEvent, Math.Min(MaxDeltaPerEvent, dx));
            dy = Math.Max(-MaxDeltaPerEvent, Math.Min(MaxDeltaPerEvent, dy));
            var remoteScreen = GetRemoteScreenSize(_remoteVirtualScreen);
            var maxX = Math.Max(0, remoteScreen.Width - 1);
            var maxY = Math.Max(0, remoteScreen.Height - 1);
            var previousX = _remoteVirtualX;
            var previousY = _remoteVirtualY;
            var nextX = previousX + dx;
            var nextY = previousY + dy;
            var clampedX = Math.Max(0, Math.Min(maxX, nextX));
            var clampedY = Math.Max(0, Math.Min(maxY, nextY));
            _remoteVirtualX = clampedX;
            _remoteVirtualY = clampedY;

            // Absolute injection instead of relative deltas: the target applies its own pointer
            // acceleration to MOUSEEVENTF_MOVE, so small relative steps were swallowed or scaled,
            // the remote pointer crawled or froze, and the old failure heuristic then kicked the
            // user back to the controller. An absolute position is 1:1 and cannot drift.
            InputPayload payload;
            if (clampedX != previousX || clampedY != previousY)
            {
                payload = new InputPayload { Kind = InputKinds.MouseMove, X = clampedX, Y = clampedY };
                AppLog.Debug("mouse.remote.send", "abs=" + clampedX + "," + clampedY + " screen=" + remoteScreen.Width + "x" + remoteScreen.Height);
            }
            else
            {
                // Already at the target screen boundary: keep the outward push as a relative step so
                // the target can detect the user pushing through the edge and return control.
                payload = new InputPayload { Kind = InputKinds.MouseMoveRelative, X = dx, Y = dy };
                LogRemoteBoundary(clampedX, clampedY, remoteScreen);
            }

            if (!_remoteInputSender.Enqueue(payload))
            {
                AppLog.Warn("Raw remote input enqueue failed; releasing remote control.");
                ReleaseControl(false);
                return;
            }

            // Return edge, owner side. The physical mouse lives on this machine, so the push
            // through the peer's return edge is visible here as clamped virtual motion. Detecting
            // it here returns control immediately instead of waiting for the peer's injected
            // pointer detector, which needed several seconds of rubbing (the 0.1.64 report).
            var returnDirection = _state != null && !string.IsNullOrEmpty(_state.LayoutDirection)
                ? _state.LayoutDirection
                : "Right";
            TrackOwnerReturnPush(returnDirection, dx, dy, clampedX, clampedY, maxX, maxY);
        }

        private void TrackOwnerReturnPush(string direction, int dx, int dy, int x, int y, int maxX, int maxY)
        {
            var atReturnEdge = false;
            var outward = 0;
            if (direction == "Right")
            {
                atReturnEdge = x <= 0;
                outward = -dx;
            }
            else if (direction == "Left")
            {
                atReturnEdge = x >= maxX;
                outward = dx;
            }
            else if (direction == "Up")
            {
                atReturnEdge = y >= maxY;
                outward = dy;
            }
            else if (direction == "Down")
            {
                atReturnEdge = y <= 0;
                outward = -dy;
            }

            // fix15：返回路径也要拦边角（fix14 回退时把这段一并删掉了，结果贴着对端屏幕的
            // 上下角往回推就直接从角落穿回来，用户看到的就是"边角阻挡失效"）。这里的坐标是
            // 对端屏幕上的虚拟坐标，要穿回去的那条边是进入边的对面，所以用 OppositeDirection；
            // 阻挡带按对端屏幕的比例算，跟前进方向保持同一视觉宽度。
            if (_blockScreenCorners &&
                EdgeSwitchDetector.IsInBlockedCorner(
                    new DrawingPoint(x, y),
                    new DrawingSize(maxX + 1, maxY + 1),
                    OppositeDirection(direction),
                    CornerBlockFor(direction, maxX + 1, maxY + 1)))
            {
                ResetOwnerReturnState();
                return;
            }

            var now = DateTime.UtcNow;

            // fix19：回切后短暂冷却，避免"刚回去又被推回来"的乒乓。
            if (now < _ownerReturnCooldownUntilUtc)
            {
                ResetOwnerReturnState();
                return;
            }

            // 武装：指针必须先真正进到对端内部（离返回边 ≥40px）；刚进场落在 8px 处不算，
            // 否则一进场就能被判成"又要回去"。
            if (GetOwnerReturnEdgeDistance(direction, x, y, maxX, maxY) >= OwnerReturnArmDistance)
            {
                _ownerReturnArmed = true;
            }

            if (!atReturnEdge || outward <= 0 || !_ownerReturnArmed ||
                (now - _ownerSinceUtc).TotalMilliseconds < OwnerReturnArmMs)
            {
                ResetOwnerReturnState();
                return;
            }

            if (_ownerReturnEdgeSinceUtc == DateTime.MinValue)
            {
                _ownerReturnEdgeSinceUtc = now;
            }

            if ((now - _ownerReturnPushUtc).TotalMilliseconds > OwnerReturnPushWindowMs)
            {
                _ownerReturnPush = 0;
            }

            _ownerReturnPushUtc = now;
            _ownerReturnPush += outward;
            AppLog.Debug("mouse.return.push",
                "direction=" + direction + " outward=" + outward + " total=" + _ownerReturnPush +
                " dwell=" + (int)(now - _ownerReturnEdgeSinceUtc).TotalMilliseconds +
                " point=" + x + "," + y + " max=" + maxX + "," + maxY);

            // fix19：光推力够不算数，还得在返回边上连续驻留够久。用户在对端左缘附近的一次
            // 快速左扫（实测 254px→0 只用 56ms）会被挡掉；真想回去时压住边缘继续推，
            // 累计推力与驻留时间会一起达标。
            if (_ownerReturnPush < OwnerReturnPushThreshold ||
                (now - _ownerReturnEdgeSinceUtc).TotalMilliseconds < OwnerReturnDwellMs)
            {
                return;
            }

            ResetOwnerReturnState();
            _ownerReturnArmed = false;
            _ownerReturnCooldownUntilUtc = now.AddMilliseconds(OwnerReturnCooldownMs);
            AppLog.Event("INFO", "screen.return.owner",
                "direction=" + direction + " point=" + x + "," + y +
                " remoteScreen=" + maxX + "x" + maxY);
            var payload = new ControlReleasePayload
            {
                RemoteX = x,
                RemoteY = y,
                RemoteScreenWidth = maxX + 1,
                RemoteScreenHeight = maxY + 1,
                Direction = direction,
                Reason = "OwnerEdgePush"
            };
            ReleaseControlToLocalEdge(payload);
        }

        /// <summary>
        /// fix19：清空返回手势的累积状态（推力、边缘驻留起点）。武装标记不在这里清，
        /// 它只由"跨进对端 ≥40px"置位、由真正回切或重新进场重置。
        /// </summary>
        private void ResetOwnerReturnState()
        {
            _ownerReturnPush = 0;
            _ownerReturnEdgeSinceUtc = DateTime.MinValue;
        }

        /// <summary>
        /// fix19：指针离"将被穿回去的那条边"还有多远（对端虚拟坐标，像素）。
        /// Right 表示从本机右侧进入对端，返回边在对端 x=0，此时距离就是 x。
        /// </summary>
        private static int GetOwnerReturnEdgeDistance(string direction, int x, int y, int maxX, int maxY)
        {
            if (direction == "Right")
            {
                return x;
            }

            if (direction == "Left")
            {
                return maxX - x;
            }

            if (direction == "Up")
            {
                return maxY - y;
            }

            if (direction == "Down")
            {
                return y;
            }

            return int.MaxValue;
        }

        private void LogKeyForward(InputPayload payload)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastKeyLogUtc).TotalMilliseconds < 200)
            {
                return;
            }

            AppLog.Debug("key.forward",
                "vk=" + payload.VirtualKey + " scan=" + payload.ScanCode +
                " down=" + (payload.IsDown ? "1" : "0") +
                " extended=" + (payload.IsExtendedKey ? "1" : "0"));
            _lastKeyLogUtc = now;
        }

        private void LogRemoteBoundary(int x, int y, DrawingSize remoteScreen)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastBoundaryLogUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            AppLog.Debug("mouse.remote.boundary", "virtual=" + x + "," + y + " screen=" + remoteScreen.Width + "x" + remoteScreen.Height);
            _lastBoundaryLogUtc = now;
        }

        private void OnRemoteInputSendFailed(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_link.Role == ControlLinkRole.Owner)
                {
                    ReleaseControl(false);
                }
            }));
        }

        private void OnDirectInputBatchReceived(object sender, InputBatchReceivedEventArgs e)
        {
            // Inject on the UDP receive thread. A busy or minimized UI thread must never delay or
            // stall remote input; SendInput needs no window handle and is thread safe.
            if (!_link.Snapshot(DateTime.UtcNow.Ticks).ShouldInjectRemoteInput)
            {
                LogIgnoredDirectInput();
                return;
            }

            for (int i = 0; i < e.Payloads.Length; i++)
            {
                InjectPeerInput(e.Payloads[i]);
            }
        }

        private void LogIgnoredDirectInput()
        {
            _ignoredInputCount++;
            var now = DateTime.UtcNow;
            if ((now - _lastIgnoredInputLogUtc).TotalMilliseconds < 2000)
            {
                return;
            }

            AppLog.Debug("input.ignored",
                "count=" + _ignoredInputCount +
                " target=" + (_session.IsTargetPeer ? "peer" : _session.IsTargetLocal ? "local" : "unknown") +
                " role=" + _link.Role +
                " paused=" + (_link.IsPaused ? "1" : "0"));
            _lastIgnoredInputLogUtc = now;
            _ignoredInputCount = 0;
        }

        private void InjectPeerInput(InputPayload payload)
        {
            if (payload == null || !_link.Snapshot(DateTime.UtcNow.Ticks).ShouldInjectRemoteInput)
            {
                return;
            }

            _link.NoteRemoteInput(DateTime.UtcNow.Ticks);
            if (TryHandleClipboardPaste(payload))
            {
                return;
            }

            if (!HandleConflictWindowBeforeInjection(payload))
            {
                return;
            }

            var before = CursorController.GetPosition();
            var result = _injector.Inject(payload);
            var after = CursorController.GetPosition();
            if (!HandleInjectionResult(payload, result, before, after))
            {
                return;
            }

            if (payload.Kind == InputKinds.Key)
            {
                TrackInjectedKey(payload);
            }

            if (payload.Kind == InputKinds.MouseMove || payload.Kind == InputKinds.MouseMoveRelative)
            {
                LogRemoteInjectedPosition(payload, after);
            }

            if (_link.Role == ControlLinkRole.Target &&
                (payload.Kind == InputKinds.MouseMove || payload.Kind == InputKinds.MouseMoveRelative))
            {
                var moveX = payload.X;
                var moveY = payload.Y;
                if (payload.Kind == InputKinds.MouseMove)
                {
                    moveX = _lastInjectedMouseX < 0 ? 0 : payload.X - _lastInjectedMouseX;
                    moveY = _lastInjectedMouseY < 0 ? 0 : payload.Y - _lastInjectedMouseY;
                    _lastInjectedMouseX = payload.X;
                    _lastInjectedMouseY = payload.Y;
                }

                CheckRemoteReturnToController(moveX, moveY);
            }
        }

        private void LogRemoteInjectedPosition(InputPayload payload, DrawingPoint after)
        {
            var now = DateTime.UtcNow;
            var intervalMs = AppLog.IsVerbose ? 250 : 2000;
            if ((now - _lastRemoteInjectLogUtc).TotalMilliseconds < intervalMs)
            {
                return;
            }

            _lastRemoteInjectLogUtc = now;
            var screen = GetActiveScreenBounds(after.X, after.Y);
            var details = "kind=" + payload.Kind +
                          " delta=" + payload.X + "," + payload.Y +
                          " cursor=" + after.X + "," + after.Y +
                          " screen=" + screen.Left + "," + screen.Top + "," + screen.Width + "," + screen.Height +
                          " role=" + _link.Role;
            if (AppLog.IsVerbose)
            {
                AppLog.Debug("mouse.remote.position", details);
            }
            else
            {
                AppLog.Event("INFO", "mouse.remote.position", details);
            }
        }

        private bool HandleConflictWindowBeforeInjection(InputPayload payload)
        {
            if (!IsConflictSensitivePayload(payload))
            {
                return true;
            }

            var info = ForegroundWindowInspector.GetForegroundWindowInfo();
            if (!KnownConflictProcessDetector.IsKnownConflictProcess(info.ProcessName))
            {
                return true;
            }

            var processName = info.NormalizedProcessName;
            if (_ignoredConflictProcesses.Contains(processName))
            {
                return true;
            }

            // Never block the injection path with a modal dialog: this machine is being
            // controlled remotely, so nobody is sitting in front of it to answer. A modal
            // prompt used to freeze every queued pointer event and then replay them in a
            // burst seconds later. Warn once per process and keep injecting instead.
            _ignoredConflictProcesses.Add(processName);
            AppLog.Warn("Known input-conflict window detected; continuing injection without blocking. Process=" +
                        info.ProcessName + " Title=" + info.Title);
            return true;
        }

        private bool HandleInjectionResult(InputPayload payload, InputInjectionResult result, DrawingPoint before, DrawingPoint after)
        {
            var failed = result != null && !result.Success;
            var reason = failed ? result.Reason + " LastError=" + result.LastError : string.Empty;

            // A relative mouse move that does not change the cursor position is normal: the cursor is
            // clamped at a screen edge, or pointer acceleration ate the step. Treating it as a failure
            // used to release control (0.1.64: "mouse suddenly returns to the controller").
            if (!failed && payload.Kind == InputKinds.MouseMoveRelative && (payload.X != 0 || payload.Y != 0) &&
                before.X == after.X && before.Y == after.Y)
            {
                LogMouseClamped(before);
            }

            if (!failed)
            {
                _injectionFailures.Reset();
                return true;
            }

            AppLog.Warn("Input injection suspected failed. Kind=" + payload.Kind + " Reason=" + reason);
            if (_injectionFailures.RegisterFailure(DateTime.UtcNow))
            {
                ReleaseForInjectionBlocked(ForegroundWindowInspector.GetForegroundWindowInfo(), reason);
                _injectionFailures.Reset();
            }

            return false;
        }

        private void ReleaseForInjectionBlocked(ForegroundWindowInfo info, string reason)
        {
            var position = CursorController.GetPosition();
            var screen = GetActiveScreenBounds(position.X, position.Y);
            var point = new DrawingPoint(position.X - screen.Left, position.Y - screen.Top);
            var direction = _state != null ? _state.LayoutDirection ?? "Right" : "Right";
            var payload = new ControlReleasePayload
            {
                RemoteX = point.X,
                RemoteY = point.Y,
                RemoteScreenWidth = screen.Width,
                RemoteScreenHeight = screen.Height,
                Direction = direction,
                Reason = reason
            };

            if (_remoteInputSender != null)
            {
                _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, ProtocolSerializer.ToJson(payload));
            }

            StopLocalCrossScreenState();
            var process = info != null && !string.IsNullOrWhiteSpace(info.ProcessName) ? info.ProcessName : "?";
            AppLog.Warn("Control released because input injection was blocked. Process=" + process + " Reason=" + reason);
            Notify(UiText.ConflictTitle, UiText.ConflictReturned);
        }

        private static bool IsConflictSensitivePayload(InputPayload payload)
        {
            return payload != null &&
                   (payload.Kind == InputKinds.MouseMove ||
                    payload.Kind == InputKinds.MouseMoveRelative ||
                    payload.Kind == InputKinds.MouseButton ||
                    payload.Kind == InputKinds.MouseWheel ||
                    payload.Kind == InputKinds.Key);
        }

        private void CheckRemoteReturnToController(int deltaX, int deltaY)
        {
            if (_state == null || !_state.IsConnected || !_state.IsTrusted)
            {
                return;
            }

            // Never hand control back right after taking it. The injected pointer lands a few
            // pixels from the entry edge and the first fast flick towards that edge produced a
            // large position delta, which used to fire a bogus "EdgePush" and bounce the user
            // back to the controller about once every two seconds.
            if ((DateTime.UtcNow - _targetEnteredUtc).TotalMilliseconds < TargetReturnGraceMs)
            {
                return;
            }

            // A fast flick can produce a several-hundred pixel delta in one sample. Clamp it so a
            // single jump cannot satisfy the whole push threshold at once.
            deltaX = Math.Max(-MaxReturnSamplePixels, Math.Min(MaxReturnSamplePixels, deltaX));
            deltaY = Math.Max(-MaxReturnSamplePixels, Math.Min(MaxReturnSamplePixels, deltaY));

            var position = CursorController.GetPosition();
            var screen = GetActiveScreenBounds(position.X, position.Y);
            var point = new DrawingPoint(position.X - screen.Left, position.Y - screen.Top);
            var direction = _state.LayoutDirection ?? "Right";
            var result = _remoteReturnDetector.Update(point, new DrawingSize(screen.Width, screen.Height), direction, deltaX, deltaY, _session.IsPaused, _blockScreenCorners, CornerBlockFor(direction, screen.Width, screen.Height));
            if (!result.ShouldRelease)
            {
                return;
            }

            // Release back to local only; never promote this machine to controller here.
            // Two machines believing they are both controllers swallow each other's input,
            // which is exactly the 0.1.45 "keyboard and mouse frozen" failure.
            // Ask the owner to release and stop injecting on our side right away, so this
            // machine cannot stay a target that silently drops input.
            _link.Release(ControlLinkReleaseReason.TargetRequestedRelease, DateTime.UtcNow.Ticks);
            _session.SetLocalTarget();
            SyncLinkFlags("target release request");
            ArmEdgeSwitch("remote return");
            _remoteReturnDetector.Reset();
            var payload = new ControlReleasePayload
            {
                RemoteX = point.X,
                RemoteY = point.Y,
                RemoteScreenWidth = screen.Width,
                RemoteScreenHeight = screen.Height,
                Direction = direction,
                Reason = result.Reason
            };
            AppLog.Event("INFO", "screen.switch.return",
                "direction=" + direction +
                " point=" + point.X + "," + point.Y +
                " delta=" + deltaX + "," + deltaY +
                " reason=" + result.Reason +
                " screen=" + screen.Width + "x" + screen.Height);
            _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, ProtocolSerializer.ToJson(payload));
            UpdateControlTargetText();
        }

        private void OnPollFailed(Exception ex)
        {
            _pollFailures++;

            // 服务掉线后每次轮询都会 500ms 超时，连续记录会把日志刷到几十万行；
            // 只记录开头几次，之后每 100 次记一次。
            if (_pollFailures <= 3 || (_pollFailures % 100) == 0)
            {
                AppLog.Error("Poll events failed. consecutive=" + _pollFailures.ToString(CultureInfo.InvariantCulture), ex);
            }

            if (_pollFailures < 20 || _shutdownStarted)
            {
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastServiceRecoveryTicks);
            if (last != 0 && now - last < TimeSpan.TicksPerSecond * 15)
            {
                return;
            }

            Interlocked.Exchange(ref _lastServiceRecoveryTicks, now);
            if (_serviceRecoveries >= 10)
            {
                return;
            }

            _serviceRecoveries++;
            try
            {
                AppLog.Warn("Service connection lost; restarting managed service. attempt=" + _serviceRecoveries);
                KillStaleServiceProcesses();
                StartAgentManagedService();
            }
            catch (Exception recoverEx)
            {
                AppLog.Error("Restart managed service failed.", recoverEx);
            }
        }

        private void OnPollEvents(object sender, EventArgs e)
        {
            if (_pollInFlight)
            {
                return;
            }

            _pollInFlight = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ProtocolEnvelope[] events = null;
                try
                {
                    var json = _pipe.Send(AgentCommands.PollEvents, string.Empty, PipeConstants.StatusTimeoutMs);
                    events = ProtocolSerializer.FromJson<ProtocolEnvelope[]>(json);
                }
                catch (Exception ex)
                {
                    OnPollFailed(ex);
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _pollInFlight = false;
                    if (events == null)
                    {
                        return;
                    }

                    _pollFailures = 0;
                    _serviceRecoveries = 0;
                    for (int i = 0; i < events.Length; i++)
                    {
                        ApplyEnvelope(events[i]);
                    }
                }));
            });
        }

        private void ApplyEnvelope(ProtocolEnvelope envelope)
        {
            if (envelope == null)
            {
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlPause)
            {
                ApplyPeerPause(envelope.PayloadJson);
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlResume)
            {
                ApplyPeerResume();
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlRelease)
            {
                ApplyPeerControlRelease(envelope.PayloadJson);
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlTargetChanged)
            {
                ApplyPeerControlTarget(envelope.PayloadJson);
                return;
            }

            if (_session.IsPaused && IsCrossScreenEnvelope(envelope.MessageType))
            {
                AppLog.Info("Envelope ignored while paused. Type=" + envelope.MessageType);
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlLease)
            {
                HandleRelayedLease(envelope.PayloadJson);
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.ControlLeaseAck)
            {
                HandleRelayedAck(envelope.PayloadJson);
                return;
            }
            if (envelope.MessageType == ProtocolMessageTypes.InputEvent)
            {
                InjectPeerInput(ProtocolSerializer.FromJson<InputPayload>(envelope.PayloadJson));
            }
            else if (envelope.MessageType == ProtocolMessageTypes.InputBatch)
            {
                var batch = ProtocolSerializer.FromJson<InputBatchPayload>(envelope.PayloadJson);
                if (batch != null && batch.Events != null)
                {
                    for (int i = 0; i < batch.Events.Length; i++)
                    {
                        InjectPeerInput(batch.Events[i]);
                    }
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ControlAcquire)
            {
                // Notice only: the role itself is decided by the lease channel (I5).
                _session.SetLocalTarget();
                _remoteReturnDetector.Reset();
                AppLog.Debug("control.link.acquire.notice", _link.Snapshot(DateTime.UtcNow.Ticks).Text());
                UpdateControlTargetText();
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ClipboardPasteRequest)
            {
                OnClipboardPasteRequested(envelope.PayloadJson);
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ClipboardUpdate)
            {
                _clipboardSync.ApplyRemote(ProtocolSerializer.FromJson<ClipboardPayload>(envelope.PayloadJson));
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileOffer)
            {
                if (!_pastingFiles)
                {
                    Notify(UiText.ReceivingFile, UiText.FileSavedToDesktop);
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileProgress)
            {
                var progressPayload = ProtocolSerializer.FromJson<FileProgressPayload>(envelope.PayloadJson);
                if (progressPayload != null)
                {
                    Dispatcher.BeginInvoke(new Action(delegate { ApplyFileProgress(progressPayload); }));
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileComplete)
            {
                var complete = ProtocolSerializer.FromJson<FileCompletePayload>(envelope.PayloadJson);
                var savedPath = complete != null ? complete.SavedPath : null;
                if (!string.IsNullOrEmpty(savedPath))
                {
                    AppLog.Debug("file.saved", "path=" + savedPath);
                    savedPath = ExpandClipboardArchive(savedPath);
                }

                var succeeded = complete == null || complete.Success;
                if (!succeeded || string.IsNullOrEmpty(savedPath))
                {
                    // fix7：失败或没有落盘路径时不要报"传输完成/已保存"，否则用户以为
                    // 文件已经躺在对端桌面上了。以前这里对所有 FileComplete 一视同仁。
                    if (complete != null && !complete.Success)
                    {
                        AppLog.Event("WARN", "file.transfer.failed", "message=" + (complete.Message ?? "-"));
                        if (!_pastingFiles)
                        {
                            SetStatus(UiText.FileTransferFailed + (complete.Message ?? string.Empty));
                        }
                    }
                    else
                    {
                        AppLog.Debug("file.complete.no.path", "ignored");
                    }

                    return;
                }

                if (_pastingFiles)
                {
                    // Part of a clipboard paste: no "saved" popup and no folder window.
                    OnPasteFileArrived(savedPath);
                }
                else
                {
                    // Only a status line. Opening a folder (and worse, launching the received file)
                    // steals foreground and CPU on the controlled PC, which showed up as a big input
                    // latency spike right after a transfer.
                    SetStatus(UiText.FileTransferComplete + " " + UiText.FileSavedComplete);
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ClipboardPull)
            {
                var pull = ProtocolSerializer.FromJson<ClipboardPullRequestPayload>(envelope.PayloadJson);
                if (pull != null && pull.FilePaths != null && pull.FilePaths.Length > 0)
                {
                    AppLog.Event("INFO", "clipboard.pull.serve",
                        "count=" + pull.FilePaths.Length + " requester=" + (pull.RequesterDeviceId ?? "-"));
                    _fileDropSender.SendFiles(pull.FilePaths, true);
                }
            }
        }

        /// <summary>
        /// 对端公告了文件剪贴板：这里只登记文件清单并提示用户，不传输任何字节。
        /// 文件在真正粘贴时才按需拉取（本机 Ctrl+V、"拉取并粘贴"按钮、主控端粘贴请求），
        /// 因此"复制"不会触发传输，只有"粘贴"才会（问题①修复）。
        /// </summary>
        private void OnRemoteFilesPending(object sender, FileClipboardDetectedEventArgs e)
        {
            if (e == null || e.FilePaths == null || e.FilePaths.Length == 0)
            {
                return;
            }

            var signature = string.Join("|", e.FilePaths);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_pastingFiles || string.Equals(_lastPrefetchSignature, signature, StringComparison.Ordinal))
                {
                    return;
                }

                _lastPrefetchSignature = signature;

                // Preferred path: publish delayed-rendering clipboard entries so the file names
                // exist immediately and the bytes are only pulled when the user really pastes.
                if (_clipboardSync.PublishVirtualFiles(e.FilePaths, e.FileSizes, FetchRemoteForClipboard))
                {
                    AppLog.Event("INFO", "clipboard.files.virtual.published",
                        "count=" + e.FilePaths.Length + " first=" + e.FilePaths[0]);
                    SetStatus("\u526A\u8D34\u677F\u5DF2\u540C\u6B65 " + e.FilePaths.Length +
                        " \u4E2A\u6587\u4EF6\uFF08\u7C98\u8D34\u65F6\u624D\u4F20\uFF09");
                    return;
                }

                // 懒传输：不再预取文件（旧的 BeginPasteFiles(e.FilePaths, false) 会在
                // 对端一复制时就开始传输）。粘贴链路由 TryInterceptLocalFilePaste /
                // TryHandleClipboardPaste / OnClipboardPasteRequested / "拉取并粘贴"按钮触发。
                AppLog.Event("INFO", "clipboard.files.lazy.pending",
                    "count=" + e.FilePaths.Length + " first=" + e.FilePaths[0]);
                SetStatus("\u5BF9\u7AEF\u5DF2\u590D\u5236 " + e.FilePaths.Length +
                    " \u4E2A\u6587\u4EF6\uFF0C\u53EF\u6309 Ctrl+V \u6216\u70B9\u201C\u62C9\u53D6\u5E76\u7C98\u8D34\u201D");
            }));
        }
        private string PullOneFile(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath) || _fileLink == null)
            {
                return null;
            }

            var started = DateTime.UtcNow;
            var name = System.IO.Path.GetFileName(remotePath);
            var local = _fileLink.PullFile(name, remotePath, 0);
            var elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;
            if (!string.IsNullOrEmpty(local))
            {
                AppLog.Event("INFO", "clipboard.files.fetch.done",
                    "ms=" + elapsedMs.ToString("0") + " path=" + remotePath + " local=" + local);
                return local;
            }

            AppLog.Warn("clipboard.files.fetch.failed path=" + remotePath + " ms=" + elapsedMs.ToString("0"));
            return null;
        }

        /// <summary>
        /// Called by the clipboard data object on first read of one file: asks the service to pull
        /// that single peer path and returns the local copy. Blocks the pasting application (not
        /// the UI thread) until the transfer finishes or the timeout expires.
        /// </summary>
        private string FetchRemoteForClipboard(string remotePath)
        {
            try
            {
                return PullOneFile(remotePath);
            }
            catch (Exception ex)
            {
                AppLog.Warn("clipboard.files.fetch.failed path=" + remotePath + " error=" + ex.Message);
                return null;
            }
        }
        // ===== fix8：FileLink 通道（Agent↔Agent，TCP 47541）的接线 =====

        private void OnFileLinkProgress(object sender, LanMouseShare.Agent.FileLink.FileLinkProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            // fix9：Done 必须如实置位，否则界面永远停在 "100%" 不显示"已发送/已接收"；
            // 速度也在这里算，FileLinkProgress 只带累计字节。
            var done = progress.TotalBytes > 0 && progress.DoneBytes >= progress.TotalBytes;
            long speed = 0;
            var now = Environment.TickCount;
            if (!string.Equals(_fileLinkProgressName, progress.Name, StringComparison.Ordinal))
            {
                _fileLinkProgressName = progress.Name;
                _fileLinkProgressTick = now;
                _fileLinkProgressBytes = 0;
            }
            else
            {
                var ms = now - _fileLinkProgressTick;
                if (ms >= 200)
                {
                    var delta = progress.DoneBytes - _fileLinkProgressBytes;
                    if (delta > 0)
                    {
                        speed = (long)(delta * 1000.0 / ms);
                    }

                    _fileLinkProgressTick = now;
                    _fileLinkProgressBytes = progress.DoneBytes;
                }
            }

            var payload = new FileProgressPayload
            {
                FileName = progress.Name,
                Transferred = progress.DoneBytes,
                Total = progress.TotalBytes,
                Direction = progress.Receiving ? "receive" : "send",
                Done = done,
                SpeedBytesPerSecond = speed
            };
            Dispatcher.BeginInvoke(new Action(delegate { ApplyFileProgress(payload); }));
        }


        private void OnFileLinkFilesReceived(object sender, LanMouseShare.Agent.FileLink.FileLinkIncomingEventArgs e)
        {
            if (e == null || e.SavedPaths == null || e.SavedPaths.Length == 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                var finals = new List<string>();
                foreach (var path in e.SavedPaths)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    finals.Add(_fileLink != null ? _fileLink.ExpandIfArchive(path) : path);
                }

                if (finals.Count == 0)
                {
                    return;
                }

                AppLog.Event("INFO", "filelink.files.received",
                    "count=" + finals.Count + " first=" + finals[0]);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetStatus(UiText.FileTransferComplete + " " + finals.Count + " \u4E2A\u6587\u4EF6");
                    HintText.Text = finals[0];
                }));
            });
        }

        private void OnFileLinkClipboardReceived(object sender, LanMouseShare.Agent.FileLink.FileLinkClipboardEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            var payload = new ClipboardPayload
            {
                Format = e.Kind == LanMouseShare.Agent.FileLink.FileLinkProtocol.ClipboardImage
                    ? ClipboardFormats.ImagePng
                    : ClipboardFormats.Text,
                Text = e.Text,
                ImagePngBytes = e.Data
            };
            Dispatcher.BeginInvoke(new Action(delegate { _clipboardSync.ApplyRemote(payload); }));
        }

        private void OnFileLinkPushCompleted(object sender, LanMouseShare.Agent.FileLink.FileLinkPushResult result)
        {
            if (result == null)
            {
                return;
            }

            var ok = result.OkCount;
            var fail = result.FailCount;
            var error = result.Error;
            AppLog.Event(fail > 0 ? "WARN" : "INFO", "filelink.push.done",
                "ok=" + ok + " fail=" + fail + " error=" + (error ?? string.Empty));
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (fail > 0 || ok == 0)
                {
                    var reason = string.IsNullOrEmpty(error) ? ("\u5931\u8D25 " + fail + " \u4E2A") : error;
                    SetStatus(UiText.FileTransferFailed + reason);
                    HintText.Text = "\u8BF7\u786E\u8BA4\u5BF9\u7AEF\u5DF2\u8FD0\u884C\u540C\u4E00\u7248\u672C\u4E14\u5DF2\u914D\u5BF9\uFF08TCP 47541\uFF09";
                    return;
                }

                SetStatus(UiText.TransferSent + " " + ok + " \u4E2A\u6587\u4EF6");
                if (result.SourcePaths != null && result.SourcePaths.Length > 0)
                {
                    HintText.Text = result.SourcePaths[0];
                }
            }));
        }

        /// <summary>把本机选中的文件/文件夹推到对端（拖拽与"发送文件"按钮共用）。</summary>
        private void SendFilesViaFileLink(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            AppLog.Event("INFO", "filelink.push.start", "count=" + paths.Length + " first=" + paths[0]);
            SetStatus(UiText.TransferSending + " " + paths.Length + " \u4E2A\u6587\u4EF6");
            if (_fileLink == null)
            {
                SetStatus(UiText.FileTransferFailed + "\u6570\u636E\u901A\u9053\u672A\u521D\u59CB\u5316");
                return;
            }

            _fileLink.PushFiles(paths);
        }

        /// <summary>
        /// fix10：右键菜单"传输到对端"的批量结果（由 SendRequestChannel 去抖聚合后回调）。
        /// 该回调在命名管道线程上触发，切回 UI 线程再发文件。
        /// </summary>
        private void OnSendRequestBatch(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                SendFilesViaFileLink(paths);
            }));
        }

        /// <summary>文本/图片剪贴板也走 FileLink：旧路径要经过 Service 的命令队列，容易被状态卡住。</summary>
        private bool SendClipboardViaFileLink(ClipboardPayload payload)
        {
            if (payload == null || _fileLink == null)
            {
                return false;
            }

            if (payload.Format == ClipboardFormats.Text)
            {
                return _fileLink.SendClipboardText(payload.Text);
            }

            if (payload.Format == ClipboardFormats.ImagePng && payload.ImagePngBytes != null)
            {
                return _fileLink.SendClipboardImage(payload.ImagePngBytes);
            }

            return false;
        }

        private void OnFileClipboardAnnounced(object sender, FileClipboardDetectedEventArgs e)
        {
            if (e == null || e.FilePaths == null || e.FilePaths.Length == 0)
            {
                return;
            }

            _lastRemoteFileCount = e.FilePaths.Length;
            // Announcement only: nothing is transferred here. The peer pulls the bytes when the
            // user actually pastes, so copying a large file never floods the link.
            AppLog.Event("INFO", "clipboard.files.copied",
                "count=" + e.FilePaths.Length + " first=" + e.FilePaths[0]);
            // Twin announcement over the low-latency channel so the peer knows about the
            // files even when the TCP clipboard envelope is stalled.
            if (_directInputChannel != null)
            {
                _directInputChannel.SendClipboardFiles(e.FilePaths);
            }
            // fix8：同时用 FileLink 发一份清单（带真实对端路径与体积），
            // 对端据此注册"待粘贴文件"，粘贴时再逐个拉取。
            if (_fileLink != null)
            {
                _fileLink.OfferFiles(e.FilePaths);
            }
            SetStatus("\u526A\u8D34\u677F\u5DF2\u590D\u5236 " + e.FilePaths.Length + " \u4E2A\u6587\u4EF6\uFF0C\u8DE8\u5C4F\u540E\u6309 Ctrl+V \u7C98\u8D34");
        }

        private void OnClipboardFilesAnnounced(object sender, ClipboardFilesReceivedEventArgs e)
        {
            if (e == null || e.Paths == null || e.Paths.Length == 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                _clipboardSync.RegisterRemoteFiles(e.Paths, "udp");
                if (_clipboardSync.PublishVirtualFiles(e.Paths, null, FetchRemoteForClipboard))
                {
                    AppLog.Event("INFO", "clipboard.files.virtual.published",
                        "count=" + e.Paths.Length + " source=udp");
                    SetStatus("\u526A\u8D34\u677F\u5DF2\u540C\u6B65 " + e.Paths.Length +
                        " \u4E2A\u6587\u4EF6\uFF08\u7C98\u8D34\u65F6\u624D\u4F20\uFF09");
                    return;
                }

                SetStatus("\u5BF9\u7AEF\u5DF2\u590D\u5236 " + e.Paths.Length +
                    " \u4E2A\u6587\u4EF6\uFF0C\u53EF\u6309 Ctrl+V \u6216\u70B9\u201C\u62C9\u53D6\u5E76\u7C98\u8D34\u201D");
            }));
        }

        /// <summary>
        /// Intercepts Ctrl+V on the controlled side when the peer clipboard holds files.
        /// The paste is held back until the files arrive, then the clipboard is filled and
        /// the key press is replayed, so the file lands wherever the user pastes.
        /// </summary>
        private bool TryHandleClipboardPaste(InputPayload payload)
        {
            if (payload.Kind != InputKinds.Key || payload.VirtualKey != 0x56)
            {
                return false;
            }

            if (!payload.IsDown)
            {
                if (_pastingFiles)
                {
                    AppLog.Debug("clipboard.paste.swallow", "phase=keyup");
                    return true;
                }

                return false;
            }

            if (!IsInjectedKeyHeld(0x11) || !_clipboardSync.HasPendingRemoteFiles)
            {
                return false;
            }

            var files = _clipboardSync.PendingRemoteFiles;
            if (files == null || files.Length == 0)
            {
                return false;
            }

            BeginPasteFiles(files);
            return true;
        }

        /// <summary>
        /// Pulls the announced files and pastes them on this side. Used both by the injected key
        /// path and by the controller driven paste request.
        /// </summary>
        private void BeginPasteFiles(string[] files)
        {
            BeginPasteFiles(files, true);
        }

        private void BeginPasteFiles(string[] files, bool replayKey)
        {
            if (files == null || files.Length == 0 || _pastingFiles)
            {
                return;
            }

            _replayPasteKey = replayKey;

            _pastingFiles = true;
            _pendingPasteExpected = files.Length;
            _pendingPasteFiles.Clear();
            _pendingPasteStartedUtc = DateTime.UtcNow;
            var request = new ClipboardPullRequestPayload
            {
                FilePaths = files,
                RequesterDeviceId = _session.LocalDeviceId
            };
            var json = ProtocolSerializer.ToJson(request);
            AppLog.Event("INFO", "clipboard.pull.request",
                "count=" + files.Length + " first=" + files[0] + " target=" + _session.ActiveTargetDeviceId);
            // fix8：直接走 Agent↔Agent 的 FileLink 通道逐个拉取，不再让 Service 转发。
            // 旧链路里这一跳要先排队进 Service 的命令队列，暂停共享或管道繁忙时会被静默丢弃。
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var remote in files)
                {
                    if (!_pastingFiles)
                    {
                        return;
                    }

                    try
                    {
                        var local = PullOneFile(remote);
                        if (!string.IsNullOrEmpty(local))
                        {
                            var saved = local;
                            Dispatcher.BeginInvoke(new Action(delegate { OnPasteFileArrived(saved); }));
                        }
                        else
                        {
                            AppLog.Warn("clipboard.pull.empty path=" + remote);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("clipboard.pull.failed path=" + remote, ex);
                    }
                }
            });
            StartPasteTimeout();
            SetStatus("\u6B63\u5728\u63A5\u6536\u6587\u4EF6\uFF1A" + files.Length);
        }

        /// <summary>Asks the peer to pull our announced files and paste them on its side.</summary>
        private void RequestRemoteFilePaste()
        {
            var local = _clipboardSync.LocalFilePaths;
            var request = new ClipboardPasteRequestPayload
            {
                RequesterDeviceId = _session.LocalDeviceId,
                FileCount = local.Length,
                FilePaths = local
            };
            var json = ProtocolSerializer.ToJson(request);
            AppLog.Event("INFO", "clipboard.paste.request",
                "count=" + request.FileCount + " target=" + _session.ActiveTargetDeviceId);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.RequestClipboardPaste, json);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Clipboard paste request failed.", ex);
                }
            });
        }

        private void OnClipboardPasteRequested(string payloadJson)
        {
            // The request carries the copied paths, so the paste works even when the lazy
            // announcement has not arrived yet (that race was the "clipboard not updated" report).
            var request = string.IsNullOrEmpty(payloadJson)
                ? null
                : ProtocolSerializer.FromJson<ClipboardPasteRequestPayload>(payloadJson);
            if (request != null && request.FilePaths != null && request.FilePaths.Length > 0)
            {
                _clipboardSync.RegisterRemoteFiles(request.FilePaths, "paste-request");
            }

            if (!_clipboardSync.HasPendingRemoteFiles)
            {
                AppLog.Debug("clipboard.paste.request.nofiles", _link.Snapshot(DateTime.UtcNow.Ticks).Text());
                return;
            }

            BeginPasteFiles(_clipboardSync.PendingRemoteFiles);
        }

        /// <summary>
        /// Handles a real Ctrl+V typed on this machine while files copied on the peer are
        /// waiting in the lazy clipboard list. The paste is served locally: pull the files,
        /// put them on the clipboard and replay the key, so no key ever depends on the peer
        /// seeing an injected event (its hook filters those).
        /// </summary>
        private bool TryInterceptLocalFilePaste(InputCapturedEventArgs e)
        {
            // fix10：用户要求彻底放弃"复制文件 → 跨机粘贴"，改成资源管理器右键"传输到对端"。
            // 这里直接返回 false，Ctrl+V 不再被拦截改写成跨机文件粘贴请求（文本/图片剪贴板
            // 仍走原来的 ApplyRemote 通道，不受影响）。
            return false;
#pragma warning disable 0162
            if (e.Payload.Kind != InputKinds.Key)
            {
                return false;
            }

            var key = e.Payload.VirtualKey;
            if (key == 0x56 && !e.Payload.IsDown && _swallowPasteKeyUp)
            {
                _swallowPasteKeyUp = false;
                e.SuppressLocalInput = true;
                return true;
            }

            if (key != 0x56 || !e.Payload.IsDown)
            {
                return false;
            }

            if (_link.Role == ControlLinkRole.Owner)
            {
                return false;
            }

            if (!IsPhysicalKeyDown(0x11) || !_clipboardSync.HasPendingRemoteFiles)
            {
                return false;
            }

            var files = _clipboardSync.PendingRemoteFiles;
            if (files == null || files.Length == 0)
            {
                return false;
            }

            _swallowPasteKeyUp = true;
            e.SuppressLocalInput = true;
            AppLog.Event("INFO", "clipboard.paste.local-intercept",
                "count=" + files.Length +
                " role=" + _link.Role +
                " first=" + files[0]);
            BeginPasteFiles(files);
            return true;
#pragma warning restore 0162
        }

        private bool IsInjectedKeyHeld(int virtualKey)
        {
            return _injectedHeldKeys.Contains(virtualKey);
        }

        /// <summary>True while the key is physically held on this machine (ignores injected state).</summary>
        private static bool IsPhysicalKeyDown(int virtualKey)
        {
            return CursorController.IsKeyDown(virtualKey);
        }

        private void StartPasteTimeout()
        {
            if (_pasteTimeoutTimer == null)
            {
                _pasteTimeoutTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _pasteTimeoutTimer.Interval = TimeSpan.FromSeconds(1);
                _pasteTimeoutTimer.Tick += OnPasteTimeoutTick;
            }

            _pendingPasteLastProgressUtc = DateTime.UtcNow;
            _pasteTimeoutTimer.Stop();
            _pasteTimeoutTimer.Start();
        }

        /// <summary>
        /// A paste of a large file legitimately takes minutes, so the old fixed 20 s timeout
        /// aborted the transfer halfway (which reset the receiving connection). The timer now
        /// only fires when the transfer has made no progress for PasteIdleTimeoutSeconds, and the
        /// hard ceiling just protects against a wedged state.
        /// </summary>
        private void OnPasteTimeoutTick(object sender, EventArgs e)
        {
            var timer = (DispatcherTimer)sender;
            if (!_pastingFiles)
            {
                timer.Stop();
                return;
            }

            var now = DateTime.UtcNow;
            var idle = (now - _pendingPasteLastProgressUtc).TotalSeconds;
            var total = (now - _pendingPasteStartedUtc).TotalSeconds;
            if (idle < PasteIdleTimeoutSeconds && total < PasteMaxSeconds)
            {
                return;
            }

            timer.Stop();
            AppLog.Warn("clipboard.paste.timeout " +
                "expected=" + _pendingPasteExpected + " received=" + _pendingPasteFiles.Count +
                " idleMs=" + (int)(idle * 1000) +
                " elapsedMs=" + (int)(total * 1000));
            _pastingFiles = false;
            _pendingPasteFiles.Clear();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.CancelTransfers, "{}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("CancelTransfers failed. " + ex.Message);
                }
            });
            Notify("\u7C98\u8D34\u8D85\u65F6", "\u5BF9\u7AEF\u672A\u80FD\u53D1\u9001\u6587\u4EF6\uFF0C\u8BF7\u91CD\u8BD5\u3002");
        }

        /// <summary>
        /// Folders are zipped by the sending side (0.1.64). Unpack the archive next to it and
        /// hand the folder to the clipboard, so pasting a folder works like pasting a file.
        /// </summary>
        private static string ExpandClipboardArchive(string savedPath)
        {
            try
            {
                var name = System.IO.Path.GetFileName(savedPath);
                if (!name.StartsWith("lms-copy-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return savedPath;
                }

                var directory = System.IO.Path.GetDirectoryName(savedPath);
                var stem = name;
                if (stem.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem.Substring(0, stem.Length - 4);
                }

                if (stem.StartsWith("lms-copy-", StringComparison.Ordinal))
                {
                    stem = stem.Substring("lms-copy-".Length);
                }

                var dash = stem.LastIndexOf('-');
                if (dash > 0 && stem.Length - dash - 1 == 8 && IsHexSuffix(stem.Substring(dash + 1)))
                {
                    stem = stem.Substring(0, dash);
                }

                if (stem.Length == 0)
                {
                    stem = "clipboard-folder";
                }

                var temp = System.IO.Path.Combine(directory, "lms-unpack-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                System.IO.Compression.ZipFile.ExtractToDirectory(savedPath, temp);
                System.IO.File.Delete(savedPath);

                var effective = System.IO.Path.Combine(directory, stem);
                var innerDirs = System.IO.Directory.GetDirectories(temp);
                var innerFiles = System.IO.Directory.GetFiles(temp);
                if (innerDirs.Length == 1 && innerFiles.Length == 0)
                {
                    var innerName = System.IO.Path.GetFileName(innerDirs[0]);
                    if (!string.IsNullOrEmpty(innerName))
                    {
                        effective = System.IO.Path.Combine(directory, innerName);
                    }
                }

                if (System.IO.Directory.Exists(effective))
                {
                    effective = effective + "-" + DateTime.Now.ToString("HHmmss");
                }

                if (innerDirs.Length == 1 && innerFiles.Length == 0)
                {
                    System.IO.Directory.Move(innerDirs[0], effective);
                }
                else
                {
                    System.IO.Directory.Move(temp, effective);
                }

                if (System.IO.Directory.Exists(temp))
                {
                    System.IO.Directory.Delete(temp, true);
                }

                AppLog.Event("INFO", "clipboard.files.extracted", "folder=" + effective);
                return effective;

            }
            catch (Exception ex)
            {
                AppLog.Warn("clipboard.files.extract.failed path=" + savedPath + " error=" + ex.Message);
                return savedPath;
            }
        }

        private static bool IsHexSuffix(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private void OnPasteFileArrived(string savedPath)
        {
            if (string.IsNullOrEmpty(savedPath))
            {
                return;
            }

            if (!_pastingFiles)
            {
                return;
            }

            _pendingPasteFiles.Add(savedPath);
            AppLog.Debug("clipboard.paste.arrived",
                "received=" + _pendingPasteFiles.Count + " expected=" + _pendingPasteExpected + " path=" + savedPath);
            if (_pendingPasteFiles.Count < _pendingPasteExpected)
            {
                return;
            }

            CompleteClipboardPaste();
        }

        private void CompleteClipboardPaste()
        {
            if (_pasteTimeoutTimer != null)
            {
                _pasteTimeoutTimer.Stop();
            }

            var files = _pendingPasteFiles.ToArray();
            var replay = _replayPasteKey;
            _replayPasteKey = true;
            _pastingFiles = false;
            _pendingPasteFiles.Clear();
            _clipboardSync.ClearPendingRemoteFiles();
            _clipboardSync.PutFilesOnClipboardAsync(files, delegate(bool ok)
            {
                if (!ok)
                {
                    AppLog.Warn("clipboard.paste.failed " + "reason=clipboard-set files=" + files.Length);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetStatus("\u7C98\u8D34\u5931\u8D25\uFF1A\u526A\u8D34\u677F\u672A\u5199\u5165");
                    }));
                    return;
                }

                AppLog.Event(replay ? "INFO" : "DEBUG", replay ? "clipboard.files.pasted" : "clipboard.files.prefetched", "count=" + files.Length + " first=" + files[0]);
                if (!replay)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetStatus("\u5DF2\u62C9\u53D6 " + files.Length + " \u4E2A\u6587\u4EF6\u5230\u526A\u8D34\u677F");
                    }));
                    return;
                }

                try
                {
                    // Replay the paste now that the clipboard really holds files on this side.
                    _injector.Inject(new InputPayload
                    {
                        Kind = InputKinds.Key,
                        VirtualKey = 0x56,
                        ScanCode = 0x2F,
                        IsDown = true
                    });
                    Thread.Sleep(40);
                    _injector.Inject(new InputPayload
                    {
                        Kind = InputKinds.Key,
                        VirtualKey = 0x56,
                        ScanCode = 0x2F,
                        IsDown = false
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("Replay clipboard paste failed.", ex);
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetStatus("\u5DF2\u7C98\u8D34 " + files.Length + " \u4E2A\u6587\u4EF6");
                }));
            });
        }

        private void ApplyPeerPause(string payloadJson)
        {
            var payload = ProtocolSerializer.FromJson<ControlPausePayload>(payloadJson);
            StopLocalCrossScreenState();
            _session.SetPaused(true);
            _link.SetPaused(true, DateTime.UtcNow.Ticks);
            SyncLinkFlags("peer-pause");
            SetStatus(payload != null && payload.StopAll ? UiText.ControlStopped : UiText.CrossScreenPaused);
            UpdatePauseButton();
            UpdateControlTargetText();
        }

        private void ApplyPeerResume()
        {
            _session.SetPaused(false);
            _link.SetPaused(false, DateTime.UtcNow.Ticks);
            SyncLinkFlags("peer-resume");
            SetStatus(UiText.CrossScreenResumed);
            UpdatePauseButton();
            UpdateControlTargetText();
        }

        private void ApplyPeerControlTarget(string payloadJson)
        {
            var payload = ProtocolSerializer.FromJson<ControlTargetPayload>(payloadJson);
            if (payload != null)
            {
                _session.SetTarget(payload.ActiveTargetDeviceId);
            }

            // Display-only mirror. Control role is owned by ControlLinkMachine and must never be
            // flipped by a peer-announced target (see docs/CONTROL-STATE-MODEL.md I5).
            AppLog.Debug("control.target.mirror",
                "target=" + _session.ActiveTargetDeviceId +
                " reason=" + (payload != null ? payload.Reason : "-") +
                " " + _link.Snapshot(DateTime.UtcNow.Ticks).Text());
            UpdateControlTargetText();
        }

        private void ApplyPeerControlRelease(string payloadJson)
        {
            var payload = ProtocolSerializer.FromJson<ControlReleasePayload>(payloadJson);
            if (_link.Role == ControlLinkRole.Owner)
            {
                ReleaseControlToLocalEdge(payload);
                return;
            }

            _link.Release(ControlLinkReleaseReason.PeerReleased, DateTime.UtcNow.Ticks);
            SyncLinkFlags("peer-release");
            _remoteReturnDetector.Reset();
            ArmEdgeSwitch("peer released");
            AppLog.Event("INFO", "control.link.released",
                "reason=PeerReleased remotePoint=" +
                (payload != null ? payload.RemoteX + "," + payload.RemoteY : "-") +
                " detail=" + (payload != null ? payload.Reason : "-"));
            UpdateControlTargetText();
        }

        private void ReleaseControlToLocalEdge(ControlReleasePayload release)
        {
            var direction = _state != null ? _state.LayoutDirection : "Right";
            var localScreen = _localScreenBounds.Width > 0 ? _localScreenBounds : GetActiveScreenBounds(CursorController.GetPosition().X, CursorController.GetPosition().Y);
            var remoteWidth = release != null && release.RemoteScreenWidth > 0 ? release.RemoteScreenWidth : _remoteVirtualScreen.Width;
            var remoteHeight = release != null && release.RemoteScreenHeight > 0 ? release.RemoteScreenHeight : _remoteVirtualScreen.Height;
            var remoteX = release != null ? Clamp(release.RemoteX, 0, Math.Max(0, remoteWidth - 1)) : Clamp(_remoteVirtualX, 0, Math.Max(0, remoteWidth - 1));
            var remoteY = release != null ? Clamp(release.RemoteY, 0, Math.Max(0, remoteHeight - 1)) : Clamp(_remoteVirtualY, 0, Math.Max(0, remoteHeight - 1));
            ReleaseControl(true);
            _session.SetLocalTarget();

            var mapped = EdgeTransitionMapper.MapReturnToLocal(
                new DrawingPoint(remoteX, remoteY),
                new DrawingSize(remoteWidth, remoteHeight),
                new DrawingSize(localScreen.Width, localScreen.Height),
                direction);
            var targetX = localScreen.Left + mapped.X;
            var targetY = localScreen.Top + mapped.Y;

            // Land clearly inside the screen, not 8 px from the edge. Without this the pointer sat
            // inside the edge-switch band after a return, the switch stayed disarmed, and the next
            // attempt did nothing until the user moved away and pushed again ("cross twice").
            const int ReturnInset = 40;
            if (direction == "Right")
            {
                targetX -= ReturnInset;
            }
            else if (direction == "Left")
            {
                targetX += ReturnInset;
            }
            else if (direction == "Up")
            {
                targetY += ReturnInset;
            }
            else if (direction == "Down")
            {
                targetY -= ReturnInset;
            }

            targetX = Clamp(targetX, localScreen.Left, localScreen.Right - 2);
            targetY = Clamp(targetY, localScreen.Top, localScreen.Bottom - 2);

            CursorController.SetPosition(targetX, targetY);
            ArmEdgeSwitch("return local");
            AppLog.Event("INFO", "screen.switch.back",
                "direction=" + direction +
                " remotePoint=" + remoteX + "," + remoteY +
                " remoteScreen=" + remoteWidth + "x" + remoteHeight +
                " mapped=" + mapped.X + "," + mapped.Y +
                " localPoint=" + targetX + "," + targetY +
                " localScreen=" + localScreen.Left + "," + localScreen.Top + "," + localScreen.Width + "," + localScreen.Height +
                " reason=" + (release != null ? release.Reason : "-"));
            UpdateControlTargetText();
        }

        private void OnDropFiles(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                AppLog.Event("INFO", "file.drop.ui", "count=" + (paths != null ? paths.Length : 0));
                SendFilesViaFileLink(paths);
            }
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Fallback for drag and drop: when the agent runs elevated, Explorer (medium integrity)
        /// cannot hand a drop to this window because of UIPI. The picker always works.
        /// </summary>
        private void OnSendFilesClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = UiText.SendFilesTitle,
                Multiselect = true,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            {
                AppLog.Event("INFO", "file.pick", "count=" + dialog.FileNames.Length);
                SendFilesViaFileLink(dialog.FileNames);
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= 1024L * 1024)
            {
                return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            if (bytes >= 1024)
            {
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        /// <summary>Draws the transfer bar from the service's FileProgress events.</summary>
        private void ApplyFileProgress(FileProgressPayload progress)
        {
            if (progress == null)
            {
                return;
            }

            _pendingPasteLastProgressUtc = DateTime.UtcNow;
            _lastTransferProgressUtc = DateTime.UtcNow;

            var total = progress.Total > 0 ? progress.Total : 1;
            var percent = Math.Max(0, Math.Min(100, (int)(progress.Transferred * 100.0 / total)));
            var arrow = progress.Direction == "receive" ? "<-" : "->";
            var text = (progress.Direction == "receive" ? UiText.TransferReceiving : UiText.TransferSending) +
                       " " + (progress.FileName ?? "") +
                       " " + arrow + " " + percent + "%" +
                       " (" + FormatFileSize(progress.Transferred) + " / " + FormatFileSize(progress.Total) + ")" +
                       " " + FormatFileSize(progress.SpeedBytesPerSecond) + "/s";

            if (text != _lastTransferText)
            {
                _lastTransferText = text;
                AppLog.Debug("file.progress", "dir=" + progress.Direction + " pct=" + percent +
                                              " bytes=" + progress.Transferred + "/" + progress.Total +
                                              " speed=" + progress.SpeedBytesPerSecond);
            }

            if (TransferProgress != null)
            {
                TransferProgress.Value = progress.Done ? 100 : percent;
            }

            if (TransferStatusText != null)
            {
                TransferStatusText.Text = progress.Done
                    ? (progress.Direction == "receive" ? UiText.TransferReceived : UiText.TransferSent) +
                      " " + (progress.FileName ?? "") + " (" + FormatFileSize(progress.Total) + ")"
                    : text;
            }
        }

        private void OnPairClicked(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand(AgentCommands.PairDevice, PairingCodeBox.Text);
        }

        private void OnApproveClicked(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand(AgentCommands.ApprovePairing, PairingCodeBox.Text);
        }

        private void OnUseManualIpClicked(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand(AgentCommands.SetManualPeer, ManualIpBox.Text);
        }

        private void OnDirectionClicked(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null)
            {
                return;
            }

            var layout = new LayoutConfig { PeerDirection = button.Tag.ToString() };
            var json = ProtocolSerializer.ToJson(layout);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.UpdateLayout, json);
                    RefreshStateAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Layout update failed.", ex);
                }
            });
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            RefreshStateAsync();
        }

        private void OnPauseClicked(object sender, RoutedEventArgs e)
        {
            TogglePauseControl(true);
        }

        private void OnStopControlClicked(object sender, RoutedEventArgs e)
        {
            StopControlEverywhere(true);
        }

        private void OnCancelTransfersClicked(object sender, RoutedEventArgs e)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // fix9：新通道的取消是真的断连接，不是发个没人理的命令。
                    if (_fileLink != null)
                    {
                        _fileLink.CancelActive();
                    }

                    try
                    {
                        _pipe.Send(AgentCommands.CancelTransfers, "{}");
                    }
                    catch (Exception)
                    {
                    }

                    AppLog.Event("INFO", "file.transfer.cancel.request", "source=ui");
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetStatus("已取消传输");
                        HintText.Text = "半截文件已清理，可以重新发送或重新复制";
                        if (TransferProgress != null)
                        {
                            TransferProgress.Value = 0;
                        }
                    }));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("CancelTransfers failed. " + ex.Message);
                }
            });
        }

        private void OnOpenTransferFolderClicked(object sender, RoutedEventArgs e)
        {
            OpenTransferFolder();
        }

        private void OnOpenLogsClicked(object sender, RoutedEventArgs e)
        {
            OpenLogFolder(AppLog.LiveDirectory);
        }

        private void OnUninstallClicked(object sender, RoutedEventArgs e)
        {
            RequestUninstall();
        }

        /// <summary>
        /// 从软件里发起卸载：结束自己再以管理员身份拉起安装器的卸载流程
        /// （安装器会接着问"要不要连配置和日志一起删"）。
        /// </summary>
        private void RequestUninstall()
        {
            var setupPath = FindUninstaller();
            if (string.IsNullOrEmpty(setupPath))
            {
                MessageBox.Show(UiText.UninstallNotFound, UiText.Uninstall, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(UiText.UninstallConfirm, UiText.Uninstall, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo(setupPath, "--uninstall");
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                AppLog.Info("Uninstall requested. Setup=" + setupPath);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                AppLog.Error("Uninstall launch failed.", ex);
                SetStatus(UiText.Uninstall + " " + ex.Message);
                return;
            }

            ExitApplication();
        }

        /// <summary>
        /// 安装版布局是 &lt;安装目录&gt;\Agent\LanMouseShare.Agent.exe，
        /// 卸载程序在上一级；也兼容安装器被放在别处时按常见路径找。
        /// </summary>
        private static string FindUninstaller()
        {
            var candidates = new List<string>();
            try
            {
                var agentDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var root = Path.GetDirectoryName(agentDirectory.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(root))
                {
                    candidates.Add(Path.Combine(root, "LanMouseShareSetup.exe"));
                }
            }
            catch (Exception)
            {
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LanMouseShare", "LanMouseShareSetup.exe"));

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private void OnOpenRemoteLogsClicked(object sender, RoutedEventArgs e)
        {
            OpenLogFolder(AppLog.RemoteDirectory);
        }

        private void OpenLogFolder(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", "\"" + directory + "\"");
            }
            catch (Exception ex)
            {
                AppLog.Error("Open logs failed. Directory=" + directory, ex);
            }
        }

        private void OnShareRoleChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ShareRoleBox.SelectedItem as ComboBoxItem;
            var role = item != null && item.Tag is string ? ShareRoleValues.Normalize(item.Tag as string) : ShareRoleValues.Controller;
            if (string.Equals(role, _shareRole, StringComparison.Ordinal))
            {
                return;
            }

            _shareRole = role;
            SaveSettingsAsync();
            RefreshStateAsync();
        }

        private void OnLogSettingsLostFocus(object sender, RoutedEventArgs e)
        {
            var addressText = (ControllerAddressBox.Text ?? string.Empty).Trim();
            var tokenText = (DebugTokenBox.Text ?? string.Empty).Trim();
            var retentionText = (LogRetentionBox.Text ?? string.Empty).Trim();
            var retention = _logRetentionDays;
            if (!int.TryParse(retentionText, out retention) || retention <= 0 || retention > 30)
            {
                retention = _logRetentionDays <= 0 ? 3 : _logRetentionDays;
                LogRetentionBox.Text = retention.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var changed = !string.Equals(addressText, _controllerAddress, StringComparison.Ordinal) ||
                          !string.Equals(tokenText, _debugToken, StringComparison.Ordinal) ||
                          retention != _logRetentionDays;
            _controllerAddress = addressText;
            _debugToken = tokenText;
            _logRetentionDays = retention;
            if (changed)
            {
                SaveSettingsAsync();
            }
        }

        private void OnMouseSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _mouseSpeedMultiplier = e.NewValue;
            if (MouseSpeedText != null)
            {
                MouseSpeedText.Text = _mouseSpeedMultiplier.ToString("0.00") + "x";
            }

            SaveSettingsAsync();
        }

        private void OnMouseRateChanged(object sender, SelectionChangedEventArgs e)
        {
            _mouseUpdateRateHz = SelectedMouseRate();
            if (MouseRateText != null)
            {
                MouseRateText.Text = _mouseUpdateRateHz + "Hz";
            }

            if (CurrentRateText != null)
            {
                CurrentRateText.Text = _mouseUpdateRateHz + "Hz";
            }

            if (_remoteInputSender != null)
            {
                _remoteInputSender.SetMouseUpdateRate(_mouseUpdateRateHz);
            }

            SaveSettingsAsync();
        }

        private void OnSettingsChanged(object sender, RoutedEventArgs e)
        {
            // UI 线程上顺手把剪贴板开关镜像到 volatile 字段，供剪贴板线程/管道线程读取。
            _clipboardEnabledCache = ClipboardEnabledBox != null && ClipboardEnabledBox.IsChecked == true;
            SaveSettingsAsync();
        }

        private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            key = key == Key.ImeProcessed ? e.ImeProcessedKey : key;
            if (IsModifierOnly(key))
            {
                e.Handled = true;
                return;
            }

            var text = BuildHotkeyText(Keyboard.Modifiers, key);
            if (!string.IsNullOrEmpty(text))
            {
                box.Text = text;
                SaveHotkeyFieldsFromUi();
                SaveSettingsAsync();
            }

            e.Handled = true;
        }

        private void OnHotkeyLostFocus(object sender, RoutedEventArgs e)
        {
            SaveHotkeyFieldsFromUi();
            SaveSettingsAsync();
        }

        private void SaveSettingsAsync()
        {
            if (!_settingsLoaded || _applyingSettingsToUi)
            {
                return;
            }

            SaveHotkeyFieldsFromUi();
            if (HasDuplicateHotkeys())
            {
                SetStatus(UiText.HotkeyDuplicate);
                return;
            }

            _startWithWindows = StartWithWindowsBox.IsChecked == true;
            _minimizeToTrayOnClose = MinimizeToTrayBox.IsChecked == true;
            _blockScreenCorners = BlockCornersBox.IsChecked == true;
            _runAsAdministratorByDefault = RunAsAdminDefaultBox.IsChecked == true;
            _verboseLogging = VerboseLoggingBox.IsChecked == true;
            _logTransferEnabled = LogTransferBox.IsChecked == true;
            ApplyStartupOptions(_startWithWindows, _runAsAdministratorByDefault);

            var settings = new AgentSettingsDto
            {
                MouseSpeedMultiplier = _mouseSpeedMultiplier,
                MouseUpdateRateHz = _mouseUpdateRateHz,
                ClipboardEnabled = ClipboardEnabledBox.IsChecked == true,
                PauseHotkey = _pauseHotkey,
                StopControlHotkey = _stopControlHotkey,
                ExitHotkey = _exitHotkey,
                StartWithWindows = _startWithWindows,
                MinimizeToTrayOnClose = _minimizeToTrayOnClose,
                BlockScreenCorners = _blockScreenCorners,
                RunAsAdministratorByDefault = _runAsAdministratorByDefault,
                ShareRole = _shareRole,
                ControllerAddress = _controllerAddress,
                DebugToken = _debugToken,
                VerboseLogging = _verboseLogging,
                LogRetentionDays = _logRetentionDays,
                LogTransferEnabled = _logTransferEnabled
            };
            var json = ProtocolSerializer.ToJson(settings);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.UpdateSettings, json);
                }
                catch (Exception ex)
                {
                    AppLog.Error("UpdateSettings failed.", ex);
                }
            });
        }

        private void SaveHotkeyFieldsFromUi()
        {
            _pauseHotkey = NormalizeHotkeyText(PauseHotkeyBox.Text, "Ctrl+F11");
            _stopControlHotkey = NormalizeHotkeyText(StopHotkeyBox.Text, "Ctrl+F10");
            _exitHotkey = NormalizeHotkeyText(ExitHotkeyBox.Text, "Ctrl+Alt+End");
            if (!string.Equals(PauseHotkeyBox.Text, _pauseHotkey, StringComparison.Ordinal))
            {
                PauseHotkeyBox.Text = _pauseHotkey;
            }

            if (!string.Equals(StopHotkeyBox.Text, _stopControlHotkey, StringComparison.Ordinal))
            {
                StopHotkeyBox.Text = _stopControlHotkey;
            }

            if (!string.Equals(ExitHotkeyBox.Text, _exitHotkey, StringComparison.Ordinal))
            {
                ExitHotkeyBox.Text = _exitHotkey;
            }
        }

        private bool HasDuplicateHotkeys()
        {
            return string.Equals(_pauseHotkey, _stopControlHotkey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(_pauseHotkey, _exitHotkey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(_stopControlHotkey, _exitHotkey, StringComparison.OrdinalIgnoreCase);
        }

        private void SendSimpleCommand(string command, string payload)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(command, payload);
                    RefreshStateAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Command failed. Command=" + command, ex);
                }
            });
        }

        private void SendControlTarget(string targetDeviceId, string reason)
        {
            if (_remoteInputSender == null || string.IsNullOrWhiteSpace(targetDeviceId))
            {
                return;
            }

            var payload = new ControlTargetPayload
            {
                ActiveTargetDeviceId = targetDeviceId,
                Reason = reason
            };
            _remoteInputSender.SendCommand(AgentCommands.SetControlTarget, ProtocolSerializer.ToJson(payload));
        }

        private void TogglePauseControl(bool notifyPeer)
        {
            if (_session.IsPaused)
            {
                _session.SetPaused(false);
                _link.SetPaused(false, DateTime.UtcNow.Ticks);
                if (notifyPeer && _remoteInputSender != null)
                {
                    _remoteInputSender.SendCommand(AgentCommands.ResumeControl, "{}");
                }

                SetStatus(UiText.CrossScreenResumed);
            }
            else
            {
                _session.SetPaused(true);
                _link.SetPaused(true, DateTime.UtcNow.Ticks);
                StopLocalCrossScreenState();
                if (notifyPeer && _remoteInputSender != null)
                {
                    var payload = new ControlPausePayload { StopAll = false, Reason = "Pause" };
                    _remoteInputSender.SendCommand(AgentCommands.PauseControl, ProtocolSerializer.ToJson(payload));
                }

                SetStatus(UiText.CrossScreenPaused);
            }

            UpdatePauseButton();
            UpdateControlTargetText();
        }

        private void StopControlEverywhere(bool notifyPeer)
        {
            _session.SetPaused(true);
            _link.SetPaused(true, DateTime.UtcNow.Ticks);
            StopLocalCrossScreenState();
            if (notifyPeer && _remoteInputSender != null)
            {
                var payload = new ControlPausePayload { StopAll = true, Reason = "StopAll" };
                _remoteInputSender.SendCommand(AgentCommands.PauseControl, ProtocolSerializer.ToJson(payload));
            }

            SetStatus(UiText.ControlStopped);
            UpdatePauseButton();
            UpdateControlTargetText();
        }

        private void StopLocalCrossScreenState()
        {
            _link.Release(ControlLinkReleaseReason.StopAll, DateTime.UtcNow.Ticks);
            _session.SetLocalTarget();
            _remoteReturnDetector.Reset();
            _injectionFailures.Reset();
            ResetEdgeSwitch("stop local cross-screen");
            _hasLastLocalMousePoint = false;
            if (_remoteInputSender != null)
            {
                _remoteInputSender.Clear();
            }

            _injector.ReleaseAllMouseButtons();
            SyncLinkFlags("stop-local");
        }

        private void ReleaseControl(bool notifyPeer)
        {
            var now = DateTime.UtcNow.Ticks;
            var wasOwner = _link.Role == ControlLinkRole.Owner;
            var wasTarget = _link.Role == ControlLinkRole.Target;
            var epoch = _link.Epoch;
            _link.Release(ControlLinkReleaseReason.LocalRequest, now);
            _session.SetLocalTarget();
            _remoteReturnDetector.Reset();
            _injectionFailures.Reset();
            ResetEdgeSwitch("release");
            _hasLastLocalMousePoint = false;
            if (_remoteInputSender != null)
            {
                _remoteInputSender.Clear();
            }

            if (notifyPeer && wasOwner && _remoteInputSender != null)
            {
                SendControlTarget(_session.ActiveTargetDeviceId, "Release");
                _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, "{}");
            }

            if (wasOwner || wasTarget)
            {
                AppLog.Event("INFO", "control.link.release",
                    "reason=LocalRequest epoch=" + epoch +
                    " wasOwner=" + (wasOwner ? "1" : "0") +
                    " wasTarget=" + (wasTarget ? "1" : "0") +
                    " notifyPeer=" + (notifyPeer ? "1" : "0"));
            }

            SyncLinkFlags("release");

            if (_state != null && _state.IsConnected && !_session.IsPaused)
            {
                SetStatus(UiText.Connected + (_state.PeerDeviceName ?? _state.PeerAddress));
            }

            UpdateControlTargetText();
        }

        private void UpdatePauseButton()
        {
            if (PauseButton != null)
            {
                PauseButton.Content = _session.IsPaused ? UiText.Restore : UiText.Pause;
            }
        }

        private void UpdateControlTargetText()
        {
            if (ControlTargetText == null)
            {
                return;
            }

            if (_session.IsPaused)
            {
                ControlTargetText.Text = UiText.TargetLocalPaused;
            }
            else if (_session.IsTargetPeer)
            {
                ControlTargetText.Text = UiText.TargetPeer + (_state != null ? _state.PeerDeviceName ?? string.Empty : string.Empty);
            }
            else if (_link.Role == ControlLinkRole.Target)
            {
                ControlTargetText.Text = UiText.TargetLocalPeerCanInput;
            }
            else
            {
                ControlTargetText.Text = UiText.TargetLocal;
            }
        }

        private void SyncLinkFlags(string reason)
        {
            var snapshot = _link.Snapshot(DateTime.UtcNow.Ticks);
            if (snapshot.Role == ControlLinkRole.Owner)
            {
                if (_link.Role != ControlLinkRole.Owner || _ownerSinceUtc == DateTime.MinValue)
                {
                    _ownerSinceUtc = DateTime.UtcNow;
                    // fix19：每次重新接管对端都从"未武装"开始——必须先进到对端 ≥40px，
                    // 才允许把左缘的推动判成"要回去"。
                    _ownerReturnArmed = false;
                    ResetOwnerReturnState();
                }

                _session.SetPeerTarget();
            }
            else
            {
                _ownerSinceUtc = DateTime.MinValue;
            }

            if (snapshot.Role != ControlLinkRole.Owner)
            {
                _session.SetLocalTarget();
            }

            if (snapshot.Role != ControlLinkRole.Owner)
            {
                // Parked-edge guard: after leaving the target (or losing control) the physical
                // cursor may still sit on the screen edge, which would instantly re-trigger a
                // cross-screen switch. Arm the detector so the edge must be left first.
                ArmEdgeSwitch("link-" + snapshot.Role);
            }

            AppLog.Debug("control.link.sync", "reason=" + reason + " " + snapshot.Text());
            if (snapshot.Role != ControlLinkRole.Target)
            {
                ReleaseInjectedKeys(reason);
            }
        }

        private void TrackInjectedKey(InputPayload payload)
        {
            var key = payload.VirtualKey != 0 ? payload.VirtualKey : payload.ScanCode;
            if (key == 0)
            {
                return;
            }

            if (payload.IsDown)
            {
                _injectedHeldKeys.Add(key);
            }
            else
            {
                _injectedHeldKeys.Remove(key);
            }

            var now = DateTime.UtcNow;
            if ((now - _lastKeyLogUtc).TotalMilliseconds < 200)
            {
                return;
            }

            AppLog.Debug("key.inject",
                "vk=" + payload.VirtualKey + " scan=" + payload.ScanCode +
                " down=" + (payload.IsDown ? "1" : "0") +
                " extended=" + (payload.IsExtendedKey ? "1" : "0") +
                " sys=" + (payload.IsSystemKey ? "1" : "0") +
                " held=" + _injectedHeldKeys.Count);
            _lastKeyLogUtc = now;
        }

        /// <summary>
        /// Injects KEYUP for keys that are still logically held on this machine. Without this the
        /// remote computer keeps a modifier or a repeat down after control changes hands, which is
        /// the "keyboard does not really interoperate" symptom.
        /// </summary>
        private void ReleaseInjectedKeys(string reason)
        {
            if (_injectedHeldKeys.Count == 0)
            {
                return;
            }

            var keys = new int[_injectedHeldKeys.Count];
            _injectedHeldKeys.CopyTo(keys);
            _injectedHeldKeys.Clear();
            for (int i = 0; i < keys.Length; i++)
            {
                var up = new InputPayload { Kind = InputKinds.Key, VirtualKey = keys[i], IsDown = false };
                _injector.Inject(up);
            }

            AppLog.Info("Released keys still held locally. Count=" + keys.Length + " Reason=" + reason);
        }

        private void LogMouseClamped(DrawingPoint point)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastBoundaryLogUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            AppLog.Debug("mouse.inject.clamped", "cursor=" + point.X + "," + point.Y);
            _lastBoundaryLogUtc = now;
        }

        private const int RelayLeaseIntervalMs = 300;
        private const int RelayLeaseSkipAckMs = 400;
        private string _lastLeaseSource = "udp";

        private void SendLeaseNow(ControlLease lease)
        {
            if (lease == null)
            {
                return;
            }

            var viaUdp = _directInputChannel != null && _directInputChannel.SendControlLease(lease);
            if (!viaUdp)
            {
                AppLog.Debug("control.lease.send.fail",
                    "path=udp epoch=" + lease.Epoch +
                    " hasPeer=" + (_directInputChannel != null && _directInputChannel.HasPeer ? "1" : "0"));
            }

            var now = DateTime.UtcNow.Ticks;
            var snapshot = _link.Snapshot(now);
            if (snapshot.AckAgeMs >= 0 && snapshot.AckAgeMs < RelayLeaseSkipAckMs)
            {
                return;
            }

            if (now - _lastRelayLeaseTicks < TimeSpan.TicksPerMillisecond * RelayLeaseIntervalMs)
            {
                return;
            }

            _lastRelayLeaseTicks = now;
            var payload = new ControlLeasePayload();
            payload.Epoch = lease.Epoch;
            payload.OwnerDeviceId = lease.OwnerDeviceId;
            payload.TtlMs = lease.TtlMs;
            payload.Sequence = lease.Sequence;
            var json = ProtocolSerializer.ToJson(payload);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.SendControlLease, json, PipeConstants.InputTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("control.lease.relay.fail", "error=" + ex.Message);
                }
            });
        }

        private void SendAckNow()
        {
            SendAckNow(_lastLeaseSource == "relay");
        }

        private void SendAckNow(bool relayThroughService)
        {
            var now = DateTime.UtcNow.Ticks;
            var epoch = _link.Epoch;
            if (_directInputChannel != null)
            {
                _directInputChannel.SendControlAck(epoch);
            }

            _link.MarkAckSent(now);

            if (!relayThroughService)
            {
                return;
            }

            var payload = new ControlLeaseAckPayload();
            payload.Epoch = epoch;
            var json = ProtocolSerializer.ToJson(payload);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.SendControlLeaseAck, json, PipeConstants.InputTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("control.ack.relay.fail", "error=" + ex.Message);
                }
            });
        }

        private void LinkLoop()
        {
            while (_linkRunning)
            {
                try
                {
                    TickLink(DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Link beat failed. Error=" + ex.Message);
                }

                Thread.Sleep(ControlLinkMachine.LeaseIntervalMs);
            }
        }

        private void TickLink(long now)
        {
            ControlLinkReleaseReason reason;
            if (_link.Tick(now, out reason))
            {
                AppLog.Event("WARN", "control.link.timeout",
                    "reason=" + reason + " " + _link.Snapshot(now).Text());
                Dispatcher.BeginInvoke(new Action(delegate { HandleLinkTimeout(reason); }));
                return;
            }

            LogLinkTelemetry(now);

            if (_link.ShouldSendLease(now))
            {
                SendLeaseNow(_link.BuildLease(now));
            }

            if (_link.ShouldSendAck(now))
            {
                SendAckNow();
            }
        }

        private void HandleLinkTimeout(ControlLinkReleaseReason reason)
        {
            _session.SetLocalTarget();
            _remoteReturnDetector.Reset();
            _injectionFailures.Reset();
            ResetEdgeSwitch("link timeout");
            _hasLastLocalMousePoint = false;
            if (_remoteInputSender != null)
            {
                _remoteInputSender.Clear();
            }

            SyncLinkFlags("timeout");
            UpdateControlTargetText();
        }

        private void OnControlLeaseReceived(object sender, ControlLeaseReceivedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            ApplyIncomingLease(e.Lease, "udp");
        }

        private void ApplyIncomingLease(ControlLease lease, string source)
        {
            if (lease == null)
            {
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            var wasOwner = _link.Role == ControlLinkRole.Owner;
            _lastLeaseSource = source;
            var changed = _link.OnLease(lease, now);

            if (_link.ShouldSendAck(now))
            {
                SendAckNow(source == "relay");
            }

            if (!changed || _link.Role != ControlLinkRole.Target)
            {
                return;
            }

            var ownerDeviceId = lease.OwnerDeviceId;
            var epoch = lease.Epoch;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_link.Role != ControlLinkRole.Target)
                {
                    return;
                }

                _session.SetLocalTarget();
                _remoteReturnDetector.Reset();
                _targetEnteredUtc = DateTime.UtcNow;
                _injectionFailures.Reset();
                _lastInjectedMouseX = -1;
                _lastInjectedMouseY = -1;
                if (_remoteInputSender != null)
                {
                    _remoteInputSender.Clear();
                }

                AppLog.Event("INFO", "control.link.arbitrated",
                    "epoch=" + epoch +
                    " owner=" + ownerDeviceId +
                    " wasOwner=" + (wasOwner ? "1" : "0") +
                    " path=" + source);
                SyncLinkFlags("lease-target");
                SetStatus(UiText.ControlledByPeer);
                UpdateControlTargetText();
            }));
        }

        private void HandleRelayedLease(string payloadJson)
        {
            ControlLeasePayload payload;
            try
            {
                payload = ProtocolSerializer.FromJson<ControlLeasePayload>(payloadJson);
            }
            catch (Exception ex)
            {
                AppLog.Debug("control.lease.relay.parse.fail", "error=" + ex.Message);
                return;
            }

            if (payload == null || string.IsNullOrEmpty(payload.OwnerDeviceId))
            {
                return;
            }

            var lease = new ControlLease();
            lease.Epoch = payload.Epoch;
            lease.OwnerDeviceId = payload.OwnerDeviceId;
            lease.TtlMs = payload.TtlMs;
            lease.Sequence = payload.Sequence;
            ApplyIncomingLease(lease, "relay");
        }

        private void HandleRelayedAck(string payloadJson)
        {
            ControlLeaseAckPayload payload;
            try
            {
                payload = ProtocolSerializer.FromJson<ControlLeaseAckPayload>(payloadJson);
            }
            catch (Exception ex)
            {
                AppLog.Debug("control.ack.relay.parse.fail", "error=" + ex.Message);
                return;
            }

            if (payload == null)
            {
                return;
            }

            _link.OnAck(payload.Epoch, DateTime.UtcNow.Ticks);
        }

        private void OnControlAckReceived(object sender, ControlAckReceivedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            _link.OnAck(e.Epoch, DateTime.UtcNow.Ticks);
        }
        private void LogLinkTelemetry(long now)
        {
            var snapshot = _link.Snapshot(now);
            if (snapshot.Role != _lastLinkRole)
            {
                AppLog.Event("INFO", "control.link.role",
                    "from=" + _lastLinkRole + " to=" + snapshot.Role + " " + snapshot.Text());
                _lastLinkRole = snapshot.Role;
                // Called from the link beat thread; the label belongs to the UI thread.
                Dispatcher.BeginInvoke(new Action(UpdateControlTargetText));
            }

            var stale = _link.StaleMessages;
            if (stale > _lastStaleLogged)
            {
                _lastStaleLogged = stale;
                AppLog.Debug("control.link.stale", "count=" + stale + " " + snapshot.Text());
            }

            if (snapshot.Role == ControlLinkRole.Idle ||
                now - _lastLeaseLogTicks < TimeSpan.TicksPerSecond * 2)
            {
                return;
            }

            _lastLeaseLogTicks = now;
            AppLog.Debug("control.link.lease", snapshot.Text());
        }

        private void HideToTray()
        {
            EnsureTrayIcon();
            _trayIcon.Visible = true;
            Hide();
            SetStatus(UiText.MinimizedToTray);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void EnsureTrayIcon()
        {
            if (_trayIcon != null)
            {
                return;
            }

            var menu = new Forms.ContextMenu();
            menu.MenuItems.Add(UiText.ShowWindow, delegate { Dispatcher.BeginInvoke(new Action(ShowFromTray)); });
            menu.MenuItems.Add(UiText.PauseResume, delegate { Dispatcher.BeginInvoke(new Action(delegate { TogglePauseControl(true); })); });
            menu.MenuItems.Add(UiText.StopControl, delegate { Dispatcher.BeginInvoke(new Action(delegate { StopControlEverywhere(true); })); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(UiText.Uninstall, delegate { Dispatcher.BeginInvoke(new Action(RequestUninstall)); });
            menu.MenuItems.Add(UiText.Exit, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });

            _trayIcon = new Forms.NotifyIcon();
            _trayIcon.Text = "LanMouseShare " + AppVersion;
            _trayIcon.Icon = LoadTrayIcon();
            _trayIcon.ContextMenu = menu;
            _trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowFromTray)); };
        }

        /// <summary>
        /// fix18：托盘图标改用应用自带图标里的 16×16 那一帧，不再拿系统默认图标顶上。
        /// 资源读不到时退回系统图标——图标问题绝不能把托盘拖挂。
        /// </summary>
        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
                if (resource != null)
                {
                    using (var stream = resource.Stream)
                    using (var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16)))
                    {
                        return (System.Drawing.Icon)icon.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("tray.icon.load.failed " + ex.GetType().Name + ": " + ex.Message);
            }

            return System.Drawing.SystemIcons.Application;
        }

        private void ExitApplication()
        {
            _exitRequested = true;
            Close();
        }

        private void EnsureLocalServiceAvailable()
        {
            if (CanReachService())
            {
                return;
            }

            // 管道不通但还有遗留的服务进程（上次 Agent 异常退出留下的），
            // 它会占着命名管道，新实例连不上就会被它卡住，看起来像“软件打不开”。
            KillStaleServiceProcesses();
            StartAgentManagedService();
        }

        /// <summary>上一轮 Agent 异常退出后遗留的、不再响应的服务进程。</summary>
        private void KillStaleServiceProcesses()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(ServiceProcessName);
            }
            catch (Exception ex)
            {
                AppLog.Debug("service.sweep.enumerate.fail", "error=" + ex.GetType().Name);
                return;
            }

            for (int i = 0; i < processes.Length; i++)
            {
                var process = processes[i];
                try
                {
                    AppLog.Warn("Stale service process found; terminating. pid=" + process.Id);
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("service.sweep.kill.fail", "pid=" + SafeId(process) + " error=" + ex.GetType().Name);
                }
                finally
                {
                    try { process.Dispose(); } catch (Exception) { }
                }
            }
        }

        private static int SafeId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private bool CanReachService()
        {
            try
            {
                _pipe.Send(AgentCommands.GetPeerState, string.Empty, 500);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StartAgentManagedService()
        {
            try
            {
                var agentDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                var servicePath = Path.GetFullPath(Path.Combine(agentDirectory, "..", "Service", "LanMouseShare.Service.exe"));
                if (!File.Exists(servicePath))
                {
                    servicePath = Path.Combine(agentDirectory, "LanMouseShare.Service.exe");
                }

                if (!File.Exists(servicePath))
                {
                    AppLog.Warn("Agent-managed service executable not found.");
                    return;
                }

                var startInfo = new ProcessStartInfo(servicePath, "--agent " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                startInfo.WorkingDirectory = Path.GetDirectoryName(servicePath);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                _managedServiceProcess = Process.Start(startInfo);

                for (int i = 0; i < 20; i++)
                {
                    if (CanReachService())
                    {
                        return;
                    }

                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Start agent-managed service failed.", ex);
            }
        }

        private void RequestServiceShutdown()
        {
            try
            {
                _pipe.Send(AgentCommands.ShutdownService, string.Empty, 1000);
            }
            catch
            {
            }

            try
            {
                if (_managedServiceProcess != null)
                {
                    if (!_managedServiceProcess.WaitForExit(1500))
                    {
                        // 服务不理会关闭请求就直接结束进程，否则它会一直挂着。
                        AppLog.Warn("Agent-managed service did not stop; terminating. pid=" + SafeId(_managedServiceProcess));
                        _managedServiceProcess.Kill();
                        _managedServiceProcess.WaitForExit(2000);
                    }

                    _managedServiceProcess = null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("service.shutdown.wait.fail", "error=" + ex.GetType().Name);
            }

            // 收尾：把任何仍然活着的服务进程一起清掉，避免“关掉软件后还有服务在跑”。
            KillStaleServiceProcesses();

            StopWindowsServiceFallback();
        }

        private static void StopWindowsServiceFallback()
        {
            try
            {
                var startInfo = new ProcessStartInfo("sc.exe", "stop LanMouseShare");
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit(1500);
                }
            }
            catch
            {
            }
        }

        private void ApplyStartupOptions(bool startWithWindows, bool runAsAdministratorByDefault)
        {
            try
            {
                var path = System.Reflection.Assembly.GetEntryAssembly().Location;
                StartupTaskManager.ApplyStartup(startWithWindows, runAsAdministratorByDefault, path);
            }
            catch (Exception ex)
            {
                AppLog.Error("Update startup options failed.", ex);
                SetStatus(UiText.StartupSettingFailed);
            }
        }

        private void UpdatePrivilegeStatus()
        {
            if (PrivilegeStatusText == null)
            {
                return;
            }

            var isAdmin = StartupTaskManager.IsCurrentProcessAdministrator();
            PrivilegeStatusText.Text = isAdmin ? UiText.AdminPrivilege : UiText.NormalPrivilege;
            if (RestartAsAdminButton != null)
            {
                RestartAsAdminButton.IsEnabled = !isAdmin;
            }
        }

        private void OnRestartAsAdminClicked(object sender, RoutedEventArgs e)
        {
            if (StartupTaskManager.IsCurrentProcessAdministrator())
            {
                UpdatePrivilegeStatus();
                SetStatus(UiText.AlreadyAdmin);
                return;
            }

            try
            {
                if (StartupTaskManager.RestartElevated())
                {
                    _exitRequested = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Restart elevated failed.", ex);
                SetStatus(UiText.AdminRestartFailed);
            }
        }

        private DrawingSize GetRemoteScreenSize(DrawingSize fallback)
        {
            if (_state != null && _state.PeerScreenWidth > 0 && _state.PeerScreenHeight > 0)
            {
                return new DrawingSize(_state.PeerScreenWidth, _state.PeerScreenHeight);
            }

            return fallback;
        }

        private void InitializeRemoteVirtualPosition(DrawingPoint entryPoint, DrawingSize localScreen)
        {
            var direction = _state != null ? _state.LayoutDirection : "Right";
            var mapped = EdgeTransitionMapper.MapEntryToRemote(entryPoint, localScreen, _remoteVirtualScreen, direction);
            _remoteVirtualX = mapped.X;
            _remoteVirtualY = mapped.Y;
        }

        private System.Drawing.Rectangle GetActiveScreenBounds(int x, int y)
        {
            return DesktopEnvironment.GetScreenBoundsFromPoint(x, y);
        }

        // fix15：边角阻挡带按屏幕比例算，左/右边缘看高度、上/下边缘看宽度，80 像素只是下限。
        // 这么算两台不同分辨率、不同缩放比例的机器上，"不许从角落切屏"的范围看起来才一样大。
        private static int CornerBlockFor(string direction, int width, int height)
        {
            var reference = direction == "Down" || direction == "Up" ? width : height;
            var scaled = (int)Math.Round(reference * CornerBlockRatio);
            return scaled > CornerBlockPixels ? scaled : CornerBlockPixels;
        }

        private static string OppositeDirection(string direction)
        {
            if (direction == "Left")
            {
                return "Right";
            }

            if (direction == "Right")
            {
                return "Left";
            }

            if (direction == "Up")
            {
                return "Down";
            }

            if (direction == "Down")
            {
                return "Up";
            }

            return "Right";
        }

        private int ScaleDelta(int delta)
        {
            if (delta == 0)
            {
                return 0;
            }

            var scaled = (int)Math.Round(delta * _mouseSpeedMultiplier);
            if (scaled == 0)
            {
                return delta > 0 ? 1 : -1;
            }

            return scaled;
        }

        private void ResetEdgeSwitch(string reason)
        {
            ArmEdgeSwitch(reason);
        }

        /// <summary>
        /// Closes the edge-switch gate for a short cooldown and forces the pointer to travel
        /// away from the active edge before another switch is allowed. Without this latch the
        /// cursor lands on the very edge after a return and immediately switches again, which
        /// made both machines look frozen while they fought over who owns the input.
        /// </summary>
        private void ArmEdgeSwitch(string reason)
        {
            _edgeSwitchArmed = false;
            _edgeSwitchLockUntilUtc = DateTime.UtcNow.AddMilliseconds(EdgeSwitchCooldownMs);
            _lastEdgeTraceUtc = DateTime.UtcNow;
            AppLog.Debug("edge.latch", "reason=" + reason + " cooldownMs=" + EdgeSwitchCooldownMs);
        }

        private void ArmEdgeSwitchIfMovedAway(DrawingPoint point, System.Drawing.Rectangle screen)
        {
            if (_edgeSwitchArmed)
            {
                return;
            }

            if (DateTime.UtcNow < _edgeSwitchLockUntilUtc)
            {
                return;
            }

            var distance = DistanceFromActiveEdge(point, new DrawingSize(screen.Width, screen.Height), _state != null ? _state.LayoutDirection : null);
            if (distance < EdgeArmDistance)
            {
                return;
            }

            _edgeSwitchArmed = true;
            AppLog.Debug("edge.rearm", "point=" + point.X + "," + point.Y + " distance=" + distance);
        }

        private static int DistanceFromActiveEdge(DrawingPoint point, DrawingSize screen, string direction)
        {
            if (string.Equals(direction, "Left", StringComparison.OrdinalIgnoreCase))
            {
                return point.X;
            }

            if (string.Equals(direction, "Top", StringComparison.OrdinalIgnoreCase))
            {
                return point.Y;
            }

            if (string.Equals(direction, "Bottom", StringComparison.OrdinalIgnoreCase))
            {
                return screen.Height - 1 - point.Y;
            }

            return screen.Width - 1 - point.X;
        }

        private void LogEdgeTrace(DrawingPoint point, DrawingPoint previousPoint, System.Drawing.Rectangle screen)
        {
            if ((DateTime.UtcNow - _lastEdgeTraceUtc).TotalMilliseconds < 500)
            {
                return;
            }

            _lastEdgeTraceUtc = DateTime.UtcNow;
            AppLog.Debug("mouse.edge.trace", "Direction=" + (_state != null ? _state.LayoutDirection : "?") +
                                            " Point=" + point.X + "," + point.Y +
                                            " Previous=" + previousPoint.X + "," + previousPoint.Y +
                                            " Screen=" + screen.Width + "x" + screen.Height +
                                            " Armed=" + _edgeSwitchArmed +
                                            " BlockCorners=" + _blockScreenCorners);
        }

        private static bool IsCrossScreenEnvelope(string messageType)
        {
            // fix7：暂停只该拦输入与控制。文件/剪贴板类消息必须继续处理，
            // 否则一次暂停会让跨机复制粘贴与拖拽文件静默失效。
            return messageType == ProtocolMessageTypes.InputEvent ||
                   messageType == ProtocolMessageTypes.InputBatch ||
                   messageType == ProtocolMessageTypes.ControlLease ||
                   messageType == ProtocolMessageTypes.ControlLeaseAck ||
                   messageType == ProtocolMessageTypes.ControlAcquire;
        }

        private static bool IsConfiguredHotkey(InputPayload payload, string hotkeyText)
        {
            if (payload == null || payload.Kind != InputKinds.Key || !payload.IsDown || string.IsNullOrWhiteSpace(hotkeyText))
            {
                return false;
            }

            Forms.Keys key;
            bool needCtrl;
            bool needAlt;
            bool needShift;
            if (!TryParseHotkey(hotkeyText, out key, out needCtrl, out needAlt, out needShift))
            {
                return false;
            }

            var modifiers = Forms.Control.ModifierKeys;
            var hasCtrl = (modifiers & Forms.Keys.Control) == Forms.Keys.Control;
            var hasAlt = (modifiers & Forms.Keys.Alt) == Forms.Keys.Alt;
            var hasShift = (modifiers & Forms.Keys.Shift) == Forms.Keys.Shift;
            return payload.VirtualKey == (int)key &&
                   hasCtrl == needCtrl &&
                   hasAlt == needAlt &&
                   hasShift == needShift;
        }

        private static bool TryParseHotkey(string text, out Forms.Keys key, out bool ctrl, out bool alt, out bool shift)
        {
            key = Forms.Keys.None;
            ctrl = false;
            alt = false;
            shift = false;
            var parts = (text ?? string.Empty).Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                }
                else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                }
                else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                }
                else
                {
                    try
                    {
                        key = (Forms.Keys)Enum.Parse(typeof(Forms.Keys), part, true);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return key != Forms.Keys.None;
        }

        private static string NormalizeHotkeyText(string text, string fallback)
        {
            Forms.Keys key;
            bool ctrl;
            bool alt;
            bool shift;
            if (!TryParseHotkey(text, out key, out ctrl, out alt, out shift))
            {
                return fallback;
            }

            var result = string.Empty;
            if (ctrl) result += "Ctrl+";
            if (alt) result += "Alt+";
            if (shift) result += "Shift+";
            return result + key;
        }

        private static string BuildHotkeyText(ModifierKeys modifiers, Key key)
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey <= 0)
            {
                return null;
            }

            var result = string.Empty;
            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control) result += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) result += "Alt+";
            if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) result += "Shift+";
            return result + ((Forms.Keys)virtualKey);
        }

        private static bool IsModifierOnly(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin;
        }

        private static int Scale(int value, int source, int target)
        {
            return source <= 0 ? 0 : value * target / source;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
            {
                return min;
            }

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int NormalizeMouseRate(int rateHz)
        {
            if (rateHz <= 0) return 200;
            if (rateHz < 60) return 60;
            if (rateHz > 250) return 250;
            return rateHz;
        }

        private int SelectedMouseRate()
        {
            var item = MouseRateBox != null ? MouseRateBox.SelectedItem as ComboBoxItem : null;
            var text = item != null ? item.Content as string : null;
            if (string.IsNullOrEmpty(text))
            {
                return _mouseUpdateRateHz;
            }

            int value;
            return int.TryParse(text.Replace("Hz", string.Empty), out value) ? value : _mouseUpdateRateHz;
        }

        private void SelectMouseRate(int rateHz)
        {
            if (MouseRateBox == null)
            {
                return;
            }

            for (int i = 0; i < MouseRateBox.Items.Count; i++)
            {
                var item = MouseRateBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Content as string, rateHz + "Hz", StringComparison.OrdinalIgnoreCase))
                {
                    MouseRateBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string DirectionText(string direction)
        {
            if (direction == "Left") return UiText.PeerLeft;
            if (direction == "Up") return UiText.PeerUp;
            if (direction == "Down") return UiText.PeerDown;
            return UiText.PeerRight;
        }

        private void OpenTransferFolder()
        {
            // fix9：原来 CreateDirectory 在公共桌面没权限时直接抛异常 → 按钮"点了没反应"。
            // 现在目录解析自带可写回退，打开失败要把原因说到界面上。
            string folder = null;
            try
            {
                folder = GetTransferDirectory();
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception exCreate)
                {
                    AppLog.Warn("transfer.folder.create.fail path=" + folder + " " + exCreate.Message);
                }

                AppLog.Event("INFO", "transfer.folder.open", "path=" + folder);
                try
                {
                    Process.Start("explorer.exe", "\"" + folder + "\"");
                }
                catch (Exception exExplorer)
                {
                    AppLog.Warn("transfer.folder.open.explorer.fail " + exExplorer.Message);
                    Process.Start(folder);
                }

                SetStatus("接收文件夹：" + folder);
                HintText.Text = folder;
            }
            catch (Exception ex)
            {
                AppLog.Error("Open transfer folder failed.", ex);
                SetStatus("打开接收文件夹失败：" + ex.Message);
                HintText.Text = folder ?? string.Empty;
            }
        }

        private static string _transferDirectoryCache;

        /// <summary>
        /// 接收目录：优先公共桌面，不可写就退到当前用户桌面/下载/LocalAppData。
        /// 必须与界面"打开接收文件夹"用同一个结果，否则会出现"传成功但找不到文件"。
        /// </summary>
        private static string GetTransferDirectory()
        {
            if (!string.IsNullOrEmpty(_transferDirectoryCache))
            {
                return _transferDirectoryCache;
            }

            var candidates = new List<string>();
            // fix10：用户明确要求"传过来的文件直接放桌面"，不要再套一层 Mouse传输文件。
            // 当前登录用户自己的桌面永远可写，优先用它；公共桌面需要管理员权限（被拒后会
            // 掉到 Temp，用户就"找不到文件"了），只作为备选。
            var myDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(myDesktop))
            {
                candidates.Add(myDesktop);
            }

            var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrEmpty(publicRoot))
            {
                candidates.Add(Path.Combine(publicRoot, "Desktop"));
            }

            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(commonDesktop))
            {
                candidates.Add(commonDesktop);
            }

            var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(downloads))
            {
                candidates.Add(Path.Combine(downloads, "Downloads"));
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanMouseShare"));

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(candidate);
                    var probe = Path.Combine(candidate, ".lms-write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    AppLog.Info("transfer.dir.ready path=" + candidate);
                    _transferDirectoryCache = candidate;
                    return candidate;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("transfer.dir.unusable path=" + candidate + " " + ex.Message);
                }
            }

            var fallback = Path.Combine(Path.GetTempPath(), UiText.TransferFolderName);
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception)
            {
            }

            AppLog.Warn("transfer.dir.fallback path=" + fallback);
            _transferDirectoryCache = fallback;
            return fallback;
        }

        private void Notify(string title, string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                SetStatus(title);
                HintText.Text = message ?? string.Empty;
            }));
            AppLog.Info(title + " " + message);
        }

        private void SetStatus(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { SetStatus(text); }));
                return;
            }

            StatusText.Text = text ?? string.Empty;
        }
    }
}


