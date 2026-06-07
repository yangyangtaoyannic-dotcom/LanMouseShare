using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace LanMouseShare.Core.Diagnostics
{
    public static class AppLog
    {
        private static readonly object Sync = new object();
        private const int MaxLogFilesPerComponent = 10;
        private static string _component = "App";
        private static string _path;

        public static void Initialize(string component, bool commonAppData)
        {
            _component = string.IsNullOrWhiteSpace(component) ? "App" : component;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var directory = Path.Combine(root, "LanMouseShare", "Logs");
            Directory.CreateDirectory(directory);
            CleanupOldLogs(directory, _component, MaxLogFilesPerComponent - 1);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _path = Path.Combine(directory, _component + "_" + timestamp + "_" + Process.GetCurrentProcess().Id + ".log");
            Info("Logger initialized. Version=0.1.41 Process=" + Process.GetCurrentProcess().ProcessName + " Path=" + _path);
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                if (string.IsNullOrEmpty(_path))
                {
                    Initialize("App", true);
                }

                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                           " [" + level + "] [" + _component + "] [T" + Thread.CurrentThread.ManagedThreadId + "] " +
                           (message ?? string.Empty);
                if (exception != null)
                {
                    line += Environment.NewLine + exception;
                }

                lock (Sync)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        public static string LogDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanMouseShare", "Logs");
            }
        }

        private static void CleanupOldLogs(string directory, string component, int maxFilesToKeep)
        {
            try
            {
                var files = Directory.GetFiles(directory, component + "_*.log")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .ThenByDescending(file => file.LastWriteTimeUtc)
                    .Skip(Math.Max(0, maxFilesToKeep))
                    .ToArray();

                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
