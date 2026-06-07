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
        private readonly Dictionary<string, DateTime> _outgoingTransfers = new Dictionary<string, DateTime>();
        private volatile bool _sharingPaused;
        private string _activeTargetDeviceId;

        public PeerCoordinator()
        {
            _configStore = new ConfigStore(ConfigStore.DefaultPath());
        }

        public void Start()
        {
            _config = _configStore.LoadOrCreate();
            AppLog.Info("Coordinator start. DeviceId=" + _config.DeviceId + " DeviceName=" + _config.DeviceName);
            _running = true;
            _discovery = new UdpDiscoveryService(DiscoveryPacket.Create(_config.DeviceId, _config.DeviceName, TcpPort));
            _discovery.DeviceDiscovered += OnDeviceDiscovered;
            _discovery.Start();

            _listener = new TcpListener(IPAddress.Any, TcpPort);
            _listener.Start();
            AppLog.Info("TCP listener started on port " + TcpPort);
            _listenerThread = new Thread(ListenTcpLoop);
            _listenerThread.IsBackground = true;
            _listenerThread.Start();

            _inputUdp = new UdpClient(UdpInputPort);
            _udpInputThread = new Thread(ListenUdpInputLoop);
            _udpInputThread.IsBackground = true;
            _udpInputThread.Start();
            AppLog.Info("UDP input listener started on port " + UdpInputPort);
        }

        public void Stop()
        {
            AppLog.Info("Coordinator stop.");
            _running = false;
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

            if (command.Command == AgentCommands.ShutdownService)
            {
                AppLog.Info("ShutdownService requested.");
                ServiceStopSignal.RequestStop();
                LanMouseShareService.RequestStop();
                return "OK";
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
                command.Command == AgentCommands.SendClipboard)
            {
                if (_sharingPaused && command.Command != AgentCommands.ReleaseControl)
                {
                    AppLog.Info("Command ignored while sharing is paused. Command=" + command.Command);
                    return "OK";
                }

                SendRawPayloadAsync(command.Command, command.PayloadJson);
                return "OK";
            }

            if (command.Command == AgentCommands.SendFiles)
            {
                if (_sharingPaused)
                {
                    AppLog.Info("SendFiles ignored while sharing is paused.");
                    return "OK";
                }

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
                    ActiveTargetDeviceId = string.IsNullOrWhiteSpace(_activeTargetDeviceId) ? _config.DeviceId : _activeTargetDeviceId,
                    RunAsAdministratorByDefault = _config.RunAsAdministratorByDefault,
                    PairingStatus = pairingStatus,
                    IsPairingMismatch = mismatch
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
                        if (envelope != null && envelope.MessageType == ProtocolMessageTypes.FileStreamStart && !_sharingPaused)
                        {
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
                    _configStore.Save(_config);
                    AppLog.Info("LayoutUpdate received. PeerDirection=" + layout.PeerDirection);
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
                if (_sharingPaused)
                {
                    AppLog.Info("FileOffer ignored while sharing is paused.");
                    return;
                }

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
                }
                else if (_sharingPaused && IsCrossScreenEnvelope(envelope.MessageType))
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
            else if (command == AgentCommands.SendClipboard)
            {
                messageType = ProtocolMessageTypes.ClipboardUpdate;
            }

            if (messageType == ProtocolMessageTypes.InputBatch || messageType == ProtocolMessageTypes.InputEvent)
            {
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

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendEnvelope(peer, ProtocolMessageTypes.ScreenInfoUpdate, ProtocolSerializer.ToJson(screen));
                }
                catch (Exception ex)
                {
                    AppLog.Error("ScreenInfoUpdate send failed.", ex);
                }
            });
        }

        private void ApplySettings(AgentSettingsDto settings)
        {
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
                _config.RunAsAdministratorByDefault = settings.RunAsAdministratorByDefault;
                _configStore.Save(_config);
            }

            AppLog.Info("Settings updated. Speed=" + settings.MouseSpeedMultiplier.ToString("0.00") +
                        " RateHz=" + NormalizeMouseRate(settings.MouseUpdateRateHz) +
                        " Clipboard=" + settings.ClipboardEnabled +
                        " PauseHotkey=" + settings.PauseHotkey +
                        " StopHotkey=" + settings.StopControlHotkey +
                        " ExitHotkey=" + settings.ExitHotkey +
                        " StartWithWindows=" + settings.StartWithWindows +
                        " MinimizeToTray=" + settings.MinimizeToTrayOnClose +
                        " BlockCorners=" + settings.BlockScreenCorners +
                        " RunAsAdminDefault=" + settings.RunAsAdministratorByDefault);
        }

        private void SetSharingPaused(bool paused, string reason)
        {
            lock (_sync)
            {
                _sharingPaused = paused;
                if (paused)
                {
                    _activeTargetDeviceId = _config != null ? _config.DeviceId : null;
                }
            }

            if (paused)
            {
                CancelTransfers(reason);
            }

            AppLog.Info("Sharing paused state changed. Paused=" + paused + " Reason=" + reason);
        }

        private void ApplyControlTargetPayload(string payloadJson, string reason)
        {
            var payload = ProtocolSerializer.FromJson<ControlTargetPayload>(payloadJson);
            var target = payload != null ? payload.ActiveTargetDeviceId : null;
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = _config.DeviceId;
                }

                var peerId = _peer != null && !string.IsNullOrWhiteSpace(_peer.DeviceId)
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

            AppLog.Info("Control target changed. Target=" + target + " Reason=" + reason);
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
                }
            }
        }

        private void BeginIncomingFile(FileOfferPayload offer)
        {
            if (offer == null)
            {
                return;
            }

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mouse传输文件");

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

            IncomingTransfer transfer;
            lock (_sync)
            {
                if (!_incomingTransfers.TryGetValue(start.TransferId, out transfer))
                {
                    AppLog.Warn("FileStreamStart ignored: no incoming transfer. TransferId=" + start.TransferId);
                    return;
                }
            }

            var buffer = new byte[1024 * 1024];
            long remaining = start.Length;
            long received = 0;
            long lastLoggedBytes = 0;
            var startedUtc = DateTime.UtcNow;
            AppLog.Info("FileStream receiving. TransferId=" + start.TransferId + " Length=" + start.Length);
            lock (transfer.Sync)
            {
                transfer.Stream.Position = 0;
                while (remaining > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("File stream ended before all bytes were received.");
                    }

                    transfer.Stream.Write(buffer, 0, read);
                    remaining -= read;
                    received += read;
                    transfer.ReceivedBytes = received;
                    if (received == read || remaining == 0 || received - lastLoggedBytes >= 16L * 1024 * 1024)
                    {
                        lastLoggedBytes = received;
                        AppLog.Info("FileStream received. TransferId=" + start.TransferId +
                                    " Received=" + received +
                                    "/" + start.Length +
                                    " SpeedMBps=" + FormatSpeedMbps(received, startedUtc));
                    }
                }
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
            using (var client = new TcpClient())
            {
                ConnectWithTimeout(client, peer.Address, peer.TcpPort, 500);
                client.SendTimeout = 500;
                client.ReceiveTimeout = 500;
                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = 500;
                    SendEnvelopeOnStream(stream, peer, messageType, payloadJson);
                }
            }
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
            using (var udp = new UdpClient())
            {
                udp.Send(bytes, bytes.Length, new IPEndPoint(peer.Address, UdpInputPort));
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
                   messageType == ProtocolMessageTypes.ClipboardUpdate;
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
