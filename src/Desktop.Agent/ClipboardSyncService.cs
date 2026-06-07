using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class ClipboardSyncService : IDisposable
    {
        private const int MaxImageBytes = 10 * 1024 * 1024;
        private static readonly TimeSpan SuppressDuration = TimeSpan.FromSeconds(2);

        private readonly PipeClient _pipe;
        private readonly Func<PeerStateDto> _getState;
        private readonly Func<bool> _isEnabled;
        private readonly Dispatcher _dispatcher;
        private readonly ClipboardMonitor _monitor = new ClipboardMonitor();
        private readonly DispatcherTimer _debounceTimer;
        private ClipboardPayload _pendingPayload;
        private string _pendingSignature;
        private string _lastSentSignature;
        private DateTime _lastSentUtc = DateTime.MinValue;
        private string _lastRemoteSignature;
        private DateTime _lastRemoteUtc = DateTime.MinValue;
        private DateTime _suppressUntilUtc = DateTime.MinValue;
        private long _version;
        private bool _applyingRemote;
        private bool _sendInFlight;

        public ClipboardSyncService(PipeClient pipe, Func<PeerStateDto> getState, Func<bool> isEnabled, Dispatcher dispatcher)
        {
            _pipe = pipe;
            _getState = getState;
            _isEnabled = isEnabled;
            _dispatcher = dispatcher;
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _debounceTimer.Tick += OnDebounceTick;
        }

        public void Attach(Window window)
        {
            _monitor.Attach(window);
            _monitor.ClipboardChanged += OnClipboardChanged;
        }

        public void ApplyRemote(ClipboardPayload payload)
        {
            if (payload == null || !_isEnabled())
            {
                return;
            }

            var state = _getState();
            if (state != null && payload.OriginDeviceId == state.LocalDeviceId)
            {
                return;
            }

            var signature = BuildSignature(payload);
            var now = DateTime.UtcNow;
            if (signature == _lastRemoteSignature && (now - _lastRemoteUtc).TotalMilliseconds < 2000)
            {
                return;
            }

            _lastRemoteSignature = signature;
            _lastRemoteUtc = now;
            _dispatcher.BeginInvoke(new Action(delegate { TryApply(payload, signature, 0); }));
        }

        private void OnClipboardChanged(object sender, EventArgs e)
        {
            if (_applyingRemote || DateTime.UtcNow < _suppressUntilUtc || !_isEnabled())
            {
                return;
            }

            var state = _getState();
            if (state == null || !state.IsConnected || !state.IsTrusted)
            {
                return;
            }

            try
            {
                ClipboardPayload payload = null;
                if (Clipboard.ContainsText())
                {
                    payload = new ClipboardPayload
                    {
                        OriginDeviceId = state.LocalDeviceId,
                        Version = ++_version,
                        Format = ClipboardFormats.Text,
                        Text = Clipboard.GetText()
                    };
                }
                else if (Clipboard.ContainsImage())
                {
                    var bytes = EncodeImage(Clipboard.GetImage());
                    if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
                    {
                        AppLog.Warn("Clipboard image ignored. Bytes=" + (bytes != null ? bytes.Length : 0));
                        return;
                    }

                    payload = new ClipboardPayload
                    {
                        OriginDeviceId = state.LocalDeviceId,
                        Version = ++_version,
                        Format = ClipboardFormats.ImagePng,
                        ImagePngBytes = bytes
                    };
                }

                if (payload != null)
                {
                    QueueSend(payload);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Read clipboard failed.", ex);
            }
        }

        private void QueueSend(ClipboardPayload payload)
        {
            var signature = BuildSignature(payload);
            var now = DateTime.UtcNow;
            if (signature == _pendingSignature ||
                (signature == _lastSentSignature && (now - _lastSentUtc).TotalMilliseconds < 2000))
            {
                return;
            }

            _pendingPayload = payload;
            _pendingSignature = signature;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            if (_sendInFlight)
            {
                _debounceTimer.Start();
                return;
            }

            var payload = _pendingPayload;
            var signature = _pendingSignature;
            _pendingPayload = null;
            _pendingSignature = null;
            if (payload == null)
            {
                return;
            }

            _lastSentSignature = signature;
            _lastSentUtc = DateTime.UtcNow;
            _sendInFlight = true;
            var json = ProtocolSerializer.ToJson(payload);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.SendClipboard, json);
                }
                catch (Exception ex)
                {
                    AppLog.Error("SendClipboard failed.", ex);
                }
                finally
                {
                    _sendInFlight = false;
                }
            });
        }

        private void TryApply(ClipboardPayload payload, string signature, int attempt)
        {
            try
            {
                _applyingRemote = true;
                _suppressUntilUtc = DateTime.UtcNow.Add(SuppressDuration);
                if (payload.Format == ClipboardFormats.Text)
                {
                    Clipboard.SetText(payload.Text ?? string.Empty);
                }
                else if (payload.Format == ClipboardFormats.ImagePng && payload.ImagePngBytes != null)
                {
                    Clipboard.SetImage(DecodeImage(payload.ImagePngBytes));
                }

                _lastSentSignature = signature;
                _lastSentUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                if (attempt >= 5)
                {
                    AppLog.Error("Apply remote clipboard failed.", ex);
                    return;
                }

                var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
                timer.Interval = TimeSpan.FromMilliseconds(Math.Min(1000, 150 * (attempt + 1)));
                timer.Tick += delegate
                {
                    timer.Stop();
                    TryApply(payload, signature, attempt + 1);
                };
                timer.Start();
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static byte[] EncodeImage(BitmapSource image)
        {
            if (image == null)
            {
                return null;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static BitmapSource DecodeImage(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private static string BuildSignature(ClipboardPayload payload)
        {
            if (payload == null)
            {
                return string.Empty;
            }

            if (payload.Format == ClipboardFormats.Text)
            {
                var text = payload.Text ?? string.Empty;
                return "Text:" + text.Length + ":" + StableHash(text).ToString("X8");
            }

            if (payload.Format == ClipboardFormats.ImagePng && payload.ImagePngBytes != null)
            {
                return "Image:" + payload.ImagePngBytes.Length + ":" + StableHash(payload.ImagePngBytes).ToString("X8");
            }

            return payload.Format ?? string.Empty;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 23;
                for (int i = 0; i < value.Length; i++)
                {
                    hash = hash * 31 + value[i];
                }

                return hash;
            }
        }

        private static int StableHash(byte[] value)
        {
            unchecked
            {
                var hash = 23;
                for (int i = 0; i < value.Length; i++)
                {
                    hash = hash * 31 + value[i];
                }

                return hash;
            }
        }

        public void Dispose()
        {
            _debounceTimer.Stop();
            _monitor.Dispose();
        }
    }
}
