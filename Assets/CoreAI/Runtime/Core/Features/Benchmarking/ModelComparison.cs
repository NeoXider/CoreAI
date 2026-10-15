using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// A single model's headline numbers, extracted from one benchmark run. Hosts build these from the
    /// machine-readable JSON reports (<see cref="ModelComparison.ParseSummaryJson"/>) or directly via
    /// <see cref="From"/>, and feed them to <see cref="ModelComparison.Build"/> for a cross-model report.
    /// </summary>
    public sealed class ModelSummary
    {
        public string ModelId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;

        /// <summary>Suite version stamped into the run (scores compare only within one version).</summary>
        public string SuiteVersion { get; set; } = string.Empty;

        /// <summary>The run's <c>reasoning_effort</c>; empty for the provider default or an older report.</summary>
        public string ReasoningEffort { get; set; } = string.Empty;

        public int Repetitions { get; set; } = 1;
        public double SuiteBase { get; set; }
        public double PassRate { get; set; }
        public int Pass { get; set; }
        public int Partial { get; set; }
        public int Fail { get; set; }
        public long TotalTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public double TotalGenerationMs { get; set; }
        public double TotalLatencyMs { get; set; }
        public double MeanEfficiencyBonus { get; set; }
        public double MeanTokenBonus { get; set; }
        public double MeanTimeBonus { get; set; }
        public double ToolErrorRate { get; set; }

        /// <summary>Distinct graded scenarios behind <see cref="SuiteBase"/>, both tiers.</summary>
        public int Scenarios { get; set; }

        /// <summary>Mean base over the graded regular-tier scenarios (equals the suite score for runs without an extreme tier).</summary>
        public double RegularBase { get; set; }

        public int RegularScenarios { get; set; }

        /// <summary>Mean base over the graded extreme scenarios; meaningful only when <see cref="HasExtreme"/>.</summary>
        public double ExtremeBase { get; set; }

        public int ExtremeScenarios { get; set; }
        public int ExtremePass { get; set; }

        /// <summary>True when the run graded at least one extreme-tier scenario.</summary>
        public bool HasExtreme => ExtremeScenarios > 0;

        /// <summary>Generation throughput: COMPLETION tokens per wall-clock second (the real "tokens/sec").</summary>
        public double TokensPerSecond
        {
            get
            {
                double elapsedMs = TotalGenerationMs > 0 ? TotalGenerationMs : TotalLatencyMs;
                return elapsedMs <= 0 ? 0 : TotalCompletionTokens / (elapsedMs / 1000.0);
            }
        }

        public double GameFitOverall { get; set; }
        public string BestRole { get; set; } = "";

        /// <summary>Per-role 0..10 fitness (key = role name).</summary>
        public Dictionary<string, double> Roles { get; set; } = new();

        /// <summary>Per-dimension scores (key = <see cref="BenchmarkDimension"/> name).</summary>
        public Dictionary<string, double> Dimensions { get; set; } = new();

        /// <summary>Per-group mean scores over both tiers (key = group id, e.g. "G1").</summary>
        public Dictionary<string, double> Groups { get; set; } = new();

        /// <summary>Per-group extreme-scenario mean, only for groups whose extreme scenario was graded.</summary>
        public Dictionary<string, double> ExtremeGroups { get; set; } = new();

        public static ModelSummary From(BenchmarkReport report)
        {
            ModelSummary s = new()
            {
                ModelId = report.Metadata.ModelId,
                RunId = report.Metadata.RunId,
                TimestampUtc = report.Metadata.TimestampUtc,
                SuiteVersion = report.Metadata.SuiteVersion,
                ReasoningEffort = report.Metadata.ReasoningEffort ?? string.Empty,
                Repetitions = report.Metadata.Repetitions,
                SuiteBase = report.SuiteBaseScore,
                PassRate = report.PassRate,
                Pass = report.PassCount,
                Partial = report.PartialCount,
                Fail = report.FailCount,
                TotalTokens = report.TotalTokens,
                TotalCompletionTokens = report.TotalCompletionTokens,
                TotalGenerationMs = report.TotalGenerationMs,
                TotalLatencyMs = report.TotalLatencyMs,
                MeanEfficiencyBonus = report.MeanEfficiencyBonus,
                MeanTokenBonus = report.MeanTokenBonus,
                MeanTimeBonus = report.MeanTimeBonus,
                ToolErrorRate = report.ToolErrorRate,
                Scenarios = report.ScenarioCount,
                RegularBase = report.RegularBaseScore,
                RegularScenarios = report.RegularScenarioCount,
                ExtremeBase = report.ExtremeBaseScore,
                ExtremeScenarios = report.ExtremeScenarioCount,
                ExtremePass = report.ExtremePassCount
            };

            foreach (DimensionScore d in report.DimensionBreakdown())
            {
                s.Dimensions[d.Dimension.ToString()] = d.Score;
            }

            RoleFitness.Result fit = RoleFitness.Evaluate(report);
            s.GameFitOverall = fit.Overall;
            s.BestRole = fit.BestRole;
            foreach (RoleFitness.RoleScore r in fit.Roles)
            {
                s.Roles[r.Role] = r.Rating;
            }

            foreach (GroupScore g in report.GroupBreakdown())
            {
                s.Groups[g.Group] = g.MeanBase;
                if (g.HasExtreme)
                {
                    s.ExtremeGroups[g.Group] = g.ExtremeMeanBase;
                }
            }

            return s;
        }
    }

    /// <summary>
    /// Builds a cross-model comparison report (ranking table, group and extreme-tier tables, a Mermaid chart)
    /// and its SVG charts from several <see cref="ModelSummary"/> rows. Pure/dependency-free so it lives in
    /// the core.
    /// </summary>
    public static class ModelComparison
    {
        private static readonly string[] DimensionOrder =
        {
            nameof(BenchmarkDimension.ToolCorrectness), nameof(BenchmarkDimension.IntentSequence),
            nameof(BenchmarkDimension.TaskCompletion), nameof(BenchmarkDimension.Determinism),
            nameof(BenchmarkDimension.Reasoning), nameof(BenchmarkDimension.InstructionAdherence)
        };

        public static string Build(IReadOnlyList<ModelSummary> models, string title = "Model comparison",
            string pinnedModelId = null)
        {
            StringBuilder sb = new();
            sb.AppendLine($"# 🏆 CoreAI Benchmark — {title}");
            sb.AppendLine();

            if (models == null || models.Count == 0)
            {
                sb.AppendLine("_No model reports found._");
                return sb.ToString();
            }

            List<ModelSummary> ranked = RankModels(models, pinnedModelId);
            List<string> dimKeys = DimensionOrder.Where(d => ranked.Any(m => m.Dimensions.ContainsKey(d))).ToList();
            List<string> groupKeys = GroupKeys(ranked);
            bool anyExtreme = ranked.Any(m => m.HasExtreme);

            sb.AppendLine($"{ranked.Count} model(s), ranked by suite score; ties are broken by the extreme-tier " +
                          "score, the sub-score that still separates models once the regular tier saturates.");
            List<string> suites = ranked.Select(m => m.SuiteVersion).Where(v => !string.IsNullOrEmpty(v))
                .Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (suites.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine($"> ⚠ Mixed suite versions ({string.Join(", ", suites.Select(v => "v" + v))}): " +
                              "scores are only comparable within one suite version.");
            }

            sb.AppendLine();
            sb.AppendLine("![Game-Creation Benchmark](COMPARISON.svg)");
            sb.AppendLine();

            sb.Append("| # | Model | Suite | Extreme | Regular | Pass-rate | P/PA/F |");
            foreach (string d in dimKeys)
            {
                sb.Append($" {Short(d)} |");
            }

            sb.AppendLine(" Eff | Tool-err | Tokens | Run |");
            sb.Append("|---:|---|---:|---:|---:|---:|---|");
            foreach (string _ in dimKeys)
            {
                sb.Append("---:|");
            }

            sb.AppendLine("---:|---:|---:|---|");

            for (int i = 0; i < ranked.Count; i++)
            {
                ModelSummary m = ranked[i];
                // WHY: a summary built without tier data (an older host parser) has no regular count; a dash
                // there is honest where 0 would read as a failed tier.
                string regular = m.RegularScenarios > 0 ? F(m.RegularBase) : "–";
                sb.Append($"| {i + 1} | `{m.ModelId}` | **{F(m.SuiteBase)}** | {ExtremeCell(m)} | " +
                          $"{regular} | {F(m.PassRate * 100)}% | {m.Pass}/{m.Partial}/{m.Fail} |");
                foreach (string d in dimKeys)
                {
                    sb.Append($" {(m.Dimensions.TryGetValue(d, out double v) ? F(v) : "–")} |");
                }

                sb.AppendLine($" {F(m.MeanEfficiencyBonus)} | {F(m.ToolErrorRate * 100)}% | {m.TotalTokens} | " +
                              $"`{m.RunId}` |");
            }

            if (groupKeys.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Scores by group");
                sb.AppendLine();
                sb.Append("| Model |");
                foreach (string g in groupKeys)
                {
                    sb.Append($" {g} |");
                }

                sb.AppendLine(" Extreme |");
                sb.Append("|---|");
                foreach (string _ in groupKeys)
                {
                    sb.Append("---:|");
                }

                sb.AppendLine("---:|");
                foreach (ModelSummary m in ranked)
                {
                    sb.Append($"| `{m.ModelId}` |");
                    foreach (string g in groupKeys)
                    {
                        string cell = m.Groups.TryGetValue(g, out double v) ? F(v) : "–";
                        if (m.ExtremeGroups.TryGetValue(g, out double ev))
                        {
                            cell += $" (ext {F(ev)})";
                        }

                        sb.Append($" {cell} |");
                    }

                    sb.AppendLine($" {(m.HasExtreme ? F(m.ExtremeBase) : "–")} |");
                }
            }

            if (anyExtreme)
            {
                sb.AppendLine();
                sb.AppendLine("## Extreme tier ranking");
                sb.AppendLine();
                sb.AppendLine("| # | Model | Extreme | Extreme pass | Suite |");
                sb.AppendLine("|---:|---|---:|---:|---:|");
                List<ModelSummary> byExtreme = RankByExtreme(ranked, pinnedModelId);
                for (int i = 0; i < byExtreme.Count; i++)
                {
                    ModelSummary m = byExtreme[i];
                    sb.AppendLine($"| {i + 1} | `{m.ModelId}` | **{F(m.ExtremeBase)}** | " +
                                  $"{m.ExtremePass}/{m.ExtremeScenarios} | {F(m.SuiteBase)} |");
                }
            }

            sb.AppendLine();
            sb.AppendLine(SuiteMermaid(ranked));

            if (dimKeys.Count > 0 || anyExtreme)
            {
                sb.AppendLine();
                sb.AppendLine("## Best per dimension");
                sb.AppendLine();
                foreach (string d in dimKeys)
                {
                    ModelSummary best = ranked
                        .Where(m => m.Dimensions.ContainsKey(d))
                        .OrderByDescending(m => m.Dimensions[d])
                        .FirstOrDefault();
                    if (best != null)
                    {
                        sb.AppendLine($"- **{Short(d)}:** `{best.ModelId}` ({F(best.Dimensions[d])}/100)");
                    }
                }

                ModelSummary bestExtreme = ranked.Where(m => m.HasExtreme).OrderByDescending(m => m.ExtremeBase)
                    .FirstOrDefault();
                if (bestExtreme != null)
                {
                    sb.AppendLine($"- **Extreme tier:** `{bestExtreme.ModelId}` ({F(bestExtreme.ExtremeBase)}/100, " +
                                  $"{bestExtreme.ExtremePass}/{bestExtreme.ExtremeScenarios} pass)");
                }
            }

            return sb.ToString();
        }

        public static string ToComparisonSvg(IReadOnlyList<ModelSummary> models)
        {
            return ToComparisonSvg(models, null);
        }

        /// <summary>
        /// The comparison chart: the suite-score ranking with each model's extreme-tier score on the same row,
        /// then the models × groups heatmap (with an Extreme column) and the extreme-tier ranking when the
        /// summaries carry that data. One SVG, light and dark via <c>prefers-color-scheme</c>.
        /// </summary>
        public static string ToComparisonSvg(IReadOnlyList<ModelSummary> models, string pinnedModelId = null)
        {
            return ComparisonScene(models, pinnedModelId).ToSvg();
        }

        /// <summary>The comparison chart of <see cref="ToComparisonSvg(IReadOnlyList{ModelSummary}, string)"/> as a display list.</summary>
        public static BenchmarkCardScene ComparisonScene(IReadOnlyList<ModelSummary> models, string pinnedModelId = null)
        {
            List<ModelSummary> ranked = RankModels(models, pinnedModelId);
            BenchmarkCardScene sc = new(ChartWidth, 0, $"{BenchmarkInfo.TitleWithVersion} — model comparison",
                DescribeRanking(ranked));
            double y = Header(sc, $"{BenchmarkInfo.TitleWithVersion} — model comparison",
                "Suite base score 0–100, ranked best-first; ties broken by the extreme-tier score.");
            if (ranked.Count == 0)
            {
                sc.Text(ChartWidth / 2, y + 40, "No model reports found", 14, CardInk.Muted, CardAnchor.Middle);
                sc.Height = y + 72;
                return sc;
            }

            y = SuiteRanking(sc, ranked, pinnedModelId, y);
            if (GroupKeys(ranked).Count > 0)
            {
                y = GroupHeatmap(sc, ranked, pinnedModelId, y + 28);
            }

            if (ranked.Any(m => m.HasExtreme))
            {
                y = ExtremeRanking(sc, RankByExtreme(ranked, pinnedModelId), pinnedModelId, y + 28);
            }

            sc.Height = Footer(sc, ranked, y + 20);
            return sc;
        }

        /// <summary>
        /// Models × groups heatmap: one cell per group G1–G8 plus an Extreme column, binned on a five-step
        /// ordinal blue ramp (&lt;50, 50–74.9, 75–89.9, 90–99.9, 100) with the score printed in every cell.
        /// </summary>
        public static string ToGroupHeatmapSvg(IReadOnlyList<ModelSummary> models, string pinnedModelId = null)
        {
            List<ModelSummary> ranked = RankModels(models, pinnedModelId);
            BenchmarkCardScene sc = new(ChartWidth, 0, $"{BenchmarkInfo.TitleWithVersion} — score by group");
            double y = Header(sc, $"{BenchmarkInfo.TitleWithVersion} — score by group",
                "Mean base score per group; the Extreme column is the extreme-tier sub-score.");
            y = ranked.Count == 0 || GroupKeys(ranked).Count == 0
                ? Empty(sc, y, "No per-group scores in these reports")
                : GroupHeatmap(sc, ranked, pinnedModelId, y);
            sc.Height = y + 24;
            return sc.ToSvg();
        }

        /// <summary>Extreme-tier ranking: models ordered by their extreme sub-score, pinned model first.</summary>
        public static string ToExtremeRankingSvg(IReadOnlyList<ModelSummary> models, string pinnedModelId = null)
        {
            List<ModelSummary> ranked = RankModels(models, pinnedModelId);
            BenchmarkCardScene sc = new(ChartWidth, 0, $"{BenchmarkInfo.TitleWithVersion} — extreme tier");
            double y = Header(sc, $"{BenchmarkInfo.TitleWithVersion} — extreme tier",
                "Mean base over the extreme scenarios (one per group, none for G6).");
            y = ranked.Any(m => m.HasExtreme)
                ? ExtremeRanking(sc, RankByExtreme(ranked, pinnedModelId), pinnedModelId, y)
                : Empty(sc, y, "No extreme-tier scores in these reports");
            sc.Height = y + 24;
            return sc.ToSvg();
        }

        private const double ChartWidth = 960;
        private const double Pad = 24;
        private const double LabelW = 300;

        private static double Header(BenchmarkCardScene sc, string title, string subtitle)
        {
            sc.Text(Pad, 36, title, 20, CardInk.Ink, CardAnchor.Start, true, ChartWidth - 2 * Pad);
            sc.Text(Pad, 58, subtitle, 12.5, CardInk.Ink2, CardAnchor.Start, false, ChartWidth - 2 * Pad);
            return 76;
        }

        private static double Empty(BenchmarkCardScene sc, double y, string message)
        {
            sc.Text(ChartWidth / 2, y + 36, message, 14, CardInk.Muted, CardAnchor.Middle);
            return y + 56;
        }

        /// <summary>Model label column: id (bold) above a muted sub-line; an ink rule marks the pinned model.</summary>
        private static void ModelLabel(BenchmarkCardScene sc, ModelSummary m, int rank, double top, double rowH,
            bool pinned, string sub)
        {
            if (pinned)
            {
                sc.Rect(Pad - 10, top + 5, 3, rowH - 10, CardInk.Ink, 1.5);
            }

            sc.Text(Pad + 18, top + rowH / 2 - 2, rank.ToString(CultureInfo.InvariantCulture), 12, CardInk.Muted,
                CardAnchor.End).Tabular = true;
            sc.Text(Pad + 28, top + rowH / 2 - 2, m.ModelId, 13, CardInk.Ink, CardAnchor.Start, true, LabelW - 36);
            sc.Text(Pad + 28, top + rowH / 2 + 13, pinned ? "pinned · " + sub : sub, 11, CardInk.Muted,
                CardAnchor.Start, false, LabelW - 36);
        }

        private static double SuiteRanking(BenchmarkCardScene sc, List<ModelSummary> ranked, string pinnedModelId,
            double y)
        {
            const double rowH = 40;
            const double barX = Pad + LabelW;
            const double suiteColW = 64;
            const double extColW = 84;
            double barW = ChartWidth - Pad - barX - suiteColW - extColW - 12;
            bool anyExtreme = ranked.Any(m => m.HasExtreme);
            double barH = 14;

            sc.Text(barX, y + 14, "Suite score", 13, CardInk.Ink, CardAnchor.Start, true);
            if (anyExtreme)
            {
                double lx = barX + BenchmarkCardScene.TextWidth("Suite score", 13, true) + 18;
                sc.Diamond(lx + 5, y + 10, 5.5, CardInk.Extreme);
                sc.Text(lx + 15, y + 14, "extreme-tier score", 11.5, CardInk.Ink2);
            }

            double colX = barX + barW + 12;
            sc.Text(colX + suiteColW, y + 14, "Suite", 11.5, CardInk.Muted, CardAnchor.End);
            if (anyExtreme)
            {
                sc.Text(colX + suiteColW + extColW, y + 14, "Extreme", 11.5, CardInk.Muted, CardAnchor.End);
            }

            double top = y + 24;
            for (int i = 0; i < ranked.Count; i++)
            {
                ModelSummary m = ranked[i];
                double rowTop = top + i * rowH;
                double cy = rowTop + rowH / 2;
                bool pinned = IsPinned(m, pinnedModelId);
                ModelLabel(sc, m, i + 1, rowTop, rowH, pinned, $"P/PA/F {m.Pass}/{m.Partial}/{m.Fail}" +
                                                         (string.IsNullOrEmpty(m.SuiteVersion) ? "" : $" · suite v{m.SuiteVersion}"));
                MeterRow(sc, barX, cy, barW, barH, m.SuiteBase, CardInk.Series, CardInk.SeriesTrack,
                    $"{m.ModelId}: suite {F(m.SuiteBase)}");
                if (m.HasExtreme)
                {
                    sc.Diamond(barX + barW * Clamp(m.ExtremeBase, 0, 100) / 100.0, cy, 7, CardInk.Extreme, 2).Tooltip =
                        $"{m.ModelId}: extreme tier {F(m.ExtremeBase)} ({m.ExtremePass}/{m.ExtremeScenarios} pass)";
                }

                sc.Text(colX + suiteColW, cy + 4.5, F1(m.SuiteBase), 13, CardInk.Ink, CardAnchor.End, true).Tabular =
                    true;
                if (anyExtreme)
                {
                    sc.Text(colX + suiteColW + extColW, cy + 4.5, m.HasExtreme ? F1(m.ExtremeBase) : "–", 13,
                        m.HasExtreme ? CardInk.Ink2 : CardInk.Muted, CardAnchor.End).Tabular = true;
                }
            }

            double axisY = top + ranked.Count * rowH + 14;
            AxisLabels(sc, barX, barW, axisY, 11);
            return axisY + 4;
        }

        /// <summary>A 0–100 meter: track, surface notches at 50 and 90, then the value bar.</summary>
        private static void MeterRow(BenchmarkCardScene sc, double x, double cy, double width, double h, double value,
            CardInk ink, CardInk track, string tooltip)
        {
            double top = cy - h / 2;
            sc.Rect(x, top, width, h, track, h / 2);
            sc.Rect(x + width * 0.5 - 1, top, 2, h, CardInk.Surface);
            sc.Rect(x + width * 0.9 - 1, top, 2, h, CardInk.Surface);
            sc.Bar(x, top, Math.Max(width * Clamp(value, 0, 100) / 100.0, 2), h, ink, Math.Min(4, h / 2)).Tooltip =
                tooltip;
        }

        private static void AxisLabels(BenchmarkCardScene sc, double x, double width, double baseline, double size)
        {
            sc.Text(x, baseline, "0", size, CardInk.Muted);
            sc.Text(x + width * 0.5, baseline, "50", size, CardInk.Muted, CardAnchor.Middle);
            sc.Text(x + width * 0.9, baseline, "90 pass", size, CardInk.Muted, CardAnchor.Middle);
            sc.Text(x + width, baseline, "100", size, CardInk.Muted, CardAnchor.End);
        }

        private static double GroupHeatmap(BenchmarkCardScene sc, List<ModelSummary> ranked, string pinnedModelId,
            double y)
        {
            List<string> groups = GroupKeys(ranked);
            bool anyExtreme = ranked.Any(m => m.HasExtreme);
            const double rowH = 36;
            const double gap = 2;
            const double heatX = Pad + LabelW;
            double extraGap = anyExtreme ? 14 : 0;
            int columns = groups.Count + (anyExtreme ? 1 : 0);
            double cellW = Math.Min(72, (ChartWidth - Pad - heatX - extraGap) / columns);

            sc.Text(Pad, y + 14, "Score by group", 13, CardInk.Ink, CardAnchor.Start, true);
            sc.Text(Pad + BenchmarkCardScene.TextWidth("Score by group", 13, true) + 12, y + 14,
                "mean base per group; numbers in every cell", 11.5, CardInk.Ink2);
            double headY = y + 40;
            for (int c = 0; c < groups.Count; c++)
            {
                double cx = heatX + c * cellW + cellW / 2;
                sc.Text(cx, headY, groups[c], 11.5, CardInk.Ink2, CardAnchor.Middle, true);
            }

            double extX = heatX + groups.Count * cellW + extraGap;
            if (anyExtreme)
            {
                sc.Text(extX + cellW / 2 + 6, headY, "Extreme", 11.5, CardInk.Ink2, CardAnchor.Middle, true);
                sc.Diamond(extX + cellW / 2 - BenchmarkCardScene.TextWidth("Extreme", 11.5, true) / 2 - 2, headY - 4,
                    4.5, CardInk.Extreme);
            }

            double top = headY + 8;
            for (int i = 0; i < ranked.Count; i++)
            {
                ModelSummary m = ranked[i];
                double rowTop = top + i * rowH;
                ModelLabel(sc, m, i + 1, rowTop, rowH, IsPinned(m, pinnedModelId),
                    $"suite {F1(m.SuiteBase)}");
                for (int c = 0; c < groups.Count; c++)
                {
                    string g = groups[c];
                    bool has = m.Groups.TryGetValue(g, out double v);
                    HeatCell(sc, heatX + c * cellW, rowTop, cellW, rowH, gap, has, v, $"{m.ModelId} {g}");
                }

                if (anyExtreme)
                {
                    HeatCell(sc, extX, rowTop, cellW, rowH, gap, m.HasExtreme, m.ExtremeBase,
                        $"{m.ModelId} extreme tier");
                }
            }

            double legendY = top + ranked.Count * rowH + 22;
            double lx = heatX;
            sc.Text(lx, legendY, "Score:", 11, CardInk.Ink2);
            lx += BenchmarkCardScene.TextWidth("Score:", 11) + 10;
            for (int bin = 5; bin >= 1; bin--)
            {
                string label = BenchmarkCardScene.HeatBinLabel(bin);
                sc.Rect(lx, legendY - 10, 14, 12, BenchmarkCardScene.HeatFill(bin), 2);
                sc.Text(lx + 18, legendY, label, 11, CardInk.Ink2);
                lx += 18 + BenchmarkCardScene.TextWidth(label, 11) + 14;
            }

            sc.Rect(lx, legendY - 10, 14, 12, CardInk.Track, 2);
            sc.Text(lx + 18, legendY, "not run", 11, CardInk.Ink2);
            return legendY + 4;
        }

        private static void HeatCell(BenchmarkCardScene sc, double x, double rowTop, double cellW, double rowH,
            double gap, bool has, double value, string tooltip)
        {
            double cx = x + gap / 2;
            double cy = rowTop + gap / 2;
            double w = cellW - gap;
            double h = rowH - gap;
            if (!has)
            {
                sc.Rect(cx, cy, w, h, CardInk.Track, 3);
                sc.Text(cx + w / 2, cy + h / 2 + 4, "–", 12, CardInk.Muted, CardAnchor.Middle);
                return;
            }

            int bin = BenchmarkCardScene.HeatBin(value);
            sc.Rect(cx, cy, w, h, BenchmarkCardScene.HeatFill(bin), 3).Tooltip = $"{tooltip}: {F1(value)}";
            string label = value >= 99.95 ? "100" : F1(value);
            sc.Text(cx + w / 2, cy + h / 2 + 4.5, label, 12.5, BenchmarkCardScene.HeatText(bin), CardAnchor.Middle,
                true).Tabular = true;
        }

        private static double ExtremeRanking(BenchmarkCardScene sc, List<ModelSummary> byExtreme, string pinnedModelId,
            double y)
        {
            const double rowH = 36;
            const double barX = Pad + LabelW;
            const double valueColW = 150;
            double barW = ChartWidth - Pad - barX - valueColW - 12;
            sc.Text(Pad, y + 14, "Extreme tier ranking", 13, CardInk.Ink, CardAnchor.Start, true);
            sc.Text(Pad + BenchmarkCardScene.TextWidth("Extreme tier ranking", 13, true) + 12, y + 14,
                "mean of the extreme scenarios, one per group (none for G6)", 11.5, CardInk.Ink2);
            double top = y + 24;
            for (int i = 0; i < byExtreme.Count; i++)
            {
                ModelSummary m = byExtreme[i];
                double rowTop = top + i * rowH;
                double cy = rowTop + rowH / 2;
                ModelLabel(sc, m, i + 1, rowTop, rowH, IsPinned(m, pinnedModelId), $"suite {F1(m.SuiteBase)}");
                if (!m.HasExtreme)
                {
                    sc.Text(barX, cy + 4, "no extreme tier in this run", 11.5, CardInk.Muted);
                    continue;
                }

                MeterRow(sc, barX, cy, barW, 14, m.ExtremeBase, CardInk.Extreme, CardInk.Track,
                    $"{m.ModelId}: extreme tier {F(m.ExtremeBase)}");
                sc.Text(barX + barW + 12 + 52, cy + 4.5, F1(m.ExtremeBase), 13, CardInk.Ink, CardAnchor.End, true)
                    .Tabular = true;
                sc.Text(barX + barW + 12 + 62, cy + 4.5, $"{m.ExtremePass}/{m.ExtremeScenarios} pass", 11.5,
                    CardInk.Ink2);
            }

            double axisY = top + byExtreme.Count * rowH + 14;
            AxisLabels(sc, barX, barW, axisY, 11);
            return axisY + 4;
        }

        private static double Footer(BenchmarkCardScene sc, List<ModelSummary> ranked, double y)
        {
            List<string> suites = ranked.Select(m => m.SuiteVersion).Where(v => !string.IsNullOrEmpty(v)).Distinct()
                .OrderBy(v => v, StringComparer.Ordinal).ToList();
            string suiteNote = suites.Count == 0
                ? "Suite version not recorded."
                : suites.Count == 1
                    ? $"Suite v{suites[0]}."
                    : $"Mixed suite versions ({string.Join(", ", suites.Select(v => "v" + v))}): compare only within one version.";
            sc.Line(Pad, y, ChartWidth - Pad, y, CardInk.Grid);
            sc.Text(Pad, y + 18, suiteNote + " Bars are blue meters (notches at 50 and 90 = pass); speed is " +
                                 "reported in the table, never scored.", 11, CardInk.Muted, CardAnchor.Start, false,
                ChartWidth - 2 * Pad);
            return y + 32;
        }

        private static string DescribeRanking(List<ModelSummary> ranked)
        {
            StringBuilder sb = new();
            for (int i = 0; i < ranked.Count; i++)
            {
                ModelSummary m = ranked[i];
                sb.Append($"{i + 1}. {m.ModelId}: suite {F1(m.SuiteBase)}");
                if (m.HasExtreme)
                {
                    sb.Append($", extreme {F1(m.ExtremeBase)}");
                }

                sb.Append(". ");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Groups present in any summary, canonical G1–G8 order first, unknown ids after.</summary>
        private static List<string> GroupKeys(List<ModelSummary> ranked)
        {
            HashSet<string> present = new(ranked.SelectMany(m => m.Groups.Keys), StringComparer.Ordinal);
            List<string> keys = BenchmarkInfo.Groups.Where(present.Contains).ToList();
            keys.AddRange(present.Where(g => !BenchmarkInfo.Groups.Contains(g)).OrderBy(g => g, StringComparer.Ordinal));
            return keys;
        }

        private static List<ModelSummary> RankModels(IReadOnlyList<ModelSummary> models, string pinnedModelId)
        {
            if (models == null || models.Count == 0)
            {
                return new List<ModelSummary>();
            }

            List<ModelSummary> ranked = models
                .Where(m => m != null)
                .OrderByDescending(m => m.SuiteBase)
                .ThenByDescending(m => m.HasExtreme ? m.ExtremeBase : -1)
                .ThenBy(m => m.ModelId, StringComparer.Ordinal)
                .ToList();
            return PinFirst(ranked, pinnedModelId);
        }

        private static List<ModelSummary> RankByExtreme(List<ModelSummary> ranked, string pinnedModelId)
        {
            List<ModelSummary> byExtreme = ranked
                .OrderByDescending(m => m.HasExtreme ? 1 : 0)
                .ThenByDescending(m => m.HasExtreme ? m.ExtremeBase : 0)
                .ThenByDescending(m => m.SuiteBase)
                .ThenBy(m => m.ModelId, StringComparer.Ordinal)
                .ToList();
            return PinFirst(byExtreme, pinnedModelId);
        }

        private static List<ModelSummary> PinFirst(List<ModelSummary> ranked, string pinnedModelId)
        {
            if (string.IsNullOrEmpty(pinnedModelId))
            {
                return ranked;
            }

            ModelSummary pinned = ranked.FirstOrDefault(m => IsPinned(m, pinnedModelId));
            if (pinned == null)
            {
                return ranked;
            }

            ranked.Remove(pinned);
            ranked.Insert(0, pinned);
            return ranked;
        }

        private static string ExtremeCell(ModelSummary m)
        {
            return m.HasExtreme ? $"{F(m.ExtremeBase)} ({m.ExtremePass}/{m.ExtremeScenarios})" : "–";
        }

        private static string SuiteMermaid(IReadOnlyList<ModelSummary> ranked)
        {
            StringBuilder x = new();
            StringBuilder y = new();
            for (int i = 0; i < ranked.Count; i++)
            {
                if (i > 0)
                {
                    x.Append(", ");
                    y.Append(", ");
                }

                x.Append($"\"{Trunc(ranked[i].ModelId, 14)}\"");
                y.Append(F(ranked[i].SuiteBase));
            }

            StringBuilder sb = new();
            sb.AppendLine("```mermaid");
            sb.AppendLine("xychart-beta");
            sb.AppendLine("    title \"Suite score by model\"");
            sb.AppendLine($"    x-axis [{x}]");
            sb.AppendLine("    y-axis \"Score\" 0 --> 100");
            sb.AppendLine($"    bar [{y}]");
            sb.AppendLine("```");
            return sb.ToString();
        }

        /// <summary>
        /// Parses one benchmark report JSON (as written by <see cref="BenchmarkReportFormatter.ToJson"/>) into a
        /// <see cref="ModelSummary"/>; null when the text is not a parseable report. Older reports without the
        /// group and tier fields still parse: their per-group means and regular/extreme split are recomputed from
        /// the <c>results</c> array with the suite's own rules (Environment/Framework/NotGraded excluded,
        /// repetitions averaged per scenario), and a report without <c>extreme</c> flags has no extreme tier.
        /// </summary>
        public static ModelSummary ParseSummaryJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            Dictionary<string, object> root;
            try
            {
                root = JsonReader.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                return null;
            }

            Dictionary<string, object> meta = Obj(root, "metadata");
            Dictionary<string, object> sum = Obj(root, "summary");
            if (meta == null || sum == null)
            {
                return null;
            }

            ModelSummary s = new()
            {
                ModelId = Str(meta, "modelId") ?? "unknown",
                RunId = Str(meta, "runId") ?? string.Empty,
                TimestampUtc = Str(meta, "timestampUtc") ?? string.Empty,
                SuiteVersion = Str(meta, "suiteVersion") ?? string.Empty,
                ReasoningEffort = Str(meta, "reasoningEffort") ?? string.Empty,
                Repetitions = (int)Num(meta, "repetitions", 1),
                SuiteBase = Num(sum, "suiteBaseScore"),
                PassRate = Num(sum, "passRate"),
                Pass = (int)Num(sum, "pass"),
                Partial = (int)Num(sum, "partial"),
                Fail = (int)Num(sum, "fail"),
                TotalTokens = (long)Num(sum, "totalTokens"),
                TotalCompletionTokens = (long)Num(sum, "totalCompletionTokens"),
                TotalGenerationMs = Num(sum, "totalGenerationMs"),
                TotalLatencyMs = Num(sum, "totalLatencyMs"),
                MeanEfficiencyBonus = Num(sum, "meanEfficiencyBonus"),
                MeanTokenBonus = Num(sum, "meanTokenBonus"),
                MeanTimeBonus = Num(sum, "meanTimeBonus"),
                GameFitOverall = Num(sum, "gameFitOverall"),
                BestRole = Str(sum, "bestRole") ?? string.Empty
            };

            double totalCalls = Num(sum, "totalToolCalls");
            double failedCalls = Num(sum, "failedToolCalls");
            s.ToolErrorRate = totalCalls <= 0 ? 0 : failedCalls / totalCalls;

            CopyNumbers(Obj(sum, "roles"), s.Roles);
            CopyNumbers(Obj(sum, "dimensions"), s.Dimensions);

            ResultAggregate fromResults = ResultAggregate.From(Arr(root, "results"));
            Dictionary<string, object> groups = Obj(sum, "groups");
            if (groups != null)
            {
                foreach (KeyValuePair<string, object> kv in groups)
                {
                    if (kv.Value is Dictionary<string, object> g)
                    {
                        s.Groups[kv.Key] = Num(g, "meanBase");
                        if (Num(g, "extremeScenarios") > 0)
                        {
                            s.ExtremeGroups[kv.Key] = Num(g, "extremeBase");
                        }
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, double> kv in fromResults.Groups)
                {
                    s.Groups[kv.Key] = kv.Value;
                }

                foreach (KeyValuePair<string, double> kv in fromResults.ExtremeGroups)
                {
                    s.ExtremeGroups[kv.Key] = kv.Value;
                }
            }

            if (sum.ContainsKey("extremeScenarios"))
            {
                s.Scenarios = (int)Num(sum, "scenarios", fromResults.Scenarios);
                s.RegularBase = Num(sum, "regularBaseScore");
                s.RegularScenarios = (int)Num(sum, "regularScenarios");
                s.ExtremeBase = Num(sum, "extremeBaseScore");
                s.ExtremeScenarios = (int)Num(sum, "extremeScenarios");
                s.ExtremePass = (int)Num(sum, "extremePass");
            }
            else
            {
                s.Scenarios = fromResults.Scenarios;
                s.RegularBase = fromResults.RegularScenarios > 0 ? fromResults.RegularBase : s.SuiteBase;
                s.RegularScenarios = fromResults.RegularScenarios;
                s.ExtremeBase = fromResults.ExtremeBase;
                s.ExtremeScenarios = fromResults.ExtremeScenarios;
                s.ExtremePass = fromResults.ExtremePass;
            }

            return s;
        }

        /// <summary>
        /// Recomputes per-group and per-tier means from a report's raw <c>results</c> array, mirroring
        /// <see cref="BenchmarkReport.Scenarios"/>: graded results only, repetitions averaged per scenario,
        /// PASS when the scenario mean reaches <see cref="GoalScore.PassBase"/>.
        /// </summary>
        private sealed class ResultAggregate
        {
            public int Scenarios;
            public double RegularBase;
            public int RegularScenarios;
            public double ExtremeBase;
            public int ExtremeScenarios;
            public int ExtremePass;
            public readonly Dictionary<string, double> Groups = new();
            public readonly Dictionary<string, double> ExtremeGroups = new();

            public static ResultAggregate From(List<object> results)
            {
                ResultAggregate agg = new();
                if (results == null)
                {
                    return agg;
                }

                Dictionary<string, (string Group, bool Extreme, double Sum, int Count)> perScenario = new();
                List<string> order = new();
                foreach (object item in results)
                {
                    if (!(item is Dictionary<string, object> r))
                    {
                        continue;
                    }

                    string attribution = Str(r, "attribution") ?? string.Empty;
                    if (attribution == nameof(FailureAttribution.Environment)
                        || attribution == nameof(FailureAttribution.Framework)
                        || attribution == nameof(FailureAttribution.NotGraded))
                    {
                        continue;
                    }

                    string id = Str(r, "scenarioId") ?? string.Empty;
                    bool extreme = r.TryGetValue("extreme", out object e) && e is bool b && b;
                    if (!perScenario.TryGetValue(id, out (string Group, bool Extreme, double Sum, int Count) acc))
                    {
                        acc = (Str(r, "group") ?? string.Empty, false, 0, 0);
                        order.Add(id);
                    }

                    perScenario[id] = (acc.Group, acc.Extreme || extreme, acc.Sum + Num(r, "base"), acc.Count + 1);
                }

                List<(string Group, bool Extreme, double Mean)> scenarios = order
                    .Select(id => perScenario[id])
                    .Select(a => (a.Group, a.Extreme, a.Sum / a.Count))
                    .ToList();
                agg.Scenarios = scenarios.Count;
                List<double> regular = scenarios.Where(x => !x.Extreme).Select(x => x.Mean).ToList();
                List<double> extremes = scenarios.Where(x => x.Extreme).Select(x => x.Mean).ToList();
                agg.RegularScenarios = regular.Count;
                agg.RegularBase = regular.Count == 0 ? 0 : regular.Average();
                agg.ExtremeScenarios = extremes.Count;
                agg.ExtremeBase = extremes.Count == 0 ? 0 : extremes.Average();
                agg.ExtremePass = extremes.Count(v => v >= GoalScore.PassBase);
                foreach (IGrouping<string, (string Group, bool Extreme, double Mean)> g in scenarios.GroupBy(x => x.Group))
                {
                    agg.Groups[g.Key] = g.Average(x => x.Mean);
                    List<double> ge = g.Where(x => x.Extreme).Select(x => x.Mean).ToList();
                    if (ge.Count > 0)
                    {
                        agg.ExtremeGroups[g.Key] = ge.Average();
                    }
                }

                return agg;
            }
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
        }

        private static List<object> Arr(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? v as List<object> : null;
        }

        private static string Str(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? v as string : null;
        }

        private static double Num(Dictionary<string, object> o, string key, double fallback = 0)
        {
            if (o == null || !o.TryGetValue(key, out object v))
            {
                return fallback;
            }

            return v is double d && double.IsFinite(d) ? d : fallback;
        }

        private static void CopyNumbers(Dictionary<string, object> source, Dictionary<string, double> target)
        {
            if (source == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> kv in source)
            {
                if (kv.Value is double d)
                {
                    target[kv.Key] = d;
                }
            }
        }

        /// <summary>
        /// Minimal JSON reader for report files: objects become <c>Dictionary&lt;string, object&gt;</c>, arrays
        /// <c>List&lt;object&gt;</c>, numbers <c>double</c>. Throws <see cref="FormatException"/> on malformed input.
        /// </summary>
        private sealed class JsonReader
        {
            private const int MaxDepth = 64;
            private readonly string _s;
            private int _i;

            private JsonReader(string s)
            {
                _s = s;
            }

            public static object Parse(string s)
            {
                JsonReader r = new(s);
                r.SkipWhitespace();
                object value = r.ReadValue(0);
                r.SkipWhitespace();
                if (r._i != s.Length)
                {
                    throw new FormatException($"Unexpected trailing content at {r._i}.");
                }

                return value;
            }

            private object ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new FormatException("JSON nesting is too deep.");
                }

                if (_i >= _s.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                char c = _s[_i];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return ReadString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        return ReadNumber();
                }
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                Dictionary<string, object> o = new(StringComparer.Ordinal);
                _i++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                    return o;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new FormatException($"Expected a property name at {_i}.");
                    }

                    string key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new FormatException($"Expected ':' at {_i}.");
                    }

                    _i++;
                    SkipWhitespace();
                    o[key] = ReadValue(depth + 1);
                    SkipWhitespace();
                    char next = Peek();
                    _i++;
                    if (next == '}')
                    {
                        return o;
                    }

                    if (next != ',')
                    {
                        throw new FormatException($"Expected ',' or '}}' at {_i - 1}.");
                    }
                }
            }

            private List<object> ReadArray(int depth)
            {
                List<object> a = new();
                _i++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _i++;
                    return a;
                }

                while (true)
                {
                    SkipWhitespace();
                    a.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    char next = Peek();
                    _i++;
                    if (next == ']')
                    {
                        return a;
                    }

                    if (next != ',')
                    {
                        throw new FormatException($"Expected ',' or ']' at {_i - 1}.");
                    }
                }
            }

            private string ReadString()
            {
                StringBuilder sb = new();
                _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (_i >= _s.Length)
                    {
                        break;
                    }

                    char esc = _s[_i++];
                    switch (esc)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            sb.Append(esc);
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            if (_i + 4 > _s.Length
                                || !int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out int code))
                            {
                                throw new FormatException($"Bad \\u escape at {_i}.");
                            }

                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw new FormatException($"Bad escape '\\{esc}' at {_i - 1}.");
                    }
                }

                throw new FormatException("Unterminated string.");
            }

            private double ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0)
                {
                    _i++;
                }

                if (_i == start
                    || !double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double v))
                {
                    throw new FormatException($"Unexpected character at {start}.");
                }

                return v;
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException($"Expected '{literal}' at {_i}.");
                }

                _i += literal.Length;
            }

            private char Peek()
            {
                if (_i >= _s.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                return _s[_i];
            }

            private void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }
            }
        }

        private static string Short(string dimensionName)
        {
            return dimensionName switch
            {
                nameof(BenchmarkDimension.ToolCorrectness) => "Tools",
                nameof(BenchmarkDimension.IntentSequence) => "Intent",
                nameof(BenchmarkDimension.TaskCompletion) => "Task",
                nameof(BenchmarkDimension.Determinism) => "Determ",
                nameof(BenchmarkDimension.Reasoning) => "Reason",
                nameof(BenchmarkDimension.InstructionAdherence) => "Instr",
                _ => dimensionName
            };
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s ?? "";
            }

            return s.Substring(0, max - 1) + "…";
        }

        private static bool IsPinned(ModelSummary model, string pinnedModelId)
        {
            return model != null && string.Equals(model.ModelId, pinnedModelId, StringComparison.Ordinal);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return min;
            }

            return value < min ? min : value > max ? max : value;
        }

        private static string F(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v) ? 0d : v).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string F1(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v) ? 0d : v).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
