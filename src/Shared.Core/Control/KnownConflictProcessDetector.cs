using System;
using System.Collections.Generic;
using System.IO;

namespace LanMouseShare.Core.Control
{
    public static class KnownConflictProcessDetector
    {
        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sunloginclient",
            "sunloginclient_desktop",
            "sunloginremote",
            "sunloginservice",
            "mousewithoutborders",
            "mousewithoutborderssvc",
            "mstsc",
            "anydesk",
            "teamviewer",
            "teamviewer_desktop",
            "parsecd",
            "parsec",
            "rustdesk",
            "todesk",
            "todesk_service"
        };

        public static bool IsKnownConflictProcess(string processName)
        {
            var normalized = NormalizeProcessName(processName);
            return !string.IsNullOrWhiteSpace(normalized) && ProcessNames.Contains(normalized);
        }

        public static string NormalizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            var name = processName.Trim();
            try
            {
                name = Path.GetFileNameWithoutExtension(name);
            }
            catch
            {
            }

            return name ?? string.Empty;
        }
    }
}
