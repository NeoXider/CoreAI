using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Renders a <see cref="BenchmarkReport"/> to a compact human-readable Markdown table and to
    /// canonical machine-readable JSON. Dependency-free (hand-rolled JSON) so it lives in the
    /// portable core and produces identical output on every host.
    /// </summary>
    public static class BenchmarkReportFormatter
    {
        public static string ToMarkdown(BenchmarkReport report)
        {
            BenchmarkRunMetadata m = report.Metadata;
            StringBuilder sb = new();

            // --- Scorecard: everything that matters about this model, up top ---
            sb.AppendLine($"# 🎮 {m.ModelId} — {F(report.SuiteBaseScore)}/100");
            sb.AppendLine();
            sb.AppendLine($"> **{Grade(report.SuiteBaseScore)}** · " +
                          $"PASS {report.PassCount} / PARTIAL {report.PartialCount} / FAIL {report.FailCount} " +
                          $"· pass-rate {F(report.PassRate * 100)}% · mean bonus {F(report.MeanBonus)} " +
                          $"· reps {m.Repetitions} · {BenchmarkInfo.SuiteName} {BenchmarkInfo.Version}");
            sb.AppendLine();

            sb.Append("- **By group:** ");
            IReadOnlyList<GroupScore> groups = report.GroupBreakdown();
            if (groups.Count == 0)
            {
                sb.Append("_No graded groups._");
            }
            else
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    GroupScore g = groups[i];
                    sb.Append($"{g.Group} {F(g.MeanBase)}/100 ({g.PassCount}/{g.Count} pass)");
                    if (i < groups.Count - 1)
                    {
                        sb.Append(" · ");
                    }
                }
            }

            sb.AppendLine();

            if (report.Best != null)
            {
                sb.AppendLine($"- **Best:** {report.Best.ScenarioName} ({F(report.Best.Score.Base)}) · " +
                              $"**Worst:** {report.Worst.ScenarioName} ({F(report.Worst.Score.Base)})");
            }

            sb.AppendLine($"- **Cost of run:** {report.TotalTokens} tokens " +
                          $"({report.TotalCompletionTokens} generated) · {F(report.GenerationTokensPerSecond)} tok/s provider-call " +
                          $"(prefill+decode; effective {F(report.EffectiveTokensPerSecond)} across the agentic session) · " +
                          $"${F(report.TotalCostUsd)} · {F(report.TotalLatencyMs / 1000.0)} s total");
            sb.AppendLine($"- **Speed/efficiency bonus:** mean +{F(report.MeanEfficiencyBonus)} " +
                          $"(fewer tokens +{F(report.MeanTokenBonus)}, less time +{F(report.MeanTimeBonus)})");
            sb.AppendLine($"- **Model setup:** backend `{m.Backend}` · native-tools {m.NativeToolCalling} · " +
                          $"streaming {m.Streaming} · temp {F(m.Temperature)} · reps {m.Repetitions} · " +
                          $"parallel-tools {m.MaxParallelToolCalls}");
            sb.AppendLine($"- **Run:** `{m.RunId}` ({m.TimestampUtc})" +
                          (string.IsNullOrEmpty(m.UnityVersion) ? "" : $" · Unity {m.UnityVersion} · suite {m.SuiteVersion}"));

            if (report.FrameworkFailures > 0 || report.EnvironmentFailures > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"> ⚠ **Not a clean model measurement:** {report.FrameworkFailures} framework-failure(s), " +
                              $"{report.EnvironmentFailures} environment-failure(s) — see details below.");
            }

            // --- Summary by dimension: the suite split into comparable axes, with bars + a chart ---
            IReadOnlyList<DimensionScore> dims = report.DimensionBreakdown();
            sb.AppendLine();
            sb.AppendLine("## 📐 Summary by dimension");
            sb.AppendLine();
            if (dims.Count == 0)
            {
                sb.AppendLine("_No graded checkpoints._");
            }
            else
            {
                sb.AppendLine(DimensionSummarySvg(dims, report.MeanEfficiencyBonus));
                sb.AppendLine();
                sb.AppendLine(DimensionMermaid(dims));
            }

            // --- Game-fitness by role: the headline "is it usable, and for what" answer ---
            RoleFitness.Result fit = RoleFitness.Evaluate(report);
            sb.AppendLine();
            bool anyAssessed = fit.Roles.Any(r => r.Assessed);
            string overallCell = anyAssessed ? $"{F(fit.Overall)}/10  (best: {fit.BestRole})" : "n/a (partial run)";
            sb.AppendLine($"## 🎯 Game-fitness — {overallCell}");
            sb.AppendLine();
            if (fit.TinyModelWarning)
            {
                sb.AppendLine("> ⚠ **Not suitable for agentic game-dev roles** — tool correctness is below the " +
                              "minimum a model needs to drive tools reliably.");
                sb.AppendLine();
            }

            if (!anyAssessed)
            {
                sb.AppendLine("> ℹ Run the full suite (all groups) for a complete role assessment — this run only " +
                              "measured some dimensions, so each role's score is left unrated below.");
                sb.AppendLine();
            }

            sb.AppendLine("| Role | Fit | Verdict | Why |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (RoleFitness.RoleScore r in fit.Roles)
            {
                string fitCell = r.Assessed ? $"**{F(r.Rating)}/10**" : "—";
                sb.AppendLine($"| {r.Role} | {fitCell} | {RoleVerdict(r.Verdict)} | {r.Reason} |");
            }

            // --- Tool-call statistics: correctness of tool usage, BEFORE the session ---
            sb.AppendLine();
            sb.AppendLine("## 🔧 Tool-call statistics");
            sb.AppendLine();
            sb.AppendLine($"- **Total tool calls:** {report.TotalToolCalls} · " +
                          $"failed {report.TotalFailedToolCalls} · invalid world commands {report.TotalInvalidCommands} · " +
                          $"error-rate {F(report.ToolErrorRate * 100)}%");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Group | Turns | Tool calls | Failed | Invalid | Tokens |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
            foreach (ScenarioResult r in report.Results)
            {
                string tokens = r.TokensFromProvider ? r.TotalTokens.ToString() : $"~{r.TotalTokens}";
                sb.AppendLine($"| {r.ScenarioName} | {r.Group} | {r.Turns} | {r.ToolCalls} | " +
                              $"{r.FailedToolCalls} | {r.InvalidCommands} | {tokens} |");
            }

            // --- Per-scenario median (only meaningful when repetitions > 1) ---
            IReadOnlyList<ScenarioSummary> summaries = report.Scenarios();
            bool repeated = false;
            foreach (ScenarioSummary s in summaries)
            {
                if (s.Repetitions > 1)
                {
                    repeated = true;
                    break;
                }
            }

            if (repeated)
            {
                sb.AppendLine();
                sb.AppendLine("## 📊 Scenario means (average over repetitions)");
                sb.AppendLine();
                sb.AppendLine("| Scenario | Group | Mean base | Mean bonus | Verdict | Reps | Spread |");
                sb.AppendLine("|---|---|---:|---:|---|---:|---:|");
                foreach (ScenarioSummary s in summaries)
                {
                    sb.AppendLine($"| {s.Name} | {s.Group} | {F(s.MeanBase)} | {F(s.MeanBonus)} | " +
                                  $"{VerdictText(s.Classification)} | {s.Repetitions} | {F(s.Spread)} |");
                }
            }

            // --- Scenario scores (each run) ---
            sb.AppendLine();
            sb.AppendLine(repeated ? "## 🏁 Scenario scores (per run)" : "## 🏁 Scenario scores");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Group | Base | Bonus (eff) | Total | Verdict | s |");
            sb.AppendLine("|---|---|---:|---:|---:|---|---:|");
            foreach (ScenarioResult r in report.Results)
            {
                sb.AppendLine($"| {r.ScenarioName} | {r.Group} | {F(r.Score.Base)} | " +
                              $"{F(r.Score.Bonus)} ({F(r.Score.EfficiencyBonus)}) | " +
                              $"{F(r.Score.Total)} | {Verdict(r)} | {F(r.LatencyMs / 1000.0)} |");
            }

            sb.AppendLine();
            sb.AppendLine("_Base 0..100; Bonus = correctness + efficiency (fewer tokens & less time than budget), " +
                          "capped 20. `~tokens` = BPE estimate (provider usage unavailable)._");

            // Failed checkpoints, so a run is debuggable from the artifact alone.
            sb.AppendLine();
            sb.AppendLine("## Failed checkpoints");
            bool anyFailure = false;
            foreach (ScenarioResult r in report.Results)
            {
                List<BenchmarkCheckpoint> failed = new();
                foreach (BenchmarkCheckpoint cp in r.Checkpoints)
                {
                    if (!cp.Passed)
                    {
                        failed.Add(cp);
                    }
                }

                if (failed.Count == 0 && string.IsNullOrEmpty(r.Failure))
                {
                    continue;
                }

                anyFailure = true;
                sb.AppendLine();
                sb.AppendLine($"### {r.ScenarioName}");
                if (!string.IsNullOrEmpty(r.Failure))
                {
                    sb.AppendLine($"- ❌ run failure ({r.Attribution}): {r.Failure}");
                }

                foreach (BenchmarkCheckpoint cp in failed)
                {
                    string detail = string.IsNullOrEmpty(cp.Detail) ? "" : $" — {cp.Detail}";
                    sb.AppendLine($"- [{(cp.Mandatory ? "MANDATORY" : "opt")}] {cp.Description} (w{F(cp.Weight)}){detail}");
                }

                foreach (BenchmarkPenalty p in r.Penalties)
                {
                    sb.AppendLine($"- −{F(p.Points)} penalty: {p.Reason}");
                }
            }

            if (!anyFailure)
            {
                sb.AppendLine();
                sb.AppendLine("_None — every checkpoint passed._");
            }

            // Full model session at the end: the complete per-turn transcript the model produced,
            // so a run can be understood and debugged from the report alone.
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("## Full model session");
            bool anyTranscript = false;
            foreach (ScenarioResult r in report.Results)
            {
                if (string.IsNullOrWhiteSpace(r.SessionTranscript))
                {
                    continue;
                }

                anyTranscript = true;
                sb.AppendLine();
                string repLabel = r.Repetition > 1 ? $" (run {r.Repetition})" : "";
                sb.AppendLine($"### {r.Group} · {r.ScenarioName}{repLabel}");
                sb.AppendLine();
                sb.AppendLine(r.SessionTranscript.TrimEnd());
            }

            if (!anyTranscript)
            {
                sb.AppendLine();
                sb.AppendLine("_No session captured._");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Renders a self-contained SVG "results card" for embedding in the report (a visual of the suite
        /// score, the dimension bars, and the verdict counts). Dependency-free; renders in any SVG viewer
        /// and in Markdown previews that follow image links.
        /// </summary>
        public static string ToSvg(BenchmarkReport report)
        {
            IReadOnlyList<DimensionScore> dims = report.DimensionBreakdown();
            const int width = 560;
            int rows = dims.Count;
            int height = 150 + rows * 26 + 40;

            StringBuilder sb = new();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" ")
              .Append($"viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, Arial, sans-serif\">");
            sb.Append($"<rect width=\"{width}\" height=\"{height}\" rx=\"10\" fill=\"#1e1f24\"/>");

            // Header: model + big suite score.
            sb.Append($"<text x=\"20\" y=\"34\" fill=\"#e8e8ea\" font-size=\"18\" font-weight=\"bold\">")
              .Append(Xml(report.Metadata.ModelId)).Append("</text>");
            string scoreColor = HexColor(report.SuiteBaseScore);
            sb.Append($"<text x=\"{width - 20}\" y=\"44\" fill=\"{scoreColor}\" font-size=\"40\" ")
              .Append($"font-weight=\"bold\" text-anchor=\"end\">{F(report.SuiteBaseScore)}</text>");
            sb.Append($"<text x=\"{width - 20}\" y=\"62\" fill=\"#9aa0a6\" font-size=\"11\" ")
              .Append("text-anchor=\"end\">/ 100</text>");
            sb.Append($"<text x=\"20\" y=\"58\" fill=\"#9aa0a6\" font-size=\"12\">")
              .Append($"PASS {report.PassCount} · PARTIAL {report.PartialCount} · FAIL {report.FailCount} · ")
              .Append($"{F(report.PassRate * 100)}% pass-rate</text>");

            // Dimension bars.
            const int barX = 150;
            const int barW = 320;
            int y = 96;
            foreach (DimensionScore d in dims)
            {
                double pct = Clamp(d.Score, 0, 100);
                sb.Append($"<text x=\"20\" y=\"{y + 11}\" fill=\"#c8ccd0\" font-size=\"12\">")
                  .Append(Xml(DimName(d.Dimension))).Append("</text>");
                sb.Append($"<rect x=\"{barX}\" y=\"{y}\" width=\"{barW}\" height=\"14\" rx=\"3\" fill=\"#33353b\"/>");
                int fill = (int)System.Math.Round(barW * pct / 100.0);
                sb.Append($"<rect x=\"{barX}\" y=\"{y}\" width=\"{fill}\" height=\"14\" rx=\"3\" fill=\"{HexColor(d.Score)}\"/>");
                sb.Append($"<text x=\"{barX + barW + 10}\" y=\"{y + 11}\" fill=\"#e8e8ea\" font-size=\"12\">")
                  .Append(F(d.Score)).Append("</text>");
                y += 26;
            }

            // Footer: speed / tokens.
            sb.Append($"<text x=\"20\" y=\"{y + 20}\" fill=\"#9aa0a6\" font-size=\"11\">")
              .Append($"{report.TotalCompletionTokens} gen tokens · {F(report.GenerationTokensPerSecond)} tok/s · ")
              .Append($"{F(report.TotalLatencyMs / 1000.0)} s · bonus +{F(report.MeanEfficiencyBonus)} ")
              .Append($"(tok +{F(report.MeanTokenBonus)}, time +{F(report.MeanTimeBonus)})</text>");

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string DimensionSummarySvg(IReadOnlyList<DimensionScore> dims, double meanEfficiencyBonus)
        {
            const int width = 640;
            const int left = 174;
            const int barW = 340;
            const int rowH = 28;
            const int top = 24;
            int rows = dims.Count + 1;
            int height = top * 2 + rows * rowH;
            int valueX = left + barW + 14;

            StringBuilder sb = new();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" ")
              .Append($"viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, Arial, sans-serif\">");
            sb.Append($"<rect width=\"{width}\" height=\"{height}\" rx=\"10\" fill=\"#1e1f24\"/>");

            int y = top;
            foreach (DimensionScore d in dims)
            {
                AppendDimensionBar(sb, y, left, barW, valueX, DimName(d.Dimension), d.Score, d.Score, F(d.Score), "/100");
                y += rowH;
            }

            double efficiencyPct = GoalScore.MaxBonus <= 0 ? 0 : meanEfficiencyBonus / GoalScore.MaxBonus * 100.0;
            AppendDimensionBar(
                sb,
                y,
                left,
                barW,
                valueX,
                "Efficiency bonus",
                efficiencyPct,
                efficiencyPct,
                F(meanEfficiencyBonus),
                "/" + F(GoalScore.MaxBonus));

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static void AppendDimensionBar(
            StringBuilder sb,
            int y,
            int left,
            int barW,
            int valueX,
            string label,
            double fillPct,
            double colorScore,
            string value,
            string suffix)
        {
            double pct = Clamp(fillPct, 0, 100);
            int fill = (int)System.Math.Round(barW * pct / 100.0);
            sb.Append($"<text x=\"20\" y=\"{y + 15}\" fill=\"#c8ccd0\" font-size=\"12\">")
              .Append(Xml(label)).Append("</text>");
            sb.Append($"<rect x=\"{left}\" y=\"{y}\" width=\"{barW}\" height=\"16\" rx=\"4\" fill=\"#33353b\"/>");
            sb.Append($"<rect x=\"{left}\" y=\"{y}\" width=\"{fill}\" height=\"16\" rx=\"4\" fill=\"{HexColor(colorScore)}\"/>");
            sb.Append($"<text x=\"{valueX}\" y=\"{y + 13}\" fill=\"#e8e8ea\" font-size=\"12\">")
              .Append(value).Append(Xml(suffix)).Append("</text>");
        }

        private static double Clamp(double value, double min, double max)
        {
            if (!double.IsFinite(value))
            {
                return min;
            }

            return value < min ? min : (value > max ? max : value);
        }

        private static string HexColor(double score)
        {
            if (!double.IsFinite(score))
            {
                score = 0;
            }

            if (score >= 75)
            {
                return "#4cb863";
            }

            return score >= 50 ? "#e8bd44" : "#dc5c57";
        }

        private static string Xml(string s)
        {
            return (s ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        /// <summary>Header for the rolling <c>INDEX.md</c> that lists every run (written once, when missing).</summary>
        public static string IndexHeader()
        {
            StringBuilder sb = new();
            sb.AppendLine($"# {BenchmarkInfo.SuiteName} {BenchmarkInfo.Version} — Index");
            sb.AppendLine();
            sb.AppendLine("Newest runs are appended at the bottom. Click a report to open it.");
            sb.AppendLine();
            sb.AppendLine("| Date (UTC) | Model | Score | Verdict (P/PA/F) | Pass-rate | By group | Tokens | Report |");
            sb.AppendLine("|---|---|---:|---|---:|---|---:|---|");
            return sb.ToString();
        }

        /// <summary>One <c>INDEX.md</c> table row summarizing a run and linking to its report file.</summary>
        public static string IndexRow(BenchmarkReport report, string reportFileName)
        {
            BenchmarkRunMetadata m = report.Metadata;
            StringBuilder groups = new();
            IReadOnlyList<GroupScore> gs = report.GroupBreakdown();
            for (int i = 0; i < gs.Count; i++)
            {
                groups.Append($"{gs[i].Group} {F(gs[i].MeanBase)}");
                if (i < gs.Count - 1)
                {
                    groups.Append(", ");
                }
            }

            return $"| {m.TimestampUtc} | `{m.ModelId}` | **{F(report.SuiteBaseScore)}** | " +
                   $"{report.PassCount}/{report.PartialCount}/{report.FailCount} | {F(report.PassRate * 100)}% | " +
                   $"{groups} | {report.TotalTokens} | [{reportFileName}]({reportFileName}) |";
        }

        public static string ToJson(BenchmarkReport report)
        {
            BenchmarkRunMetadata m = report.Metadata;
            StringBuilder sb = new();
            sb.Append('{');
            sb.Append("\"metadata\":{");
            sb.Append(Str("runId", m.RunId)).Append(',');
            sb.Append(Str("timestampUtc", m.TimestampUtc)).Append(',');
            sb.Append(Str("modelId", m.ModelId)).Append(',');
            sb.Append(Str("backend", m.Backend)).Append(',');
            sb.Append(Bool("nativeToolCalling", m.NativeToolCalling)).Append(',');
            sb.Append(Bool("streaming", m.Streaming)).Append(',');
            sb.Append(Num("maxParallelToolCalls", m.MaxParallelToolCalls)).Append(',');
            sb.Append(Num("temperature", m.Temperature)).Append(',');
            sb.Append(Num("repetitions", m.Repetitions)).Append(',');
            sb.Append(Str("unityVersion", m.UnityVersion)).Append(',');
            sb.Append(Str("suiteVersion", m.SuiteVersion));
            sb.Append("},");

            sb.Append("\"summary\":{");
            sb.Append(Num("suiteBaseScore", report.SuiteBaseScore)).Append(',');
            sb.Append(Num("meanBonus", report.MeanBonus)).Append(',');
            sb.Append(Num("passRate", report.PassRate)).Append(',');
            sb.Append(Num("pass", report.PassCount)).Append(',');
            sb.Append(Num("partial", report.PartialCount)).Append(',');
            sb.Append(Num("fail", report.FailCount)).Append(',');
            sb.Append(Num("frameworkFailures", report.FrameworkFailures)).Append(',');
            sb.Append(Num("environmentFailures", report.EnvironmentFailures)).Append(',');
            sb.Append(Num("totalTokens", report.TotalTokens)).Append(',');
            sb.Append(Num("totalPromptTokens", report.TotalPromptTokens)).Append(',');
            sb.Append(Num("totalCompletionTokens", report.TotalCompletionTokens)).Append(',');
            sb.Append(Num("totalGenerationMs", report.TotalGenerationMs)).Append(',');
            sb.Append(Num("generationTokensPerSecond", report.GenerationTokensPerSecond)).Append(',');
            sb.Append(Num("totalCostUsd", report.TotalCostUsd)).Append(',');
            sb.Append(Num("totalLatencyMs", report.TotalLatencyMs)).Append(',');
            sb.Append(Num("meanEfficiencyBonus", report.MeanEfficiencyBonus)).Append(',');
            sb.Append(Num("meanTokenBonus", report.MeanTokenBonus)).Append(',');
            sb.Append(Num("meanTimeBonus", report.MeanTimeBonus)).Append(',');
            sb.Append(Num("totalToolCalls", report.TotalToolCalls)).Append(',');
            sb.Append(Num("failedToolCalls", report.TotalFailedToolCalls)).Append(',');
            sb.Append(Num("invalidCommands", report.TotalInvalidCommands)).Append(',');
            sb.Append("\"dimensions\":{");
            IReadOnlyList<DimensionScore> djson = report.DimensionBreakdown();
            for (int i = 0; i < djson.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Num(djson[i].Dimension.ToString(), djson[i].Score));
            }

            sb.Append("},");

            RoleFitness.Result fit = RoleFitness.Evaluate(report);
            sb.Append(Num("gameFitOverall", fit.Overall)).Append(',');
            sb.Append(Str("bestRole", fit.BestRole)).Append(',');
            sb.Append("\"roles\":{");
            bool firstRole = true;
            foreach (RoleFitness.RoleScore r in fit.Roles)
            {
                if (!r.Assessed)
                {
                    continue; // omit unrated roles so the history window does not show them as 0
                }

                if (!firstRole)
                {
                    sb.Append(',');
                }

                firstRole = false;
                sb.Append(Num(r.Role, r.Rating));
            }

            sb.Append("}");
            sb.Append("},");

            sb.Append("\"results\":[");
            for (int i = 0; i < report.Results.Count; i++)
            {
                ScenarioResult r = report.Results[i];
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('{');
                sb.Append(Str("scenarioId", r.ScenarioId)).Append(',');
                sb.Append(Str("scenarioName", r.ScenarioName)).Append(',');
                sb.Append(Str("group", r.Group)).Append(',');
                sb.Append(Str("modelId", r.ModelId)).Append(',');
                sb.Append(Num("repetition", r.Repetition)).Append(',');
                sb.Append(Str("classification", r.Classification.ToString())).Append(',');
                sb.Append(Str("attribution", r.Attribution.ToString())).Append(',');
                sb.Append(Num("base", r.Score.Base)).Append(',');
                sb.Append(Num("bonus", r.Score.Bonus)).Append(',');
                sb.Append(Num("total", r.Score.Total)).Append(',');
                sb.Append(Num("checkpointScore", r.Score.CheckpointScore)).Append(',');
                sb.Append(Num("penalties", r.Score.Penalties)).Append(',');
                sb.Append(Num("turns", r.Turns)).Append(',');
                sb.Append(Num("toolCalls", r.ToolCalls)).Append(',');
                sb.Append(Num("promptTokens", r.PromptTokens)).Append(',');
                sb.Append(Num("completionTokens", r.CompletionTokens)).Append(',');
                sb.Append(Num("generationMs", r.GenerationMs)).Append(',');
                sb.Append(Num("latencyMs", r.LatencyMs)).Append(',');
                sb.Append(Num("costUsd", r.CostUsd)).Append(',');
                sb.Append(Bool("costKnown", r.CostKnown)).Append(',');
                sb.Append(Bool("timedOut", r.TimedOut)).Append(',');
                sb.Append(Str("failure", r.Failure)).Append(',');
                sb.Append("\"checkpoints\":[");
                for (int c = 0; c < r.Checkpoints.Count; c++)
                {
                    BenchmarkCheckpoint cp = r.Checkpoints[c];
                    if (c > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('{');
                    sb.Append(Str("id", cp.Id)).Append(',');
                    sb.Append(Str("description", cp.Description)).Append(',');
                    sb.Append(Num("weight", cp.Weight)).Append(',');
                    sb.Append(Bool("passed", cp.Passed)).Append(',');
                    sb.Append(Bool("mandatory", cp.Mandatory)).Append(',');
                    sb.Append(Str("dimension", cp.Dimension.ToString())).Append(',');
                    sb.Append(Str("detail", cp.Detail));
                    sb.Append('}');
                }

                sb.Append(']');
                sb.Append('}');
            }

            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static string Verdict(ScenarioResult r) => VerdictText(r.Classification);

        private static string VerdictText(BenchmarkClassification c)
        {
            return c switch
            {
                BenchmarkClassification.Pass => "✅ PASS",
                BenchmarkClassification.Partial => "🟡 PARTIAL",
                _ => "❌ FAIL"
            };
        }

        private static string RoleVerdict(string verdict)
        {
            return verdict switch
            {
                "Strong fit" => "✅ Strong fit",
                "Usable" => "🟢 Usable",
                "Limited" => "🟡 Limited",
                "Not assessed" => "⚪ Not assessed",
                _ => "❌ Not suitable"
            };
        }

        private static string Grade(double suiteBase)
        {
            if (suiteBase >= 90)
            {
                return "Excellent";
            }

            if (suiteBase >= 75)
            {
                return "Strong";
            }

            if (suiteBase >= 50)
            {
                return "Mixed";
            }

            return suiteBase >= 25 ? "Weak" : "Failing";
        }

        /// <summary>Unicode progress bar for a 0..100 percentage (renders in any Markdown viewer).</summary>
        private static string Bar(double pct, int width = 24)
        {
            if (double.IsNaN(pct))
            {
                pct = 0;
            }

            pct = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
            int filled = (int)System.Math.Round(pct / 100.0 * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        private static string DimName(BenchmarkDimension d)
        {
            return d switch
            {
                BenchmarkDimension.ToolCorrectness => "Tool correctness",
                BenchmarkDimension.IntentSequence => "Intent & sequence",
                BenchmarkDimension.TaskCompletion => "Task completion",
                BenchmarkDimension.Determinism => "Determinism",
                BenchmarkDimension.Reasoning => "Reasoning",
                BenchmarkDimension.InstructionAdherence => "Instruction adherence",
                _ => d.ToString()
            };
        }

        private static string DimShort(BenchmarkDimension d)
        {
            return d switch
            {
                BenchmarkDimension.ToolCorrectness => "Tools",
                BenchmarkDimension.IntentSequence => "Intent",
                BenchmarkDimension.TaskCompletion => "Task",
                BenchmarkDimension.Determinism => "Determ",
                BenchmarkDimension.Reasoning => "Reason",
                BenchmarkDimension.InstructionAdherence => "Instr",
                _ => d.ToString()
            };
        }

        /// <summary>A Mermaid bar chart of the dimension scores (renders on GitHub and many MD viewers).</summary>
        private static string DimensionMermaid(IReadOnlyList<DimensionScore> dims)
        {
            StringBuilder x = new();
            StringBuilder y = new();
            for (int i = 0; i < dims.Count; i++)
            {
                if (i > 0)
                {
                    x.Append(", ");
                    y.Append(", ");
                }

                x.Append($"\"{DimShort(dims[i].Dimension)}\"");
                y.Append(F(dims[i].Score));
            }

            StringBuilder sb = new();
            sb.AppendLine("```mermaid");
            sb.AppendLine("xychart-beta");
            sb.AppendLine("    title \"Scores by dimension\"");
            sb.AppendLine($"    x-axis [{x}]");
            sb.AppendLine("    y-axis \"Score\" 0 --> 100");
            sb.AppendLine($"    bar [{y}]");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static string F(double v)
        {
            if (!double.IsFinite(v))
            {
                v = 0;
            }

            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Str(string key, string value)
        {
            return $"\"{key}\":\"{Escape(value ?? string.Empty)}\"";
        }

        private static string Num(string key, double value)
        {
            // JSON has no NaN/Infinity literal — emit 0 so the document stays parseable.
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0;
            }

            return $"\"{key}\":{value.ToString("0.######", CultureInfo.InvariantCulture)}";
        }

        private static string Bool(string key, bool value)
        {
            return $"\"{key}\":{(value ? "true" : "false")}";
        }

        private static string Escape(string value)
        {
            StringBuilder sb = new(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }
    }
}
