using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Pure protocol helpers shared with <c>FetchSseOpenAiTransport</c> (the WebGL fetch/SSE bridge).
    /// The transport itself only compiles for the WebGL player, so everything unit-testable lives
    /// here, guard-free: the C# side of the bridge wire format (newline-flattened header strings
    /// crossing the jslib boundary in both directions) and the cancel-message contract.
    /// </summary>
    internal static class FetchSseTransportProtocol
    {
        /// <summary>
        /// Flattens request headers into the "Name:value\n..." string the jslib bridge parses.
        /// Header names/values are sanitized (CR/LF stripped) so one bad value can neither break
        /// the line format nor make the browser's fetch throw synchronously on an invalid header.
        /// Always guarantees a Content-Type: without an explicit one the browser sends
        /// text/plain;charset=UTF-8, which some OpenAI-compatible servers tolerate (Groq) and
        /// others hard-reset ("Failed to fetch" from LM Studio's Express server).
        /// </summary>
        public static string BuildHeaderString(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            var sb = new StringBuilder();
            bool hasContentType = false;
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    string name = SanitizeHeaderToken(h.Key);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        hasContentType = true;
                    }

                    sb.Append(name).Append(':').Append(SanitizeHeaderToken(h.Value)).Append('\n');
                }
            }

            if (!hasContentType)
            {
                sb.Append("Content-Type:application/json\n");
            }

            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Parses the "name:value\n..." response-header string produced by the jslib bridge into a
        /// case-insensitive multimap. Malformed lines (no colon, empty name) are skipped.
        /// </summary>
        public static IReadOnlyDictionary<string, IEnumerable<string>> ParseFlatHeaders(string flat)
        {
            Dictionary<string, IEnumerable<string>> map = new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(flat)) return map;
            foreach (string line in flat.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                string name = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (map.TryGetValue(name, out IEnumerable<string> existing))
                {
                    List<string> list = existing as List<string> ?? new List<string>(existing);
                    list.Add(value);
                    map[name] = list;
                }
                else
                {
                    map[name] = new List<string> { value };
                }
            }

            return map;
        }

        /// <summary>
        /// Whether a bridge error message means "consumer cancelled" (mapped to
        /// <see cref="OperationCanceledException"/>) rather than a real transport failure.
        /// Must accept both spellings used by the jslib ("cancelled") and any host code ("canceled").
        /// </summary>
        public static bool IsCancelledMessage(string message)
        {
            return string.Equals(message, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, "canceled", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeHeaderToken(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }

            return raw.Replace("\r", "").Replace("\n", "").Trim();
        }
    }
}
