using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LanMouseShare.Core.Control;

namespace LanMouseShare.Agent
{
    internal static class ForegroundWindowInspector
    {
        public static ForegroundWindowInfo GetForegroundWindowInfo()
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return ForegroundWindowInfo.Empty;
            }

            int processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId <= 0)
            {
                return ForegroundWindowInfo.Empty;
            }

            string processName = string.Empty;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch
            {
            }

            var title = new StringBuilder(256);
            GetWindowText(handle, title, title.Capacity);
            var normalized = KnownConflictProcessDetector.NormalizeProcessName(processName);
            return new ForegroundWindowInfo(processId, processName, normalized, title.ToString());
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    }

    internal sealed class ForegroundWindowInfo
    {
        public static readonly ForegroundWindowInfo Empty = new ForegroundWindowInfo(0, string.Empty, string.Empty, string.Empty);

        public ForegroundWindowInfo(int processId, string processName, string normalizedProcessName, string title)
        {
            ProcessId = processId;
            ProcessName = processName ?? string.Empty;
            NormalizedProcessName = normalizedProcessName ?? string.Empty;
            Title = title ?? string.Empty;
        }

        public int ProcessId { get; private set; }

        public string ProcessName { get; private set; }

        public string NormalizedProcessName { get; private set; }

        public string Title { get; private set; }
    }
}
