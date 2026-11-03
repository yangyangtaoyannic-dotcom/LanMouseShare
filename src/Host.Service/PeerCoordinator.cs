using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Crypto;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Networking;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Service
{
    internal sealed class PeerCoordinator
    {
        private const int TcpPort = 47532;
        private const int UdpInputPort = 47533;
        private static readonly TimeSpan PeerReachableTimeout = TimeSpan.FromSeconds(9);
        private readonly object _sync = new object();
        private readonly ConfigStore _configStore;
        private AppConfig _config;
        private UdpDiscoveryService _discovery;
        private TcpListener _listener;
        private UdpClient _inputUdp;
        private Thread _listenerThread;
        private Thread _udpInputThread;
        private volatile bool _running;
        private DiscoveredPeer _peer;
        private readonly Queue<ProtocolEnvelope> _inboundEvents = new Queue<ProtocolEnvelope>();
        private readonly Dictionary<string, IncomingTransfer> _incomingTransfers = new Dictionary<string, IncomingTransfer>();
        private readonly Dictionary<string, string> _fetchedPaths = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> _outgoingTransfers = new Dictionary<string, DateTime>();
        // fix7：暂停状态拆成"本机暂停"和"对端暂停"两路。
        // 旧实现只有一个布尔值，对端通知一次 Pause 之后就永久为 true：
        // 对端 Agent 退出时会发 StopAll 暂停（见 MainWindow.ShutdownAndDispose），
        // 它重新启动后新进程并不知道要发 Resume，于是本机 Service 一直静默丢弃
        // 文件传输与剪贴板命令——表现就是"文件传不过去、粘贴没反应"。
        private volatile bool _localPaused;
        private volatile bool _peerPaused;
        private bool? _announcedPauseState;
        private string _announcedPausePeer;
        private string _activeTargetDeviceId;
        private LogHub _logHub;
        private Thread _heartbeatThread;
        private int _lastRttMs = -1;
        private UdpClient _udpSender;
        private readonly object _udpSenderLock = new object();
        private TcpClient _outboundClient;
        private NetworkStream _outboundStream;
        private string _outboundEndpoint;
        private readonly object _outboundLock = new object();
        private string _lastScreenInfoSent;

        public PeerCoordinator()
        {
            _configStore = new ConfigStore(ConfigStore.DefaultPath());
        }

        /// <summary>
        /// 控制通道是否暂停。注意这只用来拦"输入/控制"相关的信令，
        /// 剪贴板与文件传输有自己的开关，不能跟着一起停（fix7）。
        /// </summary>
        private bool SharingPaused
        {
            get { return _localPaused || _peerPaused; }
        }

        public void Start()
        {
            _config = _configStore.LoadOrCreate();
            AppLog.Event("INFO", "coordinator.start", "deviceId=" + _config.DeviceId + " deviceName=" + _config.DeviceName +
                                                      " version=" + AppLog.Version);
            _logHub = new LogHub(GetPeerAddressForLogging, delegate { return _config; });
            _logHub.Start(_config);

            _running = true;
            _discovery = new UdpDiscoveryService(DiscoveryPacket.Create(_config.DeviceId, _config.DeviceName, TcpPort));
            _discovery.DeviceDiscovered += OnDeviceDiscovered;
            _discovery.Start();

            _listener = new TcpListener(IPAddress.Any, TcpPort);
            _listener.Start();
            AppLog.Event("INFO", "tcp.listen.start", "port=" + TcpPort);
            _listenerThread = new Thread(ListenTcpLoop);
            _listenerThread.IsBackground = true;
            _listenerThread.Start();

            _inputUdp = new UdpClient(UdpInputPort);
            _udpInputThread = new Thread(ListenUdpInputLoop);
            _udpInputThread.IsBackground = true;
            _udpInputThread.Start();
            AppLog.Event("INFO", "udp.input.listen.start", "port=" + UdpInputPort);

            _heartbeatThread = new Thread(HeartbeatLoop);
            _heartbeatThread.IsBackground = true;
            _heartbeatThread.Start();
        }

        private void HeartbeatLoop()
        {
            var tick = 0;
            while (_running)
            {
                Thread.Sleep(1000);
                if (!_running)
                {
                    return;
                }

                tick++;
                if (tick % 5 != 0)
                {
                    continue;
                }

                try
                {
                    var peer = GetPeer();
                    if (peer == null || peer.Address == null)
                    {
                        continue;
                    }

                    // Only probe a paired peer: an unpaired peer would make every probe fail and
                    // flood the log with connection errors every five seconds.
                    if (!IsTrustedDevice(peer.DeviceId))
                    {
                        continue;
                    }

                    var payload = new HeartbeatPayload
                    {
                        Ticks = DateTime.UtcNow.Ticks,
                        Reply = false,
                        Paused = _localPaused
                    };
                    SendEnvelope(peer, ProtocolMessageTypes.Heartbeat, ProtocolSerializer.ToJson(payload));

                    // fix7：对端消失后它施加的暂停必须自己解除，否则本机永远等不到 Resume。
                    DropStalePeerPause();
                    AnnouncePauseStateToPeer();
                }
                catch (Exception ex)
                {
                    AppLog.Debug("net.heartbeat.send.fail", ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 对端不在了（崩溃、关机、换网络），它曾经施加的暂停就没有意义了：
        /// 没有任何一方能替它发 Resume，本机只能自己解除，否则会永久停在
        /// "Sharing paused" 状态，文件与剪贴板全部静默失败（fix7）。
        /// </summary>
        private void DropStalePeerPause()
        {
            if (!_peerPaused)
            {
                return;
            }

            var peer = GetPeer();
            if (peer != null && IsPeerReachable(peer))
            {
                return;
            }

            SetSharingPaused(false, "PeerGone");
        }

        /// <summary>
        /// 把本机的暂停状态同步给对端。只在"对端换了"或"本机暂停状态变了"时发，
        /// 这样对端重启后也能重新对齐，不会一边暂停一边被当成已恢复。
        /// </summary>
        private void AnnouncePauseStateToPeer()
        {
            if (!_running)
            {
                return;
            }

            var peer = GetPeer();
            if (peer == null || peer.Address == null || !IsTrustedDevice(peer.DeviceId) || !IsPeerReachable(peer))
            {
                return;
            }

            var paused = _localPaused;
            if (string.Equals(_announcedPausePeer, peer.DeviceId, StringComparison.Ordinal)
                && _announcedPauseState.HasValue
                && _announcedPauseState.Value == paused)
            {
                return;
            }

            _announcedPausePeer = peer.DeviceId;
            _announcedPauseState = paused;

            var payload = ProtocolSerializer.ToJson(new ControlPausePayload { StopAll = false, Reason = "Sync" });
            SendEnvelope(peer, paused ? ProtocolMessageTypes.ControlPause : ProtocolMessageTypes.ControlResume, payload);
            AppLog.Event("INFO", "control.pause.sync", "paused=" + (paused ? 1 : 0) + " peer=" + peer.DeviceId);
        }

        /// <summary>
        /// 心跳里带的暂停状态校准。旧版对端不带这个字段（null），直接跳过。
        /// </summary>
        private void ReconcilePeerPause(bool? peerPaused)
        {
            if (!peerPaused.HasValue || _peerPaused == peerPaused.Value)
            {
                return;
            }

            SetSharingPaused(peerPaused.Value, "PeerHeartbeat");
        }

        private void HandleHeartbeat(ProtocolEnvelope envelope)
        {
            var payload = ProtocolSerializer.FromJson<HeartbeatPayload>(envelope.PayloadJson);
            if (payload == null)
            {
                return;
            }

            if (!payload.Reply)
            {
                // 对端在心跳里带了暂停状态：以它为准校准"对端暂停"。
                // 只在真的不一致时才动，避免每个周期都触发一次 CancelTransfers。
                ReconcilePeerPause(payload.Paused);

                var peer = GetPeer();
                if (peer != null && peer.Address != null)
                {
                    var reply = new HeartbeatPayload
                    {
                        Ticks = payload.Ticks,
                        Reply = true,
                        Paused = _localPaused
                    };
                    SendEnvelope(peer, ProtocolMessageTypes.Heartbeat, ProtocolSerializer.ToJson(reply));
                }

                return;
            }

            // 回包本身也带对方的暂停状态，双向都能自愈。
            ReconcilePeerPause(payload.Paused);

            var rttMs = (DateTime.UtcNow.Ticks - payload.Ticks) / TimeSpan.TicksPerMillisecond;
            if (rttMs < 0)
            {
                rttMs = 0;
            }

            _lastRttMs = (int)Math.Min(int.MaxValue, rttMs);
            if (_lastRttMs >= 100)
            {
                AppLog.Event("WARN", "net.rtt", "rttMs=" + _lastRttMs + " peer=" + envelope.SourceDeviceId +
                                                  " note=high-latency");
            }
            else
            {
                AppLog.Debug("net.rtt", "rttMs=" + _lastRttMs + " peer=" + envelope.SourceDeviceId);
            }
        }

        public void Stop()
        {
            AppLog.Event("INFO", "coordinator.stop", "deviceId=" + (_config != null ? _config.DeviceId : "-"));
            _running = false;
            if (_heartbeatThread != null)
            {
                _heartbeatThread.Join(1000);
                _heartbeatThread = null;
            }
            if (_logHub != null)
            {
                _logHub.Stop();
                _logHub.Dispose();
                _logHub = null;
            }

            if (_discovery != null)
            {
                _discovery.Dispose();
                _discovery = null;
            }

            if (_listener != null)
            {
                _listener.Stop();
                _listener = null;
            }

            if (_inputUdp != null)
            {
                _inputUdp.Close();
                _inputUdp = null;
            }

            lock (_outboundLock)
            {
                CloseOutboundLocked();
            }
        }

        private string GetPeerAddressForLogging()
        {
            lock (_sync)
            {
                if (_peer != null && _peer.Address != null)
                {
                    return _peer.Address.ToString();
                }
            }

            var trusted = GetTrustedDeviceForPeer();
            if (trusted != null && !string.IsNullOrWhiteSpace(trusted.LastKnownAddress))
            {
                return trusted.LastKnownAddress;
            }

            return null;
        }


        public string HandleCommand(AgentCommand command)
        {
            if (command == null)
            {
                AppLog.Warn("Null IPC command.");
                return string.Empty;
            }

            if (command.Command == AgentCommands.GetPeerState)
            {
                return ProtocolSerializer.ToJson(GetPeerState());
            }

            if (command.Command == AgentCommands.FetchClipboardFile)
            {
                return FetchClipboardFiles(command.PayloadJson);
            }

            if (command.Command == AgentCommands.ShutdownService)
            {
                AppLog.Info("ShutdownService requested.");
                ServiceStopSignal.RequestStop();
                LanMouseShareService.RequestStop();
                return "OK";
            }

            if (command.Command == AgentCommands.CancelTransfers)
            {
                // Allowed even while paused: the user is aborting a paste that will never finish.
                return CancelIncomingTransfers();
            }
            if (command.Command == AgentCommands.UpdateLayout)
            {
                var layout = ProtocolSerializer.FromJson<LayoutConfig>(command.PayloadJson);
                if (layout != null)
                {
                    _config.Layout = layout;
                    _configStore.Save(_config);
                    SendLayoutUpdateToPeer(layout);
                }

                return "OK";
            }

            if (command.Command == AgentCommands.UpdateScreenInfo)
            {
                var screen = ProtocolSerializer.FromJson<ScreenInfoPayload>(command.PayloadJson);
                if (screen != null && screen.Width > 0 && screen.Height > 0)
                {
                    _config.ScreenWidth = screen.Width;
                    _config.ScreenHeight = screen.Height;
                    _configStore.Save(_config);
                    SendScreenInfoToPeer(screen);
                    AppLog.Info("Screen info updated. " + screen.Width + "x" + screen.Height);
                }

                return "OK";
            }

            if (command.Command == AgentCommands.UpdateSettings)
            {
                var settings = ProtocolSerializer.FromJson<AgentSettingsDto>(command.PayloadJson);
                if (settings != null)
                {
                    ApplySettings(settings);
                }

                return "OK";
            }

            if (command.Command == AgentCommands.SetControlTarget)
            {
                ApplyControlTargetPayload(command.PayloadJson, "LocalTarget");
                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.PauseControl)
            {
                SetSharingPaused(true, "LocalPause");
                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.ResumeControl)
            {
                SetSharingPaused(false, "LocalResume");
                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.PairDevice)
            {
                AppLog.Info("PairDevice requested.");
                SendPairingRequest(command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.ApprovePairing)
            {
                AppLog.Info("ApprovePairing requested.");
                ApprovePairing(command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.SetManualPeer)
            {
                AppLog.Info("SetManualPeer requested. Address=" + command.PayloadJson);
                SetManualPeer(command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.AcquireControl ||
                command.Command == AgentCommands.ReleaseControl ||
                command.Command == AgentCommands.SetControlTarget ||
                command.Command == AgentCommands.SendInput ||
                command.Command == AgentCommands.SendInputBatch ||
                command.Command == AgentCommands.SendControlLease ||
                command.Command == AgentCommands.SendControlLeaseAck)
            {
                if (SharingPaused && command.Command != AgentCommands.ReleaseControl)
                {
                    AppLog.Info("Command ignored while sharing is paused. Command=" + command.Command);
                    return "OK";
                }

                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            // fix7：暂停的语义是"停止跨屏输入/控制"，剪贴板有自己的开关（ClipboardEnabled）。
            // 旧实现把这三个命令也一起停掉，只要对端进过一次暂停，本机就再也拉不到文件。
            if (command.Command == AgentCommands.SendClipboard ||
                command.Command == AgentCommands.RequestClipboardFiles ||
                command.Command == AgentCommands.RequestClipboardPaste)
            {
                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.SendFiles)
            {
                // fix7：文件传输不再受"暂停"影响。暂停只针对键鼠控制，
                // 用暂停来表达"别传文件"会让人在界面上看不出任何原因（静默丢弃）。
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        SendFiles(command.PayloadJson);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("SendFiles worker failed.", ex);
                    }
                });
                return "OK";
            }

            if (command.Command == AgentCommands.PollEvents)
            {
                return ProtocolSerializer.ToJson(DrainInboundEvents());
            }

            return string.Empty;
        }

        private PeerStateDto GetPeerState()
        {
            lock (_sync)
            {
                var trusted = GetTrustedDeviceForPeer();
                var mismatch = IsPairingMismatchLocked(trusted);
                var peerScreenWidth = _peer != null && _peer.ScreenWidth > 0 ? _peer.ScreenWidth : trusted != null ? trusted.ScreenWidth : 0;
                var peerScreenHeight = _peer != null && _peer.ScreenHeight > 0 ? _peer.ScreenHeight : trusted != null ? trusted.ScreenHeight : 0;
                var reachable = _peer != null && IsPeerReachable(_peer);
                var verified = reachable && trusted != null && !mismatch && IsPeerProtectedVerified(_peer);
                var pairingStatus = PairingStatusLocked(trusted, reachable, verified, mismatch);
                return new PeerStateDto
                {
                    LocalDeviceId = _config.DeviceId,
                    LocalDeviceName = _config.DeviceName,
                    PeerDeviceId = _peer != null ? _peer.DeviceId : trusted != null ? trusted.DeviceId : null,
                    PeerDeviceName = _peer != null ? _peer.DeviceName : trusted != null ? trusted.DeviceName : null,
                    PeerAddress = _peer != null ? _peer.Address.ToString() : trusted != null ? trusted.LastKnownAddress : null,
                    IsConnected = verified,
                    IsTrusted = verified,
                    LayoutDirection = _config.Layout != null ? _config.Layout.PeerDirection : "Right",
                    LocalScreenWidth = _config.ScreenWidth,
                    LocalScreenHeight = _config.ScreenHeight,
                    PeerScreenWidth = peerScreenWidth,
                    PeerScreenHeight = peerScreenHeight,
                    MouseSpeedMultiplier = _config.MouseSpeedMultiplier <= 0 ? 1.0 : _config.MouseSpeedMultiplier,
                    MouseUpdateRateHz = NormalizeMouseRate(_config.MouseUpdateRateHz),
                    ClipboardEnabled = _config.ClipboardEnabled,
                    PauseHotkey = _config.PauseHotkey,
                    StopControlHotkey = _config.StopControlHotkey,
                    ExitHotkey = _config.ExitHotkey,
                    StartWithWindows = _config.StartWithWindows,
                    MinimizeToTrayOnClose = _config.MinimizeToTrayOnClose,
                    BlockScreenCorners = _config.BlockScreenCorners,
                    ScreenEntryRippleEnabled = _config.ScreenEntryRippleEnabled,
                    IncomingDirectory = _config.IncomingDirectory ?? string.Empty,
                    ActiveTargetDeviceId = string.IsNullOrWhiteSpace(_activeTargetDeviceId) ? _config.DeviceId : _activeTargetDeviceId,
                    RunAsAdministratorByDefault = _config.RunAsAdministratorByDefault,
                    PairingStatus = pairingStatus,
                    IsPairingMismatch = mismatch,
                    ShareRole = ShareRoleValues.Normalize(_config.ShareRole),
                    ShareRoleText = ShareRoleValues.DisplayName(_config.ShareRole),
                    ControllerAddress = _config.ControllerAddress,
                    DebugToken = _config.DebugToken,
                    LogRetentionDays = _config.LogRetentionDays <= 0 ? 3 : _config.LogRetentionDays,
                    VerboseLogging = _config.VerboseLogging,
                    LogDirectory = AppLog.LiveDirectory,
                    RemoteLogDirectory = AppLog.RemoteDirectory,
                    LogTransportStatus = _logHub != null ? _logHub.StatusText() : "-",
                    LogTransferEnabled = _config.LogTransferEnabled,
                    NetworkRttMs = _lastRttMs
                };
            }
        }

        private void OnDeviceDiscovered(object sender, DiscoveryReceivedEventArgs e)
        {
            AppLog.Info("Discovered peer. DeviceId=" + e.Packet.DeviceId + " Name=" + e.Packet.DeviceName + " Address=" + e.Address);
            lock (_sync)
            {
                var previousPeer = _peer;
                var isSamePeer = previousPeer != null &&
                                 (previousPeer.DeviceId == e.Packet.DeviceId ||
                                  previousPeer.Address.ToString() == e.Address.ToString());
                var pendingCode = isSamePeer
                    ? previousPeer.PendingPairingCode
                    : null;
                var protectedUtc = isSamePeer
                    ? previousPeer.LastProtectedEnvelopeUtc
                    : DateTime.MinValue;
                _peer = new DiscoveredPeer
                {
                    DeviceId = e.Packet.DeviceId,
                    DeviceName = e.Packet.DeviceName,
                    Address = e.Address,
                    TcpPort = e.Packet.TcpPort,
                    PendingPairingCode = pendingCode,
                    ScreenWidth = isSamePeer ? previousPeer.ScreenWidth : 0,
                    ScreenHeight = isSamePeer ? previousPeer.ScreenHeight : 0,
                    LastReachableUtc = DateTime.UtcNow,
                    LastProtectedEnvelopeUtc = protectedUtc
                };
            }
            if (IsTrustedDevice(e.Packet.DeviceId))
            {
                SendScreenInfoToPeer(new ScreenInfoPayload { Width = _config.ScreenWidth, Height = _config.ScreenHeight });
            }
        }

        private void SetManualPeer(string addressText)
        {
            IPAddress address;
            if (!IPAddress.TryParse((addressText ?? string.Empty).Trim(), out address))
            {
                AppLog.Warn("Invalid manual peer IP: " + addressText);
                return;
            }

            lock (_sync)
            {
                var trusted = _config.TrustedDevices.FirstOrDefault(x => x.LastKnownAddress == address.ToString()) ??
                              _config.TrustedDevices.FirstOrDefault();
                _peer = new DiscoveredPeer
                {
                    DeviceId = trusted != null ? trusted.DeviceId : null,
                    DeviceName = trusted != null ? trusted.DeviceName : "Manual peer",
                    Address = address,
                    TcpPort = TcpPort,
                    ScreenWidth = trusted != null ? trusted.ScreenWidth : 0,
                    ScreenHeight = trusted != null ? trusted.ScreenHeight : 0,
                    LastReachableUtc = DateTime.MinValue
                };
            }
            AppLog.Info("Manual peer set. Address=" + address);
            SendScreenInfoToPeer(new ScreenInfoPayload { Width = _config.ScreenWidth, Height = _config.ScreenHeight });
        }

        private void ListenTcpLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(ReadClient, client);
                }
                catch
                (Exception ex)
                {
                    if (_running)
                    {
                        AppLog.Error("TCP accept loop error.", ex);
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private void ListenUdpInputLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint endpoint = null;
                    var bytes = _inputUdp.Receive(ref endpoint);
                    var frame = ProtocolSerializer.FromUtf8JsonBytes<TransportFrame>(bytes);
                    var envelope = OpenFrame(frame);
                    HandleEnvelope(envelope, endpoint.Address.ToString());
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        AppLog.Error("UDP input receive error.", ex);
                        Thread.Sleep(50);
                    }
                }
            }
        }

        private void ReadClient(object state)
        {
            try
            {
                using (var client = (TcpClient)state)
                using (var stream = client.GetStream())
                {
                    var address = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    while (true)
                    {
                        var bytes = TryReadFrame(stream);
                        if (bytes == null)
                        {
                            break;
                        }

                        var frame = ProtocolSerializer.FromUtf8JsonBytes<TransportFrame>(bytes);
                        var envelope = OpenFrame(frame);
                        AppLog.Info("Received TCP envelope. Type=" + (envelope != null ? envelope.MessageType : "<null>"));
                        HandleEnvelope(envelope, address);
                        if (envelope != null && envelope.MessageType == ProtocolMessageTypes.FileStreamStart)
                        {
                            // fix7：不管这次传输收不收，都必须把 start.Length 个字节从流里读掉。
                            // 之前暂停时直接跳过，文件内容留在 TCP 流里，下一次 TryReadFrame
                            // 会把文件字节当成长度前缀解析，报 "Invalid TCP frame length"，
                            // 整条连接此后就废了。
                            ReadIncomingFileStream(stream, ProtocolSerializer.FromJson<FileStreamStartPayload>(envelope.PayloadJson));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TCP client read/handle error.", ex);
            }
        }

        private void HandleEnvelope(ProtocolEnvelope envelope, string address)
        {
            if (envelope == null)
            {
                return;
            }

            MarkPeerReachable(envelope.SourceDeviceId, address);

            if (envelope.MessageType == ProtocolMessageTypes.Heartbeat)
            {
                HandleHeartbeat(envelope);
                return;
            }

            if (envelope.MessageType == ProtocolMessageTypes.PairingRequest)
            {
                var payload = ProtocolSerializer.FromJson<PairingRequestPayload>(envelope.PayloadJson);
                if (payload != null)
                {
                    AppLog.Info("PairingRequest received from " + payload.DeviceName + " code=" + payload.PairingCode);
                    lock (_sync)
                    {
                        _peer = new DiscoveredPeer
                        {
                            DeviceId = payload.DeviceId,
                            DeviceName = payload.DeviceName,
                            Address = IPAddress.Parse(address),
                            TcpPort = TcpPort,
                            PendingPairingCode = payload.PairingCode,
                            LastReachableUtc = DateTime.UtcNow
                        };
                    }
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.PairingConfirm)
            {
                var payload = ProtocolSerializer.FromJson<PairingConfirmPayload>(envelope.PayloadJson);
                if (payload != null)
                {
                    AppLog.Info("PairingConfirm received from " + payload.DeviceName);
                    TrustDevice(payload.DeviceId, payload.DeviceName, address, payload.SharedSecret);
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.LayoutUpdate)
            {
                var layout = ProtocolSerializer.FromJson<LayoutConfig>(envelope.PayloadJson);
                if (layout != null)
                {
                    _config.Layout = layout;
                    // 边角阻挡随布局同步（问题③修复）：对端勾选/取消时本机跟随，
                    // 保证两个切换方向的行为一致。收包只保存不转发，避免回环。
                    if (layout.BlockScreenCorners.HasValue)
                    {
                        _config.BlockScreenCorners = layout.BlockScreenCorners.Value;
                    }

                    _configStore.Save(_config);
                    AppLog.Info("LayoutUpdate received. PeerDirection=" + layout.PeerDirection +
                        " BlockScreenCorners=" + _config.BlockScreenCorners);
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ScreenInfoUpdate)
            {
                var screen = ProtocolSerializer.FromJson<ScreenInfoPayload>(envelope.PayloadJson);
                if (screen != null && screen.Width > 0 && screen.Height > 0)
                {
                    UpdatePeerScreenInfo(envelope.SourceDeviceId, screen.Width, screen.Height);
                    AppLog.Info("ScreenInfoUpdate received. " + screen.Width + "x" + screen.Height);
                }
            }
            else if (envelope.MessageType == ProtocolMessageTypes.ControlTargetChanged)
            {
                ApplyControlTargetPayload(envelope.PayloadJson, "PeerTarget");
                QueueEvent(envelope);
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileOffer)
            {
                // fix7：暂停只拦输入/控制，文件公告必须照收，否则对端拖过来的文件
                // 会被静默丢弃，发送端最后只看到 "远程主机强迫关闭了一个现有的连接"。
                BeginIncomingFile(ProtocolSerializer.FromJson<FileOfferPayload>(envelope.PayloadJson));
                QueueEvent(envelope);
            }
            else if (envelope.MessageType == ProtocolMessageTypes.FileComplete)
            {
                FinishIncomingFile(ProtocolSerializer.FromJson<FileCompletePayload>(envelope.PayloadJson));
            }
            else
            {
                if (envelope.MessageType == ProtocolMessageTypes.ControlPause)
                {
                    SetSharingPaused(true, "PeerPause");
                }
                else if (envelope.MessageType == ProtocolMessageTypes.ControlResume)
                {
                    SetSharingPaused(false, "PeerResume");
                    if (_localPaused)
                    {
                        // 本机用户自己也按了暂停：对端的 Resume 只解除"对端暂停"，
                        // 不能把界面上的"已暂停"清掉，否则界面与实际行为不一致。
                        AppLog.Info("Peer resume kept local pause. Local=True Peer=False");
                        return;
                    }
                }
                else if (SharingPaused && IsCrossScreenEnvelope(envelope.MessageType))
                {
                    AppLog.Info("Envelope ignored while sharing is paused. Type=" + envelope.MessageType);
                    return;
                }

                QueueEvent(envelope);
                if (!IsNoisyEnvelope(envelope.MessageType))
                {
                    AppLog.Info("Envelope queued. Type=" + envelope.MessageType);
                }
            }
        }

        private void SendPairingRequest(string pairingCode)
        {
            var peer = GetPeer();
            if (peer == null)
            {
                AppLog.Warn("SendPairingRequest ignored: no peer.");
                return;
            }

            var payload = new PairingRequestPayload
            {
                DeviceId = _config.DeviceId,
                DeviceName = _config.DeviceName,
                PairingCode = string.IsNullOrWhiteSpace(pairingCode) ? SecureRandom.NumericCode(6) : pairingCode
            };
            SendEnvelope(peer, ProtocolMessageTypes.PairingRequest, ProtocolSerializer.ToJson(payload));
            AppLog.Info("PairingRequest sent to " + peer.Address + " code=" + payload.PairingCode);
            SendScreenInfoToPeer(new ScreenInfoPayload { Width = _config.ScreenWidth, Height = _config.ScreenHeight });
        }

        private void ApprovePairing(string code)
        {
            var peer = GetPeer();
            if (peer == null || string.IsNullOrWhiteSpace(code) || peer.PendingPairingCode != code)
            {
                AppLog.Warn("ApprovePairing ignored. HasPeer=" + (peer != null) + " CodeMatches=" + (peer != null && peer.PendingPairingCode == code));
                return;
            }

            var sharedSecret = SecureRandom.Bytes(32);
            TrustDevice(peer.DeviceId, peer.DeviceName, peer.Address.ToString(), sharedSecret);
            var payload = new PairingConfirmPayload
            {
                DeviceId = _config.DeviceId,
                DeviceName = _config.DeviceName,
                SharedSecret = sharedSecret
            };
            SendEnvelope(peer, ProtocolMessageTypes.PairingConfirm, ProtocolSerializer.ToJson(payload));
            AppLog.Info("PairingConfirm sent to " + peer.Address);
            SendScreenInfoToPeer(new ScreenInfoPayload { Width = _config.ScreenWidth, Height = _config.ScreenHeight });
        }

        private void SendRawPayload(string command, string payloadJson)
        {
            var peer = GetPeer();
            if (peer == null)
            {
                AppLog.Warn("SendRawPayload ignored: no peer. Command=" + command);
                return;
            }

            if (!IsPeerUsable(peer))
            {
                AppLog.Warn("SendRawPayload ignored: peer is not verified. Command=" + command + " PeerDeviceId=" + peer.DeviceId);
                return;
            }

            string messageType = command;
            if (command == AgentCommands.AcquireControl)
            {
                messageType = ProtocolMessageTypes.ControlAcquire;
            }
            else if (command == AgentCommands.ReleaseControl)
            {
                messageType = ProtocolMessageTypes.ControlRelease;
            }
            else if (command == AgentCommands.SetControlTarget)
            {
                messageType = ProtocolMessageTypes.ControlTargetChanged;
            }
            else if (command == AgentCommands.PauseControl)
            {
                messageType = ProtocolMessageTypes.ControlPause;
            }
            else if (command == AgentCommands.ResumeControl)
            {
                messageType = ProtocolMessageTypes.ControlResume;
            }
            else if (command == AgentCommands.SendInput)
            {
                messageType = ProtocolMessageTypes.InputEvent;
            }
            else if (command == AgentCommands.SendInputBatch)
            {
                messageType = ProtocolMessageTypes.InputBatch;
            }
            else if (command == AgentCommands.SendControlLease)
            {
                messageType = ProtocolMessageTypes.ControlLease;
            }
            else if (command == AgentCommands.SendControlLeaseAck)
            {
                messageType = ProtocolMessageTypes.ControlLeaseAck;
            }
            else if (command == AgentCommands.SendClipboard)
            {
                messageType = ProtocolMessageTypes.ClipboardUpdate;
            }
            else if (command == AgentCommands.RequestClipboardFiles)
            {
                messageType = ProtocolMessageTypes.ClipboardPull;
            }
            else if (command == AgentCommands.RequestClipboardPaste)
            {
                messageType = ProtocolMessageTypes.ClipboardPasteRequest;
            }

            if (messageType == ProtocolMessageTypes.InputBatch ||
                messageType == ProtocolMessageTypes.InputEvent ||
                messageType == ProtocolMessageTypes.ControlLease ||
                messageType == ProtocolMessageTypes.ControlLeaseAck)
            {
                if (_logHub != null)
                {
                    // Log traffic is the only thing that can compete with input for this NIC;
                    // tell the forwarder to hold back its batches for a moment.
                    _logHub.NotifyInputActivity();
                }

                SendEnvelopeUdp(peer, messageType, payloadJson);
            }
            else
            {
                SendEnvelope(peer, messageType, payloadJson);
            }
        }

        private void SendLayoutUpdateToPeer(LayoutConfig localLayout)
        {
            var peer = GetPeer();
            if (peer == null || localLayout == null)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var peerLayout = new LayoutConfig { PeerDirection = OppositeDirection(localLayout.PeerDirection) };
                    // 边角阻挡布尔值对两个方向语义相同，直接透传（问题③修复）。
                    if (localLayout.BlockScreenCorners.HasValue)
                    {
                        peerLayout.BlockScreenCorners = localLayout.BlockScreenCorners;
                    }

                    SendEnvelope(peer, ProtocolMessageTypes.LayoutUpdate, ProtocolSerializer.ToJson(peerLayout));
                    AppLog.Info("LayoutUpdate sent. Local=" + localLayout.PeerDirection + " Peer=" + peerLayout.PeerDirection);
                }
                catch (Exception ex)
                {
                    AppLog.Error("LayoutUpdate send failed.", ex);
                }
            });
        }

        private void SendScreenInfoToPeer(ScreenInfoPayload screen)
        {
            var peer = GetPeer();
            if (peer == null || screen == null)
            {
                return;
            }

            if (!IsTrustedDevice(peer.DeviceId))
            {
                AppLog.Warn("ScreenInfoUpdate ignored: peer is not trusted. PeerDeviceId=" + peer.DeviceId);
                return;
            }

            var payloadJson = ProtocolSerializer.ToJson(screen);
            lock (_sync)
            {
                if (_lastScreenInfoSent == payloadJson)
                {
                    AppLog.Debug("net.send.skip", "type=ScreenInfoUpdate reason=unchanged");
                    return;
                }

                _lastScreenInfoSent = payloadJson;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendEnvelope(peer, ProtocolMessageTypes.ScreenInfoUpdate, payloadJson);
                }
                catch (Exception ex)
                {
                    AppLog.Error("ScreenInfoUpdate send failed.", ex);
                }
            });
        }

        private void ApplySettings(AgentSettingsDto settings)
        {
            string localPeerDirection;
            lock (_sync)
            {
                _config.MouseSpeedMultiplier = settings.MouseSpeedMultiplier <= 0 ? 1.0 : settings.MouseSpeedMultiplier;
                _config.MouseUpdateRateHz = NormalizeMouseRate(settings.MouseUpdateRateHz);
                _config.ClipboardEnabled = settings.ClipboardEnabled;
                _config.PauseHotkey = string.IsNullOrWhiteSpace(settings.PauseHotkey) ? "Ctrl+F11" : settings.PauseHotkey;
                _config.StopControlHotkey = string.IsNullOrWhiteSpace(settings.StopControlHotkey) ? "Ctrl+F10" : settings.StopControlHotkey;
                _config.ExitHotkey = string.IsNullOrWhiteSpace(settings.ExitHotkey) ? "Ctrl+Alt+End" : settings.ExitHotkey;
                _config.StartWithWindows = settings.StartWithWindows;
                _config.MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
                _config.BlockScreenCorners = settings.BlockScreenCorners;
                _config.ScreenEntryRippleEnabled = settings.ScreenEntryRippleEnabled;
                // fix22：空字符串表示“用默认桌面”，不要写 null，否则旧配置反序列化后会把界面显示成空白。
                _config.IncomingDirectory = settings.IncomingDirectory == null ? string.Empty : settings.IncomingDirectory.Trim();
                _config.RunAsAdministratorByDefault = settings.RunAsAdministratorByDefault;
                _config.ShareRole = ShareRoleValues.Normalize(settings.ShareRole);
                _config.ControllerAddress = string.IsNullOrWhiteSpace(settings.ControllerAddress) ? null : settings.ControllerAddress.Trim();
                _config.DebugToken = string.IsNullOrWhiteSpace(settings.DebugToken) ? null : settings.DebugToken.Trim();
                _config.LogRetentionDays = settings.LogRetentionDays <= 0 ? 3 : Math.Min(settings.LogRetentionDays, 30);
                _config.VerboseLogging = settings.VerboseLogging;
                _config.LogTransferEnabled = settings.LogTransferEnabled;
                _configStore.Save(_config);
                localPeerDirection = _config.Layout != null ? _config.Layout.PeerDirection : null;
            }

            AppLog.SetVerbose(settings.VerboseLogging);
            if (_logHub != null)
            {
                _logHub.Apply(_config);
            }

            AppLog.Event("INFO", "settings.updated",
                "speed=" + settings.MouseSpeedMultiplier.ToString("0.00") +
                " rateHz=" + NormalizeMouseRate(settings.MouseUpdateRateHz) +
                " clipboard=" + settings.ClipboardEnabled +
                " pauseHotkey=" + settings.PauseHotkey +
                " stopHotkey=" + settings.StopControlHotkey +
                " exitHotkey=" + settings.ExitHotkey +
                " startWithWindows=" + settings.StartWithWindows +
                " minimizeToTray=" + settings.MinimizeToTrayOnClose +
                " blockCorners=" + settings.BlockScreenCorners +
                " ripple=" + settings.ScreenEntryRippleEnabled +
                " incomingDir=" + (string.IsNullOrEmpty(_config.IncomingDirectory) ? "default" : _config.IncomingDirectory) +
                " runAsAdminDefault=" + settings.RunAsAdministratorByDefault +
                " shareRole=" + ShareRoleValues.DisplayName(_config.ShareRole) +
                " controllerAddress=" + (_config.ControllerAddress ?? "auto") +
                " logRetentionDays=" + _config.LogRetentionDays +
                " verbose=" + _config.VerboseLogging +
                " logTransfer=" + _config.LogTransferEnabled);

            // 边角阻挡开关同步给对端（问题③修复）：BlockScreenCorners 原本只存本机，
            // 导致一台机器挡角生效、另一台失效。设置保存后随布局广播，最后写者胜。
            if (!string.IsNullOrEmpty(localPeerDirection))
            {
                SendLayoutUpdateToPeer(new LayoutConfig
                {
                    PeerDirection = localPeerDirection,
                    BlockScreenCorners = settings.BlockScreenCorners
                });
            }
        }

        private void SetSharingPaused(bool paused, string reason)
        {
            bool peerScoped = !string.IsNullOrEmpty(reason) && reason.StartsWith("Peer", StringComparison.Ordinal);
            bool changed;

            lock (_sync)
            {
                if (peerScoped)
                {
                    changed = _peerPaused != paused;
                    _peerPaused = paused;
                }
                else
                {
                    changed = _localPaused != paused;
                    _localPaused = paused;
                }

                if (paused)
                {
                    _activeTargetDeviceId = _config != null ? _config.DeviceId : null;
                }
            }

            if (paused)
            {
                CancelTransfers(reason);
            }

            AppLog.Info("Sharing paused state changed. Paused=" + paused +
                " Local=" + _localPaused + " Peer=" + _peerPaused +
                " Effective=" + SharingPaused +
                " Changed=" + changed + " Reason=" + reason);
        }

        private void ApplyControlTargetPayload(string payloadJson, string reason)
        {
            var payload = ProtocolSerializer.FromJson<ControlTargetPayload>(payloadJson);
            var target = payload != null ? payload.ActiveTargetDeviceId : null;
            var previous = _activeTargetDeviceId;
            string peerId;
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = _config.DeviceId;
                }

                peerId = _peer != null && !string.IsNullOrWhiteSpace(_peer.DeviceId)
                    ? _peer.DeviceId
                    : _config.TrustedDevices.FirstOrDefault() != null
                        ? _config.TrustedDevices.FirstOrDefault().DeviceId
                        : null;

                if (!string.Equals(target, _config.DeviceId, StringComparison.Ordinal) &&
                    !string.Equals(target, peerId, StringComparison.Ordinal))
                {
                    AppLog.Warn("Control target ignored: unknown target. Target=" + target + " Reason=" + reason);
                    target = _config.DeviceId;
                }

                _activeTargetDeviceId = target;
            }

            var targetRole = string.Equals(target, _config.DeviceId, StringComparison.Ordinal) ? "local" : "peer";
            if (string.Equals(previous, target, StringComparison.Ordinal))
            {
                AppLog.Debug("control.target.unchanged", "target=" + target + " role=" + targetRole + " reason=" + reason);
            }
            else
            {
                AppLog.Event("INFO", "control.target",
                    "previous=" + (previous ?? "-") + " target=" + target + " role=" + targetRole +
                    " reason=" + reason + (payload != null && !string.IsNullOrWhiteSpace(payload.Reason)
                        ? " peerReason=" + payload.Reason
                        : string.Empty));
            }

            // Two machines both believing they are the controller is what wedged input in 0.1.43:
            // each side swallowed its physical input and the other side dropped it because it was
            // not the active target. Keep the evidence in the log so the next report is diagnosable.
            if (string.Equals(reason, "PeerTarget", StringComparison.Ordinal) &&
                string.Equals(target, _config.DeviceId, StringComparison.Ordinal) &&
                previous != null && string.Equals(previous, peerId, StringComparison.Ordinal))
            {
                AppLog.Event("WARN", "control.conflict",
                    "peer=" + (peerId ?? "-") + " targetRole=local previousRole=peer peerReason=" +
                    (payload != null ? payload.Reason : "-") +
                    " hint=both-sides-may-claim-controller");
            }
        }

        private void CancelTransfers(string reason)
        {
            IncomingTransfer[] transfers;
            lock (_sync)
            {
                transfers = _incomingTransfers.Values.ToArray();
                _incomingTransfers.Clear();
                _outgoingTransfers.Clear();
            }

            for (int i = 0; i < transfers.Length; i++)
            {
                try
                {
                    transfers[i].Stream.Close();
                }
                catch
                {
                }

                try
                {
                    if (!string.IsNullOrEmpty(transfers[i].TempPath) && File.Exists(transfers[i].TempPath))
                    {
                        File.Delete(transfers[i].TempPath);
                    }
                }
                catch
                {
                }
            }

            AppLog.Info("Transfers canceled. Count=" + transfers.Length + " Reason=" + reason);
        }

        /// <summary>
        /// Pulls the requested peer paths and waits (bounded) for the transfers to complete so the
        /// caller can serve them as clipboard content on demand (delayed rendering).
        /// </summary>
        private string FetchClipboardFiles(string payloadJson)
        {
            var request = ProtocolSerializer.FromJson<ClipboardPullRequestPayload>(payloadJson);
            var wanted = request != null && request.FilePaths != null
                ? request.FilePaths
                : new string[0];
            var result = new ClipboardFetchResultPayload
            {
                Paths = new string[0],
                Missing = wanted
            };

            if (wanted.Length == 0)
            {
                return ProtocolSerializer.ToJson(result);
            }

            lock (_sync)
            {
                for (int i = 0; i < wanted.Length; i++)
                {
                    if (!string.IsNullOrEmpty(wanted[i]))
                    {
                        _fetchedPaths.Remove(wanted[i]);
                    }
                }
            }

            AppLog.Event("INFO", "clipboard.fetch.start", "count=" + wanted.Length);
            SendRawPayloadAsync(AgentCommands.RequestClipboardFiles, payloadJson);

            var deadline = DateTime.UtcNow.AddSeconds(25);
            var found = new List<string>();
            var missing = new List<string>();
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync)
                {
                    found.Clear();
                    missing.Clear();
                    for (int i = 0; i < wanted.Length; i++)
                    {
                        string path = wanted[i];
                        string saved;
                        if (!string.IsNullOrEmpty(path) && _fetchedPaths.TryGetValue(path, out saved) && File.Exists(saved))
                        {
                            found.Add(saved);
                        }
                        else
                        {
                            missing.Add(path);
                        }
                    }
                }

                if (missing.Count == 0)
                {
                    break;
                }

                Thread.Sleep(60);
            }

            result.Paths = found.ToArray();
            result.Missing = missing.ToArray();
            result.TimedOut = missing.Count > 0;
            AppLog.Event("INFO", "clipboard.fetch.done",
                "wanted=" + wanted.Length + " got=" + found.Count + " timedOut=" + (result.TimedOut ? 1 : 0));
            return ProtocolSerializer.ToJson(result);
        }
        private void SendRawPayloadAsync(string command, string payloadJson)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendRawPayload(command, payloadJson);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Async send failed. Command=" + command, ex);
                }
            });
        }

        private void SendFiles(string payloadJson)
        {
            var peer = GetPeer();
            if (peer == null)
            {
                AppLog.Warn("SendFiles ignored: no peer.");
                return;
            }

            if (!IsPeerUsable(peer))
            {
                AppLog.Warn("SendFiles ignored: peer is not verified. PeerDeviceId=" + peer.DeviceId);
                return;
            }

            var payload = ProtocolSerializer.FromJson<FileTransferRequestPayload>(payloadJson);
            if (payload == null || payload.FilePaths == null)
            {
                AppLog.Warn("SendFiles ignored: empty payload.");
                return;
            }

            AppLog.Info("SendFiles requested. Count=" + payload.FilePaths.Length + " Peer=" + peer.DeviceName + " Address=" + peer.Address);
            foreach (var path in payload.FilePaths)
            {
                if (!File.Exists(path))
                {
                    AppLog.Warn("SendFiles skipped missing path. Path=" + path);
                    continue;
                }

                var fileInfo = new FileInfo(path);
                var transferKey = BuildOutgoingTransferKey(path, fileInfo);
                if (!TryBeginOutgoingTransfer(transferKey))
                {
                    AppLog.Info("SendFiles ignored: transfer already in progress. Path=" + path + " Length=" + fileInfo.Length);
                    continue;
                }

                try
                {
                    SendFile(peer, path, fileInfo);
                }
                catch (Exception ex)
                {
                    AppLog.Error("SendFile failed. Path=" + path + " Peer=" + peer.DeviceName + " Address=" + peer.Address, ex);
                }
                finally
                {
                    EndOutgoingTransfer(transferKey);
                }
            }
        }

        private void SendFile(DiscoveredPeer peer, string path, FileInfo fileInfo)
        {
            var transferId = Guid.NewGuid().ToString("N");
            var sha256 = ComputeSha256(path);
            var offer = new FileOfferPayload
            {
                TransferId = transferId,
                FileName = fileInfo.Name,
                Length = fileInfo.Length,
                Sha256Hex = sha256
            };

            AppLog.Info("FileOffer sending. TransferId=" + transferId + " Path=" + path + " Length=" + fileInfo.Length + " Sha256=" + sha256);
            using (var client = new TcpClient())
            {
                ConnectWithTimeout(client, peer.Address, peer.TcpPort, 5000);
                client.SendTimeout = 30000;
                client.ReceiveTimeout = 30000;
                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = 30000;
                    SendEnvelopeOnStream(stream, peer, ProtocolMessageTypes.FileOffer, ProtocolSerializer.ToJson(offer));
                    SendEnvelopeOnStream(stream, peer, ProtocolMessageTypes.FileStreamStart, ProtocolSerializer.ToJson(new FileStreamStartPayload
                    {
                        TransferId = transferId,
                        Length = fileInfo.Length
                    }));
                    SendFileStream(stream, transferId, path, fileInfo.Length);
                    SendEnvelopeOnStream(stream, peer, ProtocolMessageTypes.FileComplete, ProtocolSerializer.ToJson(new FileCompletePayload
                    {
                        TransferId = transferId,
                        Success = true,
                        Message = "Sent"
                    }));
                    AppLog.Info("FileComplete sent. Path=" + path + " TransferId=" + transferId);
                }
            }
        }

        private void SendFileStream(Stream stream, string transferId, string path, long length)
        {
            var buffer = new byte[1024 * 1024];
            long sent = 0;
            long lastLoggedBytes = 0;
            var startedUtc = DateTime.UtcNow;
            var lastProgressUtc = DateTime.MinValue;
            var fileName = Path.GetFileName(path);
            using (var file = File.OpenRead(path))
            {
                while (true)
                {
                    int read = file.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    stream.Write(buffer, 0, read);
                    sent += read;
                    if (sent == read || sent == length || sent - lastLoggedBytes >= 16L * 1024 * 1024)
                    {
                        lastLoggedBytes = sent;
                        AppLog.Info("FileStream sent. TransferId=" + transferId +
                                    " Sent=" + sent +
                                    "/" + length +
                                    " SpeedMBps=" + FormatSpeedMbps(sent, startedUtc));
                    }

                    // A 250 ms cadence keeps the UI smooth without flooding the event queue.
                    var now = DateTime.UtcNow;
                    if ((now - lastProgressUtc).TotalMilliseconds >= 250 || sent >= length)
                    {
                        lastProgressUtc = now;
                        QueueFileProgress(transferId, fileName, sent, length, "send",
                            (long)FormatBytesPerSecond(sent, startedUtc), sent >= length);
                    }
                }
            }
        }

        /// <summary>Pushes one progress snapshot to the local agent so the UI can draw a bar.</summary>
        private void QueueFileProgress(string transferId, string fileName, long transferred, long total,
            string direction, long speedBytesPerSecond, bool done)
        {
            try
            {
                var payload = ProtocolSerializer.ToJson(new FileProgressPayload
                {
                    TransferId = transferId,
                    FileName = fileName,
                    Transferred = transferred,
                    Total = total,
                    Direction = direction,
                    SpeedBytesPerSecond = speedBytesPerSecond,
                    Done = done
                });
                QueueEvent(ProtocolEnvelope.Create(_config.DeviceId, null,
                    ProtocolMessageTypes.FileProgress, payload));
            }
            catch (Exception ex)
            {
                AppLog.Warn("QueueFileProgress failed. " + ex.Message);
            }
        }

        private static double FormatBytesPerSecond(long bytes, DateTime startedUtc)
        {
            var seconds = (DateTime.UtcNow - startedUtc).TotalSeconds;
            return seconds <= 0.001 ? 0 : bytes / seconds;
        }

        /// <summary>
        /// The service runs as LocalSystem, so SpecialFolder.DesktopDirectory points at the
        /// system profile. Received files must land where the interactive user can see them,
        /// therefore the public desktop is used.
        /// </summary>
        private string ResolveIncomingFolder()
        {
            // fix22：用户自定义接收目录优先。服务以 LocalSystem 跑，能不能写要现场探一次；
            // 探不进去就退回下面的公共桌面逻辑，绝不让一次传输因为配置坏掉而失败。
            var custom = _config != null ? _config.IncomingDirectory : null;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                var resolved = TryPrepareDirectory(custom.Trim());
                if (resolved != null)
                {
                    AppLog.Debug("transfer.dir.custom", "path=" + resolved);
                    return resolved;
                }

                AppLog.Warn("transfer.dir.custom.invalid path=" + custom);
            }

            var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
            if (string.IsNullOrEmpty(publicRoot) || !Directory.Exists(publicRoot))
            {
                publicRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            }

            if (string.IsNullOrEmpty(publicRoot) || !Directory.Exists(publicRoot))
            {
                publicRoot = Path.GetTempPath();
            }

            var desktop = Path.Combine(publicRoot, "Desktop");
            if (!Directory.Exists(desktop))
            {
                desktop = publicRoot;
            }

            return Path.Combine(desktop, "Mouse传输文件");
        }

        /// <summary>
        /// fix22：确认目录存在且真的能写。系统盘根目录、只读网络盘、被拔掉的 U 盘都会在这里被挡下来，
        /// 由调用方退回默认目录，而不是让文件流写到一半才抛异常。
        /// </summary>
        private static string TryPrepareDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var full = Path.GetFullPath(path);
                Directory.CreateDirectory(full);
                var probe = Path.Combine(full, ".lms-write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return full;
            }
            catch (Exception ex)
            {
                AppLog.Warn("transfer.dir.probe.failed path=" + path + " " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private string CancelIncomingTransfers()
        {
            IncomingTransfer[] transfers;
            lock (_sync)
            {
                transfers = _incomingTransfers.Values.ToArray();
                _incomingTransfers.Clear();
            }

            var removed = 0;
            foreach (var transfer in transfers)
            {
                try
                {
                    // The reader thread holds transfer.Sync while writing. Taking the same lock
                    // makes the cancel wait for the current 1 MB write instead of disposing the
                    // file stream underneath it (that used to throw a NullReferenceException on
                    // the reader thread and reset the connection).
                    lock (transfer.Sync)
                    {
                        if (transfer.Stream != null)
                        {
                            transfer.Stream.Dispose();
                            transfer.Stream = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(transfer.TempPath) && File.Exists(transfer.TempPath))
                    {
                        File.Delete(transfer.TempPath);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Cancel transfer cleanup failed. " + ex.Message);
                }
            }

            AppLog.Event("INFO", "file.transfer.cancelled",
                "transfers=" + transfers.Length + " deleted=" + removed);
            return "OK";
        }
        private void BeginIncomingFile(FileOfferPayload offer)
        {
            if (offer == null)
            {
                return;
            }

            var directory = ResolveIncomingFolder();

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var safeFileName = Path.GetFileName(offer.FileName);
            var destination = UniquePath(Path.Combine(directory, safeFileName));
            var tempPath = destination + ".part";
            if (File.Exists(tempPath))
            {
                tempPath = UniquePath(tempPath);
            }

            var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            lock (_sync)
            {
                _incomingTransfers[offer.TransferId] = new IncomingTransfer
                {
                    Offer = offer,
                    DestinationPath = destination,
                    TempPath = tempPath,
                    Stream = stream
                };
            }
            AppLog.Info("Incoming file started. File=" + safeFileName + " Length=" + offer.Length + " Temp=" + tempPath);
        }

        private void ReadIncomingFileStream(Stream input, FileStreamStartPayload start)
        {
            if (start == null || string.IsNullOrEmpty(start.TransferId) || start.Length < 0)
            {
                AppLog.Warn("FileStreamStart ignored: invalid payload.");
                return;
            }

            IncomingTransfer transfer = null;
            lock (_sync)
            {
                _incomingTransfers.TryGetValue(start.TransferId, out transfer);
            }

            // 没有对应的 FileOffer（被拒绝 / 已经取消 / 不属于这条连接）：仍然要把字节读掉，
            // 否则文件内容会污染后面的帧解析（"Invalid TCP frame length"），整条连接报废。
            var draining = transfer == null || transfer.Stream == null;
            if (draining)
            {
                AppLog.Event("WARN", "file.transfer.rejected",
                    "transferId=" + start.TransferId + " length=" + start.Length +
                    " reason=" + (transfer == null ? "no-offer" : "cancelled"));
            }

            var buffer = new byte[1024 * 1024];
            long remaining = start.Length;
            long received = 0;
            long lastLoggedBytes = 0;
            var startedUtc = DateTime.UtcNow;
            var lastProgressUtc = DateTime.MinValue;
            var fileName = transfer != null && transfer.Offer != null ? transfer.Offer.FileName : "";
            AppLog.Info("FileStream receiving. TransferId=" + start.TransferId + " Length=" + start.Length +
                        " Mode=" + (draining ? "drain" : "write"));
            lock (transfer != null ? transfer.Sync : new object())
            {
                if (!draining)
                {
                    transfer.Stream.Position = 0;
                }

                while (remaining > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("File stream ended before all bytes were received.");
                    }

                    if (!draining && transfer.Stream == null)
                    {
                        // 传输中途被取消：剩下的字节继续读掉，避免污染帧流。
                        AppLog.Warn("FileStream receiving aborted mid-stream: transfer was cancelled. TransferId=" + start.TransferId);
                        draining = true;
                    }

                    if (!draining)
                    {
                        transfer.Stream.Write(buffer, 0, read);
                    }

                    remaining -= read;
                    received += read;
                    if (!draining)
                    {
                        transfer.ReceivedBytes = received;
                    }

                    if (received == read || remaining == 0 || received - lastLoggedBytes >= 16L * 1024 * 1024)
                    {
                        lastLoggedBytes = received;
                        AppLog.Info("FileStream received. TransferId=" + start.TransferId +
                                    " Received=" + received +
                                    "/" + start.Length +
                                    " SpeedMBps=" + FormatSpeedMbps(received, startedUtc));
                    }

                    var now = DateTime.UtcNow;
                    if (!draining
                        && ((now - lastProgressUtc).TotalMilliseconds >= 250 || remaining == 0))
                    {
                        lastProgressUtc = now;
                        QueueFileProgress(start.TransferId, fileName, received, start.Length, "receive",
                            (long)FormatBytesPerSecond(received, startedUtc), remaining == 0);
                    }
                }
            }

            if (draining && transfer != null && transfer.Stream == null)
            {
                // 已取消的传输：清掉残留的 .part，别留下半个文件。
                RemoveIncomingTransfer(start.TransferId);
                AppLog.Event("INFO", "file.transfer.drained", "transferId=" + start.TransferId + " bytes=" + received);
            }
        }

        private void FinishIncomingFile(FileCompletePayload complete)
        {
            if (complete == null)
            {
                return;
            }

            IncomingTransfer transfer = null;
            lock (_sync)
            {
                if (_incomingTransfers.TryGetValue(complete.TransferId, out transfer))
                {
                    transfer.CompletePayload = complete;
                    transfer.CompleteReceived = true;
                }
            }

            if (transfer != null)
            {
                AppLog.Info("FileComplete received. TransferId=" + complete.TransferId + " Success=" + complete.Success + " Message=" + complete.Message);
                TryCompleteIncomingTransfer(complete.TransferId);
            }
        }

        private void TryCompleteIncomingTransfer(string transferId)
        {
            IncomingTransfer transfer;
            lock (_sync)
            {
                if (!_incomingTransfers.TryGetValue(transferId, out transfer))
                {
                    return;
                }
            }

            FileCompletePayload completePayload;
            lock (transfer.Sync)
            {
                if (transfer.Completed || !transfer.CompleteReceived || transfer.ReceivedBytes < transfer.Offer.Length)
                {
                    if (transfer.CompleteReceived && transfer.ReceivedBytes < transfer.Offer.Length)
                    {
                        AppLog.Warn("Incoming file waiting for chunks. TransferId=" + transferId +
                                    " Received=" + transfer.ReceivedBytes +
                                    " Expected=" + transfer.Offer.Length);
                    }

                    return;
                }

                transfer.Completed = true;
                transfer.Stream.Flush(true);
                transfer.Stream.Dispose();
                completePayload = transfer.CompletePayload ?? new FileCompletePayload { TransferId = transferId, Success = true };
            }

            try
            {
                var actualHash = ComputeSha256(transfer.TempPath);
                if (!string.Equals(actualHash, transfer.Offer.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Error("Incoming file hash mismatch. File=" + transfer.Offer.FileName +
                                 " Expected=" + transfer.Offer.Sha256Hex + " Actual=" + actualHash, null);
                    RemoveIncomingTransfer(transferId);
                    return;
                }

                if (File.Exists(transfer.DestinationPath))
                {
                    transfer.DestinationPath = UniquePath(transfer.DestinationPath);
                }

                File.Move(transfer.TempPath, transfer.DestinationPath);
                completePayload.SavedPath = transfer.DestinationPath;
                if (!string.IsNullOrEmpty(transfer.Offer.SourcePath))
                {
                    lock (_sync)
                    {
                        _fetchedPaths[transfer.Offer.SourcePath] = transfer.DestinationPath;
                    }
                }

                RemoveIncomingTransfer(transferId);
                AppLog.Info("Incoming file completed. File=" + transfer.DestinationPath);
                QueueEvent(ProtocolEnvelope.Create(_config.DeviceId, null, ProtocolMessageTypes.FileComplete, ProtocolSerializer.ToJson(completePayload)));
            }
            catch (Exception ex)
            {
                RemoveIncomingTransfer(transferId);
                AppLog.Error("Incoming file completion failed. TransferId=" + transferId, ex);
            }
        }

        private void RemoveIncomingTransfer(string transferId)
        {
            lock (_sync)
            {
                _incomingTransfers.Remove(transferId);
            }
        }

        private void SendEnvelope(DiscoveredPeer peer, string messageType, string payloadJson)
        {
            if (peer == null || peer.Address == null)
            {
                AppLog.Debug("net.send.skip", "type=" + messageType + " reason=no-peer");
                return;
            }

            var started = DateTime.UtcNow;
            try
            {
                SendEnvelopePersistent(peer, messageType, payloadJson);
            }
            catch (Exception ex)
            {
                if (IsInputEnvelope(messageType))
                {
                    // Never replay input: a duplicated click or key press is worse than a lost one.
                    AppLog.Warn("net.send.fail type=" + messageType + " error=" + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                AppLog.Warn("net.send.retry type=" + messageType + " error=" + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    SendEnvelopePersistent(peer, messageType, payloadJson);
                }
                catch (Exception retryEx)
                {
                    AppLog.Error("net.send.fail type=" + messageType + " peer=" + peer.DeviceId, retryEx);
                    return;
                }
            }

            var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (elapsedMs >= 300)
            {
                AppLog.Event("WARN", "net.send.slow", "type=" + messageType + " elapsedMs=" + elapsedMs +
                                                           " peer=" + peer.DeviceId + " address=" + peer.Address);
            }
            else
            {
                AppLog.Debug("net.send", "type=" + messageType + " elapsedMs=" + elapsedMs + " peer=" + peer.DeviceId);
            }
        }

        private void SendEnvelopePersistent(DiscoveredPeer peer, string messageType, string payloadJson)
        {
            lock (_outboundLock)
            {
                EnsureOutboundLocked(peer);
                try
                {
                    SendEnvelopeOnStream(_outboundStream, peer, messageType, payloadJson);
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("net.send.reconnect", "type=" + messageType + " error=" + ex.GetType().Name);
                    CloseOutboundLocked();
                }

                EnsureOutboundLocked(peer);
                SendEnvelopeOnStream(_outboundStream, peer, messageType, payloadJson);
            }
        }

        private void EnsureOutboundLocked(DiscoveredPeer peer)
        {
            var endpoint = peer.Address + ":" + peer.TcpPort;
            if (_outboundClient != null && _outboundStream != null && _outboundEndpoint == endpoint)
            {
                return;
            }

            CloseOutboundLocked();
            var client = new TcpClient();
            ConnectWithTimeout(client, peer.Address, peer.TcpPort, 1500);
            try
            {
                client.NoDelay = true;
                client.SendTimeout = 500;
                client.ReceiveTimeout = 500;
            }
            catch
            {
            }

            var stream = client.GetStream();
            stream.WriteTimeout = 500;
            _outboundClient = client;
            _outboundStream = stream;
            _outboundEndpoint = endpoint;
            AppLog.Debug("net.outbound.open", "endpoint=" + endpoint);
        }

        private void CloseOutboundLocked()
        {
            try
            {
                if (_outboundStream != null)
                {
                    _outboundStream.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                if (_outboundClient != null)
                {
                    _outboundClient.Close();
                }
            }
            catch
            {
            }

            _outboundStream = null;
            _outboundClient = null;
            _outboundEndpoint = null;
        }

        private static bool IsInputEnvelope(string messageType)
        {
            return messageType == ProtocolMessageTypes.InputEvent || messageType == ProtocolMessageTypes.InputBatch;
        }

        private void SendEnvelopeOnStream(Stream stream, DiscoveredPeer peer, string messageType, string payloadJson)
        {
            var envelope = ProtocolEnvelope.Create(_config.DeviceId, peer.DeviceId, messageType, payloadJson);
            var bytes = ProtocolSerializer.ToUtf8JsonBytes(CreateFrame(peer.DeviceId, envelope));
            WriteFrame(stream, bytes);
            MarkPeerReachable(peer.DeviceId, peer.Address != null ? peer.Address.ToString() : null);
        }

        private void SendEnvelopeUdp(DiscoveredPeer peer, string messageType, string payloadJson)
        {
            var envelope = ProtocolEnvelope.Create(_config.DeviceId, peer.DeviceId, messageType, payloadJson);
            var bytes = ProtocolSerializer.ToUtf8JsonBytes(CreateFrame(peer.DeviceId, envelope));
            var endpoint = new IPEndPoint(peer.Address, UdpInputPort);
            lock (_udpSenderLock)
            {
                if (_udpSender == null)
                {
                    _udpSender = new UdpClient();
                    var marked = TrafficPriority.TryMarkInteractive(_udpSender.Client);
                    try
                    {
                        _udpSender.Client.SendBufferSize = 64 * 1024;
                    }
                    catch
                    {
                    }

                    AppLog.Debug("net.input.socket", "udp=47533 qos=" + (marked ? "dscp46" : "none"));
                }

                _udpSender.Send(bytes, bytes.Length, endpoint);
            }

            MarkPeerReachable(peer.DeviceId, peer.Address != null ? peer.Address.ToString() : null);
        }

        private static void ConnectWithTimeout(TcpClient client, IPAddress address, int port, int timeoutMs)
        {
            var asyncResult = client.BeginConnect(address, port, null, null);
            if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                client.Close();
                throw new TimeoutException("TCP connect timed out.");
            }

            client.EndConnect(asyncResult);
        }

        private TransportFrame CreateFrame(string peerDeviceId, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageTypes.PairingRequest ||
                envelope.MessageType == ProtocolMessageTypes.PairingConfirm)
            {
                return new TransportFrame
                {
                    SourceDeviceId = _config.DeviceId,
                    IsProtected = false,
                    EnvelopeJson = ProtocolSerializer.ToJson(envelope)
                };
            }

            var secret = GetTrustedSecret(peerDeviceId);
            if (secret == null)
            {
                throw new InvalidOperationException("Cannot send protected message before pairing.");
            }

            var protector = new SecureChannelProtector(secret);
            return new TransportFrame
            {
                SourceDeviceId = _config.DeviceId,
                IsProtected = true,
                ProtectedEnvelopeBytes = protector.Protect(ProtocolSerializer.ToUtf8JsonBytes(envelope))
            };
        }

        private ProtocolEnvelope OpenFrame(TransportFrame frame)
        {
            if (frame == null)
            {
                return null;
            }

            if (!frame.IsProtected)
            {
                return ProtocolSerializer.FromJson<ProtocolEnvelope>(frame.EnvelopeJson);
            }

            var secret = GetTrustedSecret(frame.SourceDeviceId);
            if (secret == null)
            {
                throw new InvalidOperationException("Received protected message from an untrusted device.");
            }

            var protector = new SecureChannelProtector(secret);
            var envelope = ProtocolSerializer.FromUtf8JsonBytes<ProtocolEnvelope>(protector.Unprotect(frame.ProtectedEnvelopeBytes));
            if (envelope != null)
            {
                MarkPeerProtected(envelope.SourceDeviceId);
            }

            return envelope;
        }

        private void TrustDevice(string deviceId, string deviceName, string address, byte[] sharedSecret)
        {
            lock (_sync)
            {
                var existing = _config.TrustedDevices.FirstOrDefault(x => x.DeviceId == deviceId);
                _config.TrustedDevices.Clear();
                _config.TrustedDevices.Add(new TrustedDevice
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    LastKnownAddress = address,
                    ProtectedSharedSecret = _configStore.Protect(sharedSecret),
                    ScreenWidth = existing != null ? existing.ScreenWidth : 0,
                    ScreenHeight = existing != null ? existing.ScreenHeight : 0
                });
                if (_peer != null && _peer.DeviceId == deviceId)
                {
                    _peer.PendingPairingCode = null;
                    _peer.LastProtectedEnvelopeUtc = DateTime.MinValue;
                }

                _inboundEvents.Clear();
                _configStore.Save(_config);
            }
        }

        private void UpdatePeerScreenInfo(string deviceId, int width, int height)
        {
            lock (_sync)
            {
                if (_peer != null && (string.IsNullOrEmpty(_peer.DeviceId) || _peer.DeviceId == deviceId))
                {
                    _peer.ScreenWidth = width;
                    _peer.ScreenHeight = height;
                }

                var trusted = _config.TrustedDevices.FirstOrDefault(x => x.DeviceId == deviceId) ??
                              _config.TrustedDevices.FirstOrDefault();
                if (trusted != null)
                {
                    trusted.ScreenWidth = width;
                    trusted.ScreenHeight = height;
                    _configStore.Save(_config);
                }
            }
        }

        private DiscoveredPeer GetPeer()
        {
            lock (_sync)
            {
                return _peer;
            }
        }

        private byte[] GetTrustedSecret(string deviceId)
        {
            lock (_sync)
            {
                var trusted = _config.TrustedDevices.FirstOrDefault(x => x.DeviceId == deviceId);
                return trusted == null ? null : _configStore.Unprotect(trusted.ProtectedSharedSecret);
            }
        }

        private TrustedDevice GetTrustedDeviceForPeer()
        {
            if (_peer != null && !string.IsNullOrEmpty(_peer.DeviceId))
            {
                return _config.TrustedDevices.FirstOrDefault(x => x.DeviceId == _peer.DeviceId);
            }

            if (_peer != null)
            {
                return _config.TrustedDevices.FirstOrDefault(x => x.LastKnownAddress == _peer.Address.ToString());
            }

            return _config.TrustedDevices.FirstOrDefault();
        }

        private bool IsTrustedDevice(string deviceId)
        {
            lock (_sync)
            {
                return !string.IsNullOrEmpty(deviceId) && _config.TrustedDevices.Any(x => x.DeviceId == deviceId);
            }
        }

        private bool IsPeerUsable(DiscoveredPeer peer)
        {
            lock (_sync)
            {
                if (peer == null || string.IsNullOrEmpty(peer.DeviceId))
                {
                    return false;
                }

                var trusted = _config.TrustedDevices.FirstOrDefault(x => x.DeviceId == peer.DeviceId);
                return trusted != null && IsPeerReachable(peer) && IsPeerProtectedVerified(peer);
            }
        }

        private void MarkPeerProtected(string deviceId)
        {
            lock (_sync)
            {
                if (_peer == null ||
                    string.IsNullOrEmpty(deviceId) ||
                    string.IsNullOrEmpty(_peer.DeviceId) ||
                    !string.Equals(_peer.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    return;
                }

                _peer.LastProtectedEnvelopeUtc = DateTime.UtcNow;
            }
        }

        private ProtocolEnvelope[] DrainInboundEvents()
        {
            lock (_sync)
            {
                var items = _inboundEvents.ToArray();
                _inboundEvents.Clear();
                return items;
            }
        }

        private void QueueEvent(ProtocolEnvelope envelope)
        {
            lock (_sync)
            {
                _inboundEvents.Enqueue(envelope);
                while (_inboundEvents.Count > 200)
                {
                    _inboundEvents.Dequeue();
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, name + " (" + i + ")" + extension);
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, name + " (" + Guid.NewGuid().ToString("N") + ")" + extension);
        }

        private bool TryBeginOutgoingTransfer(string transferKey)
        {
            lock (_sync)
            {
                CleanupOutgoingTransfers();
                if (_outgoingTransfers.ContainsKey(transferKey))
                {
                    return false;
                }

                _outgoingTransfers[transferKey] = DateTime.UtcNow;
                return true;
            }
        }

        private void EndOutgoingTransfer(string transferKey)
        {
            lock (_sync)
            {
                _outgoingTransfers.Remove(transferKey);
            }
        }

        private void CleanupOutgoingTransfers()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            var expired = _outgoingTransfers.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray();
            for (int i = 0; i < expired.Length; i++)
            {
                _outgoingTransfers.Remove(expired[i]);
            }
        }

        private static string BuildOutgoingTransferKey(string path, FileInfo fileInfo)
        {
            return Path.GetFullPath(path).ToUpperInvariant() + "|" + fileInfo.Length + "|" + fileInfo.LastWriteTimeUtc.Ticks;
        }

        private static string FormatSpeedMbps(long bytes, DateTime startedUtc)
        {
            var seconds = Math.Max(0.001, (DateTime.UtcNow - startedUtc).TotalSeconds);
            return (bytes / 1024.0 / 1024.0 / seconds).ToString("0.00");
        }

        private static string OppositeDirection(string direction)
        {
            if (direction == "Left") return "Right";
            if (direction == "Right") return "Left";
            if (direction == "Up") return "Down";
            if (direction == "Down") return "Up";
            return "Left";
        }

        private static bool IsNoisyEnvelope(string messageType)
        {
            return messageType == ProtocolMessageTypes.InputEvent ||
                   messageType == ProtocolMessageTypes.InputBatch ||
                   messageType == ProtocolMessageTypes.ControlLease ||
                   messageType == ProtocolMessageTypes.ControlLeaseAck ||
                   messageType == ProtocolMessageTypes.ClipboardUpdate ||
                   messageType == ProtocolMessageTypes.ClipboardPull ||
                   messageType == ProtocolMessageTypes.ClipboardPasteRequest;
        }

        private static bool IsCrossScreenEnvelope(string messageType)
        {
            // fix7：只保留"输入/控制"类。剪贴板与文件传输不受暂停影响：
            // 它们有自己的开关（ClipboardEnabled），把它们算进来会让一次暂停
            // 顺带废掉跨机复制粘贴与拖拽文件，而且用户看不出原因。
            return messageType == ProtocolMessageTypes.InputEvent ||
                   messageType == ProtocolMessageTypes.InputBatch ||
                   messageType == ProtocolMessageTypes.ControlLease ||
                   messageType == ProtocolMessageTypes.ControlLeaseAck ||
                   messageType == ProtocolMessageTypes.ControlAcquire;
        }

        private static bool IsPeerReachable(DiscoveredPeer peer)
        {
            if (peer == null)
            {
                return false;
            }

            if (peer.LastReachableUtc == DateTime.MinValue)
            {
                return false;
            }

            return (DateTime.UtcNow - peer.LastReachableUtc) <= PeerReachableTimeout;
        }

        private static bool IsPeerProtectedVerified(DiscoveredPeer peer)
        {
            if (peer == null || peer.LastProtectedEnvelopeUtc == DateTime.MinValue)
            {
                return false;
            }

            return (DateTime.UtcNow - peer.LastProtectedEnvelopeUtc) <= PeerReachableTimeout;
        }

        private bool IsPairingMismatchLocked(TrustedDevice trusted)
        {
            var existingTrust = trusted ?? _config.TrustedDevices.FirstOrDefault();
            if (existingTrust == null || _peer == null || string.IsNullOrEmpty(_peer.DeviceId))
            {
                return false;
            }

            return !string.Equals(existingTrust.DeviceId, _peer.DeviceId, StringComparison.Ordinal);
        }

        private string PairingStatusLocked(TrustedDevice trusted, bool reachable, bool verified, bool mismatch)
        {
            if (mismatch)
            {
                return PairingStatuses.PairingMismatch;
            }

            if (verified)
            {
                return PairingStatuses.PairedConnected;
            }

            if (_peer != null && !string.IsNullOrEmpty(_peer.PendingPairingCode))
            {
                return PairingStatuses.WaitingForApproval;
            }

            if (trusted != null)
            {
                return reachable ? PairingStatuses.PairingMismatch : PairingStatuses.PairedDisconnected;
            }

            return _peer == null ? PairingStatuses.NotDiscovered : PairingStatuses.DiscoveredUnpaired;
        }

        private void MarkPeerReachable(string deviceId, string address)
        {
            lock (_sync)
            {
                if (_peer == null)
                {
                    return;
                }

                var matchesDevice = !string.IsNullOrEmpty(deviceId) &&
                                    !string.IsNullOrEmpty(_peer.DeviceId) &&
                                    string.Equals(_peer.DeviceId, deviceId, StringComparison.Ordinal);
                var matchesAddress = !string.IsNullOrEmpty(address) &&
                                     _peer.Address != null &&
                                     string.Equals(_peer.Address.ToString(), address, StringComparison.OrdinalIgnoreCase);

                if (!matchesDevice && !matchesAddress)
                {
                    return;
                }

                _peer.LastReachableUtc = DateTime.UtcNow;
            }
        }

        private static int NormalizeMouseRate(int rateHz)
        {
            if (rateHz <= 0)
            {
                return 200;
            }

            if (rateHz < 60)
            {
                return 60;
            }

            if (rateHz > 250)
            {
                return 250;
            }

            return rateHz;
        }

        private static byte[] ReadFrame(Stream stream)
        {
            var lengthBytes = new byte[4];
            ReadExactly(stream, lengthBytes, 0, lengthBytes.Length);
            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid TCP frame length.");
            }

            var bytes = new byte[length];
            ReadExactly(stream, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] TryReadFrame(Stream stream)
        {
            var lengthBytes = new byte[4];
            if (!TryReadExactly(stream, lengthBytes, 0, lengthBytes.Length))
            {
                return null;
            }

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid TCP frame length.");
            }

            var bytes = new byte[length];
            ReadExactly(stream, bytes, 0, bytes.Length);
            return bytes;
        }

        private static void WriteFrame(Stream stream, byte[] bytes)
        {
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            stream.Write(lengthBytes, 0, lengthBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                count -= read;
            }
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
                count -= read;
            }

            return true;
        }

        private sealed class DiscoveredPeer
        {
            public string DeviceId;
            public string DeviceName;
            public IPAddress Address;
            public int TcpPort;
            public string PendingPairingCode;
            public int ScreenWidth;
            public int ScreenHeight;
            public DateTime LastReachableUtc;
            public DateTime LastProtectedEnvelopeUtc;
        }

        private sealed class IncomingTransfer
        {
            public readonly object Sync = new object();
            public FileOfferPayload Offer;
            public string DestinationPath;
            public string TempPath;
            public FileStream Stream;
            public long ReceivedBytes;
            public bool CompleteReceived;
            public bool Completed;
            public FileCompletePayload CompletePayload;
        }
    }
}
