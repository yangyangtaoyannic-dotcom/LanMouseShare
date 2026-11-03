using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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

        /// <summary>
        /// fix8：文本/图片的发送实现。空值时走旧的 Service 命令通道；
        /// 设置后由 FileLink 直接发给对端 Agent。返回 false 表示这次没能发出。
        /// </summary>
        public Func<ClipboardPayload, bool> Transport { get; set; }

        private readonly ClipboardMonitor _monitor = new ClipboardMonitor();

        // 0.2.0-beta-fix16：整个类的剪贴板 I/O 只在 ClipboardWorker 那条 STA 线程上跑。
        // _pending*/_lastSent* 因此也只在 worker 线程上读写，不需要锁。
        private ClipboardPayload _pendingPayload;
        private string _pendingSignature;
        private string _lastSentSignature;
        private DateTime _lastSentUtc = DateTime.MinValue;
        private string _lastRemoteSignature;
        private DateTime _lastRemoteUtc = DateTime.MinValue;
        // 抑制窗口跨越 UI 线程 / 剪贴板线程，用 ticks + Interlocked 读写，避免撕裂或过期值。
        private long _suppressUntilTicks = DateTime.MinValue.Ticks;
        private long _version;
        private volatile bool _applyingRemote;
        private bool _sendInFlight;
        private volatile bool _localHasFiles;
        private bool _debouncePending;
        private const int SendDebounceMs = 250;
        private const int MaxApplyAttempts = 3;
        private readonly List<string> _remoteFilePaths = new List<string>();
        private readonly List<string> _localFilePaths = new List<string>();
        private DateTime _remoteFilesExpireUtc = DateTime.MinValue;

        public event EventHandler<FileClipboardDetectedEventArgs> FileClipboardAnnounced;

        /// <summary>Raised when the peer announced a file clipboard; the agent pre-pulls it.</summary>
        public event EventHandler<FileClipboardDetectedEventArgs> RemoteFilesPending;

        public ClipboardSyncService(PipeClient pipe, Func<PeerStateDto> getState, Func<bool> isEnabled)
        {
            _pipe = pipe;
            _getState = getState;
            _isEnabled = isEnabled;
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
            AppLog.Event("INFO", "clipboard.remote.received", Describe(payload) + " origin=" + (payload.OriginDeviceId ?? "-"));
            if (payload.Format == ClipboardFormats.Files)
            {
                // Lazy clipboard: only the file list is announced. Nothing is transferred and
                // nothing is put on this machine's clipboard until the user actually pastes.
                var paths = payload.FilePaths ?? new string[0];
                lock (_remoteFilePaths)
                {
                    _remoteFilePaths.Clear();
                    for (int i = 0; i < paths.Length && i < 32; i++)
                    {
                        if (!string.IsNullOrEmpty(paths[i]))
                        {
                            _remoteFilePaths.Add(paths[i]);
                        }
                    }

                    _remoteFilesExpireUtc = DateTime.UtcNow.AddMinutes(30);
                }

                AppLog.Event("INFO", "clipboard.files.pending",
                    "count=" + paths.Length + " origin=" + (payload.OriginDeviceId ?? "-") +
                    " lazy=" + (payload.IsLazy ? 1 : 0));

                // Announcement arrived: fetch the files in the background and put them on this
                // machine's clipboard. Pasting then works from any path (local keyboard, injected
                // keys, app menus) instead of depending on intercepting one specific key event.
                var handler = RemoteFilesPending;
                if (handler != null)
                {
                    try
                    {
                        handler(this, new FileClipboardDetectedEventArgs(paths, payload.FileSizes));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("RemoteFilesPending handler failed. " + ex.Message);
                    }
                }

                return;
            }

            // 写剪贴板是阻塞型 OLE 往返，绝不能落在 UI / 钩子线程上（一次就能卡住整机鼠标）。
            ClipboardWorker.Post(delegate { ApplyOnWorker(payload, signature, 0); });
        }

        /// <summary>
        /// Publishes the peer's files as delayed-rendering clipboard entries: the names appear on
        /// this machine immediately, and <paramref name="fetch"/> is only called when an
        /// application actually reads a file (that is, at paste time).
        /// </summary>
        public bool PublishVirtualFiles(string[] paths, long[] sizes, Func<string, string> fetch)
        {
            if (!VirtualClipboardEnabled)
            {
                return false;
            }

            if (paths == null || paths.Length == 0 || fetch == null)
            {
                return false;
            }

            var entries = new VirtualFileEntry[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                entries[i] = new VirtualFileEntry
                {
                    FileName = string.IsNullOrEmpty(path) ? ("file-" + i) : System.IO.Path.GetFileName(path.TrimEnd('\\', '/')),
                    Length = sizes != null && i < sizes.Length ? sizes[i] : 0,
                    LastWriteUtcTicks = DateTime.UtcNow.Ticks,
                    FetchLocalPath = delegate { return fetch(path); }
                };
            }

            return VirtualFileClipboard.Publish(entries);
        }
        /// <summary>
        /// 0.2.0-beta-fix4：重新启用延迟渲染虚拟剪贴板。对端复制文件后本机
        /// 剪贴板立即出现文件名（右键"粘贴"不再灰），字节在粘贴时才拉取。
        /// VirtualFileClipboard 已加固：MTA 托管（回调不卡 UI）、FileContents
        /// 惰性 IStream（OleFlushClipboard 不触发整批渲染）、发布重试。
        /// </summary>
        private const bool VirtualClipboardEnabled = true;

        /// <summary>True when the peer announced files and the user has not pasted them yet.</summary>
        public bool HasPendingRemoteFiles
        {
            get
            {
                lock (_remoteFilePaths)
                {
                    if (_remoteFilePaths.Count == 0)
                    {
                        return false;
                    }

                    if (DateTime.UtcNow > _remoteFilesExpireUtc)
                    {
                        _remoteFilePaths.Clear();
                        return false;
                    }

                    return true;
                }
            }
        }

        public int PendingRemoteFileCount
        {
            get { lock (_remoteFilePaths) { return _remoteFilePaths.Count; } }
        }

        public string[] PendingRemoteFiles
        {
            get { lock (_remoteFilePaths) { return _remoteFilePaths.ToArray(); } }
        }

        /// <summary>
        /// Registers the file list announced over the low-latency channel (0.1.55). The TCP
        /// clipboard envelope carries the same list later and simply overwrites it.
        /// </summary>
        public void RegisterRemoteFiles(string[] paths, string origin)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            lock (_remoteFilePaths)
            {
                _remoteFilePaths.Clear();
                for (int i = 0; i < paths.Length && i < 32; i++)
                {
                    if (!string.IsNullOrEmpty(paths[i]))
                    {
                        _remoteFilePaths.Add(paths[i]);
                    }
                }

                _remoteFilesExpireUtc = DateTime.UtcNow.AddMinutes(30);
            }

            AppLog.Event("INFO", "clipboard.files.announced",
                "count=" + paths.Length + " origin=" + (origin ?? "-") + " path=udp");
            AppLog.Debug("clipboard.files.pending-status", "count=" + paths.Length);
        }
        /// <summary>True while the local clipboard holds a file list (CF_HDROP).</summary>
        public bool LocalHasFiles
        {
            get { return _localHasFiles; }
            private set { _localHasFiles = value; }
        }

        /// <summary>
        /// Re-reads the local clipboard when Ctrl+V is forwarded. The cached flag can lag behind
        /// (the clipboard monitor is debounced), and a stale false here means the paste request
        /// never reaches the peer, which looks exactly like "copy on A, paste on B does nothing".
        ///
        /// 0.2.0-beta-fix16：这里原来直接调 Clipboard.ContainsFileDropList()，而调用方是
        /// 钩子线程（Ctrl+V 转发）—— 一次跨进程 OLE 往返就把整机鼠标卡住。现在原地只回
        /// 缓存值，真实重读投递给剪贴板线程，下一次粘贴看到新结果。
        /// </summary>
        public bool RefreshLocalHasFiles()
        {
            if (ClipboardWorker.IsOnWorkerThread)
            {
                ReadLocalHasFiles();
                return _localHasFiles;
            }

            ClipboardWorker.Post(delegate { ReadLocalHasFiles(); });
            return _localHasFiles;
        }

        private void ReadLocalHasFiles()
        {
            try
            {
                _localHasFiles = Clipboard.ContainsFileDropList();
            }
            catch (Exception)
            {
                // Clipboard busy: keep the previous answer instead of dropping the paste.
            }
        }

        /// <summary>The local file list, used to answer the peer's paste request directly.</summary>
        public string[] LocalFilePaths
        {
            get
            {
                lock (_localFilePaths)
                {
                    return _localFilePaths.ToArray();
                }
            }
        }

        public void ClearPendingRemoteFiles()
        {
            lock (_remoteFilePaths)
            {
                _remoteFilePaths.Clear();
                _remoteFilesExpireUtc = DateTime.MinValue;
            }
        }

        /// <summary>Puts already transferred local files on the clipboard so the app can paste them.</summary>
        public bool PutFilesOnClipboard(string[] localPaths)
        {
            if (localPaths == null || localPaths.Length == 0)
            {
                return false;
            }

            var collection = new StringCollection();
            var added = 0;
            for (int i = 0; i < localPaths.Length; i++)
            {
                if (!string.IsNullOrEmpty(localPaths[i]) && (File.Exists(localPaths[i]) || Directory.Exists(localPaths[i])))
                {
                    collection.Add(localPaths[i]);
                    added++;
                }
            }

            if (added == 0)
            {
                return false;
            }

            try
            {
                var started = Environment.TickCount;
                _applyingRemote = true;
                SuppressFor(SuppressDuration);
                Clipboard.SetFileDropList(collection);
                // Render the data now: without a flush the file list disappears as soon as the
                // writing apartment thread exits, which looks exactly like "clipboard not updated".
                Clipboard.Flush();
                LocalHasFiles = true;
                AppLog.Event("INFO", "clipboard.files.applied",
                    "count=" + added + " first=" + localPaths[0] +
                    " cost=" + unchecked(Environment.TickCount - started) + "ms");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Put files on clipboard failed.", ex);
                return false;
            }
            finally
            {
                _applyingRemote = false;
            }
        }
        /// <summary>
        /// Writes the file list on the shared clipboard thread (<see cref="ClipboardWorker"/>).
        /// Clipboard.SetFileDropList blocks while another process holds the clipboard, which used
        /// to freeze the UI thread and stall the low level input hook.
        /// </summary>
        public void PutFilesOnClipboardAsync(string[] localPaths, Action<bool> completed)
        {
            ClipboardWorker.Post(delegate
            {
                var ok = false;
                try
                {
                    ok = PutFilesOnClipboard(localPaths);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Clipboard write failed.", ex);
                }

                if (completed != null)
                {
                    try
                    {
                        completed(ok);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Clipboard write callback failed. " + ex.Message);
                    }
                }
            });
        }

        private void OnClipboardChanged(object sender, EventArgs e)
        {
            if (_applyingRemote || IsSuppressed() || !_isEnabled())
            {
                return;
            }

            var state = _getState();
            if (state == null || !state.IsConnected || !state.IsTrusted)
            {
                return;
            }

            // WM_CLIPBOARDUPDATE 的 WndProc 跑在 UI 线程上；读剪贴板（图片分支还要做 PNG
            // 编码）是阻塞型 OLE 调用，全部搬到剪贴板线程，UI 线程只做一次投递。
            ClipboardWorker.Post(delegate { ReadLocalClipboard(state); });
        }

        private void ReadLocalClipboard(PeerStateDto state)
        {
            if (_applyingRemote || IsSuppressed() || !_isEnabled())
            {
                return;
            }

            var started = Environment.TickCount;
            try
            {
                ClipboardPayload payload = null;

                // 优先检测文本和图片，避免文件拖拽时误检测
                if (Clipboard.ContainsText())
                {
                    // 确保不是文件路径列表（有些应用复制文件时也会设置文本）
                    var text = Clipboard.GetText();
                    var hasFiles = Clipboard.ContainsFileDropList();
                    if (!hasFiles || text.Length < 260)  // MAX_PATH
                    {
                        payload = new ClipboardPayload
                        {
                            OriginDeviceId = state.LocalDeviceId,
                            Version = Interlocked.Increment(ref _version),
                            Format = ClipboardFormats.Text,
                            Text = text
                        };
                    }
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
                        Version = Interlocked.Increment(ref _version),
                        Format = ClipboardFormats.ImagePng,
                        ImagePngBytes = bytes
                    };
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    // fix10：按用户要求彻底停用"复制文件 → 对端粘贴"这条链路。
                    // 现在传文件只用资源管理器右键"传输到对端"（Agent 收到 --send 后直传）。
                    // 这里必须主动忽略，否则会公告文件清单、写虚拟剪贴板，对端粘贴时
                    // 拿到的是不存在的对端路径，表现为"粘贴没反应"。
                    AppLog.Debug("clipboard.files.ignored", "reason=disabled-fix10");
                    return;
                }

                LocalHasFiles = payload != null && payload.Format == ClipboardFormats.Files;
                if (!LocalHasFiles)
                {
                    lock (_localFilePaths)
                    {
                        _localFilePaths.Clear();
                    }
                }

                _clipboardRetryCount = 0;
                if (payload != null)
                {
                    AppLog.Event("INFO", "clipboard.local.detected",
                        Describe(payload) + " peer=" + state.PeerDeviceName +
                        " cost=" + unchecked(Environment.TickCount - started) + "ms");
                    QueueSend(payload);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Read clipboard failed; retry scheduled. " + ex.Message);
                ScheduleClipboardRetry();
            }
        }

        private int _clipboardRetryCount;

        private void ScheduleClipboardRetry()
        {
            if (_clipboardRetryCount >= 6)
            {
                _clipboardRetryCount = 0;
                AppLog.Warn("Clipboard read gave up after retries.");
                return;
            }

            _clipboardRetryCount++;
            var attempt = _clipboardRetryCount;
            // 原来是 Thread.Sleep + Dispatcher.BeginInvoke；现在直接投递到剪贴板线程。
            ClipboardWorker.PostDelayed(180 * attempt, delegate
            {
                OnClipboardChanged(this, EventArgs.Empty);
            });
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

            // 去抖计时器原来建在 UI 线程上（DispatcherTimer）；现在统一由剪贴板线程延迟投递，
            // 单飞标志保证同时只有一个待触发的去抖任务。
            if (_debouncePending)
            {
                return;
            }

            _debouncePending = true;
            ClipboardWorker.PostDelayed(SendDebounceMs, OnDebounceTick);
        }

        private void OnDebounceTick()
        {
            _debouncePending = false;
            if (_sendInFlight)
            {
                _debouncePending = true;
                ClipboardWorker.PostDelayed(SendDebounceMs, OnDebounceTick);
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
            AppLog.Event("INFO", "clipboard.send", Describe(payload));
            // fix8：外部注入的传输实现（Agent↔Agent 的 FileLink 通道）优先；
            // 没有注入时退回旧的 Service 命令通道。
            var transport = Transport;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (transport != null)
                    {
                        transport(payload);
                    }
                    else
                    {
                        _pipe.Send(AgentCommands.SendClipboard, json);
                    }
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

        /// <summary>
        /// 在剪贴板线程上把远端内容写进本机剪贴板（只在 worker 线程调用）。
        /// 重试上限从 5 次收敛到 <see cref="MaxApplyAttempts"/>，间隔也更短：写入失败时
        /// 用户要的是"别卡"，而不是无限期地跟占用剪贴板的进程抢。
        /// </summary>
        private void ApplyOnWorker(ClipboardPayload payload, string signature, int attempt)
        {
            var started = Environment.TickCount;
            try
            {
                _applyingRemote = true;
                SuppressFor(SuppressDuration);
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
                LocalHasFiles = payload != null && payload.Format == ClipboardFormats.Files;
                AppLog.Event("INFO", "clipboard.remote.applied",
                    Describe(payload) + " attempt=" + attempt +
                    " cost=" + unchecked(Environment.TickCount - started) + "ms");
            }
            catch (Exception ex)
            {
                var next = attempt + 1;
                if (next >= MaxApplyAttempts)
                {
                    AppLog.Error("Apply remote clipboard failed after " + next + " attempts.", ex);
                    return;
                }

                var delay = Math.Min(800, 90 * next);
                AppLog.Warn("clipboard.remote.apply.retry attempt=" + next +
                    " delay=" + delay + "ms " + ex.Message);
                ClipboardWorker.PostDelayed(delay, delegate { ApplyOnWorker(payload, signature, next); });
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void SuppressFor(TimeSpan duration)
        {
            Interlocked.Exchange(ref _suppressUntilTicks, DateTime.UtcNow.Add(duration).Ticks);
        }

        private bool IsSuppressed()
        {
            return DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks);
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

        private static string Describe(ClipboardPayload payload)
        {
            if (payload == null)
            {
                return "payload=none";
            }

            if (payload.Format == ClipboardFormats.Text)
            {
                var text = payload.Text ?? string.Empty;
                return "format=Text chars=" + text.Length + " version=" + payload.Version;
            }

            if (payload.Format == ClipboardFormats.ImagePng)
            {
                var bytes = payload.ImagePngBytes;
                return "format=Png bytes=" + (bytes != null ? bytes.Length : 0) + " version=" + payload.Version;
            }

            if (payload.Format == ClipboardFormats.Files)
            {
                var files = payload.FilePaths;
                return "format=Files count=" + (files != null ? files.Length : 0) + " version=" + payload.Version;
            }

            return "format=" + (payload.Format ?? "-") + " version=" + payload.Version;
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

            if (payload.Format == ClipboardFormats.Files && payload.FilePaths != null)
            {
                return "Files:" + payload.FilePaths.Length + ":" + StableHash(string.Join("|", payload.FilePaths)).ToString("X8");
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
            _monitor.Dispose();
        }
    }

    internal sealed class FileClipboardDetectedEventArgs : EventArgs
    {
        public FileClipboardDetectedEventArgs(string[] filePaths)
            : this(filePaths, null)
        {
        }

        public FileClipboardDetectedEventArgs(string[] filePaths, long[] fileSizes)
        {
            FilePaths = filePaths;
            FileSizes = fileSizes;
        }

        public long[] FileSizes { get; private set; }

        public string[] FilePaths { get; private set; }
    }
}
