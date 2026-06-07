using System;
using System.Runtime.InteropServices;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Input
{
    public sealed class InputHook : IDisposable
    {
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private NativeMethods.LowLevelProc _keyboardProc;
        private NativeMethods.LowLevelProc _mouseProc;

        public event EventHandler<InputCapturedEventArgs> InputCaptured;

        public void Start()
        {
            if (_keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero)
            {
                return;
            }

            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        }

        public void Stop()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hook = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                if ((hook.flags & NativeMethods.LLKHF_INJECTED) == NativeMethods.LLKHF_INJECTED)
                {
                    return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                }

                int message = wParam.ToInt32();
                if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_KEYUP ||
                    message == NativeMethods.WM_SYSKEYDOWN || message == NativeMethods.WM_SYSKEYUP)
                {
                    if (OnInputCaptured(new InputPayload
                    {
                        Kind = InputKinds.Key,
                        VirtualKey = hook.vkCode,
                        ScanCode = hook.scanCode,
                        Flags = hook.flags,
                        IsExtendedKey = (hook.flags & NativeMethods.LLKHF_EXTENDED) == NativeMethods.LLKHF_EXTENDED,
                        IsSystemKey = message == NativeMethods.WM_SYSKEYDOWN ||
                                      message == NativeMethods.WM_SYSKEYUP ||
                                      (hook.flags & NativeMethods.LLKHF_ALTDOWN) == NativeMethods.LLKHF_ALTDOWN,
                        IsDown = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN
                    }))
                    {
                        return (IntPtr)1;
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hook = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                if ((hook.flags & NativeMethods.LLMHF_INJECTED) == NativeMethods.LLMHF_INJECTED)
                {
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }

                int message = wParam.ToInt32();
                var payload = new InputPayload { X = hook.pt.X, Y = hook.pt.Y };
                if (message == NativeMethods.WM_MOUSEMOVE)
                {
                    payload.Kind = InputKinds.MouseMove;
                }
                else if (message == NativeMethods.WM_MOUSEWHEEL)
                {
                    payload.Kind = InputKinds.MouseWheel;
                    payload.Delta = (short)((hook.mouseData >> 16) & 0xffff);
                }
                else
                {
                    payload.Kind = InputKinds.MouseButton;
                    payload.Button = message;
                    payload.IsDown = message == NativeMethods.WM_LBUTTONDOWN ||
                                     message == NativeMethods.WM_RBUTTONDOWN ||
                                     message == NativeMethods.WM_MBUTTONDOWN;
                }

                if (OnInputCaptured(payload))
                {
                    return (IntPtr)1;
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private bool OnInputCaptured(InputPayload payload)
        {
            var handler = InputCaptured;
            if (handler != null)
            {
                var args = new InputCapturedEventArgs(payload);
                handler(this, args);
                return args.SuppressLocalInput;
            }

            return false;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    public sealed class InputCapturedEventArgs : EventArgs
    {
        public InputCapturedEventArgs(InputPayload payload)
        {
            Payload = payload;
        }

        public InputPayload Payload { get; private set; }

        public bool SuppressLocalInput { get; set; }
    }
}
