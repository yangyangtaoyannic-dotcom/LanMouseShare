using System;
using System.Drawing;
using System.IO;
using System.Text;
using LanMouseShare.Core.Configuration;
using LanMouseShare.Core.Control;
using LanMouseShare.Core.Crypto;
using LanMouseShare.Core.Ipc;
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
            Assert(xaml.Contains("LanMouseShare 0.1.41") &&
                   xaml.Contains("&#x5C40;&#x57DF;&#x7F51;&#x952E;&#x9F20;&#x5171;&#x4EAB;") &&
                   xaml.Contains("&#x8FDE;&#x63A5;&#x72B6;&#x6001;"),
                "Agent UI should use 0.1.41 and Chinese XML entities.");

            Assert(File.ReadAllText(appPath).Contains("AppVersion = \"0.1.41\""), "Agent version should be 0.1.41.");
            Assert(File.ReadAllText(setupPath).Contains("SetupVersion = \"0.1.41\""), "Setup version should be 0.1.41.");
            Assert(File.ReadAllText(scriptPath).Contains("$Version = \"0.1.41\""), "Build script default version should be 0.1.41.");
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

            result = detector.Update(new Point(0, 300), screen, "Left", -10, 0, false, false, 0);
            Assert(result.ShouldRelease && result.Reason == "RealEdgeOutward",
                "Outward movement at the real edge should release immediately.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(799, 300), screen, "Right", 10, 0, false, false, 0);
            Assert(result.ShouldRelease, "Right edge outward push should release.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(300, 0), screen, "Up", 0, -10, false, false, 0);
            Assert(result.ShouldRelease, "Up edge outward push should release.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(300, 599), screen, "Down", 0, 10, false, false, 0);
            Assert(result.ShouldRelease, "Down edge outward push should release.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(0, 300), screen, "Left", 0, 12, false, false, 0);
            Assert(!result.ShouldRelease && result.Reason == "Armed",
                "Vertical movement along the return edge should not release.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(799, 20), screen, "Right", 10, 0, false, true, 80);
            Assert(!result.ShouldRelease && result.Reason == "BlockedCorner",
                "Remote right top corner should block return release.");

            detector = new RemoteEdgeReturnDetector();
            result = detector.Update(new Point(799, 20), screen, "Right", 10, 0, false, false, 80);
            Assert(result.ShouldRelease,
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
            Assert(leftReturn.X == 2 && leftReturn.Y == 300,
                "Left return should preserve Y ratio on the local screen.");

            var downEntry = EdgeTransitionMapper.MapEntryToRemote(new Point(400, 599), local, remote, "Down");
            Assert(downEntry.X == 800 && downEntry.Y == 8,
                "Down entry should preserve X ratio on the remote screen.");

            var upReturn = EdgeTransitionMapper.MapReturnToLocal(new Point(800, 0), remote, local, "Up");
            Assert(upReturn.X == 400 && upReturn.Y == 2,
                "Up return should preserve X ratio on the local screen.");
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
