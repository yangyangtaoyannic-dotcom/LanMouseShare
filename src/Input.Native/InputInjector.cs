using System;
using System.Runtime.InteropServices;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Input
{
    public sealed class InputInjector
    {
        public InputInjectionResult Inject(InputPayload payload)
        {
            if (payload == null)
            {
                return InputInjectionResult.Succeeded();
            }

            if (payload.Kind == InputKinds.Key)
            {
                return InjectKey(payload);
            }
            else if (payload.Kind == InputKinds.MouseMove)
            {
                return InjectMouseMove(payload.X, payload.Y);
            }
            else if (payload.Kind == InputKinds.MouseMoveRelative)
            {
                return InjectMouseMoveRelative(payload.X, payload.Y);
            }
            else if (payload.Kind == InputKinds.MouseWheel)
            {
                return InjectMouseWheel(payload.Delta);
            }
            else if (payload.Kind == InputKinds.MouseButton)
            {
                return InjectMouseButton(payload.Button, payload.IsDown);
            }

            return InputInjectionResult.Succeeded();
        }

        public void ReleaseLeftMouseButton()
        {
            InjectMouseButton(NativeMethods.WM_LBUTTONUP, false);
        }

        public void ReleaseAllMouseButtons()
        {
            InjectMouseButton(NativeMethods.WM_LBUTTONUP, false);
            InjectMouseButton(NativeMethods.WM_RBUTTONUP, false);
            InjectMouseButton(NativeMethods.WM_MBUTTONUP, false);
        }

        private static InputInjectionResult InjectKey(InputPayload payload)
        {
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            if (payload.ScanCode > 0)
            {
                input.u.ki.wScan = (ushort)payload.ScanCode;
                input.u.ki.dwFlags = NativeMethods.KEYEVENTF_SCANCODE;
            }
            else
            {
                input.u.ki.wVk = (ushort)payload.VirtualKey;
            }

            if (payload.IsExtendedKey)
            {
                input.u.ki.dwFlags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            }

            if (!payload.IsDown)
            {
                input.u.ki.dwFlags |= NativeMethods.KEYEVENTF_KEYUP;
            }

            return Send(input);
        }

        private static InputInjectionResult InjectMouseMove(int x, int y)
        {
            int width = Math.Max(1, NativeMethods.GetSystemMetrics(0) - 1);
            int height = Math.Max(1, NativeMethods.GetSystemMetrics(1) - 1);
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_MOUSE;
            input.u.mi.dx = x * 65535 / width;
            input.u.mi.dy = y * 65535 / height;
            input.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE;
            return Send(input);
        }

        private static InputInjectionResult InjectMouseMoveRelative(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
            {
                return InputInjectionResult.Succeeded();
            }

            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_MOUSE;
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE;
            return Send(input);
        }

        private static InputInjectionResult InjectMouseWheel(int delta)
        {
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_MOUSE;
            input.u.mi.mouseData = unchecked((uint)delta);
            input.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_WHEEL;
            return Send(input);
        }

        private static InputInjectionResult InjectMouseButton(int message, bool isDown)
        {
            uint flags = 0;
            if (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_LBUTTONUP)
            {
                flags = isDown ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
            }
            else if (message == NativeMethods.WM_RBUTTONDOWN || message == NativeMethods.WM_RBUTTONUP)
            {
                flags = isDown ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
            }
            else if (message == NativeMethods.WM_MBUTTONDOWN || message == NativeMethods.WM_MBUTTONUP)
            {
                flags = isDown ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
            }

            if (flags != 0)
            {
                var input = new NativeMethods.INPUT();
                input.type = NativeMethods.INPUT_MOUSE;
                input.u.mi.dwFlags = flags;
                return Send(input);
            }

            return InputInjectionResult.Succeeded();
        }

        private static InputInjectionResult Send(NativeMethods.INPUT input)
        {
            var inputs = new[] { input };
            var sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            if (sent == 1)
            {
                return InputInjectionResult.Succeeded();
            }

            return InputInjectionResult.Failed("SendInput failed", Marshal.GetLastWin32Error());
        }
    }

    public sealed class InputInjectionResult
    {
        private InputInjectionResult(bool success, string reason, int lastError)
        {
            Success = success;
            Reason = reason ?? string.Empty;
            LastError = lastError;
        }

        public bool Success { get; private set; }

        public string Reason { get; private set; }

        public int LastError { get; private set; }

        public static InputInjectionResult Succeeded()
        {
            return new InputInjectionResult(true, string.Empty, 0);
        }

        public static InputInjectionResult Failed(string reason, int lastError)
        {
            return new InputInjectionResult(false, reason, lastError);
        }
    }
}
