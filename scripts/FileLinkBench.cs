using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LanMouseShare.Agent.FileLink;
using LanMouseShare.Core.Ipc;
using LanMouseShare.Core.Diagnostics;


internal static class FileLinkBench
{
    private static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string Inbox = Path.Combine(Base, "bench-inbox");
    private static readonly string Source = Path.Combine(Base, "bench-source.bin");
    private const int BenchPort = 47561;
    private const long SizeBytes = 300L * 1024 * 1024;

    private static int Main()
    {
        AppLog.Initialize("Agent", false);
        if (Directory.Exists(Inbox)) Directory.Delete(Inbox, true);
        Directory.CreateDirectory(Inbox);

        if (!File.Exists(Source))
        {
            using (var fs = new FileStream(Source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                fs.SetLength(SizeBytes);
            }
        }

        var state = new PeerStateDto
        {
            LocalDeviceId = "bench-local",
            LocalDeviceName = "bench",
            PeerDeviceId = "bench-peer",
            PeerDeviceName = "bench-peer",
            PeerAddress = "127.0.0.1",
            IsConnected = true,
            IsTrusted = true
        };

        using (var server = new FileLinkServer(delegate { return state; }, delegate { return Inbox; }, BenchPort))
        {
            server.Start();
            Thread.Sleep(200);

            var payloadSize = 0L;
            var progressCalls = 0;
            var client = new FileLinkClient(delegate { return "127.0.0.1"; }, delegate { return null; }, "bench-peer", "bench", BenchPort);
            int okCount = 0;
            int failCount = 0;
            client.PushFiles(new[] { Source }, delegate(FileLinkProgress p)
            {
                Interlocked.Increment(ref progressCalls);
                Interlocked.Exchange(ref payloadSize, p.DoneBytes);
            }, out okCount, out failCount);

            var received = Path.Combine(Inbox, Path.GetFileName(Source));
            var receivedSize = File.Exists(received) ? new FileInfo(received).Length : 0;

            var sw = Stopwatch.StartNew();
            client = new FileLinkClient(delegate { return "127.0.0.1"; }, delegate { return null; }, "bench-peer", "bench", BenchPort);
            client.PushFiles(new[] { Source }, null, out okCount, out failCount);
            sw.Stop();

            var mb = SizeBytes / (1024.0 * 1024.0);
            Console.WriteLine("bench push ok=" + okCount + " fail=" + failCount + " ms=" + sw.ElapsedMilliseconds
                + " MB=" + mb.ToString("F1") + " MB/s=" + (mb / sw.Elapsed.TotalSeconds).ToString("F1"));
            Console.WriteLine("received first=" + receivedSize + " callbackFirst=" + progressCalls + " lastError=" + client.LastError);
            return okCount == 1 ? 0 : 1;
        }
    }
}
