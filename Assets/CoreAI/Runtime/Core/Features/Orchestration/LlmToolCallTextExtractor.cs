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

        // Hermes / Qwen-Agent XML tool-call template emitted by many local GGUF models when native
        // tool_calls is empty, e.g.:
        //   <tool_call><function=call_skill_tool>
        //     <parameter=tool_name>craft_item</parameter>
        //     <parameter=arguments_json>{"item":"Flame Sword"}</parameter>
        //   </function></tool_call>
        private static readonly Regex XmlFunctionRegex = new(
            @"<function\s*=\s*([^>\s]+)\s*>(.*?)</function>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex XmlParameterRegex = new(
            @"<parameter\s*=\s*([^>\s]+)\s*>(.*?)</parameter>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

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
                // Hermes/Qwen-Agent XML tool-call template (common local-model fallback when native
                // tool_calls is empty) before function-call syntax and memory pseudo-write.
                if (TryExtractXmlToolCallSyntax(text, out matches, out cleanedText))
                {
                    return true;
                }

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
        /// Extracts Hermes / Qwen-Agent XML tool-call syntax that many local GGUF models emit as assistant
        /// text when their native <c>tool_calls</c> array is empty:
        /// <c>&lt;function=NAME&gt;&lt;parameter=KEY&gt;VALUE&lt;/parameter&gt;...&lt;/function&gt;</c>.
        /// Each <c>&lt;parameter&gt;</c> value is kept as a string (so an inner <c>arguments_json</c> JSON
        /// string stays intact for tools like <c>call_skill_tool</c>). The wrapping
        /// <c>&lt;tool_call&gt;</c> tags are stripped from the cleaned reply.
        /// </summary>
        private static bool TryExtractXmlToolCallSyntax(string text, out List<Match> matches,
            out string cleanedText)
        {
            matches = new List<Match>();
            cleanedText = text;
            if (string.IsNullOrEmpty(text) ||
                text.IndexOf("<function", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            StringBuilder clean = new(text.Length);
            int lastEnd = 0;
            foreach (System.Text.RegularExpressions.Match fn in XmlFunctionRegex.Matches(text))
            {
                string name = fn.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                JObject args = new();
                foreach (System.Text.RegularExpressions.Match p in XmlParameterRegex.Matches(fn.Groups[2].Value))
                {
                    string key = p.Groups[1].Value.Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    // Keep values as strings: the model wrote text, and an inner arguments_json must stay a
                    // JSON string. Tool arg binding coerces string -> number/bool where needed.
                    args[key] = p.Groups[2].Value.Trim();
                }

                matches.Add(new Match(name, args.ToString(Formatting.None), fn.Index, fn.Length));
                clean.Append(text, lastEnd, fn.Index - lastEnd);
                lastEnd = fn.Index + fn.Length;
            }

            if (matches.Count == 0)
            {
                return false;
            }

            clean.Append(text, lastEnd, text.Length - lastEnd);
            cleanedText = clean.ToString()
                .Replace("<tool_call>", "")
                .Replace("</tool_call>", "")
                .Trim();
            return true;
        }

        /// <summary>
        /// Detects models that emit memory writes as pseudo key/value syntax
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
                    else if (!TryParseKeywordArguments(parts, argsDict))
                    {
                        // Generic positional: first arg as "input". The keyword branch above handles
                        // python-style calls — world_command(action='spawn', x=1) — which previously fell
                        // through to here and collapsed into {"input":"action='spawn'"}, failing the
                        // tool's required-argument validation (observed live in the game benchmark).
                        argsDict["input"] = StripQuotes(parts[0]);
                    }
                }
            }

            string argsJson = JsonConvert.SerializeObject(argsDict);
            matches.Add(new Match(funcName, argsJson, 0, text.Length));
            cleanedText = string.Empty;
            return true;
        }

        /// <summary>
        /// Parses python-style keyword arguments — <c>action='spawn', targetName="Goal", x=1, solid=true</c> —
        /// into typed values (quoted → string, true/false → bool, numeric → long/double, <c>{...}</c>/<c>[...]</c>
        /// → parsed JSON, anything else → raw string). ALL parts must be <c>ident=value</c> pairs or the
        /// method leaves <paramref name="argsDict"/> untouched and returns false, so positional calls keep
        /// their legacy handling.
        /// </summary>
        private static readonly Regex KeywordArgRegex = new(
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static bool TryParseKeywordArguments(string[] parts, Dictionary<string, object> argsDict)
        {
            if (parts == null || parts.Length == 0)
            {
                return false;
            }

            Dictionary<string, object> parsed = new();
            foreach (string part in parts)
            {
                System.Text.RegularExpressions.Match m = KeywordArgRegex.Match(part ?? "");
                if (!m.Success)
                {
                    return false;
                }

                parsed[m.Groups[1].Value] = ParseKeywordValue(m.Groups[2].Value.Trim());
            }

            foreach (KeyValuePair<string, object> kv in parsed)
            {
                argsDict[kv.Key] = kv.Value;
            }

            return true;
        }

        private static object ParseKeywordValue(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }

            if (raw.Length >= 2 &&
                ((raw[0] == '"' && raw[raw.Length - 1] == '"') ||
                 (raw[0] == '\'' && raw[raw.Length - 1] == '\'')))
            {
                return raw.Substring(1, raw.Length - 2);
            }

            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }

            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                return d;
            }

            if (raw[0] == '{' || raw[0] == '[')
            {
                try
                {
                    return JsonConvert.DeserializeObject<object>(raw);
                }
                catch
                {
                    // fall through: keep the raw text
                }
            }

            return raw;
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