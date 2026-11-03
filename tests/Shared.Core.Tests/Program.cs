using System;
using System.Drawing;
using System.IO;
using System.Text;
using LanMouseShare.Core;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Crypto;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Networking;
using LanMouseShare.Core.Protocol;
using LanMouseShare.Input;

namespace LanMouseShare.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                SecureChannelRoundTrips();
                TamperDetectionWorks();
                ProtocolSerializationRoundTrips();
                ClipboardPayloadRoundTrips();
                SettingsDefaultsWork();
                AdminStartupUiAndCommandsExist();
                FileTransferRequestRoundTrips();
                ControlTargetPayloadRoundTrips();
                InputRoutingFollowsActiveTarget();
                ScrollViewerExistsInAgentUi();
                ChineseUiAndVersionWork();
                ConflictProcessDetectionWorks();
                InjectionFailureTrackerWorks();
                ControlTokenRejectsSecondOwner();
                EdgeSwitchDetectionWorks();
                EdgeCornerBlockingWorks();
                PhysicalEdgeSwitchDetectionWorks();
                RemoteEdgeReturnDetectionWorks();
                EdgeTransitionMappingWorks();
                DpiCoordinateMappingWorks();
                ShareRoleAndDebugSettingsWork();
                LogProtocolHelpersWork();
                BuildVersionIsConsistent();
                DebugLogUiExists();
                SystemInfoAndHeartbeatWork();
                ControlLinkMachineWorks();
                ControlChannelCodecWorks();
            ClipboardFileFramesWork();
            ControlLeaseRelayPayloadWorks();
            LogTransferAndFileClipboardWork();
            LazyFileClipboardWorks();
                Console.WriteLine("All tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void SecureChannelRoundTrips()
        {
            var secret = SecureRandom.Bytes(32);
            var protector = new SecureChannelProtector(secret);
            var plaintext = Encoding.UTF8.GetBytes("hello win7");
            var protectedBytes = protector.Protect(plaintext);
            var unprotected = protector.Unprotect(protectedBytes);
            Assert(Encoding.UTF8.GetString(unprotected) == "hello win7", "Secure channel round trip failed.");
        }

        private static void TamperDetectionWorks()
        {
            var protector = new SecureChannelProtector(SecureRandom.Bytes(32));
            var protectedBytes = protector.Protect(Encoding.UTF8.GetBytes("secret"));
            protectedBytes[20] ^= 0x20;

            bool failed = false;
            try
            {
                protector.Unprotect(protectedBytes);
            }
            catch
            {
                failed = true;
            }

            Assert(failed, "Tampered payload was accepted.");
        }

        private static void ProtocolSerializationRoundTrips()
        {
            var payload = new InputPayload
            {
                Kind = InputKinds.Key,
                VirtualKey = 65,
                ScanCode = 30,
                Flags = 1,
                IsExtendedKey = true,
                IsSystemKey = true,
                IsDown = true
            };
            var json = ProtocolSerializer.ToJson(payload);
            var clone = ProtocolSerializer.FromJson<InputPayload>(json);
            Assert(clone.Kind == InputKinds.Key &&
                   clone.VirtualKey == 65 &&
                   clone.ScanCode == 30 &&
                   clone.Flags == 1 &&
                   clone.IsExtendedKey &&
                   clone.IsSystemKey &&
                   clone.IsDown, "Protocol serialization failed.");
        }

        private static void SettingsDefaultsWork()
        {
            var config = new AppConfig();
            ConfigStore.ApplyDefaults(config);
            Assert(config.MouseSpeedMultiplier == 1.0, "Default mouse speed failed.");
            Assert(config.MouseUpdateRateHz == 200, "Default mouse update rate failed.");

            var settings = new AgentSettingsDto
            {
                MouseSpeedMultiplier = 1.25,
                MouseUpdateRateHz = 250,
                ClipboardEnabled = false,
                PauseHotkey = "Ctrl+F11",
                StopControlHotkey = "Ctrl+F10",
                ExitHotkey = "Ctrl+Alt+End",
                StartWithWindows = true,
                MinimizeToTrayOnClose = true,
                BlockScreenCorners = true,
                RunAsAdministratorByDefault = true
            };
            var clone = ProtocolSerializer.FromJson<AgentSettingsDto>(ProtocolSerializer.ToJson(settings));
            Assert(clone.MouseUpdateRateHz == 250 &&
                   !clone.ClipboardEnabled &&
                   clone.StopControlHotkey == "Ctrl+F10" &&
                   clone.ExitHotkey == "Ctrl+Alt+End" &&
                   clone.StartWithWindows &&
                   clone.MinimizeToTrayOnClose &&
                   clone.BlockScreenCorners &&
                   clone.RunAsAdministratorByDefault,
                "Settings serialization failed.");
        }

        private static void AdminStartupUiAndCommandsExist()
        {
            var xamlPath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml");
            var managerPath = Path.Combine("src", "Desktop.Agent", "StartupTaskManager.cs");
            if (!File.Exists(xamlPath))
            {
                xamlPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml");
                managerPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "StartupTaskManager.cs");
            }

            var xaml = File.ReadAllText(xamlPath);
            Assert(xaml.Contains("RunAsAdminDefaultBox") &&
                   xaml.Contains("RestartAsAdminButton") &&
                   xaml.Contains("PrivilegeStatusText"),
                "Agent UI should expose administrator startup controls.");

            var manager = File.ReadAllText(managerPath);
            Assert(manager.Contains("/RL HIGHEST") &&
                   manager.Contains("/SC ONLOGON") &&
                   manager.Contains("/Delete /TN"),
                "Admin startup manager should create and delete the elevated logon task.");
        }

        private static void ClipboardPayloadRoundTrips()
        {
            var text = new ClipboardPayload
            {
                OriginDeviceId = "local",
                Version = 7,
                Format = ClipboardFormats.Text,
                Text = "hello clipboard"
            };
            var textClone = ProtocolSerializer.FromJson<ClipboardPayload>(ProtocolSerializer.ToJson(text));
            Assert(textClone.Format == ClipboardFormats.Text && textClone.Text == "hello clipboard" && textClone.Version == 7,
                "Text clipboard serialization failed.");

            var image = new ClipboardPayload
            {
                OriginDeviceId = "local",
                Version = 8,
                Format = ClipboardFormats.ImagePng,
                ImagePngBytes = new byte[] { 1, 2, 3, 4 }
            };
            var imageClone = ProtocolSerializer.FromJson<ClipboardPayload>(ProtocolSerializer.ToJson(image));
            Assert(imageClone.Format == ClipboardFormats.ImagePng && imageClone.ImagePngBytes.Length == 4 && imageClone.ImagePngBytes[2] == 3,
                "Image clipboard serialization failed.");
        }

        private static void FileTransferRequestRoundTrips()
        {
            var request = new FileTransferRequestPayload
            {
                FilePaths = new[] { @"C:\Temp\a.txt", @"C:\Temp\b.txt" }
            };
            var clone = ProtocolSerializer.FromJson<FileTransferRequestPayload>(ProtocolSerializer.ToJson(request));
            Assert(clone.FilePaths.Length == 2 && clone.FilePaths[1].EndsWith("b.txt"), "File transfer request serialization failed.");
        }

        private static void ControlTargetPayloadRoundTrips()
        {
            var payload = new ControlTargetPayload
            {
                ActiveTargetDeviceId = "device-b",
                Reason = "EdgeSwitch"
            };
            var clone = ProtocolSerializer.FromJson<ControlTargetPayload>(ProtocolSerializer.ToJson(payload));
            Assert(clone.ActiveTargetDeviceId == "device-b" && clone.Reason == "EdgeSwitch",
                "Control target payload serialization failed.");
        }

        private static void InputRoutingFollowsActiveTarget()
        {
            var router = new InputRoutingController();
            var key = new InputPayload { Kind = InputKinds.Key, VirtualKey = 65, IsDown = true };
            var mouse = new InputPayload { Kind = InputKinds.MouseMove, X = 799, Y = 300 };

            var targetLocal = new ControlSessionSnapshot
            {
                IsConnected = true,
                LocalDeviceId = "a",
                PeerDeviceId = "b",
                ActiveTargetDeviceId = "a"
            };
            Assert(router.Route(key, targetLocal) == InputRouteAction.AllowLocal,
                "Local target should allow local keyboard input.");
            Assert(router.Route(mouse, targetLocal) == InputRouteAction.CheckEdgeSwitch,
                "Local target mouse move should check edge switching.");
            Assert(router.ShouldInjectRemoteInput(targetLocal),
                "Local target should accept remote keyboard injection.");

            var targetPeer = new ControlSessionSnapshot
            {
                IsConnected = true,
                LocalDeviceId = "b",
                PeerDeviceId = "a",
                ActiveTargetDeviceId = "a"
            };
            Assert(router.Route(key, targetPeer) == InputRouteAction.ForwardToPeer,
                "Peer target should forward keyboard input.");
            Assert(router.Route(mouse, targetPeer) == InputRouteAction.SuppressLocal,
                "Peer target absolute mouse move should be suppressed locally.");
            Assert(!router.ShouldInjectRemoteInput(targetPeer),
                "Peer target should not inject remote input locally.");

            targetPeer.IsPaused = true;
            Assert(router.Route(key, targetPeer) == InputRouteAction.Ignore,
                "Paused sessions should ignore input.");
        }

        private static void ScrollViewerExistsInAgentUi()
        {
            var path = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml");
            if (!File.Exists(path))
            {
                path = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml");
            }

            var xaml = File.ReadAllText(path);
            Assert(xaml.Contains("<ScrollViewer") && xaml.Contains("VerticalScrollBarVisibility=\"Auto\""),
                "Agent UI should contain an auto vertical ScrollViewer.");
        }

        private static void ChineseUiAndVersionWork()
        {
            var xamlPath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml");
            var appPath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml.cs");
            var setupPath = Path.Combine("src", "Setup", "Program.cs");
            var scriptPath = Path.Combine("scripts", "Build-Installer.ps1");
            if (!File.Exists(xamlPath))
            {
                xamlPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml");
                appPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml.cs");
                setupPath = Path.Combine("..", "..", "..", "src", "Setup", "Program.cs");
                scriptPath = Path.Combine("..", "..", "..", "scripts", "Build-Installer.ps1");
            }

            var xaml = File.ReadAllText(xamlPath);
            Assert(xaml.Contains("LanMouseShare 0.2.0-beta-fix19") &&
                   xaml.Contains("&#x5C40;&#x57DF;&#x7F51;&#x952E;&#x9F20;&#x5171;&#x4EAB;") &&
                   xaml.Contains("&#x8FDE;&#x63A5;&#x72B6;&#x6001;"),
                "Agent UI should use 0.2.0-beta-fix19 and Chinese XML entities.");

            Assert(File.ReadAllText(appPath).Contains("AppVersion = \"0.2.0-beta-fix19\""), "Agent version should be 0.2.0-beta-fix19.");
            Assert(File.ReadAllText(setupPath).Contains("SetupVersion = \"0.2.4\""), "Setup version should stay numeric for upgrade comparison.");
            Assert(File.ReadAllText(scriptPath).Contains("$Version = \"0.2.0-beta-fix19\""), "Build script default version should be 0.2.0-beta-fix19.");
        }

        private static void ConflictProcessDetectionWorks()
        {
            Assert(KnownConflictProcessDetector.IsKnownConflictProcess("MouseWithoutBorders.exe"),
                "Mouse without Borders should be detected as a conflict process.");
            Assert(KnownConflictProcessDetector.IsKnownConflictProcess("SunloginClient"),
                "Sunlogin should be detected as a conflict process.");
            Assert(KnownConflictProcessDetector.IsKnownConflictProcess("mstsc.exe"),
                "Remote Desktop should be detected as a conflict process.");
            Assert(!KnownConflictProcessDetector.IsKnownConflictProcess("chrome.exe"),
                "Normal browser process should not be treated as a conflict process.");
        }

        private static void InjectionFailureTrackerWorks()
        {
            var tracker = new InjectionFailureTracker(3, TimeSpan.FromSeconds(2));
            var now = new DateTime(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc);
            Assert(!tracker.RegisterFailure(now), "First injection failure should not release control.");
            Assert(!tracker.RegisterFailure(now.AddMilliseconds(200)), "Second injection failure should not release control.");
            Assert(tracker.RegisterFailure(now.AddMilliseconds(400)), "Third quick injection failure should release control.");
            tracker.Reset();
            Assert(!tracker.RegisterFailure(now.AddSeconds(5)), "Tracker reset should clear failures.");
            Assert(!tracker.RegisterFailure(now.AddSeconds(8)), "Failures outside the window should restart counting.");
        }

        private static void ControlTokenRejectsSecondOwner()
        {
            var state = new ControlTokenState();
            Assert(state.TryAcquire("a"), "First owner should acquire.");
            Assert(!state.TryAcquire("b"), "Second owner should be rejected.");
            state.Release("a");
            Assert(state.TryAcquire("b"), "Second owner should acquire after release.");
        }

        private static void EdgeSwitchDetectionWorks()
        {
            var detector = new EdgeSwitchDetector(0);
            Assert(detector.ShouldSwitch(new Point(799, 300), new Size(800, 600), "Right"), "Right edge not detected.");
            Assert(detector.ShouldSwitch(new Point(799, 300), new Point(798, 300), new Size(800, 600), "Right"), "Right outward movement not detected.");
            Assert(!detector.ShouldSwitch(new Point(799, 300), new Point(799, 300), new Size(800, 600), "Right"), "Stationary edge point should not switch.");
            Assert(!detector.ShouldSwitch(new Point(798, 300), new Point(799, 300), new Size(800, 600), "Right"), "Inward movement should not switch.");
            Assert(!detector.ShouldSwitch(new Point(400, 300), new Size(800, 600), "Right"), "Center should not switch.");
            Assert(detector.ShouldSwitch(new Point(0, 300), new Point(1, 300), new Size(800, 600), "Left"), "Left outward movement not detected.");
            Assert(detector.ShouldSwitch(new Point(300, 0), new Point(300, 1), new Size(800, 600), "Up"), "Up outward movement not detected.");
            Assert(detector.ShouldSwitch(new Point(300, 599), new Point(300, 598), new Size(800, 600), "Down"), "Down outward movement not detected.");
        }

        private static void PhysicalEdgeSwitchDetectionWorks()
        {
            var detector = new EdgeSwitchDetector(0);
            var screen = new Size(800, 600);
            Assert(!detector.ShouldSwitch(new Point(798, 300), screen, "Right"),
                "Inside right edge should not be treated as physical edge.");
            Assert(detector.ShouldSwitch(new Point(799, 300), screen, "Right"),
                "Right edge contact should be exact.");
            Assert(detector.ShouldSwitch(new Point(799, 300), new Point(100, 300), screen, "Right"),
                "Fast right movement to the physical edge should switch.");
            Assert(detector.ShouldSwitch(new Point(0, 300), new Point(700, 300), screen, "Left"),
                "Fast left movement to the physical edge should switch.");
            Assert(detector.ShouldSwitch(new Point(300, 0), new Point(300, 500), screen, "Up"),
                "Fast up movement to the physical edge should switch.");
            Assert(detector.ShouldSwitch(new Point(300, 599), new Point(300, 10), screen, "Down"),
                "Fast down movement to the physical edge should switch.");
        }

        private static void EdgeCornerBlockingWorks()
        {
            var detector = new EdgeSwitchDetector(0);
            var screen = new Size(800, 600);
            Assert(!detector.ShouldSwitch(new Point(799, 20), new Point(798, 20), screen, "Right", true, 80),
                "Right top blocked corner should not switch.");
            Assert(!detector.ShouldSwitch(new Point(799, 580), new Point(798, 580), screen, "Right", true, 80),
                "Right bottom blocked corner should not switch.");
            Assert(detector.ShouldSwitch(new Point(799, 300), new Point(798, 300), screen, "Right", true, 80),
                "Right edge middle should still switch.");
            Assert(detector.ShouldSwitch(new Point(799, 20), new Point(798, 20), screen, "Right", false, 80),
                "Disabled corner blocking should not affect switching.");
            Assert(!detector.ShouldSwitch(new Point(20, 0), new Point(20, 1), screen, "Up", true, 80),
                "Top left blocked corner should not switch.");
            Assert(detector.ShouldSwitch(new Point(400, 0), new Point(400, 1), screen, "Up", true, 80),
                "Top edge middle should still switch.");
        }

        private static void RemoteEdgeReturnDetectionWorks()
        {
            var detector = new RemoteEdgeReturnDetector();
            var screen = new Size(800, 600);
            var result = detector.Update(new Point(10, 300), screen, "Left", -20, 0, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "AwayFromEdge",
                "Remote return should not release before the real cursor reaches the edge.");

            result = detector.Update(new Point(0, 300), screen, "Left", -30, 0, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "Disarmed",
                "A push at the entry edge must not release before the detector is armed.");

            result = detector.Update(new Point(400, 300), screen, "Left", 0, 0, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "AwayFromEdge",
                "Moving away from the entry edge should arm the detector.");

            result = detector.Update(new Point(0, 300), screen, "Left", -50, 0, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "Armed",
                "A single small step at the edge must not release; the pointer is clamped there.");

            result = detector.Update(new Point(0, 300), screen, "Left", -50, 0, false, false, 0);
            Assert(result.ShouldRelease && result.Reason == "EdgePush",
                "Accumulated push reaching the threshold should release control.");

            result = detector.Update(new Point(0, 300), screen, "Left", -40, 0, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "Cooldown",
                "After a release the detector must stay quiet so the next step cannot release again.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(0, 300), screen, "Right", 0, 0, false, false, 0);
            detector.Update(new Point(799, 300), screen, "Right", 50, 0, false, false, 0);
            result = detector.Update(new Point(799, 300), screen, "Right", 50, 0, false, false, 0);
            Assert(result.ShouldRelease, "Right edge sustained push should release.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(300, 500), screen, "Up", 0, 0, false, false, 0);
            detector.Update(new Point(300, 0), screen, "Up", 0, -50, false, false, 0);
            result = detector.Update(new Point(300, 0), screen, "Up", 0, -50, false, false, 0);
            Assert(result.ShouldRelease, "Up edge sustained push should release.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(300, 20), screen, "Down", 0, 0, false, false, 0);
            detector.Update(new Point(300, 599), screen, "Down", 0, 50, false, false, 0);
            result = detector.Update(new Point(300, 599), screen, "Down", 0, 50, false, false, 0);
            Assert(result.ShouldRelease, "Down edge sustained push should release.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(400, 300), screen, "Left", 0, 0, false, false, 0);
            result = detector.Update(new Point(0, 300), screen, "Left", 0, 12, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "Armed",
                "Vertical movement along the return edge should not release.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(300, 300), screen, "Right", 0, 0, false, false, 0);
            detector.Update(new Point(799, 20), screen, "Right", 10, 0, false, true, 80);
            result = detector.Update(new Point(799, 20), screen, "Right", 10, 0, false, true, 80);
            Assert(!result.ShouldRelease && result.Reason == "BlockedCorner",
                "Remote right top corner should block return release.");

            detector = new RemoteEdgeReturnDetector();
            detector.Update(new Point(300, 300), screen, "Right", 0, 0, false, false, 80);
            detector.Update(new Point(799, 20), screen, "Right", 25, 0, false, false, 80);
            var released = false;
            for (int i = 0; i < 5; i++)
            {
                result = detector.Update(new Point(799, 20), screen, "Right", 25, 0, false, false, 80);
                released = released || result.ShouldRelease;
            }

            Assert(released,
                "Disabled remote corner blocking should allow return release.");
        }

        private static void EdgeTransitionMappingWorks()
        {
            var local = new Size(800, 600);
            var remote = new Size(1600, 900);

            var rightEntry = EdgeTransitionMapper.MapEntryToRemote(new Point(799, 300), local, remote, "Right");
            Assert(rightEntry.X == 8 && rightEntry.Y == 450,
                "Right entry should preserve Y ratio on the remote screen.");

            var leftReturn = EdgeTransitionMapper.MapReturnToLocal(new Point(0, 450), remote, local, "Left");
            Assert(leftReturn.X == 8 && leftReturn.Y == 300,
                "Left return should land 8px inside the local edge to avoid re-triggering the switch.");

            var downEntry = EdgeTransitionMapper.MapEntryToRemote(new Point(400, 599), local, remote, "Down");
            Assert(downEntry.X == 800 && downEntry.Y == 8,
                "Down entry should preserve X ratio on the remote screen.");

            var upReturn = EdgeTransitionMapper.MapReturnToLocal(new Point(800, 0), remote, local, "Up");
            Assert(upReturn.X == 400 && upReturn.Y == 8,
                "Up return should land 8px inside the local edge to avoid re-triggering the switch.");
        }

        private static void DpiCoordinateMappingWorks()
        {
            Assert(AreNearlyEqual(DpiCoordinateMapper.PixelsToDip(1920, 96), 1920), "96 DPI conversion failed.");
            Assert(AreNearlyEqual(DpiCoordinateMapper.PixelsToDip(1920, 120), 1536), "120 DPI conversion failed.");
            Assert(AreNearlyEqual(DpiCoordinateMapper.PixelsToDip(1920, 144), 1280), "144 DPI conversion failed.");
            Assert(AreNearlyEqual(DpiCoordinateMapper.PixelsToDip(1920, 192), 960), "192 DPI conversion failed.");

            var scale120 = new DpiScale(120, 120);
            var pixelRect = Rectangle.FromLTRB(200, 100, 2120, 1180);
            var dipRect = DpiCoordinateMapper.PixelRectToDip(pixelRect, scale120);
            Assert(AreNearlyEqual(dipRect.Left, DpiCoordinateMapper.PixelsToDip(pixelRect.Left, 120)), "Rect left conversion mismatch.");
            Assert(AreNearlyEqual(dipRect.Right, DpiCoordinateMapper.PixelsToDip(pixelRect.Right, 120)), "Rect right conversion mismatch.");
            Assert(AreNearlyEqual(dipRect.Width, DpiCoordinateMapper.PixelsToDip(pixelRect.Width, 120)), "Rect width conversion mismatch.");

            var edgePixelRect = Rectangle.FromLTRB(2536, 0, 2560, 1440);
            var edgeDipRect = DpiCoordinateMapper.PixelRectToDip(edgePixelRect, new DpiScale(144, 144));
            Assert(AreNearlyEqual(edgeDipRect.Right, DpiCoordinateMapper.PixelsToDip(edgePixelRect.Right, 144)),
                "Right-edge mapping should stay aligned after DIP conversion.");

            var wideEdgePixelRect = Rectangle.FromLTRB(2496, 0, 2560, 1440);
            var wideEdgeDipRect = DpiCoordinateMapper.PixelRectToDip(wideEdgePixelRect, new DpiScale(144, 144));
            Assert(AreNearlyEqual(wideEdgeDipRect.Width, DpiCoordinateMapper.PixelsToDip(wideEdgePixelRect.Width, 144)),
                "Wide edge capture mapping should preserve physical width after DIP conversion.");
        }

        private static void ShareRoleAndDebugSettingsWork()
        {
            Assert(ShareRoleValues.Normalize(null) == ShareRoleValues.Controller,
                "Empty role should fall back to controller.");
            Assert(ShareRoleValues.Normalize("agent") == ShareRoleValues.Agent,
                "Role parsing should ignore case.");
            Assert(ShareRoleValues.DisplayName(ShareRoleValues.Agent) == "\u88AB\u63A7\u7AEF",
                "Agent role display text failed.");

            var config = new AppConfig();
            ConfigStore.ApplyDefaults(config);
            Assert(config.ShareRole == ShareRoleValues.Controller, "Default share role should be controller.");
            Assert(config.LogRetentionDays == 3, "Default log retention should be three days.");
            Assert(!config.VerboseLogging, "Verbose logging should start disabled.");

            config.ShareRole = ShareRoleValues.Agent;
            config.ControllerAddress = "192.168.1.9:47540";
            config.DebugToken = "tok";
            config.VerboseLogging = true;
            config.LogRetentionDays = 5;
            var clone = ProtocolSerializer.FromJson<AppConfig>(ProtocolSerializer.ToJson(config));
            Assert(clone.ShareRole == ShareRoleValues.Agent &&
                   clone.ControllerAddress == "192.168.1.9:47540" &&
                   clone.DebugToken == "tok" &&
                   clone.VerboseLogging &&
                   clone.LogRetentionDays == 5,
                "Debug settings serialization failed.");

            var settings = new AgentSettingsDto
            {
                ShareRole = ShareRoleValues.Agent,
                ControllerAddress = "10.0.0.2",
                DebugToken = "abc",
                VerboseLogging = true,
                LogRetentionDays = 4
            };
            var settingsClone = ProtocolSerializer.FromJson<AgentSettingsDto>(ProtocolSerializer.ToJson(settings));
            Assert(settingsClone.ShareRole == ShareRoleValues.Agent &&
                   settingsClone.ControllerAddress == "10.0.0.2" &&
                   settingsClone.DebugToken == "abc" &&
                   settingsClone.VerboseLogging &&
                   settingsClone.LogRetentionDays == 4,
                "Debug settings transfer failed.");
        }

        private static void LogProtocolHelpersWork()
        {
            var hello = LogProtocol.BuildHello("dev-1", "PC A", ShareRoleValues.Agent, "tok", "0.1.64");
            Assert(hello.StartsWith(LogProtocol.HelloPrefix + " ", StringComparison.Ordinal),
                "Hello prefix failed.");
            var pairs = LogProtocol.ParseKeyValues(hello, 1);
            Assert(pairs["proto"] == LogProtocol.ProtocolVersion &&
                   pairs["id"] == "dev-1" &&
                   pairs["name"] == "PC_A" &&
                   pairs["role"] == ShareRoleValues.Agent &&
                   pairs["version"] == "0.1.64",
                "Hello parsing failed.");

            Assert(LogProtocol.BuildOk("Host").StartsWith(LogProtocol.OkPrefix, StringComparison.Ordinal),
                "Ok line failed.");
            Assert(LogProtocol.BuildDeny("bad token").StartsWith(LogProtocol.DenyPrefix, StringComparison.Ordinal),
                "Deny line failed.");
            var origin = LogProtocol.BuildOrigin("d1", "n1", ShareRoleValues.Controller, "0.1.64", "1.2.3.4:47540");
            Assert(origin.StartsWith(LogProtocol.MarkerPrefix + "ORIGIN " + "id=d1", StringComparison.Ordinal) &&
                   origin.Contains("role=Controller"),
                "Origin header failed.");

            Assert(LogProtocol.Safe("a\nb\t|c") == "a_b__c", "Safe sanitization failed.");
            Assert(LogProtocol.Safe("123456", 4) == "1234", "Safe truncation failed.");
            Assert(LogProtocol.Safe(null) == "-", "Safe empty value failed.");

            int port;
            Assert(LogProtocol.TryParsePort("47540", out port) && port == 47540, "Port parsing failed.");
            Assert(!LogProtocol.TryParsePort("0", out port), "Port zero should be rejected.");
            Assert(!LogProtocol.TryParsePort("70000", out port), "Out of range port should be rejected.");

            string host;
            LogProtocol.SplitEndpoint("192.168.1.5:5000", out host, out port);
            Assert(host == "192.168.1.5" && port == 5000, "Endpoint split with port failed.");
            LogProtocol.SplitEndpoint("192.168.1.5", out host, out port);
            Assert(host == "192.168.1.5" && port == LogProtocol.DefaultPort, "Endpoint split fallback failed.");
        }

        private static void BuildVersionIsConsistent()
        {
            var expected = BuildInfo.Version;
            Assert(expected == "0.2.0-beta-fix19", "Build version should be 0.2.0-beta-fix19.");
            Assert(AppLog.Version == expected, "Log header version should follow BuildInfo.");

            var agentPath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml.cs");
            var setupPath = Path.Combine("src", "Setup", "Program.cs");
            var scriptPath = Path.Combine("scripts", "Build-Installer.ps1");
            if (!File.Exists(agentPath))
            {
                agentPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml.cs");
                setupPath = Path.Combine("..", "..", "..", "src", "Setup", "Program.cs");
                scriptPath = Path.Combine("..", "..", "..", "scripts", "Build-Installer.ps1");
            }

            Assert(File.ReadAllText(agentPath).Contains("AppVersion = \"" + expected + "\""),
                "Agent version should match BuildInfo.");
            // SetupVersion 保持纯数字（升级比较依赖 Version.TryParse），单独维护，必须比上一版大。
            Assert(File.ReadAllText(setupPath).Contains("SetupVersion = \"0.2.4\""),
                "Setup version should stay numeric (0.2.4) for upgrade comparison.");
            Assert(File.ReadAllText(scriptPath).Contains("$Version = \"" + expected + "\""),
                "Build script version should match BuildInfo.");
        }
        private static void SystemInfoAndHeartbeatWork()
        {
            var report = SystemInfoReport.Build("Test");
            Assert(report.Contains("LanMouseShare"), "System info report should name the product.");
            Assert(report.Contains("[\u64CD\u4F5C\u7CFB\u7EDF"), "System info report should include the operating system section.");
            Assert(report.Contains("[\u663E\u793A\u5668"), "System info report should include the display section.");
            Assert(report.Contains("[\u7F51\u7EDC"), "System info report should include the network section.");
            Assert(report.Contains("[\u914D\u7F6E"), "System info report should include the configuration section.");

            Assert(SystemInfoReport.FileNameFor("Agent") == "SystemInfo-Agent.txt",
                "Agent report file name should be stable.");
            Assert(SystemInfoReport.FileNameFor("Agent") != SystemInfoReport.FileNameFor("Service"),
                "Agent and service reports must not overwrite each other.");

            Assert(LogProtocol.SafeLogLine("  a  b\r\n", 100) == "  a  b",
                "Log line sanitiser should keep inner spaces and drop line breaks.");
            Assert(LogProtocol.SafeLogLine("abcdef", 3) == "abc", "Log line sanitiser should truncate.");

            var heartbeat = new HeartbeatPayload { Ticks = 123456789L, Reply = true };
            var json = ProtocolSerializer.ToJson(heartbeat);
            var parsed = ProtocolSerializer.FromJson<HeartbeatPayload>(json);
            Assert(parsed != null && parsed.Ticks == heartbeat.Ticks && parsed.Reply,
                "Heartbeat payload should round trip.");
        }

        private static void DebugLogUiExists()
        {
            var xamlPath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml");
            var codePath = Path.Combine("src", "Desktop.Agent", "MainWindow.xaml.cs");
            if (!File.Exists(xamlPath))
            {
                xamlPath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml");
                codePath = Path.Combine("..", "..", "..", "src", "Desktop.Agent", "MainWindow.xaml.cs");
            }

            var xaml = File.ReadAllText(xamlPath);
            Assert(xaml.Contains("ShareRoleBox") &&
                   xaml.Contains("ControllerAddressBox") &&
                   xaml.Contains("DebugTokenBox") &&
                   xaml.Contains("LogRetentionBox") &&
                   xaml.Contains("VerboseLoggingBox") &&
                   xaml.Contains("LogStatusText") &&
                   xaml.Contains("LogPathText") &&
                   xaml.Contains("OnOpenLogsClicked") &&
                   xaml.Contains("OnOpenRemoteLogsClicked"),
                "Agent UI should expose the debug log settings.");

            var code = File.ReadAllText(codePath);
            Assert(code.Contains("ShareRole") &&
                   code.Contains("ApplyLogSettingsToUi") &&
                   code.Contains("LogRetentionDays"),
                "Agent code should persist the debug log settings.");
        }

        private static void ControlLinkMachineWorks()
        {
            const long Ms = TimeSpan.TicksPerMillisecond;
            var now = DateTime.UtcNow.Ticks;

            var machine = new ControlLinkMachine("A");
            ControlLease lease;
            Assert(machine.Role == ControlLinkRole.Idle, "Link should start idle.");
            Assert(!machine.TryBeginControl(now, out lease), "Link must not begin control without a peer.");

            machine.SetPeerDeviceId("B");
            Assert(machine.Snapshot(now).CanBeginControl, "Link should allow control once the peer is known.");
            Assert(machine.TryBeginControl(now, out lease), "Link should begin control.");
            Assert(machine.Role == ControlLinkRole.Owner, "Claiming side must become the owner.");
            Assert(lease.Epoch >= now && lease.OwnerDeviceId == "A", "First claim must use a fresh comparable epoch.");
            Assert(machine.Snapshot(now).ShouldSuppressLocalInput, "Owner must swallow local input.");
            Assert(!machine.Snapshot(now).ShouldInjectRemoteInput, "Owner must not inject remote input.");

            var renew = machine.BuildLease(now + 60 * Ms);
            Assert(renew != null && renew.Epoch == lease.Epoch, "Lease renewal must keep the epoch.");
            machine.OnAck(renew.Epoch, now + 100 * Ms);

            var peer = new ControlLinkMachine("B");
            peer.SetPeerDeviceId("A");
            Assert(peer.OnLease(lease, now + 20 * Ms), "Target must adopt the peer lease.");
            Assert(peer.Role == ControlLinkRole.Target, "Receiving a lease must turn the peer into the target.");
            Assert(peer.Snapshot(now + 30 * Ms).ShouldInjectRemoteInput, "Target must inject remote input.");
            Assert(!peer.Snapshot(now + 30 * Ms).ShouldSuppressLocalInput, "Target must keep local input usable.");
            Assert(!peer.OnLease(lease, now + 40 * Ms), "Re-applying the same lease must not change the role.");

            var stale = new ControlLease { Epoch = lease.Epoch - 1, OwnerDeviceId = "C", TtlMs = 1200, Sequence = 1 };
            Assert(!peer.OnLease(stale, now + 50 * Ms), "Stale epoch must be ignored.");

            Assert(ControlLinkMachine.IsLeaseStronger(5, "b", 5, "a"), "Equal epoch must fall back to the device id.");
            Assert(!ControlLinkMachine.IsLeaseStronger(5, "a", 5, "b"), "Arbitration must be symmetric.");
            Assert(ControlLinkMachine.IsLeaseStronger(6, "a", 5, "b"), "Higher epoch must win.");
            Assert(!ControlLinkMachine.IsLeaseStronger(4, "z", 5, "a"), "Lower epoch must lose.");

            ControlLinkReleaseReason reason;
            Assert(!machine.Tick(now + 2400 * Ms, out reason), "Owner should keep control while acked.");
            Assert(machine.Tick(now + 2700 * Ms, out reason), "Owner must drop control after an ack timeout.");
            Assert(reason == ControlLinkReleaseReason.AckTimeout, "Ack timeout must report its reason.");
            Assert(machine.Role == ControlLinkRole.Idle, "Ack timeout must return the link to idle.");

            peer.NoteRemoteInput(now + 400 * Ms);
            Assert(!peer.Tick(now + 2000 * Ms, out reason), "Remote input activity must keep the target alive.");
            Assert(!peer.Tick(now + 2500 * Ms, out reason), "Fresh activity must still hold the target.");
            Assert(peer.Tick(now + 3000 * Ms, out reason), "Target must drop a stale lease.");
            Assert(reason == ControlLinkReleaseReason.LeaseExpired, "Lease expiry must report its reason.");
            Assert(peer.Role == ControlLinkRole.Idle, "Lease expiry must return the link to idle.");

            var paused = new ControlLinkMachine("A");
            paused.SetPeerDeviceId("B");
            Assert(paused.TryBeginControl(now, out lease), "Pause fixture should claim control.");
            paused.SetPaused(true, now + 10 * Ms);
            Assert(paused.Role == ControlLinkRole.Idle, "Pausing must drop control.");
            Assert(!paused.TryBeginControl(now + 20 * Ms, out lease), "Paused link must not start control.");

            var owner = new ControlLinkMachine("A");
            owner.SetPeerDeviceId("B");
            Assert(owner.TryBeginControl(now, out lease), "Target release fixture should claim control.");
            var target = new ControlLinkMachine("B");
            target.SetPeerDeviceId("A");
            Assert(target.OnLease(lease, now + 10 * Ms), "Target release fixture should adopt the lease.");
            target.Release(ControlLinkReleaseReason.TargetRequestedRelease, now + 20 * Ms);
            Assert(target.Role == ControlLinkRole.Idle, "Target must stop injecting as soon as it asks to be released.");
            Assert(!target.Snapshot(now + 30 * Ms).ShouldInjectRemoteInput, "Released target must not inject.");
            Assert(target.LastReleaseReason == ControlLinkReleaseReason.TargetRequestedRelease,
                "Target release must report its own reason.");
            Assert(owner.Tick(now + 2700 * Ms, out reason), "Owner must recover once the target stops acking.");
            Assert(reason == ControlLinkReleaseReason.AckTimeout, "Silent target must time the owner out.");
        }

        private static void ClipboardFileFramesWork()
        {
            var paths = new[] { @"C:\Users\Public\clip-a.txt", @"C:\Users\Public\folder-b" };
            var bytes = ControlChannelCodec.EncodeClipboardFiles(paths);
            Assert(bytes != null && bytes.Length > 12, "Clipboard frame encoding failed.");

            string[] decoded;
            Assert(ControlChannelCodec.TryDecodeClipboardFiles(bytes, out decoded), "Clipboard frame decoding failed.");
            Assert(decoded.Length == 2, "Clipboard frame count mismatch.");
            Assert(decoded[0] == paths[0] && decoded[1] == paths[1], "Clipboard frame paths mismatch.");

            string[] none;
            Assert(!ControlChannelCodec.TryDecodeClipboardFiles(new byte[] { 1, 2, 3 }, out none), "Truncated clipboard frame accepted.");
            Assert(ControlChannelCodec.EncodeClipboardFiles(new string[0]) == null, "Empty clipboard list should encode to null.");
        }

        private static void ControlChannelCodecWorks()
        {
            var lease = new ControlLease { Epoch = 42, OwnerDeviceId = "device-a", TtlMs = 1200, Sequence = 7 };
            var bytes = ControlChannelCodec.EncodeLease(lease);
            Assert(bytes != null && bytes.Length > 10, "Lease frame must be encoded.");

            ControlLease decoded;
            Assert(ControlChannelCodec.TryDecodeLease(bytes, out decoded), "Lease frame must decode.");
            Assert(decoded.Epoch == 42 && decoded.OwnerDeviceId == "device-a" &&
                   decoded.TtlMs == 1200 && decoded.Sequence == 7, "Lease round trip failed.");

            var ackBytes = ControlChannelCodec.EncodeAck(42);
            long epoch;
            Assert(ControlChannelCodec.TryDecodeAck(ackBytes, out epoch) && epoch == 42, "Ack round trip failed.");

            Assert(!ControlChannelCodec.TryDecodeAck(bytes, out epoch), "A lease frame must not decode as an ack.");
            Assert(!ControlChannelCodec.TryDecodeLease(ackBytes, out decoded), "An ack frame must not decode as a lease.");

            var corrupted = (byte[])bytes.Clone();
            corrupted[0] ^= 0xFF;
            Assert(!ControlChannelCodec.TryDecodeLease(corrupted, out decoded), "Bad magic must be rejected.");
            Assert(!ControlChannelCodec.TryDecodeLease(new byte[2], out decoded), "Truncated frame must be rejected.");
            Assert(!ControlChannelCodec.TryDecodeLease(null, out decoded), "Null frame must be rejected.");
        }

        private static void LogTransferAndFileClipboardWork()
        {
            var config = AppConfig.CreateDefault("local-device", "Local PC", new byte[32]);
            Assert(config.LogTransferEnabled, "Log transfer must default to enabled.");
            Assert(config.ConfigRevision == 50, "Config revision default must be 50.");

            var legacy = AppConfig.CreateDefault("local-device", "Local PC", new byte[32]);
            legacy.LogTransferEnabled = false;
            legacy.ConfigRevision = 0;
            ConfigStore.ApplyDefaults(legacy);
            Assert(legacy.LogTransferEnabled, "Legacy config must migrate to log transfer enabled.");
            Assert(legacy.ConfigRevision == 50, "Legacy config must migrate its revision.");

            var settings = new AgentSettingsDto { LogTransferEnabled = false, ShareRole = ShareRoleValues.Agent };
            var settingsJson = ProtocolSerializer.ToJson(settings);
            var settingsBack = ProtocolSerializer.FromJson<AgentSettingsDto>(settingsJson);
            Assert(!settingsBack.LogTransferEnabled, "Settings must carry log transfer off.");
            Assert(settingsBack.ShareRole == ShareRoleValues.Agent, "Settings must keep the share role.");

            var state = new PeerStateDto { LogTransferEnabled = false };
            var stateBack = ProtocolSerializer.FromJson<PeerStateDto>(ProtocolSerializer.ToJson(state));
            Assert(!stateBack.LogTransferEnabled, "Peer state must carry log transfer off.");

            var files = new ClipboardPayload
            {
                Format = ClipboardFormats.Files,
                FilePaths = new string[] { @"C:\Temp\a.txt", @"C:\Temp\b.png" },
                Version = 7
            };
            var filesBack = ProtocolSerializer.FromJson<ClipboardPayload>(ProtocolSerializer.ToJson(files));
            Assert(filesBack.Format == ClipboardFormats.Files, "File clipboard format must round-trip.");
            Assert(filesBack.FilePaths != null && filesBack.FilePaths.Length == 2, "File clipboard paths must round-trip.");
            Assert(filesBack.FilePaths[0] == @"C:\Temp\a.txt", "File clipboard path content must round-trip.");

            var complete = new FileCompletePayload { TransferId = "t1", Success = true, SavedPath = @"C:\Temp\a.txt" };
            var completeBack = ProtocolSerializer.FromJson<FileCompletePayload>(ProtocolSerializer.ToJson(complete));
            Assert(completeBack.SavedPath == @"C:\Temp\a.txt", "Saved path must round-trip.");

            Assert(TrafficPriority.InteractiveDscp == 46, "Interactive DSCP must be EF 46.");
            Assert(TrafficPriority.BulkDscp == 8, "Bulk DSCP must be CS1 8.");
            Assert((TrafficPriority.InteractiveDscp << 2) == 184, "DSCP must be shifted into the TOS byte.");
        }
        private static void LazyFileClipboardWorks()
        {
            var payload = new ClipboardPayload
            {
                Format = ClipboardFormats.Files,
                FilePaths = new string[] { @"C:\Temp\a.txt", @"C:\Temp\b.bin" },
                FileSizes = new long[] { 12, 4096 },
                IsLazy = true,
                Version = 9
            };
            var json = ProtocolSerializer.ToJson(payload);
            var back = ProtocolSerializer.FromJson<ClipboardPayload>(json);
            Assert(back.IsLazy, "Lazy file clipboard flag must round-trip.");
            Assert(back.FileSizes != null && back.FileSizes.Length == 2, "File sizes must round-trip.");
            Assert(back.FileSizes[1] == 4096, "File size value must round-trip.");
            Assert(back.FilePaths.Length == 2, "File paths must still round-trip on the lazy payload.");

            var pull = new ClipboardPullRequestPayload
            {
                FilePaths = new string[] { @"C:\Temp\a.txt" },
                RequesterDeviceId = "requester"
            };
            var pullBack = ProtocolSerializer.FromJson<ClipboardPullRequestPayload>(ProtocolSerializer.ToJson(pull));
            Assert(pullBack.FilePaths[0] == @"C:\Temp\a.txt", "Clipboard pull paths must round-trip.");
            Assert(pullBack.RequesterDeviceId == "requester", "Clipboard pull requester must round-trip.");

            Assert(ProtocolMessageTypes.ClipboardPull == "ClipboardPull", "Clipboard pull message type must be stable.");
            Assert(AgentCommands.RequestClipboardFiles == "RequestClipboardFiles", "Clipboard pull command must be stable.");

            var pasteRequest = new ClipboardPasteRequestPayload
            {
                RequesterDeviceId = "controller",
                FileCount = 3
            };
            var pasteBack = ProtocolSerializer.FromJson<ClipboardPasteRequestPayload>(
                ProtocolSerializer.ToJson(pasteRequest));
            Assert(pasteBack != null && pasteBack.FileCount == 3 && pasteBack.RequesterDeviceId == "controller",
                "Clipboard paste request must round-trip.");
            Assert(ProtocolMessageTypes.ClipboardPasteRequest == "ClipboardPasteRequest",
                "Clipboard paste request message type must be stable.");
            Assert(AgentCommands.RequestClipboardPaste == "RequestClipboardPaste",
                "Clipboard paste request command must be stable.");
        }
        private static void ControlLeaseRelayPayloadWorks()
        {
            var payload = new ControlLeasePayload();
            payload.Epoch = 9;
            payload.OwnerDeviceId = "device-a";
            payload.TtlMs = 1200;
            payload.Sequence = 3;

            var json = ProtocolSerializer.ToJson(payload);
            var back = ProtocolSerializer.FromJson<ControlLeasePayload>(json);
            Assert(back != null, "Relay lease payload must deserialize.");
            Assert(back.Epoch == 9 && back.OwnerDeviceId == "device-a" &&
                   back.TtlMs == 1200 && back.Sequence == 3,
                "Relay lease payload must survive the service relay hop.");

            var ack = new ControlLeaseAckPayload();
            ack.Epoch = 9;
            var ackBack = ProtocolSerializer.FromJson<ControlLeaseAckPayload>(ProtocolSerializer.ToJson(ack));
            Assert(ackBack != null && ackBack.Epoch == 9, "Relay ack payload round trip failed.");
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static bool AreNearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.01;
        }
    }
}
