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
                        if (command == null || !IsNoisyCommand(command.Command))
                        {
                            AppLog.Info("IPC command: " + (command != null ? command.Command : "<null>"));
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
                    AppLog.Error("IPC listener error.", ex);
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
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private static bool IsNoisyCommand(string command)
        {
            return command == AgentCommands.PollEvents ||
                   command == AgentCommands.SendInputBatch ||
                   command == AgentCommands.SendInput ||
                   command == AgentCommands.SendClipboard;
        }
    }
}
