using System;
using System.Collections.Generic;
using System.Drawing;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 自适应边缘返回检测器 - 改进版
    /// 解决问题：
    /// 1. 单次大位移触发返回（现在使用滑动窗口和样本限制）
    /// 2. 鼠标约 2 秒弹回（现在使用更大阈值和宽限期）
    /// 3. 方向不敏感（现在只计算朝向边缘的移动）
    /// </summary>
    public sealed class AdaptiveEdgeReturnDetector
    {
        // 配置参数
        private const int ThresholdPixels = 90;              // 累计阈值（从 12px 提升到 90px）
        private const int MaxSamplePixels = 60;              // 单个样本最大贡献
        private const int MinSamples = 2;                    // 最少样本数
        private const int WindowMs = 300;                    // 滑动窗口时间
        private const int ArmDistancePixels = 120;           // 重新武装距离（从 12px 提升到 120px）
        private const int GracePeriodMs = 1500;              // 宽限期（刚拿到控制权后不响应返回请求）

        private readonly List<Sample> _samples;
        private string _returnDirection;  // 对于被控端来说，返回方向是对端所在的方向
        private Size _localScreenSize;
        private bool _armed;
        private DateTime _sessionStartTime;
        private DateTime _lastArmCheck;

        public AdaptiveEdgeReturnDetector()
        {
            _samples = new List<Sample>();
            _armed = false;
        }

        /// <summary>
        /// 开始新的控制会话
        /// </summary>
        public void BeginSession(string peerDirection, Size localScreenSize)
        {
            _returnDirection = peerDirection;
            _localScreenSize = localScreenSize;
            _sessionStartTime = DateTime.UtcNow;
            _armed = false;
            _samples.Clear();

            AppLog.Debug("edge.return.session.begin",
                $"returnDirection={peerDirection} gracePeriod={GracePeriodMs}ms");
        }

        /// <summary>
        /// 更新鼠标位置并检测是否应该返回
        /// </summary>
        public bool Update(Point current, Point previous, out EdgePushReason reason)
        {
            reason = EdgePushReason.None;

            // 检查是否在宽限期内
            if (!_armed)
            {
                CheckRearm(current);
                return false;
            }

            var now = DateTime.UtcNow;

            // 计算朝向返回边缘的位移
            var push = CalculatePushTowardEdge(current, previous, _returnDirection);

            if (push > 0)
            {
                // 限制单个样本的贡献
                push = Math.Min(push, MaxSamplePixels);

                _samples.Add(new Sample
                {
                    Timestamp = now,
                    Push = push
                });

                AppLog.Debug("edge.return.sample",
                    $"push={push} total={_samples.Count}");
            }

            // 清理过期样本（超出滑动窗口）
            _samples.RemoveAll(s => (now - s.Timestamp).TotalMilliseconds > WindowMs);

            // 检查是否达到阈值
            if (_samples.Count >= MinSamples)
            {
                var totalPush = 0;
                foreach (var sample in _samples)
                {
                    totalPush += sample.Push;
                }

                if (totalPush >= ThresholdPixels)
                {
                    reason = EdgePushReason.EdgePush;
                    _samples.Clear();
                    _armed = false;

                    AppLog.Info("edge.return.triggered",
                        $"totalPush={totalPush} samples={_samples.Count} direction={_returnDirection}");

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 重置检测器
        /// </summary>
        public void Reset()
        {
            _samples.Clear();
            _armed = false;
            AppLog.Debug("edge.return.reset", string.Empty);
        }

        /// <summary>
        /// 强制武装
        /// </summary>
        public void ForceArm()
        {
            _armed = true;
            AppLog.Debug("edge.return.force_arm", string.Empty);
        }

        private int CalculatePushTowardEdge(Point current, Point previous, string direction)
        {
            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;

            switch (direction)
            {
                case "Right":
                    return dx > 0 ? dx : 0;  // 向右推
                case "Left":
                    return dx < 0 ? -dx : 0;  // 向左推
                case "Up":
                    return dy < 0 ? -dy : 0;  // 向上推
                case "Down":
                    return dy > 0 ? dy : 0;  // 向下推
                default:
                    return 0;
            }
        }

        private void CheckRearm(Point current)
        {
            var now = DateTime.UtcNow;

            // 检查宽限期
            if ((now - _sessionStartTime).TotalMilliseconds < GracePeriodMs)
            {
                return;
            }

            // 检查是否离开边缘足够远
            var distanceFromEdge = GetDistanceFromEdge(current, _localScreenSize, _returnDirection);

            if (distanceFromEdge >= ArmDistancePixels)
            {
                _armed = true;
                AppLog.Debug("edge.return.armed",
                    $"distance={distanceFromEdge} gracePeriod={GracePeriodMs}ms");
            }
        }

        private int GetDistanceFromEdge(Point position, Size screenSize, string direction)
        {
            switch (direction)
            {
                case "Right":
                    return screenSize.Width - position.X;
                case "Left":
                    return position.X;
                case "Up":
                    return position.Y;
                case "Down":
                    return screenSize.Height - position.Y;
                default:
                    return 0;
            }
        }

        private struct Sample
        {
            public DateTime Timestamp;
            public int Push;
        }
    }

    public enum EdgePushReason
    {
        None = 0,
        EdgePush = 1,
        UserRequest = 2
    }
}
