using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const string AppVersion = "0.1.41";
        private const int CornerBlockPixels = 80;

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
        private readonly HashSet<string> _ignoredConflictProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private RemoteInputSender _remoteInputSender;
        private DirectInputChannel _directInputChannel;
        private RawMouseInputMonitor _rawMouseInput;
        private ClipboardSyncService _clipboardSync;
        private FileDropSender _fileDropSender;
        private Process _managedServiceProcess;
        private Forms.NotifyIcon _trayIcon;
        private PeerStateDto _state;
        private bool _remoteControlActive;
        private bool _controlledByPeer;
        private bool _pollInFlight;
        private bool _stateInFlight;
        private bool _settingsLoaded;
        private bool _applyingSettingsToUi;
        private bool _remoteReturnReleasePending;
        private bool _exitRequested;
        private bool _shutdownStarted;
        private bool _minimizeToTrayOnClose;
        private bool _blockScreenCorners;
        private bool _startWithWindows;
        private bool _runAsAdministratorByDefault;
        private DateTime _remoteReturnReleasePendingUntilUtc = DateTime.MinValue;
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
        private string _continueConflictProcess;
        private DateTime _continueConflictUntilUtc = DateTime.MinValue;

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
            EnsureLocalServiceAvailable();

            try
            {
                _directInputChannel = new DirectInputChannel();
                _directInputChannel.InputBatchReceived += OnDirectInputBatchReceived;
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

            _clipboardSync = new ClipboardSyncService(_pipe, GetState, IsClipboardEnabled, Dispatcher);
            _clipboardSync.Attach(this);
            _fileDropSender = new FileDropSender(_pipe, GetState, IsControlPaused, Notify);

            _pollTimer.Interval = TimeSpan.FromMilliseconds(100);
            _pollTimer.Tick += OnPollEvents;
            _pollTimer.Start();

            _stateTimer.Interval = TimeSpan.FromSeconds(2);
            _stateTimer.Tick += delegate { RefreshStateAsync(); };
            _stateTimer.Start();

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
            StopControlEverywhere(true);
            RequestServiceShutdown();
            _pollTimer.Stop();
            _stateTimer.Stop();
            _hook.Dispose();
            if (_clipboardSync != null) _clipboardSync.Dispose();
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
            return !_session.IsPaused && ClipboardEnabledBox != null && ClipboardEnabledBox.IsChecked == true;
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
                SetStatus(UiText.ServiceDisconnected);
                return;
            }

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
            if ((!state.IsConnected || !state.IsTrusted) && (_session.IsTargetPeer || _remoteControlActive || _controlledByPeer))
            {
                ReleaseControl(false);
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
            PauseHotkeyBox.Text = _pauseHotkey;
            StopHotkeyBox.Text = _stopControlHotkey;
            ExitHotkeyBox.Text = _exitHotkey;
            StartWithWindowsBox.IsChecked = _startWithWindows;
            MinimizeToTrayBox.IsChecked = _minimizeToTrayOnClose;
            BlockCornersBox.IsChecked = _blockScreenCorners;
            RunAsAdminDefaultBox.IsChecked = _runAsAdministratorByDefault;
            UpdatePrivilegeStatus();
            SelectMouseRate(_mouseUpdateRateHz);
            if (_remoteInputSender != null)
            {
                _remoteInputSender.SetMouseUpdateRate(_mouseUpdateRateHz);
            }

            _settingsLoaded = true;
            _applyingSettingsToUi = false;
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

            var route = _inputRouter.Route(e.Payload, _session.Snapshot(_state));
            if (route == InputRouteAction.Ignore)
            {
                ResetEdgeSwitch("inactive");
                return;
            }

            if (route == InputRouteAction.ForwardToPeer)
            {
                HandleRemoteControlInput(e);
                return;
            }

            if (route == InputRouteAction.SuppressLocal)
            {
                e.SuppressLocalInput = true;
                return;
            }

            if (route == InputRouteAction.AllowLocal)
            {
                return;
            }

            var rawPoint = new DrawingPoint(e.Payload.X, e.Payload.Y);
            var screen = GetActiveScreenBounds(rawPoint.X, rawPoint.Y);
            var point = new DrawingPoint(rawPoint.X - screen.Left, rawPoint.Y - screen.Top);
            if (!_hasLastLocalMousePoint)
            {
                _lastLocalMousePoint = point;
                _hasLastLocalMousePoint = true;
                return;
            }

            var previousPoint = _lastLocalMousePoint;
            _lastLocalMousePoint = point;

            var shouldSwitch = _edgeSwitchDetector.ShouldSwitch(
                point,
                previousPoint,
                new DrawingSize(screen.Width, screen.Height),
                _state.LayoutDirection,
                _blockScreenCorners,
                CornerBlockPixels);
            if (shouldSwitch)
            {
                ActivateRemoteControl(point, screen);
                e.SuppressLocalInput = true;
            }
            else
            {
                LogEdgeTrace(point, previousPoint, screen);
            }
        }

        private void ActivateRemoteControl(DrawingPoint entryPoint, System.Drawing.Rectangle localScreen)
        {
            _remoteControlActive = true;
            _controlledByPeer = false;
            _session.SetPeerTarget();
            _localScreenBounds = localScreen;
            _remoteVirtualScreen = GetRemoteScreenSize(new DrawingSize(localScreen.Width, localScreen.Height));
            InitializeRemoteVirtualPosition(entryPoint, new DrawingSize(localScreen.Width, localScreen.Height));
            AppLog.Info("Remote control activated. Direction=" + _state.LayoutDirection +
                        " Entry=" + entryPoint.X + "," + entryPoint.Y +
                        " RemoteScreen=" + _remoteVirtualScreen.Width + "x" + _remoteVirtualScreen.Height);
            SendControlTarget(_session.ActiveTargetDeviceId, "EdgeSwitch");
            _remoteInputSender.SendCommand(AgentCommands.AcquireControl, "{}");
            _remoteInputSender.Enqueue(new InputPayload { Kind = InputKinds.MouseMove, X = _remoteVirtualX, Y = _remoteVirtualY });
            Notify(UiText.SwitchedControl, UiText.Controlling + (_state.PeerDeviceName ?? UiText.Peer));
            UpdateControlTargetText();
        }

        private void HandleRemoteControlInput(InputCapturedEventArgs e)
        {
            if (e.Payload.Kind == InputKinds.MouseMove)
            {
                e.SuppressLocalInput = true;
                return;
            }

            if (_remoteInputSender.Enqueue(e.Payload))
            {
                e.SuppressLocalInput = true;
                return;
            }

            AppLog.Warn("Remote input enqueue failed; releasing local control.");
            ReleaseControl(false);
        }

        private void OnRawMouseMoved(object sender, RawMouseMovedEventArgs e)
        {
            if (!_session.IsTargetPeer || _session.IsPaused)
            {
                return;
            }

            var dx = ScaleDelta(e.DeltaX);
            var dy = ScaleDelta(e.DeltaY);
            _remoteVirtualX += dx;
            _remoteVirtualY += dy;
            if (!_remoteInputSender.Enqueue(new InputPayload { Kind = InputKinds.MouseMoveRelative, X = dx, Y = dy }))
            {
                AppLog.Warn("Raw remote input enqueue failed; releasing remote control.");
                ReleaseControl(false);
            }
        }

        private void OnRemoteInputSendFailed(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_session.IsTargetPeer)
                {
                    ReleaseControl(false);
                }
            }));
        }

        private void OnDirectInputBatchReceived(object sender, InputBatchReceivedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!_inputRouter.ShouldInjectRemoteInput(_session.Snapshot(_state)))
                {
                    AppLog.Warn("Direct input ignored: local device is not the active target.");
                    return;
                }

                for (int i = 0; i < e.Payloads.Length; i++)
                {
                    InjectPeerInput(e.Payloads[i]);
                }
            }));
        }

        private void InjectPeerInput(InputPayload payload)
        {
            if (payload == null || !_inputRouter.ShouldInjectRemoteInput(_session.Snapshot(_state)))
            {
                return;
            }

            if (_remoteReturnReleasePending && DateTime.UtcNow >= _remoteReturnReleasePendingUntilUtc)
            {
                _remoteReturnReleasePending = false;
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
            if (!_remoteControlActive && !_controlledByPeer && !_remoteReturnReleasePending &&
                (payload.Kind == InputKinds.MouseMove || payload.Kind == InputKinds.MouseMoveRelative))
            {
                _controlledByPeer = true;
                _remoteReturnDetector.Reset();
                AppLog.Info("Remote control inferred from peer input.");
                UpdateControlTargetText();
            }

            if (_controlledByPeer && payload.Kind == InputKinds.MouseMoveRelative)
            {
                CheckRemoteReturnToController(payload.X, payload.Y);
            }
            else if (payload.Kind == InputKinds.MouseMove)
            {
                _remoteReturnDetector.Reset();
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

            if (string.Equals(_continueConflictProcess, processName, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow < _continueConflictUntilUtc)
            {
                return true;
            }

            AppLog.Warn("Known input-conflict window detected. Process=" + info.ProcessName + " Title=" + info.Title);
            var message =
                "\u68C0\u6D4B\u5230\u53EF\u80FD\u51B2\u7A81\u7684\u8F6F\u4EF6\uFF1A" + info.ProcessName + "\n\n" +
                "\u8FD9\u7C7B\u8FDC\u63A7\u6216\u8DE8\u5C4F\u8F6F\u4EF6\u53EF\u80FD\u4F1A\u62E6\u622A\u6A21\u62DF\u9F20\u6807\u8F93\u5165\uFF0C\u5BFC\u81F4\u9F20\u6807\u770B\u8D77\u6765\u5361\u4F4F\u3002\n\n" +
                "\u662F\uFF1A\u8FD4\u56DE\u672C\u673A\u63A7\u5236\n" +
                "\u5426\uFF1A\u7EE7\u7EED\u5C1D\u8BD5\u672C\u6B21\u63A7\u5236\n" +
                "\u53D6\u6D88\uFF1A\u672C\u6B21\u8FD0\u884C\u671F\u95F4\u5BF9\u8BE5\u8FDB\u7A0B\u4E0D\u518D\u63D0\u793A";
            var decision = MessageBox.Show(this, message, UiText.ConflictTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (decision == MessageBoxResult.Yes)
            {
                ReleaseForInjectionBlocked(info, "KnownConflictWindow");
                return false;
            }

            if (decision == MessageBoxResult.Cancel)
            {
                _ignoredConflictProcesses.Add(processName);
                AppLog.Info("Known input-conflict prompt ignored for current run. Process=" + processName);
                return true;
            }

            _continueConflictProcess = processName;
            _continueConflictUntilUtc = DateTime.UtcNow.AddSeconds(10);
            AppLog.Info("Known input-conflict window allowed temporarily. Process=" + processName);
            return true;
        }

        private bool HandleInjectionResult(InputPayload payload, InputInjectionResult result, DrawingPoint before, DrawingPoint after)
        {
            var failed = result != null && !result.Success;
            var reason = failed ? result.Reason + " LastError=" + result.LastError : string.Empty;

            if (!failed && payload.Kind == InputKinds.MouseMoveRelative && (payload.X != 0 || payload.Y != 0) &&
                before.X == after.X && before.Y == after.Y)
            {
                failed = true;
                reason = "Cursor unchanged after relative mouse injection.";
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

            var position = CursorController.GetPosition();
            var screen = GetActiveScreenBounds(position.X, position.Y);
            var point = new DrawingPoint(position.X - screen.Left, position.Y - screen.Top);
            var direction = _state.LayoutDirection ?? "Right";
            var result = _remoteReturnDetector.Update(point, new DrawingSize(screen.Width, screen.Height), direction, deltaX, deltaY, _session.IsPaused, _blockScreenCorners, CornerBlockPixels);
            if (!result.ShouldRelease || (_remoteReturnReleasePending && DateTime.UtcNow < _remoteReturnReleasePendingUntilUtc))
            {
                return;
            }

            _controlledByPeer = false;
            _session.SetPeerTarget();
            _remoteReturnDetector.Reset();
            _remoteReturnReleasePending = true;
            _remoteReturnReleasePendingUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            var payload = new ControlReleasePayload
            {
                RemoteX = point.X,
                RemoteY = point.Y,
                RemoteScreenWidth = screen.Width,
                RemoteScreenHeight = screen.Height,
                Direction = direction,
                Reason = result.Reason
            };
            AppLog.Info("Remote return requested. Direction=" + direction +
                        " Point=" + point.X + "," + point.Y +
                        " Delta=" + deltaX + "," + deltaY);
            SendControlTarget(_session.ActiveTargetDeviceId, "RemoteReturn");
            _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, ProtocolSerializer.ToJson(payload));
            UpdateControlTargetText();
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
                    AppLog.Error("Poll events failed.", ex);
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _pollInFlight = false;
                    if (events == null)
                    {
                        return;
                    }

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
                _controlledByPeer = true;
                _remoteControlActive = false;
                _session.SetLocalTarget();
                _remoteReturnDetector.Reset();
                SetStatus(UiText.ControlledByPeer);
                UpdateControlTargetText();
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ClipboardUpdate)
            {
                _clipboardSync.ApplyRemote(ProtocolSerializer.FromJson<ClipboardPayload>(envelope.PayloadJson));
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileOffer)
            {
                Notify(UiText.ReceivingFile, UiText.FileSavedToDesktop);
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileComplete)
            {
                Notify(UiText.FileTransferComplete, UiText.FileSavedComplete);
                OpenTransferFolder();
            }
        }

        private void ApplyPeerPause(string payloadJson)
        {
            var payload = ProtocolSerializer.FromJson<ControlPausePayload>(payloadJson);
            StopLocalCrossScreenState();
            _session.SetPaused(true);
            SetStatus(payload != null && payload.StopAll ? UiText.ControlStopped : UiText.CrossScreenPaused);
            UpdatePauseButton();
            UpdateControlTargetText();
        }

        private void ApplyPeerResume()
        {
            _session.SetPaused(false);
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

            if (_session.IsTargetLocal)
            {
                _remoteControlActive = false;
                _controlledByPeer = true;
            }
            else if (_session.IsTargetPeer)
            {
                _remoteControlActive = true;
                _controlledByPeer = false;
            }

            UpdateControlTargetText();
        }

        private void ApplyPeerControlRelease(string payloadJson)
        {
            var payload = ProtocolSerializer.FromJson<ControlReleasePayload>(payloadJson);
            if (_remoteControlActive)
            {
                ReleaseControlToLocalEdge(payload);
                return;
            }

            _controlledByPeer = false;
            _remoteReturnReleasePending = false;
            _remoteReturnDetector.Reset();
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
            ReleaseControl(false);
            _session.SetLocalTarget();

            var mapped = EdgeTransitionMapper.MapReturnToLocal(
                new DrawingPoint(remoteX, remoteY),
                new DrawingSize(remoteWidth, remoteHeight),
                new DrawingSize(localScreen.Width, localScreen.Height),
                direction);
            var targetX = localScreen.Left + mapped.X;
            var targetY = localScreen.Top + mapped.Y;

            CursorController.SetPosition(targetX, targetY);
            ResetEdgeSwitch("return local");
            UpdateControlTargetText();
        }

        private void OnDropFiles(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _fileDropSender.SendFiles((string[])e.Data.GetData(DataFormats.FileDrop));
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

        private void OnOpenTransferFolderClicked(object sender, RoutedEventArgs e)
        {
            OpenTransferFolder();
        }

        private void OnOpenLogsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppLog.LogDirectory);
                Process.Start("explorer.exe", AppLog.LogDirectory);
            }
            catch (Exception ex)
            {
                AppLog.Error("Open logs failed.", ex);
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
                RunAsAdministratorByDefault = _runAsAdministratorByDefault
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
                if (notifyPeer && _remoteInputSender != null)
                {
                    _remoteInputSender.SendCommand(AgentCommands.ResumeControl, "{}");
                }

                SetStatus(UiText.CrossScreenResumed);
            }
            else
            {
                _session.SetPaused(true);
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
            _remoteControlActive = false;
            _controlledByPeer = false;
            _session.SetLocalTarget();
            _remoteReturnReleasePending = false;
            _remoteReturnDetector.Reset();
            _injectionFailures.Reset();
            ResetEdgeSwitch("stop local cross-screen");
            _hasLastLocalMousePoint = false;
            if (_remoteInputSender != null)
            {
                _remoteInputSender.Clear();
            }

            _injector.ReleaseAllMouseButtons();
        }

        private void ReleaseControl(bool notifyPeer)
        {
            var wasActive = _remoteControlActive;
            _remoteControlActive = false;
            _controlledByPeer = false;
            _session.SetLocalTarget();
            _remoteReturnReleasePending = false;
            _remoteReturnDetector.Reset();
            _injectionFailures.Reset();
            ResetEdgeSwitch("release");
            _hasLastLocalMousePoint = false;
            if (_remoteInputSender != null)
            {
                _remoteInputSender.Clear();
            }

            if (notifyPeer && wasActive && _remoteInputSender != null)
            {
                SendControlTarget(_session.ActiveTargetDeviceId, "Release");
                _remoteInputSender.SendCommand(AgentCommands.ReleaseControl, "{}");
            }

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
            else if (_session.IsTargetLocal && _controlledByPeer)
            {
                ControlTargetText.Text = UiText.TargetLocalPeerCanInput;
            }
            else
            {
                ControlTargetText.Text = UiText.TargetLocal;
            }
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
            menu.MenuItems.Add(UiText.Exit, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });

            _trayIcon = new Forms.NotifyIcon();
            _trayIcon.Text = "LanMouseShare " + AppVersion;
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            _trayIcon.ContextMenu = menu;
            _trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowFromTray)); };
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

            StartAgentManagedService();
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

                var startInfo = new ProcessStartInfo(servicePath, "--agent");
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
                    _managedServiceProcess.WaitForExit(1500);
                    _managedServiceProcess = null;
                }
            }
            catch
            {
            }

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
            AppLog.Info("Edge switch reset. Reason=" + reason);
        }

        private void LogEdgeTrace(DrawingPoint point, DrawingPoint previousPoint, System.Drawing.Rectangle screen)
        {
            if ((DateTime.UtcNow - _lastEdgeTraceUtc).TotalMilliseconds < 500)
            {
                return;
            }

            _lastEdgeTraceUtc = DateTime.UtcNow;
            AppLog.Info("EdgeTrace Direction=" + (_state != null ? _state.LayoutDirection : "?") +
                        " Point=" + point.X + "," + point.Y +
                        " Previous=" + previousPoint.X + "," + previousPoint.Y +
                        " Screen=" + screen.Width + "x" + screen.Height +
                        " BlockCorners=" + _blockScreenCorners);
        }

        private static bool IsCrossScreenEnvelope(string messageType)
        {
            return messageType == ProtocolMessageTypes.InputEvent ||
                   messageType == ProtocolMessageTypes.InputBatch ||
                   messageType == ProtocolMessageTypes.ClipboardUpdate ||
                   messageType == ProtocolMessageTypes.FileOffer ||
                   messageType == ProtocolMessageTypes.FileStreamStart ||
                   messageType == ProtocolMessageTypes.FileComplete ||
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
            try
            {
                var folder = GetTransferDirectory();
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                AppLog.Error("Open transfer folder failed.", ex);
            }
        }

        private static string GetTransferDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), UiText.TransferFolderName);
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
            StatusText.Text = text ?? string.Empty;
        }
    }
}
