using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Extracts tool-call text payloads from model responses.
    /// </summary>
    public static class LlmToolCallTextExtractor
    {
        private static readonly Regex CodeBlockRegex = new(@"```[\s\S]*?```", RegexOptions.Compiled);

        /// <summary>
        /// One extracted tool call: function name + raw arguments JSON + the original span
        /// in <paramref name="text"/> so callers can rebuild the cleaned reply.
        /// </summary>
        public readonly struct Match
        {
            public Match(string name, string argumentsJson, int start, int length)
            {
                Name = name ?? "";
                ArgumentsJson = argumentsJson ?? "{}";
                Start = start;
                Length = length;
            }

            public string Name { get; }
            public string ArgumentsJson { get; }
            public int Start { get; }
            public int Length { get; }
        }

        /// <summary>
        /// Attempts to extract every tool-call JSON object from <paramref name="text"/>.
        /// Returns <c>true</c> when at least one match is found; otherwise <c>false</c> and
        /// <paramref name="cleanedText"/> equals the input. JSON inside <c>```...```</c> blocks
        /// is ignored.
        /// </summary>
        public static bool TryExtract(string text, out List<Match> matches, out string cleanedText)
        {
            matches = new List<Match>();
            cleanedText = text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string searchText = StripCodeBlocks(text);
            List<(int Start, int Length)> spans = FindBalancedToolCallSpans(searchText);
            if (spans.Count == 0)
            {
                // Try function-call syntax before memory pseudo-write.
                if (TryExtractFunctionCallSyntax(text, out matches, out cleanedText))
                {
                    return true;
                }

                return TryExtractMemoryPseudoWriteSyntax(text, out matches, out cleanedText);
            }

            StringBuilder cleanBuilder = new(text.Length);
            int lastEnd = 0;
            foreach ((int Start, int Length) span in spans)
            {
                if (span.Start >= text.Length || span.Start + span.Length > text.Length)
                {
                    continue;
                }

                string original = text.Substring(span.Start, span.Length);
                if (!LooksLikeToolCallJson(original))
                {
                    continue;
                }

                string name;
                string argsJson;
                try
                {
                    JObject json = JObject.Parse(original);
                    name = json["name"]?.ToString()?.Trim();
                    // Support both "arguments" and "arguments_json" (Qwen3.5 via LLMUnity).
                    JToken args = json["arguments"] ?? json["arguments_json"];
                    if (string.IsNullOrWhiteSpace(name) || args == null)
                    {
                        continue;
                    }

                    // If args is a string ("arguments_json": "{...}"), parse it as JSON.
                    if (args.Type == JTokenType.String)
                    {
                        string argsStr = args.ToString();
                        try
                        {
                            args = JToken.Parse(argsStr);
                        }
                        catch
                        {
                            /* keep as-is */
                        }
                    }

                    argsJson = args.ToString(Formatting.None);
                }
                catch
                {
                    continue;
                }

                matches.Add(new Match(name, argsJson, span.Start, span.Length));
                cleanBuilder.Append(text, lastEnd, span.Start - lastEnd);
                lastEnd = span.Start + span.Length;
            }

            if (matches.Count == 0)
            {
                return TryExtractMemoryPseudoWriteSyntax(text, out matches, out cleanedText);
            }

            if (lastEnd < text.Length)
            {
                cleanBuilder.Append(text, lastEnd, text.Length - lastEnd);
            }

            cleanedText = cleanBuilder.ToString().Trim();
            return true;
        }

        /// <summary>
/// Executes TryExtractMemoryPseudoWriteSyntax API operation.
        /// instead of JSON tool calls, e.g. <c>Action=write content="exam on June 15"</c>. Map to the
        /// <c>memory</c> tool so the pipeline can persist and strip the noise.
        /// </summary>
        private static bool TryExtractMemoryPseudoWriteSyntax(string text, out List<Match> matches,
            out string cleanedText)
        {
            matches = new List<Match>();
            cleanedText = text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            System.Text.RegularExpressions.Match actionWrite =
                Regex.Match(text, @"\b[Aa]ction\s*=\s*write\b");
            if (!actionWrite.Success)
            {
                return false;
            }

            int aw = actionWrite.Index;
            string tailFromAction = text.Substring(aw);
            System.Text.RegularExpressions.Match contentMatch = Regex.Match(
                tailFromAction,
                @"\bcontent\s*=\s*(?<q>[""'])(?<v>(?:\\.|(?!\k<q>).)*)\k<q>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!contentMatch.Success)
            {
                return false;
            }

            string content = contentMatch.Groups["v"].Value;
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            int spanEnd = aw + contentMatch.Index + contentMatch.Length;
            string tail = text.Substring(spanEnd);
            System.Text.RegularExpressions.Match noise =
                Regex.Match(tail, @"^\s*(?:\w+\s*=\s*[""'][^""']*[""']\s*)+");
            if (noise.Success)
            {
                spanEnd += noise.Length;
            }

            while (spanEnd > aw && spanEnd <= text.Length && char.IsWhiteSpace(text[spanEnd - 1]))
            {
                spanEnd--;
            }

            string argsJson = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["action"] = "write",
                ["content"] = content
            });

            matches.Add(new Match("memory", argsJson, aw, spanEnd - aw));

            string cleaned = spanEnd <= text.Length
                ? text.Substring(0, aw) + text.Substring(spanEnd)
                : text.Substring(0, aw);

            cleanedText = cleaned.Trim();
            return true;
        }

        /// <summary>
        /// Removes any embedded tool-call JSON from <paramref name="assistantText"/> for display
/// Executes StripForDisplay API operation.
        /// that does not contain matching JSON is returned unchanged.
        /// </summary>
        public static string StripForDisplay(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return assistantText ?? string.Empty;
            }

            return TryExtract(assistantText, out _, out string cleaned) ? cleaned : assistantText;
        }

        /// <summary>
        /// Replaces fenced code blocks with whitespace of equal length so they are excluded
        /// from extraction without shifting the offsets of the remaining text.
        /// </summary>
        public static string StripCodeBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string result = CodeBlockRegex.Replace(text, m => new string(' ', m.Length));
            // BUG-6 safety: StripCodeBlocks MUST preserve length so that span offsets
            // found in the replaced text map correctly back to the original.
            System.Diagnostics.Debug.Assert(result.Length == text.Length,
                $"StripCodeBlocks length mismatch: {result.Length} vs {text.Length}");
            return result;
        }

        /// <summary>Quick textual heuristic before full JSON parsing.</summary>
        public static bool LooksLikeToolCallJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            // Accept both "arguments" and "arguments_json" (Qwen3.5 via LLMUnity emits the latter).
            return json.Contains("\"name\"") &&
                   (json.Contains("\"arguments\"") || json.Contains("\"arguments_json\""));
        }

        /// <summary>
        /// Walks <paramref name="text"/> and returns the spans of every balanced top-level
        /// <c>{...}</c> object that passes the textual heuristic. Brace counting respects
        /// strings and escapes, so JSON values containing <c>{</c> or <c>"</c> are handled.
        /// </summary>
        public static List<(int Start, int Length)> FindBalancedToolCallSpans(string text)
        {
            List<(int Start, int Length)> spans = new();
            if (string.IsNullOrEmpty(text))
            {
                return spans;
            }

            int i = 0;
            while (i < text.Length)
            {
                int braceStart = text.IndexOf('{', i);
                if (braceStart < 0)
                {
                    break;
                }

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < text.Length; j++)
                {
                    char c = text[j];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                    {
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            string candidate = text.Substring(braceStart, j - braceStart + 1);
                            if (LooksLikeToolCallJson(candidate))
                            {
                                spans.Add((braceStart, j - braceStart + 1));
                            }

                            break;
                        }
                    }
                }

                i = depth == 0 && j < text.Length ? j + 1 : braceStart + 1;
            }

            return spans;
        }

        /// <summary>
        /// Fallback for local GGUF models (Qwen3.5 via LLMUnity) that output tool calls as
        /// function-call syntax instead of JSON, e.g.:
        /// <c>read_skill("Alchemy")</c> or <c>read_skill(Crafting)</c> or
        /// <c>call_skill_tool("get_recipes", "{\"item\":\"sword\"}")</c>
        /// </summary>
        private static readonly Regex FunctionCallSyntaxRegex = new(
            @"\b([a-z_][a-z0-9_]*)\s*\(\s*(.*?)\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static bool TryExtractFunctionCallSyntax(string text, out List<Match> matches, out string cleanedText)
        {
            matches = new List<Match>();
            cleanedText = text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Only match if the ENTIRE trimmed text looks like a function call (not embedded in prose).
            string trimmed = text.Trim();
            System.Text.RegularExpressions.Match m = FunctionCallSyntaxRegex.Match(trimmed);
            if (!m.Success || m.Index != 0 || m.Length < trimmed.Length * 0.8)
            {
                return false;
            }

            string funcName = m.Groups[1].Value;
            string rawArgs = m.Groups[2].Value.Trim();

            // Build arguments JSON from the raw args string.
            // Handle: read_skill("Alchemy"), read_skill(Crafting),
            //         call_skill_tool("get_recipes", '{"item":"sword"}')
            Dictionary<string, object> argsDict = new();
            if (!string.IsNullOrEmpty(rawArgs))
            {
                // Try to parse as JSON first (e.g. {"skill_name": "Alchemy"})
                if (rawArgs.StartsWith("{"))
                {
                    try
                    {
                        argsDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(rawArgs)
                                   ?? new Dictionary<string, object>();
                    }
                    catch
                    {
                        argsDict["input"] = rawArgs;
                    }
                }
                else
                {
                    // Split by comma for multi-arg: call_skill_tool("get_recipes", '{"item":"sword"}')
                    string[] parts = SplitFunctionArgs(rawArgs);
                    if (funcName == "call_skill_tool" && parts.Length >= 2)
                    {
                        argsDict["tool_name"] = StripQuotes(parts[0]);
                        argsDict["arguments_json"] = StripQuotes(parts[1]);
                    }
                    else if (funcName == "read_skill" && parts.Length >= 1)
                    {
                        argsDict["skill_name"] = StripQuotes(parts[0]);
                    }
                    else
                    {
                        // Generic: first arg as "input"
                        argsDict["input"] = StripQuotes(parts[0]);
                    }
                }
            }

            string argsJson = JsonConvert.SerializeObject(argsDict);
            matches.Add(new Match(funcName, argsJson, 0, text.Length));
            cleanedText = string.Empty;
            return true;
        }

        private static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }

            s = s.Trim();
            if (s.Length >= 2 &&
                ((s[0] == '"' && s[s.Length - 1] == '"') ||
                 (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                return s.Substring(1, s.Length - 2);
            }

            return s;
        }

        private static string[] SplitFunctionArgs(string argsStr)
        {
            // Simple split respecting quotes and braces.
            List<string> parts = new();
            int depth = 0;
            bool inQuote = false;
            char quoteChar = '"';
            StringBuilder current = new();

            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (inQuote)
                {
                    current.Append(c);
                    if (c == quoteChar && (i == 0 || argsStr[i - 1] != '\\'))
                    {
                        inQuote = false;
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    inQuote = true;
                    quoteChar = c;
                    current.Append(c);
                }
                else if (c == '{')
                {
                    depth++;
                    current.Append(c);
                }
                else if (c == '}')
                {
                    depth--;
                    current.Append(c);
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                parts.Add(current.ToString().Trim());
            }

            return parts.ToArray();
        }
    }
}
