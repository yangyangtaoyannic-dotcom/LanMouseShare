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

            var files = (paths ?? new string[0])
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                _notify("未发送文件", "请拖入普通文件，暂不支持文件夹。");
                return;
            }

            var signature = string.Join("|", files);
            var now = DateTime.UtcNow;
            if (signature == _lastSignature && (now - _lastSentUtc).TotalMilliseconds < 1500)
            {
                return;
            }

            _lastSignature = signature;
            _lastSentUtc = now;
            var json = ProtocolSerializer.ToJson(new FileTransferRequestPayload { FilePaths = files });
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _pipe.Send(AgentCommands.SendFiles, json);
                }
                catch (Exception ex)
                {
                    AppLog.Error("SendFiles failed.", ex);
                    _notify("文件发送失败", ex.Message);
                }
            });
            _notify("文件已加入传输", "共 " + files.Length + " 个文件。");
        }
    }
}
