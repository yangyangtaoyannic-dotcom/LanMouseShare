using System;
using System.Collections.Generic;
using System.Drawing;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Input;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 输入转发控制器 - 负责将本地输入转发到远端，并注入远端输入到本地
    /// 职责：输入队列管理、坐标转换、注入执行
    /// </summary>
    public sealed class InputForwardingController : IDisposable
    {
        private readonly InputInjector _injector;
        private readonly Queue<InputEvent> _pendingEvents;
        private readonly HashSet<int> _heldKeys;

        private bool _isActive;
        private string _direction;
        private Size _remoteScreenSize;
        private Rectangle _localScreenBounds;
        private Point _remoteVirtualPosition;
        private int _injectedEventCount;

        public InputForwardingController()
        {
            _injector = new InputInjector();
            _pendingEvents = new Queue<InputEvent>();
            _heldKeys = new HashSet<int>();
        }

        /// <summary>
        /// 是否处于活跃状态
        /// </summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// 已注入事件计数
        /// </summary>
        public int InjectedEventCount => _injectedEventCount;

        /// <summary>
        /// 启动输入转发
        /// </summary>
        public void Start(string direction, Size remoteScreenSize)
        {
            _isActive = true;
            _direction = direction;
            _remoteScreenSize = remoteScreenSize;
            _remoteVirtualPosition = Point.Empty;
            _injectedEventCount = 0;

            AppLog.Info("input.forwarding.start",
                $"direction={direction} remoteSize={remoteScreenSize.Width}x{remoteScreenSize.Height}");
        }

        /// <summary>
        /// 停止输入转发
        /// </summary>
        public void Stop()
        {
            if (!_isActive) return;

            // 释放所有按住的按键
            ReleaseAllHeldKeys();

            _isActive = false;
            _pendingEvents.Clear();

            AppLog.Info("input.forwarding.stop", $"injected={_injectedEventCount}");
        }

        /// <summary>
        /// 转发本地输入到远端（当作为 Owner 时）
        /// </summary>
        public void ForwardLocalInput(InputEvent[] events)
        {
            if (!_isActive || events == null || events.Length == 0)
                return;

            foreach (var evt in events)
            {
                _pendingEvents.Enqueue(evt);
            }

            AppLog.Debug("input.forwarding.queued", $"count={events.Length}");
        }

        /// <summary>
        /// 注入远程输入到本地（当作为 Target 时）
        /// </summary>
        public void InjectRemoteInput(InputEvent[] events)
        {
            if (!_isActive || events == null || events.Length == 0)
                return;

            foreach (var evt in events)
            {
                InjectSingleEvent(evt);
            }
        }

        /// <summary>
        /// 获取待发送的输入事件
        /// </summary>
        public InputEvent[] DequeueForSending(int maxCount = 100)
        {
            if (_pendingEvents.Count == 0)
                return Array.Empty<InputEvent>();

            var count = Math.Min(_pendingEvents.Count, maxCount);
            var result = new InputEvent[count];

            for (int i = 0; i < count; i++)
            {
                result[i] = _pendingEvents.Dequeue();
            }

            return result;
        }

        /// <summary>
        /// 更新远程虚拟坐标（用于绝对定位）
        /// </summary>
        public void UpdateRemoteVirtualPosition(Point position)
        {
            _remoteVirtualPosition = ClampToScreen(position, _remoteScreenSize);
        }

        /// <summary>
        /// 获取当前远程虚拟坐标
        /// </summary>
        public Point GetRemoteVirtualPosition()
        {
            return _remoteVirtualPosition;
        }

        private void InjectSingleEvent(InputEvent evt)
        {
            try
            {
                switch (evt.Kind)
                {
                    case InputKinds.MouseMove:
                        _injector.InjectMouseMove(evt.X, evt.Y, absolute: true);
                        _injectedEventCount++;
                        break;

                    case InputKinds.MouseMoveRelative:
                        _injector.InjectMouseMove(evt.X, evt.Y, absolute: false);
                        _injectedEventCount++;
                        break;

                    case InputKinds.MouseButtonDown:
                    case InputKinds.MouseButtonUp:
                        _injector.InjectMouseButton(evt.Button, evt.Kind == InputKinds.MouseButtonDown);
                        _injectedEventCount++;
                        break;

                    case InputKinds.MouseWheel:
                        _injector.InjectMouseWheel(evt.WheelDelta);
                        _injectedEventCount++;
                        break;

                    case InputKinds.KeyDown:
                        _injector.InjectKey(evt.VirtualKey, evt.ScanCode, down: true);
                        _heldKeys.Add(evt.VirtualKey);
                        _injectedEventCount++;
                        break;

                    case InputKinds.KeyUp:
                        _injector.InjectKey(evt.VirtualKey, evt.ScanCode, down: false);
                        _heldKeys.Remove(evt.VirtualKey);
                        _injectedEventCount++;
                        break;
                }

                AppLog.Debug("input.inject.success", $"kind={evt.Kind} count={_injectedEventCount}");
            }
            catch (Exception ex)
            {
                AppLog.Error("input.inject.failed", $"kind={evt.Kind} error={ex.Message}");
            }
        }

        private void ReleaseAllHeldKeys()
        {
            if (_heldKeys.Count == 0)
                return;

            AppLog.Info("input.forwarding.release_keys", $"count={_heldKeys.Count}");

            foreach (var vk in _heldKeys)
            {
                try
                {
                    _injector.InjectKey(vk, 0, down: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("input.release_key.failed", $"vk={vk} error={ex.Message}");
                }
            }

            _heldKeys.Clear();
        }

        private Point ClampToScreen(Point position, Size screenSize)
        {
            var x = Math.Max(0, Math.Min(position.X, screenSize.Width - 1));
            var y = Math.Max(0, Math.Min(position.Y, screenSize.Height - 1));
            return new Point(x, y);
        }

        public void Dispose()
        {
            Stop();
            _injector?.Dispose();
        }
    }

    /// <summary>
    /// 输入事件（简化版）
    /// </summary>
    public class InputEvent
    {
        public string Kind { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }
        public int WheelDelta { get; set; }
        public int VirtualKey { get; set; }
        public int ScanCode { get; set; }
    }

    /// <summary>
    /// 输入类型常量
    /// </summary>
    public static class InputKinds
    {
        public const string MouseMove = "MouseMove";
        public const string MouseMoveRelative = "MouseMoveRelative";
        public const string MouseButtonDown = "MouseButtonDown";
        public const string MouseButtonUp = "MouseButtonUp";
        public const string MouseWheel = "MouseWheel";
        public const string KeyDown = "KeyDown";
        public const string KeyUp = "KeyUp";
    }
}
