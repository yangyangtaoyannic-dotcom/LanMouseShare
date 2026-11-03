using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LanMouseShare.Core.Diagnostics
{
    /// <summary>Wire format for shipping debug logs from an agent machine to the controller machine.</summary>
    public static class LogProtocol
    {
        public const int DefaultPort = 47540;
        public const string ProtocolVersion = "1";
        public const string HelloPrefix = "HELLO";
        public const string OkPrefix = "OK";
        public const string DenyPrefix = "DENY";
        public const string PingLine = "#PING";
        public const string MarkerPrefix = "#";
        public const string SysInfoBegin = "#SYSINFO-BEGIN";
        public const string SysInfoLinePrefix = "#SYSINFO ";
        public const string SysInfoEnd = "#SYSINFO-END";
        public const int MaxLineLength = 32768;
        public const int MaxSysInfoLineLength = 500;

        /// <summary>
        /// Cleans one line of free text (for example a line of the system information report)
        /// so it can travel inside a marker line. Spaces are preserved here, unlike
        /// <see cref="Safe"/>, which is meant for single token key/value fields.
        /// </summary>
        public static string SafeLogLine(string line, int maxLength)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            var cleaned = line.Replace("\r", string.Empty).Replace("\n", string.Empty);
            if (maxLength > 0 && cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength);
            }

            return cleaned;
        }

        public static string BuildHello(string deviceId, string deviceName, string role, string token, string version)
        {
            return HelloPrefix + " proto=" + ProtocolVersion +
                   " id=" + Safe(deviceId) +
                   " name=" + Safe(deviceName) +
                   " role=" + Safe(role) +
                   " token=" + Safe(token) +
                   " version=" + Safe(version);
        }

        public static string BuildOk(string controllerName)
        {
            return OkPrefix + " controller=" + Safe(controllerName);
        }

        public static string BuildDeny(string reason)
        {
            return DenyPrefix + " reason=" + Safe(reason);
        }

        public static string BuildOrigin(string deviceId, string deviceName, string role, string version, string address)
        {
            return "#ORIGIN id=" + Safe(deviceId) + " name=" + Safe(deviceName) + " role=" + Safe(role) +
                   " version=" + Safe(version) + " address=" + Safe(address) + " at=" + Safe(NowText());
        }

        public static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                builder.Append(char.IsWhiteSpace(ch) || ch == '|' ? '_' : ch);
            }

            return builder.ToString();
        }

        public static string Safe(string value, int maxLength)
        {
            var safe = Safe(value);
            if (maxLength > 0 && safe.Length > maxLength)
            {
                return safe.Substring(0, maxLength);
            }

            return safe;
        }

        public static string NowText()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>Parses "KEY k=v k2=v2" into a dictionary (keys lower-cased).</summary>
        public static Dictionary<string, string> ParseKeyValues(string line, int skipTokens)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(line))
            {
                return values;
            }

            var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = Math.Max(0, skipTokens); i < tokens.Length; i++)
            {
                var token = tokens[i];
                var index = token.IndexOf('=');
                if (index <= 0 || index == token.Length - 1)
                {
                    continue;
                }

                values[token.Substring(0, index)] = token.Substring(index + 1);
            }

            return values;
        }

        public static bool TryParsePort(string text, out int port)
        {
            port = DefaultPort;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
                   port > 0 && port <= 65535;
        }

        /// <summary>Splits "host", "host:port" or "host port" into host and port.</summary>
        public static void SplitEndpoint(string endpoint, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultPort;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return;
            }

            var text = endpoint.Trim();
            var colon = text.LastIndexOf(':');
            if (colon > 0 && colon < text.Length - 1)
            {
                var candidate = text.Substring(colon + 1);
                int parsed;
                if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                    parsed > 0 && parsed <= 65535)
                {
                    host = text.Substring(0, colon).Trim();
                    port = parsed;
                    return;
                }
            }

            host = text;
        }
    }
}
