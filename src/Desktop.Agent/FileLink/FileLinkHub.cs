using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;

namespace LanMouseShare.Agent.FileLink
{
    internal sealed class FileLinkPushResult : EventArgs
    {
        public int OkCount;
        public int FailCount;
        public string Error;
        public string[] SourcePaths;
    }

    /// <summary>
    /// fix8 新数据通道的门面：Agent 只跟它打交道，不再碰 Service/PeerCoordinator 的文件与世界面。
    ///
    /// 一条经验教训写在这里：旧链路的失败几乎全是"状态耦合"造成的——暂停共享、控制租约、
    /// IPC 管道全部挤在同一条路径上，任何一处卡住文件就静默失败。新通道刻意做成无状态：
    /// 每次操作自带连接与握手，成不成都当场有结论，UI 能立刻显示。
    /// </summary>
    internal sealed class FileLinkHub : IDisposable
    {
        private readonly Func<PeerStateDto> _getState;
        private readonly Func<string> _inbox;
        private readonly object _offerLock = new object();
        private readonly List<FileLinkFile> _pendingOffers = new List<FileLinkFile>();
        private readonly List<FileLinkClient> _clients = new List<FileLinkClient>();
        private FileLinkServer _server;
        private volatile bool _started;

        public FileLinkHub(Func<PeerStateDto> getState, Func<string> inbox)
        {
            _getState = getState;
            _inbox = inbox;
        }

        public event EventHandler<FileLinkProgress> Progress;
        public event EventHandler<FileLinkIncomingEventArgs> OfferReceived;
        public event EventHandler<FileLinkIncomingEventArgs> FilesReceived;
        public event EventHandler<FileLinkClipboardEventArgs> ClipboardReceived;
        public event EventHandler<FileLinkPushResult> PushCompleted;

        public bool Started
        {
            get { return _started; }
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            _server = new FileLinkServer(_getState, _inbox);
            _server.Progress += delegate(object s, FileLinkProgress p) { RaiseProgress(p); };
            _server.FilesReceived += delegate(object s, FileLinkIncomingEventArgs e) { RaiseFilesReceived(e); };
            _server.FilesOffered += delegate(object s, FileLinkIncomingEventArgs e) { RaiseOfferReceived(e); };
            _server.ClipboardReceived += delegate(object s, FileLinkClipboardEventArgs e) { RaiseClipboardReceived(e); };
            _server.Start();
            _started = _server.Started;
            AppLog.Info("filelink.hub.started ok=" + _started);
        }

        public void Stop()
        {
            _started = false;
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>发送文件（拖拽/选择文件）。后台线程执行，结果通过 PushCompleted 回来。</summary>
        public void PushFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                var result = new FileLinkPushResult { SourcePaths = paths };
                var temps = new List<string>();
                try
                {
                    var prepared = Prepare(paths, temps);
                    var client = Client();
                    if (client == null)
                    {
                        // 只在这里设结果，完成事件统一由 finally 抛一次，避免界面收到两次。
                        result.Error = "数据通道未就绪（对端地址未知或尚未配对）。";
                        result.FailCount = paths.Length;
                        return;
                    }

                    int ok;
                    int fail;
                    var progressHandler = new Action<FileLinkProgress>(RaiseProgress);
                    client.PushFiles(prepared, progressHandler, out ok, out fail);
                    result.OkCount = ok;
                    result.FailCount = fail;
                    result.Error = client.LastError;
                }
                catch (Exception ex)
                {
                    AppLog.Error("filelink.hub.push.fail", ex);
                    result.Error = ex.Message;
                }
                finally
                {
                    CleanupTemps(temps);
                    RaisePushCompleted(result);
                }
            });
        }

        /// <summary>把"本机复制的文件"公告给对端：只发清单，粘贴时才真正传字节。</summary>
        public void OfferFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                var temps = new List<string>();
                try
                {
                    var prepared = Prepare(paths, temps);
                    var client = Client();
                    if (client == null)
                    {
                        return;
                    }

                    client.OfferFiles(prepared);
                }
                catch (Exception ex)
                {
                    AppLog.Error("filelink.hub.offer.fail", ex);
                }
                finally
                {
                    CleanupTemps(temps);
                }
            });
        }

        /// <summary>同步拉一个文件（虚拟剪贴板的延迟渲染回调里调用）。返回本地路径，失败 null。</summary>
        public string PullFile(string remoteName, string remotePath, long size)
        {
            var client = Client();
            if (client == null)
            {
                return null;
            }

            var folder = InboxFolder();
            var name = string.IsNullOrEmpty(remoteName) ? Path.GetFileName(remotePath) : remoteName;
            if (string.IsNullOrEmpty(name))
            {
                name = "file-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }

            var progressHandler = new Action<FileLinkProgress>(RaiseProgress);

            // 旧链路把单文件拉取硬限 25 秒，大文件必然失败；这里只按"连接是否还活着"判断。
            var saved = client.PullFile(name, remotePath, size, delegate
            {
                return Path.Combine(folder, name);
            }, progressHandler);

            if (saved == null && !string.IsNullOrEmpty(client.LastError))
            {
                AppLog.Warn("filelink.hub.pull.fail name=" + name + " error=" + client.LastError);
            }

            return saved;
        }

        public IList<FileLinkFile> PendingOffers()
        {
            lock (_offerLock)
            {
                return new List<FileLinkFile>(_pendingOffers);
            }
        }

        public void ClearPendingOffers()
        {
            lock (_offerLock)
            {
                _pendingOffers.Clear();
            }
        }

        public bool SendClipboardText(string text)
        {
            var client = Client();
            return client != null && client.SendClipboard(FileLinkProtocol.ClipboardText, text, null);
        }

        /// <summary>对端发来的是"文件夹打包"的 zip 时，就地解成文件夹并删掉 zip；其它文件原样返回。</summary>
        public string ExpandIfArchive(string savedPath)
        {
            try
            {
                if (string.IsNullOrEmpty(savedPath) || !File.Exists(savedPath))
                {
                    return savedPath;
                }

                var name = Path.GetFileName(savedPath);
                if (!name.StartsWith("lms-copy-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return savedPath;
                }

                var stem = Path.GetFileNameWithoutExtension(name).Substring("lms-copy-".Length);
                var dash = stem.LastIndexOf('-');
                if (dash > 0 && stem.Length - dash - 1 == 8)
                {
                    stem = stem.Substring(0, dash);
                }

                if (string.IsNullOrEmpty(stem))
                {
                    stem = "folder";
                }

                var target = FileLinkClient.UniquePath(Path.Combine(Path.GetDirectoryName(savedPath) ?? Path.GetTempPath(), stem));
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(savedPath, target);
                File.Delete(savedPath);
                AppLog.Info("filelink.archive.expanded path=" + target);
                return target;
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.archive.expand.fail path=" + savedPath, ex);
                return savedPath;
            }
        }

        public bool SendClipboardImage(byte[] png)
        {
            var client = Client();
            return client != null && client.SendClipboard(FileLinkProtocol.ClipboardImage, null, png);
        }

        private PeerStateDto State()
        {
            try
            {
                return _getState != null ? _getState() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private FileLinkClient Client()
        {
            var state = State();
            if (state == null || string.IsNullOrEmpty(state.PeerAddress))
            {
                AppLog.Warn("filelink.hub.no-peer");
                return null;
            }

            var client = new FileLinkClient(
                delegate { return state.PeerAddress; },
                delegate { return null; },
                state.LocalDeviceId ?? string.Empty,
                state.LocalDeviceName ?? string.Empty);

            lock (_clients)
            {
                _clients.RemoveAll(delegate(FileLinkClient c) { return c == null; });
                _clients.Add(client);
            }

            return client;
        }

        /// <summary>取消当前所有传输（界面“取消传输”按钮）：断开发送/接收连接，.part 会被清理。</summary>
        public void CancelActive()
        {
            FileLinkClient[] snapshot;
            lock (_clients)
            {
                snapshot = _clients.ToArray();
                _clients.Clear();
            }

            foreach (var client in snapshot)
            {
                try { client.Cancel(); } catch (Exception) { }
            }

            if (_server != null)
            {
                try { _server.CancelActive(); } catch (Exception) { }
            }

            AppLog.Event("INFO", "filelink.cancel.request", "clients=" + snapshot.Length);
        }

        private string InboxFolder()
        {
            var folder = _inbox != null ? _inbox() : null;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.GetTempPath();
            }

            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.hub.inbox.fail path=" + folder, ex);
            }

            return folder;
        }

        /// <summary>文件夹先打成 zip（与旧实现一致），返回可发送的路径数组；临时文件塞进 temps 由调用方清理。</summary>
        private string[] Prepare(string[] paths, List<string> temps)
        {
            var files = new List<string>();
            var folders = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    folders.Add(path);
                }
                else if (File.Exists(path))
                {
                    files.Add(path);
                }
            }

            if (folders.Count == 0)
            {
                return files.ToArray();
            }

            var archive = CreateFolderArchive(files.ToArray(), folders.ToArray());
            if (!string.IsNullOrEmpty(archive))
            {
                temps.Add(archive);
                return new[] { archive };
            }

            return files.ToArray();
        }

        private static string CreateFolderArchive(string[] files, string[] folders)
        {
            try
            {
                var first = folders[0];
                var stem = Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(stem))
                {
                    stem = "folder";
                }

                var cleaned = new string(stem.Where(delegate(char c) { return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' '; }).ToArray()).Trim();
                if (cleaned.Length == 0)
                {
                    cleaned = "folder";
                }

                var archive = Path.Combine(Path.GetTempPath(),
                    "lms-copy-" + cleaned + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }

                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    foreach (var folder in folders)
                    {
                        var root = folder.TrimEnd(Path.DirectorySeparatorChar);
                        AddDirectory(zip, root, Path.GetFileName(root));
                    }

                    foreach (var file in files)
                    {
                        ZipFileExtensions.CreateEntryFromFile(zip, file, Path.GetFileName(file));
                    }
                }

                AppLog.Event("INFO", "filelink.folder.archive", "folders=" + folders.Length + " archive=" + archive);
                return archive;
            }
            catch (Exception ex)
            {
                AppLog.Error("filelink.folder.archive.fail", ex);
                return null;
            }
        }

        private static void AddDirectory(ZipArchive zip, string directory, string prefix)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                ZipFileExtensions.CreateEntryFromFile(zip, file, prefix + "/" + Path.GetFileName(file));
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                AddDirectory(zip, child, prefix + "/" + Path.GetFileName(child));
            }
        }

        private static void CleanupTemps(List<string> temps)
        {
            foreach (var path in temps)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void RaiseProgress(FileLinkProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            var handler = Progress;
            if (handler != null)
            {
                handler(this, progress);
            }
        }

        private void RaiseFilesReceived(FileLinkIncomingEventArgs args)
        {
            var handler = FilesReceived;
            if (handler != null)
            {
                handler(this, args);
            }
        }

        private void RaiseOfferReceived(FileLinkIncomingEventArgs args)
        {
            lock (_offerLock)
            {
                _pendingOffers.Clear();
                if (args.Files != null)
                {
                    _pendingOffers.AddRange(args.Files);
                }
            }

            var handler = OfferReceived;
            if (handler != null)
            {
                handler(this, args);
            }
        }

        private void RaiseClipboardReceived(FileLinkClipboardEventArgs args)
        {
            var handler = ClipboardReceived;
            if (handler != null)
            {
                handler(this, args);
            }
        }

        private void RaisePushCompleted(FileLinkPushResult result)
        {
            var handler = PushCompleted;
            if (handler != null)
            {
                handler(this, result);
            }
        }
    }
}
