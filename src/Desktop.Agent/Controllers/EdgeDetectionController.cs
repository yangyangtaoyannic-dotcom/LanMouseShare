using System;
using System.Drawing;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 边缘检测控制器 - 负责检测鼠标是否到达屏幕边缘并请求切屏
    /// 改进点：动态阈值、方向检测、防抖动
    /// </summary>
    public sealed class EdgeDetectionController : IDisposable
    {
        private const int CornerBlockPixels = 80;  // 屏幕角落阻挡区域
        private const int EdgeThresholdPixels = 2;  // 边缘检测阈值
        private const int ArmDistancePixels = 80;   // 重新武装距离
        private const int CooldownMs = 400;         // 冷却时间

        private bool _armed = true;
        private DateTime _lockUntil = DateTime.MinValue;
        private Point _lastPosition;
        private DateTime _lastMoveTime;
        private Rectangle _currentScreenBounds;
        private string _configuredDirection = "Right";  // Left, Right, Up, Down
        private Size _remoteScreenSize = new Size(1920, 1080);
        private bool _blockCorners = true;

        public event EventHandler<EdgeReachedEventArgs> EdgeReached;

        /// <summary>
        /// 设置布局方向（对端在哪一侧）
        /// </summary>
        public void SetDirection(string direction)
        {
            if (string.IsNullOrEmpty(direction)) return;
            _configuredDirection = direction;
            AppLog.Debug("edge.direction.set", $"direction={direction}");
        }

        /// <summary>
        /// 设置远端屏幕尺寸
        /// </summary>
        public void SetRemoteScreenSize(Size size)
        {
            _remoteScreenSize = size;
            AppLog.Debug("edge.remote.size", $"width={size.Width} height={size.Height}");
        }

        /// <summary>
        /// 设置是否阻挡屏幕角落
        /// </summary>
        public void SetBlockCorners(bool block)
        {
            _blockCorners = block;
        }

        /// <summary>
        /// 处理鼠标移动事件
        /// </summary>
        public void OnMouseMove(Point position, Rectangle screenBounds)
        {
            _currentScreenBounds = screenBounds;
            var now = DateTime.UtcNow;

            // 检查是否在冷却期
            if (now < _lockUntil)
            {
                CheckRearm(position);
                _lastPosition = position;
                _lastMoveTime = now;
                return;
            }

            // 检查是否已武装
            if (!_armed)
            {
                CheckRearm(position);
                _lastPosition = position;
                _lastMoveTime = now;
                return;
            }

            // 检测是否到达边缘
            if (IsAtEdge(position, screenBounds, out var edge, out var entryPoint))
            {
                // 检查是否在角落阻挡区域
                if (_blockCorners && IsInCorner(position, screenBounds))
                {
                    AppLog.Debug("edge.blocked.corner", $"x={position.X} y={position.Y}");
                    _lastPosition = position;
                    _lastMoveTime = now;
                    return;
                }

                // 检查移动方向是否朝向边缘
                if (!IsMovingTowardEdge(position, edge))
                {
                    _lastPosition = position;
                    _lastMoveTime = now;
                    return;
                }

                // 触发边缘到达事件
                AppLog.Info("edge.reached",
                    $"edge={edge} x={position.X} y={position.Y} direction={_configuredDirection}");

                EdgeReached?.Invoke(this, new EdgeReachedEventArgs
                {
                    Direction = _configuredDirection,
                    Edge = edge,
                    EntryPoint = entryPoint,
                    RemoteScreenSize = _remoteScreenSize
                });

                // 锁定并解除武装
                _armed = false;
                _lockUntil = now.AddMilliseconds(CooldownMs);
            }

            _lastPosition = position;
            _lastMoveTime = now;
        }

        /// <summary>
        /// 重置武装状态（例如控制权返回时）
        /// </summary>
        public void ResetArming()
        {
            _armed = false;
            _lockUntil = DateTime.UtcNow.AddMilliseconds(CooldownMs);
            AppLog.Debug("edge.arm.reset", "reason=control_returned");
        }

        /// <summary>
        /// 强制武装
        /// </summary>
        public void ForceArm()
        {
            _armed = true;
            _lockUntil = DateTime.MinValue;
            AppLog.Debug("edge.arm.forced", string.Empty);
        }

        private bool IsAtEdge(Point position, Rectangle bounds, out string edge, out Point entryPoint)
        {
            edge = null;
            entryPoint = position;

            switch (_configuredDirection)
            {
                case "Right":
                    if (position.X >= bounds.Right - EdgeThresholdPixels)
                    {
                        edge = "Right";
                        entryPoint = new Point(bounds.Right, position.Y);
                        return true;
                    }
                    break;

                case "Left":
                    if (position.X <= bounds.Left + EdgeThresholdPixels)
                    {
                        edge = "Left";
                        entryPoint = new Point(bounds.Left, position.Y);
                        return true;
                    }
                    break;

                case "Up":
                    if (position.Y <= bounds.Top + EdgeThresholdPixels)
                    {
                        edge = "Top";
                        entryPoint = new Point(position.X, bounds.Top);
                        return true;
                    }
                    break;

                case "Down":
                    if (position.Y >= bounds.Bottom - EdgeThresholdPixels)
                    {
                        edge = "Bottom";
                        entryPoint = new Point(position.X, bounds.Bottom);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private bool IsInCorner(Point position, Rectangle bounds)
        {
            var inLeftCorner = position.X < bounds.Left + CornerBlockPixels;
            var inRightCorner = position.X > bounds.Right - CornerBlockPixels;
            var inTopCorner = position.Y < bounds.Top + CornerBlockPixels;
            var inBottomCorner = position.Y > bounds.Bottom - CornerBlockPixels;

            return (inLeftCorner || inRightCorner) && (inTopCorner || inBottomCorner);
        }

        private bool IsMovingTowardEdge(Point current, string edge)
        {
            if (_lastPosition == Point.Empty)
                return true;

            var dx = current.X - _lastPosition.X;
            var dy = current.Y - _lastPosition.Y;

            switch (edge)
            {
                case "Right":
                    return dx > 0;
                case "Left":
                    return dx < 0;
                case "Top":
                    return dy < 0;
                case "Bottom":
                    return dy > 0;
                default:
                    return true;
            }
        }

        private void CheckRearm(Point position)
        {
            if (_armed) return;

            var distanceFromEdge = GetDistanceFromEdge(position, _currentScreenBounds, _configuredDirection);

            if (distanceFromEdge >= ArmDistancePixels)
            {
                _armed = true;
                AppLog.Debug("edge.arm.rearm", $"distance={distanceFromEdge}");
            }
        }

        private int GetDistanceFromEdge(Point position, Rectangle bounds, string direction)
        {
            switch (direction)
            {
                case "Right":
                    return bounds.Right - position.X;
                case "Left":
                    return position.X - bounds.Left;
                case "Up":
                    return position.Y - bounds.Top;
                case "Down":
                    return bounds.Bottom - position.Y;
                default:
                    return 0;
            }
        }

        public void Dispose()
        {
            // 清理资源
        }
    }

    public class EdgeReachedEventArgs : EventArgs
    {
        public string Direction { get; set; }
        public string Edge { get; set; }
        public Point EntryPoint { get; set; }
        public Size RemoteScreenSize { get; set; }
    }
}
