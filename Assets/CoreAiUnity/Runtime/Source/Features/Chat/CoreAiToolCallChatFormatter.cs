#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace CoreAI.Chat
{
    /// <summary>
    /// Builds a compact multi-line string for optional in-chat tool-call diagnostics.
    /// </summary>
    public static class CoreAiToolCallChatFormatter
    {
        public const int DefaultMaxCharsPerSection = 400;

        /// <summary>
        /// Single chat bubble body: tool name, optional args JSON, optional result JSON (each truncated).
        /// </summary>
        public static string BuildDisplayText(
            string toolName,
            IDictionary<string, object?>? arguments,
            object? result,
            int maxCharsPerSection = DefaultMaxCharsPerSection)
        {
            if (maxCharsPerSection < 32)
            {
                maxCharsPerSection = 32;
            }

            string name = string.IsNullOrWhiteSpace(toolName) ? "(tool)" : toolName.Trim();
            StringBuilder sb = new();
            sb.Append("[Tool] ").Append(name);

            string argsText = SerializeForDisplay(arguments, maxCharsPerSection);
            if (!string.IsNullOrEmpty(argsText))
            {
                sb.AppendLine().Append("args: ").Append(argsText);
            }

            string resultText = SerializeForDisplay(result, maxCharsPerSection);
            if (!string.IsNullOrEmpty(resultText))
            {
                sb.AppendLine().Append("result: ").Append(resultText);
            }

            return sb.ToString();
        }

        private static string SerializeForDisplay(object? value, int maxChars)
        {
            if (value == null)
            {
                return string.Empty;
            }

            try
            {
                string raw = value switch
                {
                    string s => s,
                    // Newtonsoft reflects over System.Text.Json's JsonElement struct and produces
                    // the useless {"ValueKind":N} - render the element's actual JSON instead.
                    System.Text.Json.JsonElement je =>
                        je.ValueKind == System.Text.Json.JsonValueKind.String
                            ? je.GetString() ?? string.Empty
                            : je.GetRawText(),
                    _ => JsonConvert.SerializeObject(value)
                };
                return Truncate(raw, maxChars);
            }
            catch
            {
                return Truncate(value.ToString() ?? string.Empty, maxChars);
            }
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            return s.Length <= maxChars ? s : s.Substring(0, maxChars) + "...";
        }
    }
}