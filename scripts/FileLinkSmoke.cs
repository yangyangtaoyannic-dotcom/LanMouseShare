// fix8 数据通道的本机冒烟测试：不用第二台机器，直接验证 push / pull / clipboard 三条链路。
// 编译：csc -out:FileLinkSmoke.exe -r:LanMouseShare.Core.dll -r:System.Runtime.Serialization.dll
//            -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll
//            scripts\FileLinkSmoke.cs src\Desktop.Agent\FileLink\*.cs
// 运行：FileLinkSmoke.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using LanMouseShare.Agent.FileLink;
using LanMouseShare.Core.Ipc;

internal static class FileLinkSmoke
{
    private const int SmokePort = 47551;

    private static string _inbox;

    private static int Main()
    {
        _inbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lms-smoke-inbox-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(_inbox);

        var state = new PeerStateDto
        {
            LocalDeviceId = "B",
            LocalDeviceName = "smoke-b",
            PeerDeviceId = "B",
            PeerDeviceName = "smoke-a",
            PeerAddress = "127.0.0.1",
            IsConnected = true,
            IsTrusted = true
        };

        var received = new List<string>();
        var clipboard = new List<string>();
        var server = new FileLinkServer(delegate { return state; }, delegate { return _inbox; }, SmokePort);
        server.FilesReceived += delegate(object s, FileLinkIncomingEventArgs e) { lock (received) { received.AddRange(e.SavedPaths); } };
        server.ClipboardReceived += delegate(object s, FileLinkClipboardEventArgs e) { lock (clipboard) { clipboard.Add(e.Kind + ":" + e.Text); } };
        server.Start();
        if (!server.Started)
        {
            Console.WriteLine("FAIL server not started");
            return 1;
        }

        Console.WriteLine("server port=" + server.Port);

        // ---- 1) push：客户端把两个文件推给服务端
        var outDir = Path.Combine(_inbox, "out");
        Directory.CreateDirectory(outDir);
        var f1 = Path.Combine(outDir, "hello.txt");
        var f2 = Path.Combine(outDir, "blob.bin");
        File.WriteAllText(f1, "hello fix8");
        var blob = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(blob);
        File.WriteAllBytes(f2, blob);

        var client = new FileLinkClient(
            delegate { return "127.0.0.1"; },
            delegate { return null; },
            "B",
            "smoke-a",
            SmokePort);

        int ok;
        int fail;
        var pushOk = client.PushFiles(new string[] { f1, f2 }, null, out ok, out fail);
        Console.WriteLine("push ok=" + pushOk + " okCount=" + ok + " failCount=" + fail + " error=" + (client.LastError ?? ""));

        var pushed = WaitFor(received, 2, 8000);
        Console.WriteLine("push received=" + pushed.Count + " " + Join(pushed));
        if (ok != 2 || pushed.Count != 2)
        {
            Console.WriteLine("FAIL push");
            return 1;
        }

        foreach (var path in pushed)
        {
            var name = Path.GetFileName(path);
            if (name == "blob.bin" && new FileInfo(path).Length != blob.Length)
            {
                Console.WriteLine("FAIL push size " + name);
                return 1;
            }
        }

        // ---- 2) pull：客户端按对端真实路径拉回一个文件
        var pullSource = Path.Combine(outDir, "pull-me.dat");
        File.WriteAllBytes(pullSource, blob);
        var pulledDir = Path.Combine(_inbox, "pulled");
        Directory.CreateDirectory(pulledDir);
        var pulled = client.PullFile("pull-me.dat", pullSource, 0, delegate { return Path.Combine(pulledDir, "pull-me.dat"); }, null);
        Console.WriteLine("pull path=" + (pulled ?? "null") + " error=" + (client.LastError ?? ""));
        if (string.IsNullOrEmpty(pulled) || !File.Exists(pulled) || new FileInfo(pulled).Length != blob.Length)
        {
            Console.WriteLine("FAIL pull");
            return 1;
        }

        var a = File.ReadAllBytes(pullSource);
        var b = File.ReadAllBytes(pulled);
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                Console.WriteLine("FAIL pull content at " + i);
                return 1;
            }
        }

        // ---- 3) clipboard 文本
        var sent = client.SendClipboard(FileLinkProtocol.ClipboardText, "粘粘粘 fix8", null);
        var got = WaitFor(clipboard, 1, 5000);
        Console.WriteLine("clipboard sent=" + sent + " received=" + (got.Count > 0 ? got[0] : "none"));
        if (!sent || got.Count == 0)
        {
            Console.WriteLine("FAIL clipboard");
            return 1;
        }

        // ---- 4) 未配对的对端必须被拒绝
        var stranger = new FileLinkClient(
            delegate { return "127.0.0.1"; },
            delegate { return null; },
            "SOMEONE-ELSE",
            "stranger",
            SmokePort);
        int sok;
        int sfail;
        stranger.PushFiles(new string[] { f1 }, null, out sok, out sfail);
        Console.WriteLine("stranger ok=" + sok + " fail=" + sfail + " error=" + (stranger.LastError ?? ""));
        if (sok != 0)
        {
            Console.WriteLine("FAIL stranger was accepted");
            return 1;
        }

        // ---- 5) 文件夹：服务端按需打 zip，客户端用对端给的名字落盘，ExpandIfArchive 能解回原目录名
        var folderSource = Path.Combine(_inbox, "资料");
        Directory.CreateDirectory(folderSource);
        File.WriteAllText(Path.Combine(folderSource, "a.txt"), "folder-a");
        Directory.CreateDirectory(Path.Combine(folderSource, "sub"));
        File.WriteAllText(Path.Combine(folderSource, "sub", "b.txt"), "folder-b");

        var folderClient = new FileLinkClient(
            delegate { return "127.0.0.1"; },
            delegate { return null; },
            "B",
            "smoke-folder",
            SmokePort);
        var folderPulled = folderClient.PullFile("资料", folderSource, 0,
            delegate { return Path.Combine(pulledDir, "资料"); }, null);
        Console.WriteLine("folder pull path=" + (folderPulled ?? "null") + " error=" + (folderClient.LastError ?? ""));
        if (string.IsNullOrEmpty(folderPulled) || !File.Exists(folderPulled))
        {
            Console.WriteLine("FAIL folder pull");
            return 1;
        }

        var zipName = Path.GetFileName(folderPulled);
        if (!zipName.StartsWith("lms-copy-", StringComparison.OrdinalIgnoreCase)
            || !zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FAIL folder pull name " + zipName);
            return 1;
        }

        using (var zip = ZipFile.OpenRead(folderPulled))
        {
            var names = new List<string>();
            foreach (var entry in zip.Entries)
            {
                names.Add(entry.FullName.Replace('\\', '/'));
            }

            Console.WriteLine("folder zip entries=" + names.Count + " " + string.Join(",", names.ToArray()));
            if (!names.Contains("a.txt") || !names.Contains("sub/b.txt"))
            {
                Console.WriteLine("FAIL folder zip entries");
                return 1;
            }
        }

        // ---- 6) 取消：传输中途 Cancel() 必须立刻断开、不留半截文件
        var bigPath = Path.Combine(outDir, "big.bin");
        using (var bigStream = new FileStream(bigPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            bigStream.SetLength(300L * 1024 * 1024);
        }

        var cancelClient = new FileLinkClient(
            delegate { return "127.0.0.1"; },
            delegate { return null; },
            "B",
            "smoke-cancel",
            SmokePort);
        var cancelOk = true;
        var cancelFail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cancelThread = new Thread(new ThreadStart(delegate
        {
            int cok;
            int cfail;
            cancelOk = cancelClient.PushFiles(new string[] { bigPath }, null, out cok, out cfail);
            cancelFail = cfail;
        }));
        cancelThread.IsBackground = true;
        cancelThread.Start();
        Thread.Sleep(200);
        cancelClient.Cancel();
        var finished = cancelThread.Join(10000);
        sw.Stop();
        Console.WriteLine("cancel finished=" + finished + " elapsedMs=" + sw.ElapsedMilliseconds
            + " pushOk=" + cancelOk + " fail=" + cancelFail + " error=" + (cancelClient.LastError ?? ""));

        var leftoverParts = Directory.GetFiles(_inbox, "*.part", SearchOption.AllDirectories);
        Console.WriteLine("cancel leftover .part=" + leftoverParts.Length);
        if (!finished || cancelOk || leftoverParts.Length > 0)
        {
            Console.WriteLine("FAIL cancel");
            return 1;
        }

        try { File.Delete(bigPath); } catch (Exception) { }

        server.Dispose();
        Console.WriteLine("SMOKE PASS");
        return 0;
    }

    private static List<string> WaitFor(List<string> target, int count, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (target)
            {
                if (target.Count >= count)
                {
                    return new List<string>(target);
                }
            }

            Thread.Sleep(50);
        }

        lock (target)
        {
            return new List<string>(target);
        }
    }

    private static string Join(List<string> items)
    {
        var parts = new List<string>();
        foreach (var item in items)
        {
            parts.Add(Path.GetFileName(item));
        }

        return string.Join(",", parts.ToArray());
    }
}
