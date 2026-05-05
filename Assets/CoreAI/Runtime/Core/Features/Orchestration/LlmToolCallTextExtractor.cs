using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Engine-agnostic helper that extracts (or strips) tool-call JSON embedded in assistant
    /// text. Mirrors the logic used by the streaming and non-streaming pipelines so the
    /// orchestrator can apply the same rules at boundary points (chat history, command
    /// envelope) without depending on Microsoft.Extensions.AI types.
    /// <para>
    /// Match rule: a balanced JSON object that contains both <c>"name"</c> and <c>"arguments"</c>
    /// keys. JSON inside fenced code blocks (<c>```...```</c>) is excluded by default to avoid
    /// stripping example snippets shown by the model.
    /// </para>
    /// <para>
    /// Fallback: local GGUF models sometimes emit pseudo key=value lines such as
    /// <c>Action=write content="..."</c> instead of JSON — those are mapped to the <c>memory</c> tool when detected.
    /// </para>
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
                return TryExtractMemoryPseudoWriteSyntax(text, out matches, out cleanedText);
            }

            StringBuilder cleanBuilder = new(text.Length);
            int lastEnd = 0;
            foreach ((int Start, int Length) span in spans)
            {
                if (span.Start >= text.Length || span.Start + span.Length > text.Length) continue;
                string original = text.Substring(span.Start, span.Length);
                if (!LooksLikeToolCallJson(original)) continue;

                string name;
                string argsJson;
                try
                {
                    JObject json = JObject.Parse(original);
                    name = json["name"]?.ToString()?.Trim();
                    JToken args = json["arguments"];
                    if (string.IsNullOrWhiteSpace(name) || args == null) continue;
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
        /// GGUF / llama.cpp stacks (e.g. Qwen via LLMUnity) sometimes emit pseudo key=value “tool” lines
        /// instead of JSON tool calls, e.g. <c>Action=write content="exam on June 15"</c>. Map to the
        /// <c>memory</c> tool so the pipeline can persist and strip the noise.
        /// </summary>
        private static bool TryExtractMemoryPseudoWriteSyntax(string text, out List<Match> matches, out string cleanedText)
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
        /// purposes. Does not execute the calls. Safe to apply to any assistant text — a payload
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
            if (string.IsNullOrEmpty(text)) return text;
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
            if (string.IsNullOrWhiteSpace(json)) return false;
            return json.Contains("\"name\"") && json.Contains("\"arguments\"");
        }

        /// <summary>
        /// Walks <paramref name="text"/> and returns the spans of every balanced top-level
        /// <c>{...}</c> object that passes the textual heuristic. Brace counting respects
        /// strings and escapes, so JSON values containing <c>{</c> or <c>"</c> are handled.
        /// </summary>
        public static List<(int Start, int Length)> FindBalancedToolCallSpans(string text)
        {
            List<(int Start, int Length)> spans = new();
            if (string.IsNullOrEmpty(text)) return spans;

            int i = 0;
            while (i < text.Length)
            {
                int braceStart = text.IndexOf('{', i);
                if (braceStart < 0) break;

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < text.Length; j++)
                {
                    char c = text[j];
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\' && inString) { escaped = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c == '{') depth++;
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

                i = (depth == 0 && j < text.Length) ? j + 1 : braceStart + 1;
            }

            return spans;
        }
    }
}
