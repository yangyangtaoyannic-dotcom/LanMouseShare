using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Service
{
    internal sealed class NamedPipeCommandServer
    {
        private readonly PeerCoordinator _coordinator;
        private readonly List<Thread> _threads = new List<Thread>();
        private volatile bool _running;
        private int _pipeListenErrors;
        private DateTime _lastPipeErrorLogUtc = DateTime.MinValue;

        public NamedPipeCommandServer(PeerCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            for (int i = 0; i < 4; i++)
            {
                var thread = new Thread(ListenLoop);
                thread.IsBackground = true;
                thread.Name = "LanMouseShare pipe listener " + i;
                _threads.Add(thread);
                thread.Start();
            }
        }

        public void Stop()
        {
            _running = false;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeConstants.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, CreatePipeSecurity()))
                    {
                        pipe.WaitForConnection();
                        var requestJson = ReadString(pipe);
                        var command = ProtocolSerializer.FromJson<AgentCommand>(requestJson);
                        if (command == null)
                        {
                            AppLog.Warn("Null IPC command.");
                        }
                        else if (!IsNoisyCommand(command.Command))
                        {
                            AppLog.Info("IPC command: " + command.Command);
                        }
                        string response;
                        try
                        {
                            response = _coordinator.HandleCommand(command);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("IPC command failed. Command=" + (command != null ? command.Command : "<null>"), ex);
                            response = "ERROR: " + ex.Message;
                        }

                        WriteString(pipe, response ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    // 4 个监听线程以前每 100~200ms 各刷一条同样的错误，日志被淹掉
                    // （单次运行上万行），真正有用的信息反而看不见。这里做节流：
                    // 前 3 次完整记录，之后每 60 秒最多一条，并带上累计次数。
                    var total = Interlocked.Increment(ref _pipeListenErrors);
                    var now = DateTime.UtcNow;
                    var shouldLog = total <= 3 || (now - _lastPipeErrorLogUtc).TotalSeconds >= 60;
                    if (shouldLog)
                    {
                        _lastPipeErrorLogUtc = now;
                        AppLog.Error("IPC listener error. Count=" + total, ex);
                    }

                    Thread.Sleep(200);
                }
            }
        }

        private static string ReadString(Stream stream)
        {
            var lengthBytes = new byte[4];
            ReadExactly(stream, lengthBytes, 0, lengthBytes.Length);
            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid pipe message length.");
            }

            var bytes = new byte[length];
            ReadExactly(stream, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            stream.Write(lengthBytes, 0, lengthBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                count -= read;
            }
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();

            // fix7：FILE_CREATE_PIPE_INSTANCE(0x4) 决定"同一名字下还能不能建第二个实例"。
            // 之前 AuthenticatedUser 只有 ReadWrite，非提权的 Service 只能建出第 1 个实例，
            // 其余 3 个监听线程每次 new NamedPipeServerStream 都抛 UnauthorizedAccessException，
            // 结果实际可用实例只有 1 个，Agent 的并发命令会撞"管道实例全忙"被静默丢弃
            // （RequestClipboardFiles / RequestClipboardPaste 就是这样丢掉的）。
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

            // 以普通用户身份运行 Service 时，管道 Owner 是当前用户；显式把自己写成 Owner +
            // FullControl，避免 ACL 里只有管理员组全权、自己反而不能加实例。
            try
            {
                var self = WindowsIdentity.GetCurrent();
                if (self != null && self.User != null)
                {
                    security.SetOwner(self.User);
                    security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("pipe.acl.owner.fail", ex.GetType().Name + ": " + ex.Message);
            }

            return security;
        }

        private static bool IsNoisyCommand(string command)
        {
            return command == AgentCommands.PollEvents ||
                   command == AgentCommands.GetPeerState ||
                   command == AgentCommands.SendInputBatch ||
                   command == AgentCommands.SendInput ||
                   command == AgentCommands.SendControlLease ||
                   command == AgentCommands.SendControlLeaseAck ||
                   command == AgentCommands.SendClipboard;
        }
    }
}
