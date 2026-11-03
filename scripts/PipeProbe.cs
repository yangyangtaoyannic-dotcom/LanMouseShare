using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

internal static class PipeProbe
{
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // fix7 候选：AuthenticatedUser 增加 FILE_CREATE_PIPE_INSTANCE(0x4)，并把当前用户
    // 写成 Owner + FullControl。用来验证"非提权进程也能建 4 个实例"。
    private static PipeSecurity CreatePipeSecurityFixed()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        var self = WindowsIdentity.GetCurrent();
        if (self != null && self.User != null)
        {
            security.SetOwner(self.User);
            security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static void Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "LmsPipeProbe";
        var max = args.Length > 1 ? int.Parse(args[1]) : 4;
        var threads = args.Length > 2 ? int.Parse(args[2]) : 4;
        var mode = args.Length > 3 ? args[3] : "sec";
        var list = new List<NamedPipeServerStream>();
        var signal = new ManualResetEvent(false);
        var ok = 0;
        var fail = 0;
        for (int i = 0; i < threads; i++)
        {
            int id = i;
            var t = new Thread(delegate()
            {
                try
                {
                    NamedPipeServerStream pipe;
                    if (mode == "nosec")
                    {
                        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, max, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0);
                    }
                    else if (mode == "fix7")
                    {
                        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, max, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, CreatePipeSecurityFixed());
                    }
                    else
                    {
                        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, max, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, CreatePipeSecurity());
                    }

                    lock (list) { list.Add(pipe); ok++; }
                    Console.WriteLine("thread " + id + ": created instance #" + ok);
                    signal.WaitOne(3000);
                }
                catch (Exception ex)
                {
                    lock (list) { fail++; }
                    Console.WriteLine("thread " + id + ": FAILED " + ex.GetType().Name + ": " + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        Thread.Sleep(1200);
        Console.WriteLine("created=" + ok + " failed=" + fail + " max=" + max + " mode=" + mode);
        signal.Set();
        Thread.Sleep(200);
        foreach (var p in list) { p.Dispose(); }
    }
}
