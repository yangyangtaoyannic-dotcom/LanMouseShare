using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Protocol;

namespace LanMouseShare.Agent
{
    internal sealed class PipeClient
    {
        public string Send(string command, string payloadJson)
        {
            return Send(command, payloadJson, PipeConstants.DefaultTimeoutMs);
        }

        public string Send(string command, string payloadJson, int timeoutMs)
        {
            var request = new AgentCommand { Command = command, PayloadJson = payloadJson };
            using (var pipe = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut))
            {
                var started = DateTime.UtcNow;
                pipe.Connect(timeoutMs);
                var elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;
                if (!IsNoisyCommand(command) || elapsedMs > 80)
                {
                    AppLog.Debug("pipe.send", "command=" + command + " ms=" + elapsedMs.ToString("0"));
                }
                pipe.ReadMode = PipeTransmissionMode.Byte;
                WriteString(pipe, ProtocolSerializer.ToJson(request));
                return ReadString(pipe);
            }
        }

        private static string ReadString(Stream stream)
        {
            var lengthBytes = new byte[4];
            ReadExactly(stream, lengthBytes, 0, lengthBytes.Length);
            int length = BitConverter.ToInt32(lengthBytes, 0);
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

        private static bool IsNoisyCommand(string command)
        {
            return command == AgentCommands.PollEvents ||
                   command == AgentCommands.SendInputBatch ||
                   command == AgentCommands.SendInput ||
                   command == AgentCommands.SendClipboard;
        }
    }
}
