using System;
using System.Collections.Generic;
using System.Threading;
using LanMouseShare.Agent;
using LanMouseShare.Core.Diagnostics;

internal static class SendChannelSmoke
{
    private static int _batches;
    private static int _totalPaths;

    private static int Main()
    {
        AppLog.Initialize("Agent", false);

        SendRequestChannel.StartServer(delegate(string[] batch)
        {
            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _totalPaths, batch.Length);
            Console.WriteLine("batch count=" + batch.Length + " -> " + string.Join(",", batch));
        });

        Thread.Sleep(200);

        var ok1 = SendRequestChannel.TrySend(new[] { @"C:\a\one.txt" });
        var ok2 = SendRequestChannel.TrySend(new[] { @"C:\a\two.txt" });
        var ok3 = SendRequestChannel.TrySend(new[] { @"C:\a\three.txt", @"C:\a\four.txt" });
        Console.WriteLine("client ok=" + ok1 + "," + ok2 + "," + ok3);

        Thread.Sleep(1500);

        var passed = ok1 && ok2 && ok3 && _batches == 1 && _totalPaths == 4;
        Console.WriteLine("batches=" + _batches + " paths=" + _totalPaths);
        Console.WriteLine(passed ? "SEND CHANNEL PASS" : "SEND CHANNEL FAIL");
        return passed ? 0 : 1;
    }
}
