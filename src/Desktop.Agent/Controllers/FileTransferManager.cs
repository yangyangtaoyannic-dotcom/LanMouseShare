using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent.Controllers
{
    /// <summary>
    /// 文件传输管理器 - 统一管理文件传输流程
    /// 改进点：
    /// 1. 小文件(<10MB)立即传输，无需等待 Ctrl+V
    /// 2. 大文件使用后台队列，显示进度
    /// 3. 统一的状态管理和错误处理
    /// </summary>
    public sealed class FileTransferManager : IDisposable
    {
        private const long SmallFileSizeThreshold = 10 * 1024 * 1024; // 10MB
        private const int MaxConcurrentTransfers = 2;

        private readonly ConcurrentDictionary<string, FileTransferTask> _activeTasks;
        private readonly SemaphoreSlim _transferSemaphore;
        private bool _disposed;

        public event EventHandler<FileTransferProgressEventArgs> TransferProgress;
        public event EventHandler<FileTransferCompletedEventArgs> TransferCompleted;
        public event EventHandler<FileTransferErrorEventArgs> TransferError;

        public FileTransferManager()
        {
            _activeTasks = new ConcurrentDictionary<string, FileTransferTask>();
            _transferSemaphore = new SemaphoreSlim(MaxConcurrentTransfers);
        }

        /// <summary>
        /// 获取活跃的传输任务数
        /// </summary>
        public int ActiveTransferCount => _activeTasks.Count;

        /// <summary>
        /// 开始文件传输
        /// </summary>
        public async Task<string> BeginTransferAsync(string[] filePaths, string peerDeviceId)
        {
            if (filePaths == null || filePaths.Length == 0)
                throw new ArgumentException("No files specified", nameof(filePaths));

            var transferId = Guid.NewGuid().ToString("N");
            var task = new FileTransferTask
            {
                TransferId = transferId,
                FilePaths = filePaths,
                PeerDeviceId = peerDeviceId,
                StartTime = DateTime.UtcNow,
                Status = TransferStatus.Pending
            };

            if (!_activeTasks.TryAdd(transferId, task))
            {
                throw new InvalidOperationException("Failed to register transfer task");
            }

            AppLog.Info("file.transfer.begin",
                $"id={transferId} files={filePaths.Length} peer={peerDeviceId}");

            // 根据文件大小决定传输策略
            var totalSize = CalculateTotalSize(filePaths);
            task.TotalBytes = totalSize;

            if (totalSize <= SmallFileSizeThreshold)
            {
                // 小文件：立即传输
                _ = Task.Run(() => ExecuteTransferAsync(task));
            }
            else
            {
                // 大文件：加入队列
                _ = Task.Run(async () =>
                {
                    await _transferSemaphore.WaitAsync();
                    try
                    {
                        await ExecuteTransferAsync(task);
                    }
                    finally
                    {
                        _transferSemaphore.Release();
                    }
                });
            }

            return transferId;
        }

        /// <summary>
        /// 取消传输
        /// </summary>
        public void CancelTransfer(string transferId)
        {
            if (_activeTasks.TryGetValue(transferId, out var task))
            {
                task.CancellationSource.Cancel();
                AppLog.Info("file.transfer.cancelled", $"id={transferId}");
            }
        }

        /// <summary>
        /// 取消所有传输
        /// </summary>
        public void CancelAllTransfers()
        {
            foreach (var task in _activeTasks.Values)
            {
                task.CancellationSource.Cancel();
            }

            AppLog.Info("file.transfer.cancel_all", $"count={_activeTasks.Count}");
        }

        /// <summary>
        /// 获取传输任务信息
        /// </summary>
        public FileTransferInfo GetTransferInfo(string transferId)
        {
            if (_activeTasks.TryGetValue(transferId, out var task))
            {
                return new FileTransferInfo
                {
                    TransferId = task.TransferId,
                    Status = task.Status,
                    TotalBytes = task.TotalBytes,
                    TransferredBytes = task.TransferredBytes,
                    FilesCount = task.FilePaths.Length,
                    StartTime = task.StartTime,
                    ElapsedTime = DateTime.UtcNow - task.StartTime
                };
            }

            return null;
        }

        /// <summary>
        /// 获取所有传输任务
        /// </summary>
        public IEnumerable<FileTransferInfo> GetAllTransfers()
        {
            return _activeTasks.Values.Select(task => new FileTransferInfo
            {
                TransferId = task.TransferId,
                Status = task.Status,
                TotalBytes = task.TotalBytes,
                TransferredBytes = task.TransferredBytes,
                FilesCount = task.FilePaths.Length,
                StartTime = task.StartTime,
                ElapsedTime = DateTime.UtcNow - task.StartTime
            });
        }

        private async Task ExecuteTransferAsync(FileTransferTask task)
        {
            try
            {
                task.Status = TransferStatus.Transferring;

                foreach (var filePath in task.FilePaths)
                {
                    if (task.CancellationSource.Token.IsCancellationRequested)
                    {
                        task.Status = TransferStatus.Cancelled;
                        break;
                    }

                    await TransferSingleFileAsync(task, filePath);
                }

                if (task.Status == TransferStatus.Transferring)
                {
                    task.Status = TransferStatus.Completed;

                    AppLog.Info("file.transfer.completed",
                        $"id={task.TransferId} files={task.FilePaths.Length} bytes={task.TransferredBytes}");

                    TransferCompleted?.Invoke(this, new FileTransferCompletedEventArgs
                    {
                        TransferId = task.TransferId,
                        FilesCount = task.FilePaths.Length,
                        TotalBytes = task.TransferredBytes
                    });
                }
            }
            catch (Exception ex)
            {
                task.Status = TransferStatus.Failed;
                task.Error = ex.Message;

                AppLog.Error("file.transfer.failed",
                    $"id={task.TransferId} error={ex.Message}");

                TransferError?.Invoke(this, new FileTransferErrorEventArgs
                {
                    TransferId = task.TransferId,
                    Error = ex.Message
                });
            }
            finally
            {
                // 清理完成的任务（延迟 30 秒以便 UI 显示结果）
                _ = Task.Delay(30000).ContinueWith(_ =>
                {
                    _activeTasks.TryRemove(task.TransferId, out _);
                });
            }
        }

        private async Task TransferSingleFileAsync(FileTransferTask task, string filePath)
        {
            if (!File.Exists(filePath))
            {
                AppLog.Warn("file.transfer.file_not_found", $"path={filePath}");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var transferred = 0L;

            // 这里实际的传输逻辑需要与现有的 FileDropSender 集成
            // 目前只是模拟进度报告
            const int chunkSize = 256 * 1024; // 256KB
            var chunks = (int)Math.Ceiling((double)fileInfo.Length / chunkSize);

            for (int i = 0; i < chunks; i++)
            {
                if (task.CancellationSource.Token.IsCancellationRequested)
                    break;

                var bytesToTransfer = Math.Min(chunkSize, fileInfo.Length - transferred);
                transferred += bytesToTransfer;
                task.TransferredBytes += bytesToTransfer;

                // 报告进度
                TransferProgress?.Invoke(this, new FileTransferProgressEventArgs
                {
                    TransferId = task.TransferId,
                    FileName = fileInfo.Name,
                    FileBytes = transferred,
                    FileTotalBytes = fileInfo.Length,
                    TotalBytes = task.TransferredBytes,
                    TotalTotalBytes = task.TotalBytes
                });

                // 模拟网络传输延迟
                await Task.Delay(10);
            }

            AppLog.Debug("file.transfer.file_completed",
                $"id={task.TransferId} file={fileInfo.Name} bytes={fileInfo.Length}");
        }

        private long CalculateTotalSize(string[] filePaths)
        {
            long total = 0;

            foreach (var path in filePaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        total += new FileInfo(path).Length;
                    }
                    else if (Directory.Exists(path))
                    {
                        total += CalculateDirectorySize(path);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("file.transfer.size_calc_failed", $"path={path} error={ex.Message}");
                }
            }

            return total;
        }

        private long CalculateDirectorySize(string directoryPath)
        {
            long size = 0;

            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    size += new FileInfo(file).Length;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("file.transfer.dir_size_failed", $"dir={directoryPath} error={ex.Message}");
            }

            return size;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelAllTransfers();
            _transferSemaphore?.Dispose();
        }
    }

    #region 数据类

    internal class FileTransferTask
    {
        public string TransferId { get; set; }
        public string[] FilePaths { get; set; }
        public string PeerDeviceId { get; set; }
        public DateTime StartTime { get; set; }
        public TransferStatus Status { get; set; }
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }
        public string Error { get; set; }
        public CancellationTokenSource CancellationSource { get; set; } = new CancellationTokenSource();
    }

    public enum TransferStatus
    {
        Pending,
        Transferring,
        Completed,
        Cancelled,
        Failed
    }

    public class FileTransferInfo
    {
        public string TransferId { get; set; }
        public TransferStatus Status { get; set; }
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }
        public int FilesCount { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan ElapsedTime { get; set; }

        public double ProgressPercentage =>
            TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

        public long SpeedBytesPerSecond =>
            ElapsedTime.TotalSeconds > 0 ? (long)(TransferredBytes / ElapsedTime.TotalSeconds) : 0;
    }

    public class FileTransferProgressEventArgs : EventArgs
    {
        public string TransferId { get; set; }
        public string FileName { get; set; }
        public long FileBytes { get; set; }
        public long FileTotalBytes { get; set; }
        public long TotalBytes { get; set; }
        public long TotalTotalBytes { get; set; }
    }

    public class FileTransferCompletedEventArgs : EventArgs
    {
        public string TransferId { get; set; }
        public int FilesCount { get; set; }
        public long TotalBytes { get; set; }
    }

    public class FileTransferErrorEventArgs : EventArgs
    {
        public string TransferId { get; set; }
        public string Error { get; set; }
    }

    #endregion
}
