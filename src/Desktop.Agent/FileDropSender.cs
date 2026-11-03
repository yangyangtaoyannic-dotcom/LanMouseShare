using System;
using System.IO;
using System.Linq;
using System.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class FileDropSender
    {
        private readonly PipeClient _pipe;
        private readonly Func<PeerStateDto> _getState;
        private readonly Func<bool> _isPaused;
        private readonly Action<string, string> _notify;
        private string _lastSignature;
        private DateTime _lastSentUtc = DateTime.MinValue;

        public FileDropSender(PipeClient pipe, Func<PeerStateDto> getState, Func<bool> isPaused, Action<string, string> notify)
        {
            _pipe = pipe;
            _getState = getState;
            _isPaused = isPaused;
            _notify = notify;
        }

        public void SendFiles(string[] paths)
        {
            SendFiles(paths, false);
        }

        /// <summary>
        /// Streams files to the peer. When <paramref name="forced"/> is true the request comes from
        /// the peer's paste action, so the short de-duplication window is skipped.
        /// </summary>
        public void SendFiles(string[] paths, bool forced)
        {
            if (_isPaused != null && _isPaused())
            {
                _notify("文件发送已暂停", "恢复跨屏控制后再发送文件。");
                return;
            }

            var state = _getState();
            if (state == null || !state.IsConnected || !state.IsTrusted)
            {
                _notify("无法发送文件", "请先连接并配对另一台电脑。");
                return;
            }

            var copy = (paths ?? new string[0]).ToArray();
            AppLog.Event("INFO", "file.drop", "count=" + copy.Length + " forced=" + (forced ? "1" : "0"));

            // Packing a folder into a zip can take seconds; doing that on the UI thread used to freeze
            // the whole agent (and every input frame with it).
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendFilesWorker(copy, forced);
                }
                catch (Exception ex)
                {
                    AppLog.Error("SendFiles failed.", ex);
                    _notify("文件发送失败", ex.Message);
                }
            });
        }

        private void SendFilesWorker(string[] paths, bool forced)
        {
            var candidates = (paths ?? new string[0])
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var files = candidates.Where(File.Exists).ToArray();
            var folders = candidates.Where(Directory.Exists).ToArray();
            if (files.Length == 0 && folders.Length == 0)
            {
                _notify("未发送文件", "没有可发送的文件或文件夹。");
                return;
            }

            if (folders.Length > 0)
            {
                // Folders cannot be streamed file by file, so pack everything into one archive
                // the receiving side unpacks next to the paste target (0.1.55).
                files = new[] { CreateFolderArchive(files, folders) };
            }

            var signature = string.Join("|", files);
            var now = DateTime.UtcNow;
            if (!forced && signature == _lastSignature && (now - _lastSentUtc).TotalMilliseconds < 1500)
            {
                return;
            }

            _lastSignature = signature;
            _lastSentUtc = now;
            var json = ProtocolSerializer.ToJson(new FileTransferRequestPayload { FilePaths = files });
            _pipe.Send(AgentCommands.SendFiles, json);
            _notify("文件已加入传输", "共 " + files.Length + " 个文件。");
        }
        private static string CreateFolderArchive(string[] files, string[] folders)
        {
            var first = folders[0];
            var stem = Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(stem))
            {
                stem = "folder";
            }

            stem = new string(stem.Where(delegate(char c) { return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' '; }).ToArray()).Trim();
            if (stem.Length == 0)
            {
                stem = "folder";
            }

            var archive = Path.Combine(Path.GetTempPath(),
                "lms-copy-" + stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var folder in folders)
                {
                    var root = folder.TrimEnd(Path.DirectorySeparatorChar);
                    var baseName = Path.GetFileName(root);
                    AddDirectory(zip, root, baseName);
                }

                foreach (var file in files)
                {
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, Path.GetFileName(file));
                }
            }

            AppLog.Event("INFO", "folder.archive.created",
                "folders=" + folders.Length + " extraFiles=" + files.Length + " entries=" + Directory.GetFileSystemEntries(first).Length + " archive=" + archive);
            return archive;
        }

        private static void AddDirectory(System.IO.Compression.ZipArchive zip, string directory, string prefix)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(
                    zip, file, prefix + "/" + Path.GetFileName(file));
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                AddDirectory(zip, child, prefix + "/" + Path.GetFileName(child));
            }
        }
    }
}
