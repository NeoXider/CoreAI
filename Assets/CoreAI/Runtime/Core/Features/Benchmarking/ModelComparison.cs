using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// A single model's headline numbers, extracted from one benchmark run. Hosts build these from the
    /// machine-readable JSON reports (or directly via <see cref="From"/>) and feed them to
    /// <see cref="ModelComparison.Build"/> to produce a cross-model comparison report.
    /// </summary>
    public sealed class ModelSummary
    {
        public string ModelId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
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

        /// <summary>Per-group mean scores (key = group id, e.g. "G1").</summary>
        public Dictionary<string, double> Groups { get; set; } = new();

        public static ModelSummary From(BenchmarkReport report)
        {
            ModelSummary s = new()
            {
                ModelId = report.Metadata.ModelId,
                RunId = report.Metadata.RunId,
                TimestampUtc = report.Metadata.TimestampUtc,
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
                ToolErrorRate = report.ToolErrorRate
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
            }

            return s;
        }
    }

    /// <summary>
    /// Builds a cross-model comparison report (ranking table, per-dimension bars, and a Mermaid chart)
    /// from several <see cref="ModelSummary"/> rows. Pure/dependency-free so it lives in the core.
    /// </summary>
    public static class ModelComparison
    {
        public static string Build(IReadOnlyList<ModelSummary> models, string title = "Model comparison", string pinnedModelId = null)
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

            // Stable union of dimension keys in enum order.
            List<string> dimKeys = new();
            foreach (BenchmarkDimension d in new[]
                     {
                         BenchmarkDimension.ToolCorrectness, BenchmarkDimension.IntentSequence,
                         BenchmarkDimension.TaskCompletion, BenchmarkDimension.Determinism,
                         BenchmarkDimension.Reasoning, BenchmarkDimension.InstructionAdherence
                     })
            {
                if (ranked.Any(m => m.Dimensions.ContainsKey(d.ToString())))
                {
                    dimKeys.Add(d.ToString());
                }
            }

            sb.AppendLine($"{ranked.Count} model(s), ranked by suite score.");
            sb.AppendLine();
            sb.AppendLine("![Game-Creation Benchmark](COMPARISON.svg)");
            sb.AppendLine();

            // Ranking table.
            sb.Append("| # | Model | Suite | Pass-rate | P/PA/F |");
            foreach (string d in dimKeys)
            {
                sb.Append($" {Short(d)} |");
            }

            sb.AppendLine(" Eff | Tool-err | Tokens | Run |");

            sb.Append("|---:|---|---:|---:|---|");
            foreach (string _ in dimKeys)
            {
                sb.Append("---:|");
            }

            sb.AppendLine("---:|---:|---:|---|");

            for (int i = 0; i < ranked.Count; i++)
            {
                ModelSummary m = ranked[i];
                sb.Append($"| {i + 1} | `{m.ModelId}` | **{F(m.SuiteBase)}** | {F(m.PassRate * 100)}% | " +
                          $"{m.Pass}/{m.Partial}/{m.Fail} |");
                foreach (string d in dimKeys)
                {
                    sb.Append($" {(m.Dimensions.TryGetValue(d, out double v) ? F(v) : "–")} |");
                }

                sb.AppendLine($" {F(m.MeanEfficiencyBonus)} | {F(m.ToolErrorRate * 100)}% | {m.TotalTokens} | " +
                              $"`{m.RunId}` |");
            }

            // Mermaid bar chart of suite scores.
            sb.AppendLine();
            sb.AppendLine(SuiteMermaid(ranked));

            // Per-dimension winner highlights.
            if (dimKeys.Count > 0)
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
            }

            return sb.ToString();
        }

        public static string ToComparisonSvg(IReadOnlyList<ModelSummary> models)
        {
            return ToComparisonSvg(models, null);
        }

        public static string ToComparisonSvg(IReadOnlyList<ModelSummary> models, string pinnedModelId = null)
        {
            List<ModelSummary> ranked = RankModels(models, pinnedModelId);
            int count = ranked.Count;
            const int minWidth = 700;
            const int barSlot = 110;
            const int left = 72;
            const int right = 34;
            const int top = 72;
            const int chartHeight = 260;
            const int axisY = top + chartHeight;
            const int labelHeight = 100;
            int width = System.Math.Max(minWidth, left + System.Math.Max(1, count * barSlot) + right);
            int height = axisY + labelHeight + 24;
            // Spread the bars evenly across the WHOLE plot area so they never cluster on the left.
            int plotWidth = width - left - right;
            int slot = count == 0 ? plotWidth : plotWidth / count;
            int barWidth = System.Math.Min(64, System.Math.Max(28, (int)(slot * 0.5)));

            StringBuilder sb = new();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" ")
              .Append($"viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, Arial, sans-serif\">");
            sb.Append($"<rect width=\"{width}\" height=\"{height}\" rx=\"10\" fill=\"#1e1f24\"/>");
            sb.Append($"<text x=\"24\" y=\"38\" fill=\"#e8e8ea\" font-size=\"22\" font-weight=\"bold\">{BenchmarkInfo.TitleWithVersion}</text>");
            sb.Append("<text x=\"24\" y=\"58\" fill=\"#9aa0a6\" font-size=\"12\">Suite base score (0–100, higher is better), ranked best-first</text>");

            for (int tick = 0; tick <= 100; tick += 25)
            {
                int y = axisY - (int)System.Math.Round(chartHeight * tick / 100.0);
                string gridColor = tick == 0 ? "#5a5f68" : "#33353b";
                sb.Append($"<line x1=\"{left}\" y1=\"{y}\" x2=\"{width - right}\" y2=\"{y}\" stroke=\"{gridColor}\" stroke-width=\"1\"/>");
                sb.Append($"<text x=\"{left - 12}\" y=\"{y + 4}\" fill=\"#9aa0a6\" font-size=\"11\" text-anchor=\"end\">{tick}%</text>");
            }

            sb.Append($"<line x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{axisY}\" stroke=\"#5a5f68\" stroke-width=\"1\"/>");

            for (int i = 0; i < count; i++)
            {
                ModelSummary model = ranked[i];
                double pct = Clamp(model.SuiteBase, 0, 100);
                int slotX = left + i * slot;
                int centerX = slotX + slot / 2;
                int barH = (int)System.Math.Round(chartHeight * pct / 100.0);
                int barX = centerX - barWidth / 2;
                int barY = axisY - barH;
                bool pinned = IsPinned(model, pinnedModelId);
                string fill = pinned ? "#65d6ff" : HexColor(model.SuiteBase);
                string stroke = pinned ? "#e8e8ea" : "none";
                int strokeWidth = pinned ? 2 : 0;

                sb.Append($"<rect x=\"{barX}\" y=\"{barY}\" width=\"{barWidth}\" height=\"{barH}\" rx=\"5\" fill=\"{fill}\" ")
                  .Append($"stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"/>");
                sb.Append($"<text x=\"{centerX}\" y=\"{barY - 8}\" fill=\"#e8e8ea\" font-size=\"12\" font-weight=\"bold\" text-anchor=\"middle\">")
                  .Append(F(model.SuiteBase)).Append("</text>");
                sb.Append($"<text x=\"{centerX - 4}\" y=\"{axisY + 24}\" fill=\"#c8ccd0\" font-size=\"11\" text-anchor=\"end\" ")
                  .Append($"transform=\"rotate(-30 {centerX - 4} {axisY + 24})\">")
                  .Append(Xml(Trunc(model.ModelId, 24))).Append("</text>");
            }

            if (count == 0)
            {
                sb.Append($"<text x=\"{width / 2}\" y=\"{top + chartHeight / 2}\" fill=\"#9aa0a6\" font-size=\"14\" text-anchor=\"middle\">No model reports found</text>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static List<ModelSummary> RankModels(IReadOnlyList<ModelSummary> models, string pinnedModelId)
        {
            if (models == null || models.Count == 0)
            {
                return new List<ModelSummary>();
            }

            List<ModelSummary> ranked = models
                .OrderByDescending(m => m.SuiteBase)
                .ThenBy(m => m.ModelId)
                .ToList();

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

        private static string Bar(double pct, int width = 24)
        {
            pct = double.IsNaN(pct) ? 0 : (pct < 0 ? 0 : (pct > 100 ? 100 : pct));
            int filled = (int)System.Math.Round(pct / 100.0 * width);
            return new string('█', filled) + new string('░', width - filled);
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
            return model != null && string.Equals(model.ModelId, pinnedModelId, System.StringComparison.Ordinal);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return min;
            }

            return value < min ? min : (value > max ? max : value);
        }

        private static string HexColor(double score)
        {
            if (double.IsNaN(score) || double.IsInfinity(score))
            {
                return "#dc5c57";
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

        private static string F(double v) =>
            (double.IsNaN(v) || double.IsInfinity(v) ? 0d : v).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
