using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class RemoteInputSender : IDisposable
    {
        private readonly PipeClient _pipe;
        private readonly DirectInputChannel _directInput;
        private readonly object _sync = new object();
        private long _framesSent;
        private long _bytesSent;
        private long _udpSends;
        private long _relaySends;
        private long _udpFailures;
        private DateTime _lastUdpFailureLogUtc = DateTime.MinValue;
        private long _rateWindowFrames;
        private long _rateWindowBytes;
        private DateTime _rateWindowStartUtc = DateTime.UtcNow;
        private double _framesPerSecond;
        private double _bytesPerSecond;
        private readonly Queue<InputPayload> _queue = new Queue<InputPayload>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _worker;
        private volatile bool _running = true;
        private InputPayload _latestMouseMove;
        private int _pendingRelativeX;
        private int _pendingRelativeY;
        private DateTime _pendingRelativeStartUtc = DateTime.MinValue;
        private long _droppedBacklogEvents;
        private int _maxRelativeStep = 160;
        private int _mouseSendIntervalMs = 5;
        private DateTime _lastMouseMoveSentUtc = DateTime.MinValue;
        private DateTime _backoffUntilUtc = DateTime.MinValue;

        public event EventHandler SendFailed;

        public RemoteInputSender(PipeClient pipe, DirectInputChannel directInput)
        {
            _pipe = pipe;
            _directInput = directInput;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "LanMouseShare remote input sender";
            _worker.Start();
            AppLog.Info("RemoteInputSender started.");
        }

        public void SetMouseUpdateRate(int rateHz)
        {
            if (rateHz <= 0)
            {
                rateHz = 200;
            }

            if (rateHz < 60)
            {
                rateHz = 60;
            }

            if (rateHz > 250)
            {
                rateHz = 250;
            }

            var interval = Math.Max(1, (int)Math.Round(1000.0 / rateHz));
            if (_mouseSendIntervalMs != interval)
            {
                _mouseSendIntervalMs = interval;
                AppLog.Info("Remote input rate set. Hz=" + rateHz + " IntervalMs=" + _mouseSendIntervalMs);
            }
        }

        public void SendCommand(string command, string payloadJson)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(command, payloadJson);
                }
                catch (Exception ex)
                {
                    AppLog.Error("SendCommand failed. Command=" + command, ex);
                }
            });
        }

        public bool Enqueue(InputPayload payload)
        {
            if (payload == null)
            {
                return false;
            }

            if (DateTime.UtcNow < _backoffUntilUtc)
            {
                return false;
            }

            lock (_sync)
            {
                if (payload.Kind == InputKinds.MouseMove)
                {
                    _latestMouseMove = payload;
                }
                else if (payload.Kind == InputKinds.MouseMoveRelative)
                {
                    if (_pendingRelativeX == 0 && _pendingRelativeY == 0)
                    {
                        _pendingRelativeStartUtc = DateTime.UtcNow;
                    }

                    _pendingRelativeX += payload.X;
                    _pendingRelativeY += payload.Y;
                    // Keep any single step bounded: a stalled link must never turn into a
                    // multi-second pointer jump when it recovers.
                    if (_pendingRelativeX > _maxRelativeStep) { _pendingRelativeX = _maxRelativeStep; }
                    if (_pendingRelativeX < -_maxRelativeStep) { _pendingRelativeX = -_maxRelativeStep; }
                    if (_pendingRelativeY > _maxRelativeStep) { _pendingRelativeY = _maxRelativeStep; }
                    if (_pendingRelativeY < -_maxRelativeStep) { _pendingRelativeY = -_maxRelativeStep; }
                }
                else
                {
                    if (_queue.Count >= 256)
                    {
                        _queue.Dequeue();
                    }

                    _queue.Enqueue(payload);
                }
            }

            _signal.Set();
            return true;
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                _signal.WaitOne(4);
                FlushOnce();
            }
        }

        private void FlushOnce()
        {
            InputPayload[] immediate;
            InputPayload mouseMove = null;
            InputPayload relativeMove = null;

            lock (_sync)
            {
                immediate = _queue.ToArray();
                _queue.Clear();

                var now = DateTime.UtcNow;
                var intervalMs = _mouseSendIntervalMs;
                if (_latestMouseMove != null && (now - _lastMouseMoveSentUtc).TotalMilliseconds >= intervalMs)
                {
                    mouseMove = _latestMouseMove;
                    _latestMouseMove = null;
                    _lastMouseMoveSentUtc = now;
                }

                if ((_pendingRelativeX != 0 || _pendingRelativeY != 0) &&
                    (now - _lastMouseMoveSentUtc).TotalMilliseconds >= intervalMs)
                {
                    // Movement that has been waiting too long is stale: sending it after a
                    // network stall is exactly the "nothing happens, then everything replays"
                    // effect. Drop it and let the pointer continue from where it is now.
                    if (_pendingRelativeStartUtc != DateTime.MinValue &&
                        (now - _pendingRelativeStartUtc).TotalMilliseconds > 150)
                    {
                        var staleMs = (int)(now - _pendingRelativeStartUtc).TotalMilliseconds;
                        _droppedBacklogEvents++;
                        _pendingRelativeX = 0;
                        _pendingRelativeY = 0;
                        _pendingRelativeStartUtc = DateTime.MinValue;
                        AppLog.Debug("input.drop.backlog",
                            "staleRelativeMs=" + staleMs +
                            " dropped=" + _droppedBacklogEvents);
                    }

                    if (_pendingRelativeX != 0 || _pendingRelativeY != 0)
                    {
                        relativeMove = new InputPayload
                        {
                            Kind = InputKinds.MouseMoveRelative,
                            X = _pendingRelativeX,
                            Y = _pendingRelativeY
                        };
                        _pendingRelativeX = 0;
                        _pendingRelativeY = 0;
                        _pendingRelativeStartUtc = DateTime.MinValue;
                        _lastMouseMoveSentUtc = now;
                    }
                }
            }

            var batch = new List<InputPayload>(immediate.Length + 1);
            for (int i = 0; i < immediate.Length; i++)
            {
                batch.Add(immediate[i]);
            }

            if (mouseMove != null)
            {
                batch.Add(mouseMove);
            }

            if (relativeMove != null)
            {
                batch.Add(relativeMove);
            }

            if (batch.Count > 0)
            {
                SendBatch(batch.ToArray());
            }
        }

        private void SendBatch(InputPayload[] payloads)
        {
            try
            {
                if (_directInput != null && _directInput.Send(payloads))
                {
                    _udpSends++;
                    RecordSent(payloads.Length, _directInput.LastFrameBytes);
                    return;
                }

                if (_directInput != null)
                {
                    _udpFailures++;
                    var now = DateTime.UtcNow;
                    if ((now - _lastUdpFailureLogUtc).TotalSeconds >= 5)
                    {
                        _lastUdpFailureLogUtc = now;
                        AppLog.Warn("input.udp.fail sends=" + _udpSends + " fails=" + _udpFailures +
                            " hasPeer=" + (_directInput.HasPeer ? "1" : "0") + " fallback=relay");
                    }
                }

                _relaySends++;
                var json = ProtocolSerializer.ToJson(new InputBatchPayload { Events = payloads });
                _pipe.Send(AgentCommands.SendInputBatch, json, PipeConstants.InputTimeoutMs);
                RecordSent(payloads.Length, Encoding.UTF8.GetByteCount(json));
            }
            catch (Exception ex)
            {
                AppLog.Error("Send input batch failed. Count=" + (payloads != null ? payloads.Length : 0), ex);
                _backoffUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
                lock (_sync)
                {
                    _queue.Clear();
                    _latestMouseMove = null;
                    _pendingRelativeX = 0;
                    _pendingRelativeY = 0;
                }

                var handler = SendFailed;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public string StatsText
        {
            get
            {
                return "input tx " + _framesPerSecond.ToString("F0") + " fps / " + (_bytesPerSecond / 1024.0).ToString("F1") + " KB/s";
            }
        }

        private void RecordSent(int eventCount, int frameBytes)
        {
            _framesSent++;
            _rateWindowFrames++;
            if (frameBytes > 0)
            {
                _bytesSent += frameBytes;
                _rateWindowBytes += frameBytes;
            }

            var elapsed = (DateTime.UtcNow - _rateWindowStartUtc).TotalMilliseconds;
            if (elapsed < 10000.0)
            {
                return;
            }

            _framesPerSecond = _rateWindowFrames * 1000.0 / elapsed;
            _bytesPerSecond = _rateWindowBytes * 1000.0 / elapsed;
            _rateWindowFrames = 0;
            _rateWindowBytes = 0;
            _rateWindowStartUtc = DateTime.UtcNow;
            AppLog.Event("INFO", "input.tx.rate",
                "fps=" + _framesPerSecond.ToString("F1") +
                " kbps=" + (_bytesPerSecond * 8.0 / 1024.0).ToString("F1") +
                " udp=" + _udpSends + " relay=" + _relaySends + " udpFail=" + _udpFailures +
                " frames=" + _framesSent + " bytes=" + _bytesSent);
        }

        public void Clear()
        {
            lock (_sync)
            {
                _queue.Clear();
                _latestMouseMove = null;
                _pendingRelativeX = 0;
                _pendingRelativeY = 0;
            }
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();
            AppLog.Info("RemoteInputSender disposed.");
        }
    }
}
