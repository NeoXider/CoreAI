using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>Restores evidence omitted by early public report JSON from its Markdown and tool trace companions.</summary>
    public static class BenchmarkLegacyReportEvidence
    {
        public static void Restore(BenchmarkTargetState state, string markdown, string toolTraceJsonl)
        {
            bool markdownProvided = markdown != null;
            markdown ??= "";
            Dictionary<string, List<TraceCall>> calls = ParseCalls(toolTraceJsonl);
            HashSet<string> assignable = new(state.Results.GroupBy(result => result.ScenarioId)
                .Where(group => group.Count() == 1).Select(group => group.Key), StringComparer.Ordinal);
            state.PreservedToolTraceJsonl = string.Concat(calls.Where(pair => !assignable.Contains(pair.Key))
                .SelectMany(pair => pair.Value).Select(call => call.Raw));
            Dictionary<string, Queue<string[]>> statistics = new(StringComparer.Ordinal);
            Dictionary<string, Queue<double>> efficiency = new(StringComparer.Ordinal);
            foreach (string line in markdown.Split('\n'))
            {
                if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
                string[] cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
                if (cells.Length != 9) continue;
                Match score = Regex.Match(cells[4], @"^[0-9.]+ \(([0-9.]+)\)$");
                if (score.Success)
                {
                    if (!efficiency.TryGetValue(cells[1], out Queue<double> bonuses))
                        efficiency[cells[1]] = bonuses = new Queue<double>();
                    bonuses.Enqueue(double.Parse(score.Groups[1].Value, CultureInfo.InvariantCulture));
                    continue;
                }
                if (!int.TryParse(cells[4], out int _)) continue;
                if (!statistics.TryGetValue(cells[1], out Queue<string[]> rows))
                    statistics[cells[1]] = rows = new Queue<string[]>();
                rows.Enqueue(cells);
            }
            List<ScenarioResult> failedRows = state.Results.Where(HasFailure).ToList();
            Dictionary<ScenarioResult, string> failures = Sections(markdown, "## Failed checkpoints",
                failedRows, BenchmarkReportFormatter.FailureHeading, true);
            Dictionary<ScenarioResult, string> sessions = Sections(markdown, "## Full model session",
                state.Results, BenchmarkReportFormatter.SessionHeading);
            HashSet<string> explicitSceneIds = new(state.Results.Where(result => result.SceneScreenshotPng != null &&
                result.SceneScreenshotPng.Length > 0).Select(result => result.ScenarioId), StringComparer.Ordinal);
            HashSet<string> completeLegacyScenes = new(StringComparer.Ordinal);
            foreach (IGrouping<string, ScenarioResult> repeats in state.Results.GroupBy(result => result.ScenarioId).Where(group => group.Count() > 1))
            {
                if (explicitSceneIds.Contains(repeats.Key)) continue;
                ScenarioResult first = repeats.First();
                string oldHeading = $"### {first.Group} \u00b7 {first.ScenarioName} \u2014 ";
                int unsuffixed = Regex.Matches(markdown, @"(?m)^" + Regex.Escape(oldHeading) + @"[^\r\n]+\r?$").Count;
                int suffixed = repeats.Count(result => result.Repetition > 1 && HasScene(markdown, result));
                if (unsuffixed == 0) continue;
                if (unsuffixed == repeats.Count() && suffixed == 0) completeLegacyScenes.Add(repeats.Key);
                else if (unsuffixed != 1 || suffixed != repeats.Count() - 1)
                    throw new InvalidDataException($"Cannot repair report: original scene repetition is ambiguous for {repeats.Key}; the legacy report did not record which run had a screenshot.");
            }
            ScenarioResult hero = BenchmarkArtifactRecovery.FindFreeBuildHeroResult(state.Results);
            List<ScenarioResult> sceneRows = state.Results.Where(result => !ReferenceEquals(result, hero)).Where(result =>
                explicitSceneIds.Contains(result.ScenarioId) ? result.SceneScreenshotPng != null && result.SceneScreenshotPng.Length > 0 :
                    completeLegacyScenes.Contains(result.ScenarioId) || HasScene(markdown, result)).ToList();
            Dictionary<ScenarioResult, string> scenes = Sections(markdown, "Scene screenshots",
                sceneRows, BenchmarkReportFormatter.SceneHeading, false, true, completeLegacyScenes);
            foreach (ScenarioResult result in state.Results)
            {
                JObject row = BenchmarkJson.ParseObject(result.ImportedJson);
                bool unique = assignable.Contains(result.ScenarioId);
                if (unique && calls.TryGetValue(result.ScenarioId, out List<TraceCall> trace))
                {
                    if (row["failedToolCalls"] == null)
                        result.FailedToolCalls = trace.Count(call => (string)call.Data["status"] == "failed");
                    if (row["invalidCommands"] == null)
                        result.InvalidCommands = trace.Sum(call => InvalidCommands(call.Data));
                    result.ToolTraceJsonl = string.Concat(trace.Select(call => call.Raw));
                }
                if (statistics.TryGetValue(result.ScenarioName, out Queue<string[]> stats) && stats.Count > 0)
                {
                    string[] cells = stats.Dequeue();
                    if (row["failedToolCalls"] == null)
                        result.FailedToolCalls = int.Parse(cells[5], CultureInfo.InvariantCulture);
                    if (row["invalidCommands"] == null)
                        result.InvalidCommands = int.Parse(cells[6], CultureInfo.InvariantCulture);
                    if (row["tokensFromProvider"] == null)
                        result.TokensFromProvider = !cells[7].StartsWith("~", StringComparison.Ordinal);
                }
                else if (row["tokensFromProvider"] == null)
                {
                    result.TokensFromProvider = result.ProviderPromptTokens.HasValue || result.ProviderCompletionTokens.HasValue;
                }
                if (efficiency.TryGetValue(result.ScenarioName, out Queue<double> savedBonuses) && savedBonuses.Count > 0)
                {
                    double savedEfficiency = savedBonuses.Dequeue();
                    if (row["efficiencyBonus"] == null)
                    {
                        GoalScore score = result.Score;
                        result.Score = GoalScore.Restore(score.CheckpointScore, score.Penalties, score.HardCap,
                            score.JudgeScore, score.JudgeWeight, score.MergedBase,
                            (double?)row["correctnessBonus"] ?? Math.Max(0, score.Bonus - savedEfficiency),
                            score.TokenBonus, score.TimeBonus, savedEfficiency, score.Bonus, score.Base, score.Total, score.Classification);
                    }
                }
                result.ImportedFailureMarkdown = failures.TryGetValue(result, out string failure) ? failure : "";
                result.ImportedSessionMarkdown = sessions.TryGetValue(result, out string session) ? session : "";
                result.ImportedSceneMarkdown = scenes.TryGetValue(result, out string scene) ? scene : "";
                if (markdownProvided && result.Attribution != FailureAttribution.Environment)
                {
                    string missing = result.ImportedSessionMarkdown.Length == 0 ? "session" :
                        failures.ContainsKey(result) || !failedRows.Contains(result) ? "" : "failure section";
                    if (missing.Length == 0 && sceneRows.Contains(result) && result.ImportedSceneMarkdown.Length == 0)
                        missing = "scene section";
                    if (missing.Length > 0)
                        throw new InvalidDataException($"Cannot repair report: original {missing} is missing for {result.ScenarioId} ({result.ScenarioName}, run {result.Repetition}).");
                }
                if (result.ImportedSessionMarkdown.Length > 0)
                {
                    string sessionBody = result.ImportedSessionMarkdown.TrimStart('\r', '\n');
                    int newline = sessionBody.IndexOf('\n');
                    result.SessionTranscript = sessionBody.Substring(newline + 1).Trim('\r', '\n');
                }
                if (result.ImportedSceneMarkdown.Length > 0)
                {
                    Match caption = Regex.Match(result.ImportedSceneMarkdown, @"(?m)^_(.+)_\r?$");
                    if (caption.Success) result.WhatItChecks = caption.Groups[1].Value;
                }
                if (row["penaltyDetails"] == null && result.ImportedFailureMarkdown.Length > 0)
                {
                    List<BenchmarkPenalty> penalties = new();
                    foreach (Match penalty in Regex.Matches(result.ImportedFailureMarkdown,
                        @"(?m)^- [−-]([0-9.]+) penalty: (.*)\r?$"))
                        penalties.Add(new BenchmarkPenalty(penalty.Groups[2].Value.TrimEnd('\r'),
                            double.Parse(penalty.Groups[1].Value, CultureInfo.InvariantCulture)));
                    result.Penalties = penalties;
                }
            }
        }

        private sealed class TraceCall
        {
            public JObject Data;
            public string Raw;
        }

        private static Dictionary<string, List<TraceCall>> ParseCalls(string jsonl)
        {
            Dictionary<string, List<TraceCall>> calls = new(StringComparer.Ordinal);
            foreach (Match line in Regex.Matches(jsonl ?? "", @"[^\n]*\n|[^\n]+$"))
            {
                if (string.IsNullOrWhiteSpace(line.Value)) continue;
                JObject call = BenchmarkJson.ParseObject(line.Value);
                string id = (string)call["scenarioId"] ?? "";
                if (!calls.TryGetValue(id, out List<TraceCall> entries)) calls[id] = entries = new List<TraceCall>();
                entries.Add(new TraceCall { Data = call, Raw = line.Value });
            }
            return calls;
        }

        private static int InvalidCommands(JObject call)
        {
            if ((string)call["name"] != "world_command") return 0;
            try
            {
                JObject arguments = BenchmarkJson.ParseObject((string)call["arguments"] ?? "{}");
                string action = (string)arguments["action"] ?? "";
                if (action.Length == 0) return 1;
                string target = (string)arguments["targetName"] ?? "";
                string prefab = (string)arguments["prefabKeyOrName"] ?? "";
                if (action == "spawn") return target.Length == 0 && prefab.Length == 0 ? 1 : 0;
                if (action != "spawn_batch") return 0;
                JArray items = arguments["items"] as JArray;
                if (items == null || items.Count == 0) return 1;
                return items.Count(item => string.IsNullOrWhiteSpace((string)(item as JObject)?["name"]) &&
                    string.IsNullOrWhiteSpace((string)(item as JObject)?["prefabKey"]) && target.Length == 0 && prefab.Length == 0);
            }
            catch (Newtonsoft.Json.JsonException) { return 1; }
        }

        private static bool HasFailure(ScenarioResult result) =>
            !string.IsNullOrEmpty(result.Failure) || result.Checkpoints.Any(checkpoint => !checkpoint.Passed);

        private static bool HasScene(string markdown, ScenarioResult result)
        {
            string heading = BenchmarkReportFormatter.SessionHeading(result) + " \u2014 ";
            return (result.SceneScreenshotPng != null && result.SceneScreenshotPng.Length > 0) ||
                Regex.IsMatch(markdown, @"(?m)^" + Regex.Escape(heading) + @"[^\r\n]+\r?$");
        }

        private static Dictionary<ScenarioResult, string> Sections(string markdown, string sectionHeading,
            IEnumerable<ScenarioResult> rows, Func<ScenarioResult, string> header, bool legacyFailure = false, bool contains = false,
            HashSet<string> legacySceneIds = null)
        {
            Dictionary<ScenarioResult, string> sections = new();
            Match heading = Regex.Match(markdown, contains
                ? @"(?m)^## [^\r\n]*" + Regex.Escape(sectionHeading) + @"\r?$"
                : @"(?m)^" + Regex.Escape(sectionHeading) + @"\r?$");
            if (!heading.Success) return sections;
            int begin = heading.Index + heading.Length;
            int end = markdown.Length;
            Match boundary = Regex.Match(markdown.Substring(begin),
                @"(?<newline>\r?\n)(?:\r?\n)+(?:---\r?\n## (?:Full model session|[^\r\n]*Scene screenshots)\r?$|\[Tool-call arguments and result previews|\[Replay the model's complete G6 Lua calls\])", RegexOptions.Multiline);
            if (boundary.Success) end = begin + boundary.Index + boundary.Groups["newline"].Length;
            List<KeyValuePair<ScenarioResult, int>> starts = new();
            int cursor = begin;
            foreach (ScenarioResult row in rows)
            {
                string expected = header(row);
                Match found = Regex.Match(markdown.Substring(cursor, end - cursor), @"(?m)^" + Regex.Escape(expected) + @"\r?$");
                if (!found.Success && contains)
                {
                    string scenePrefix = BenchmarkReportFormatter.SessionHeading(row) + " \u2014 ";
                    found = Regex.Match(markdown.Substring(cursor, end - cursor), @"(?m)^" + Regex.Escape(scenePrefix) + @"[^\r\n]+\r?$");
                    if (!found.Success && ((row.SceneScreenshotPng != null && row.SceneScreenshotPng.Length > 0) ||
                        (legacySceneIds != null && legacySceneIds.Contains(row.ScenarioId))))
                        found = Regex.Match(markdown.Substring(cursor, end - cursor), @"(?m)^" +
                            Regex.Escape($"### {row.Group} \u00b7 {row.ScenarioName} \u2014 ") + @"[^\r\n]+\r?$");
                }
                if (!found.Success && legacyFailure)
                    found = Regex.Match(markdown.Substring(cursor, end - cursor), @"(?m)^" + Regex.Escape("### " + row.ScenarioName) + @"\r?$");
                if (!found.Success) continue;
                int position = cursor + found.Index;
                int newlineLength = position >= 2 && markdown[position - 2] == '\r' ? 2 : 1;
                int start = position > begin && markdown[position - 1] == '\n' &&
                    position > newlineLength && markdown[position - newlineLength - 1] == '\n'
                    ? position - newlineLength : position;
                starts.Add(new KeyValuePair<ScenarioResult, int>(row, start));
                cursor = position + found.Length;
            }
            for (int index = 0; index < starts.Count; index++)
            {
                int next = index + 1 < starts.Count ? starts[index + 1].Value : end;
                sections[starts[index].Key] = markdown.Substring(starts[index].Value, next - starts[index].Value);
            }
            return sections;
        }
    }
}
