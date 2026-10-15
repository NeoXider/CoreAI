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

            sb.AppendLine($"# 🎮 {m.ModelId} — {F(report.SuiteBaseScore)}/100");
            sb.AppendLine();
            sb.AppendLine($"> **{Grade(report.SuiteBaseScore)}** · " +
                          $"PASS {report.PassCount} / PARTIAL {report.PartialCount} / FAIL {report.FailCount} " +
                          $"of {report.ScenarioCount} graded scenarios " +
                          $"· pass-rate {F(report.PassRate * 100)}% · mean bonus {F(report.MeanBonus)} " +
                          $"· reps {m.Repetitions} · {BenchmarkInfo.SuiteName} {BenchmarkInfo.Version}");
            sb.AppendLine();

            sb.AppendLine($"- **Scores:** suite **{F(report.SuiteBaseScore)}**/100 · regular tier " +
                          $"{F(report.RegularBaseScore)}/100 ({report.RegularScenarioCount} scenarios) · " +
                          (report.HasExtreme
                              ? $"extreme tier **{F(report.ExtremeBaseScore)}**/100 " +
                                $"({report.ExtremePassCount}/{report.ExtremeScenarioCount} pass)"
                              : "extreme tier n/a (no extreme scenario graded)"));

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
            // WHY: Speed is a REPORTED metric (see provider-call/effective tok/s above), never a score — a faster
            // model must not look "smarter". The bonus rewards correctness only.
            sb.AppendLine($"- **Model setup:** backend `{m.Backend}` · native-tools {m.NativeToolCalling} · " +
                          $"streaming {m.Streaming} · temp {F(m.Temperature)} · " +
                          $"reasoning {(string.IsNullOrEmpty(m.ReasoningEffort) ? "provider default" : m.ReasoningEffort)} · " +
                          $"reps {m.Repetitions} · " +
                          $"parallel-tools {m.MaxParallelToolCalls}");
            sb.AppendLine($"- **Run:** `{m.RunId}` ({m.TimestampUtc})" +
                          (string.IsNullOrEmpty(m.UnityVersion)
                              ? ""
                              : $" · Unity {m.UnityVersion} · suite {m.SuiteVersion}"));

            if (report.FrameworkFailures > 0 || report.EnvironmentFailures > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    $"> ⚠ **Not a clean model measurement:** {report.FrameworkFailures} framework-failure(s), " +
                    $"{report.EnvironmentFailures} environment-failure(s) — see details below.");
            }

            AppendGroupTable(sb, groups);
            AppendExtremeSection(sb, report);

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
                    sb.AppendLine(
                        $"- [{(cp.Mandatory ? "MANDATORY" : "opt")}] {cp.Description} (w{F(cp.Weight)}){detail}");
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
        /// Renders the compact SVG results card embedded at the top of the report: model, suite score and
        /// grade, extreme-tier score, verdicts, speed, per-group bars G1–G8 with extreme markers, dimension
        /// mini-bars, suite version and date. One file carries the light and the dark theme
        /// (<c>prefers-color-scheme</c>), so it reads correctly in either GitHub theme. Dependency-free.
        /// </summary>
        public static string ToSvg(BenchmarkReport report)
        {
            return ResultsCardScene(report).ToSvg();
        }

        /// <summary>The compact results card of <see cref="ToSvg"/> as a display list.</summary>
        public static BenchmarkCardScene ResultsCardScene(BenchmarkReport report)
        {
            BenchmarkRunMetadata m = report.Metadata;
            const double w = 720;
            const double pad = 20;
            BenchmarkCardScene sc = new(w, 400, $"{BenchmarkInfo.SuiteName} results: {m.ModelId}", Describe(report));

            string meta = SuiteAndDate(m);
            double metaW = BenchmarkCardScene.TextWidth(meta, 12);
            sc.Text(w - pad, 34, meta, 12, CardInk.Muted, CardAnchor.End);
            sc.Text(pad, 34, m.ModelId, 18, CardInk.Ink, CardAnchor.Start, true, w - 2 * pad - metaW - 16);
            string subtitle = string.IsNullOrEmpty(m.RunId)
                ? BenchmarkInfo.SuiteName
                : $"{BenchmarkInfo.SuiteName} · run {m.RunId}";
            sc.Text(pad, 54, subtitle, 12, CardInk.Ink2, CardAnchor.Start, false, w - 2 * pad);
            sc.Line(pad, 68, w - pad, 68, CardInk.Grid);

            double tileW = (w - 2 * pad) / 4;
            KpiTiles(sc, report, pad, 76, tileW, 11.5, 28, 12, 11.5, 2);
            sc.Line(pad, 154, w - pad, 154, CardInk.Grid);

            const double leftW = 384;
            const double rightX = pad + leftW + 28;
            const double rightW = w - pad - rightX;
            const double titleY = 178;
            sc.Text(pad, titleY, "Score by group", 13, CardInk.Ink, CardAnchor.Start, true);
            if (report.HasExtreme)
            {
                GroupLegend(sc, pad + leftW, titleY, 11);
            }

            double groupsBottom = GroupRows(sc, report.GroupBreakdown(), pad, titleY + 10, leftW, 22, 11.5, 2);

            sc.Text(rightX, titleY, "Dimensions", 13, CardInk.Ink, CardAnchor.Start, true);
            double dimsBottom = DimensionBars(sc, report.DimensionBreakdown(), rightX, titleY + 10, rightW, 22, 11.5);

            double bottom = System.Math.Max(groupsBottom, dimsBottom) + 14;
            sc.Line(pad, bottom, w - pad, bottom, CardInk.Grid);
            sc.Text(pad, bottom + 20, SpeedFooter(report), 11, CardInk.Muted, CardAnchor.Start, false, w - 2 * pad);
            sc.Height = bottom + 34;
            return sc;
        }

        /// <summary>
        /// The 16:9 model card (1600x900 scene units) a Unity host rasterizes into <c>_modelcard.png</c> and
        /// <see cref="ToModelCardSvg"/> writes as vector: headline tiles (suite, extreme tier, verdicts,
        /// game-fit), per-group bars with the extreme overlay, a dimension dot plot on a 50–100 axis (so 90 and
        /// 100 sit a fifth of the axis apart), role-fitness bars, and a tokens/speed footer.
        /// </summary>
        public static BenchmarkCardScene ModelCardScene(BenchmarkReport report)
        {
            BenchmarkRunMetadata m = report.Metadata;
            const double w = 1600;
            const double h = 900;
            const double pad = 48;
            BenchmarkCardScene sc = new(w, h, $"{BenchmarkInfo.SuiteName} model card: {m.ModelId}", Describe(report));

            string brand = BenchmarkInfo.SuiteName;
            double brandW = BenchmarkCardScene.TextWidth(brand, 24);
            sc.Text(w - pad, 80, brand, 24, CardInk.Muted, CardAnchor.End);
            sc.Text(pad, 84, m.ModelId, 46, CardInk.Ink, CardAnchor.Start, true, w - 2 * pad - brandW - 40);
            sc.Text(pad, 124, ModelCardMetaLine(report), 24, CardInk.Ink2, CardAnchor.Start, false, w - 2 * pad);
            sc.Line(pad, 150, w - pad, 150, CardInk.Grid, 2);

            KpiTiles(sc, report, pad, 172, (w - 2 * pad) / 4, 24, 84, 30, 24, 4);
            sc.Line(pad, 360, w - pad, 360, CardInk.Grid, 2);

            const double titleY = 404;
            const double captionY = 436;
            const double rowsTop = 452;
            const double groupsX = pad;
            const double groupsW = 560;
            const double dimsX = groupsX + groupsW + 48;
            const double dimsW = 504;
            const double rolesX = dimsX + dimsW + 48;
            const double rolesW = w - pad - rolesX;

            sc.Text(groupsX, titleY, "Score by group", 28, CardInk.Ink, CardAnchor.Start, true);
            if (report.HasExtreme)
            {
                GroupLegend(sc, groupsX + groupsW, captionY, 22);
            }
            else
            {
                sc.Text(groupsX, captionY, "mean base per group, 0–100", 22, CardInk.Muted);
            }

            GroupRows(sc, report.GroupBreakdown(), groupsX, rowsTop, groupsW, 42, 24, 4);

            sc.Text(dimsX, titleY, "Dimensions", 28, CardInk.Ink, CardAnchor.Start, true);
            sc.Text(dimsX, captionY, "dot plot, axis 50–100, line at 90 = pass", 22, CardInk.Muted,
                CardAnchor.Start, false, dimsW);
            DimensionDots(sc, report.DimensionBreakdown(), dimsX, rowsTop, dimsW, 56, 24);

            RoleFitness.Result fit = RoleFitness.Evaluate(report);
            sc.Text(rolesX, titleY, "Game-fitness by role", 28, CardInk.Ink, CardAnchor.Start, true);
            sc.Text(rolesX, captionY, "rating 0–10 per game-dev role", 22, CardInk.Muted, CardAnchor.Start, false,
                rolesW);
            RoleRows(sc, fit, rolesX, rowsTop, rolesW, 56, 24);

            sc.Line(pad, 846, w - pad, 846, CardInk.Grid, 2);
            sc.Text(pad, 880, SpeedFooter(report), 22, CardInk.Muted, CardAnchor.Start, false, w - 2 * pad);
            return sc;
        }

        /// <summary>The model card as a vector SVG — the headless twin of the Unity-rendered PNG.</summary>
        public static string ToModelCardSvg(BenchmarkReport report, CardTheme theme = CardTheme.Auto)
        {
            return ModelCardScene(report).ToSvg(theme);
        }

        /// <summary>Four headline tiles: suite score, extreme tier, scenario verdicts, and game-fit.</summary>
        private static void KpiTiles(BenchmarkCardScene sc, BenchmarkReport report, double x, double y, double tileW,
            double labelSize, double valueSize, double unitSize, double subSize, double scale)
        {
            RoleFitness.Result fit = RoleFitness.Evaluate(report);
            bool anyAssessed = fit.Roles.Any(r => r.Assessed);
            double labelY = y + labelSize;
            double valueY = labelY + valueSize * 0.98;
            double subY = valueY + subSize * 2.1;
            double inner = 12 * scale / 2;

            for (int i = 1; i < 4; i++)
            {
                double dx = x + i * tileW;
                sc.Line(dx, y + 2, dx, subY + subSize * 0.3, CardInk.Grid, scale / 2);
            }

            Tile(sc, x, labelY, valueY, subY, tileW - inner, "Suite score", F1(report.SuiteBaseScore), "/100",
                $"{Grade(report.SuiteBaseScore)} · regular tier {F1(report.RegularBaseScore)}",
                labelSize, valueSize, unitSize, subSize);

            double tx = x + tileW + inner;
            if (report.HasExtreme)
            {
                Tile(sc, tx, labelY, valueY, subY, tileW - 2 * inner, "Extreme tier", F1(report.ExtremeBaseScore),
                    "/100", $"{report.ExtremePassCount} of {report.ExtremeScenarioCount} scenarios pass",
                    labelSize, valueSize, unitSize, subSize);
                double labelW = BenchmarkCardScene.TextWidth("Extreme tier", labelSize);
                sc.Diamond(tx + labelW + labelSize * 0.75, labelY - labelSize * 0.33, labelSize * 0.36,
                    CardInk.Extreme);
            }
            else
            {
                Tile(sc, tx, labelY, valueY, subY, tileW - 2 * inner, "Extreme tier", "–", string.Empty,
                    "not in this run", labelSize, valueSize, unitSize, subSize);
            }

            tx = x + 2 * tileW + inner;
            int total = report.PassCount + report.PartialCount + report.FailCount;
            Tile(sc, tx, labelY, valueY, subY, tileW - 2 * inner, "Scenario verdicts",
                $"{report.PassCount}/{total}", "pass",
                $"{report.PartialCount} partial · {report.FailCount} fail", labelSize, valueSize, unitSize, subSize);
            double vbY = valueY + subSize * 0.55;
            VerdictBar(sc, tx, vbY, (tileW - 2 * inner) * 0.8, 3 * scale, report.PassCount, report.PartialCount,
                report.FailCount);

            tx = x + 3 * tileW + inner;
            Tile(sc, tx, labelY, valueY, subY, tileW - inner, "Game-fit",
                anyAssessed ? F1(fit.Overall) : "–", anyAssessed ? "/10" : string.Empty,
                anyAssessed ? $"best: {RoleFitness.ShortName(fit.BestRole)}" : "partial run, roles not rated",
                labelSize, valueSize, unitSize, subSize);
        }

        private static void Tile(BenchmarkCardScene sc, double x, double labelY, double valueY, double subY,
            double width, string label, string value, string unit, string sub, double labelSize, double valueSize,
            double unitSize, double subSize)
        {
            sc.Text(x, labelY, label, labelSize, CardInk.Muted, CardAnchor.Start, false, width);
            sc.TextWithSuffix(x, valueY, value, valueSize, CardInk.Ink, true, unit, unitSize, CardInk.Ink2, false,
                unitSize * 0.3);
            sc.Text(x, subY, sub, subSize, CardInk.Ink2, CardAnchor.Start, false, width);
        }

        /// <summary>Part-to-whole bar of PASS / PARTIAL / FAIL in status colours, with surface gaps between segments.</summary>
        private static void VerdictBar(BenchmarkCardScene sc, double x, double y, double width, double height, int pass,
            int partial, int fail)
        {
            int total = pass + partial + fail;
            if (total <= 0)
            {
                sc.Rect(x, y, width, height, CardInk.Track, height / 2);
                return;
            }

            int[] counts = { pass, partial, fail };
            CardInk[] inks = { CardInk.Good, CardInk.Warning, CardInk.Critical };
            string[] names = { "pass", "partial", "fail" };
            int segments = counts.Count(c => c > 0);
            double gap = height >= 6 ? 4 : 2;
            double usable = width - gap * (segments - 1);
            double cx = x;
            for (int i = 0; i < 3; i++)
            {
                if (counts[i] <= 0)
                {
                    continue;
                }

                double sw = usable * counts[i] / total;
                sc.Rect(cx, y, sw, height, inks[i], height / 2).Tooltip = $"{counts[i]} {names[i]}";
                cx += sw + gap;
            }
        }

        /// <summary>
        /// Legend for group rows (bar = group mean, diamond = extreme scenario), right-aligned at
        /// <paramref name="right"/>. Only drawn when a run has the second series; one series needs no legend.
        /// </summary>
        private static void GroupLegend(BenchmarkCardScene sc, double right, double baseline, double size)
        {
            const string mean = "group mean";
            const string extreme = "extreme scenario";
            double extremeW = BenchmarkCardScene.TextWidth(extreme, size);
            double meanW = BenchmarkCardScene.TextWidth(mean, size);
            double swatch = size * 0.9;
            double cy = baseline - size * 0.35;
            double ex = right - extremeW;
            sc.Text(ex, baseline, extreme, size, CardInk.Ink2);
            sc.Diamond(ex - size * 0.65, cy, size * 0.42, CardInk.Extreme);
            double mx = ex - size * 1.2 - size * 1.2 - meanW;
            sc.Text(mx, baseline, mean, size, CardInk.Ink2);
            sc.Rect(mx - swatch - size * 0.4, cy - swatch * 0.35, swatch, swatch * 0.7, CardInk.Series, 2);
        }

        /// <summary>
        /// One row per group G1–G8 (plus any unknown group the run reported): a 0–100 meter for the group mean
        /// with surface notches at 50 and 90, an extreme-scenario diamond on the same scale, and fixed value
        /// columns so the two numbers never collide. Returns the y below the axis labels.
        /// </summary>
        private static double GroupRows(BenchmarkCardScene sc, IReadOnlyList<GroupScore> groups, double x, double y,
            double width, double rowH, double font, double scale)
        {
            Dictionary<string, GroupScore> byId = new();
            foreach (GroupScore g in groups)
            {
                byId[g.Group ?? string.Empty] = g;
            }

            List<string> ids = BenchmarkInfo.Groups.ToList();
            foreach (GroupScore g in groups)
            {
                if (!ids.Contains(g.Group ?? string.Empty))
                {
                    ids.Add(g.Group ?? string.Empty);
                }
            }

            double labelW = 0;
            foreach (string id in ids)
            {
                double lw = BenchmarkCardScene.TextWidth(id + " ", font, true) +
                            BenchmarkCardScene.TextWidth(BenchmarkInfo.TitleFor(id), font);
                labelW = System.Math.Max(labelW, lw);
            }

            labelW = System.Math.Min(labelW + font * 0.6, width * 0.42);
            double meanW = BenchmarkCardScene.TextWidth("100.0", font, true) + font * 0.7;
            double extW = font * 1.1 + BenchmarkCardScene.TextWidth("100.0", font) + font * 0.3;
            double barX = x + labelW;
            double barW = width - labelW - meanW - extW;
            double barH = System.Math.Max(6, System.Math.Round(rowH * 0.34));
            double dr = barH * 0.62;

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                double cy = y + i * rowH + rowH / 2;
                double baseline = cy + font * 0.35;
                sc.TextWithSuffix(x, baseline, id, font, CardInk.Ink, true, BenchmarkInfo.TitleFor(id), font,
                    CardInk.Ink2, false, font * 0.4, CardAnchor.Start, labelW - font * 0.4);

                if (!byId.TryGetValue(id, out GroupScore g))
                {
                    sc.Text(barX, baseline, "not run", font * 0.92, CardInk.Muted);
                    continue;
                }

                double top = cy - barH / 2;
                sc.Rect(barX, top, barW, barH, CardInk.SeriesTrack, barH / 2);
                sc.Rect(barX + barW * 0.5 - scale / 2, top, scale, barH, CardInk.Surface);
                sc.Rect(barX + barW * 0.9 - scale / 2, top, scale, barH, CardInk.Surface);
                double mean = Clamp(g.MeanBase, 0, 100);
                sc.Bar(barX, top, System.Math.Max(barW * mean / 100.0, scale), barH, CardInk.Series,
                        System.Math.Min(2 * scale, barH / 2)).Tooltip =
                    $"{id} {BenchmarkInfo.TitleFor(id)}: mean {F1(g.MeanBase)} over {g.Count} scenario(s), {g.PassCount} pass";
                sc.Text(barX + barW + meanW, baseline, F1(g.MeanBase), font, CardInk.Ink, CardAnchor.End, true)
                    .Tabular = true;

                if (g.HasExtreme)
                {
                    double ext = Clamp(g.ExtremeMeanBase, 0, 100);
                    sc.Diamond(barX + barW * ext / 100.0, cy, dr, CardInk.Extreme, scale).Tooltip =
                        $"{id} extreme scenario: {F1(g.ExtremeMeanBase)}";
                    sc.Diamond(barX + barW + meanW + font * 0.75, cy, font * 0.3, CardInk.Extreme);
                    sc.Text(x + width, baseline, F1(g.ExtremeMeanBase), font, CardInk.Ink2, CardAnchor.End)
                        .Tabular = true;
                }
                else
                {
                    sc.Text(x + width, baseline, "–", font, CardInk.Muted, CardAnchor.End);
                }
            }

            double axisY = y + ids.Count * rowH + font * 0.95;
            double tick = font * 0.85;
            sc.Text(barX, axisY, "0", tick, CardInk.Muted);
            sc.Text(barX + barW * 0.5, axisY, "50", tick, CardInk.Muted, CardAnchor.Middle);
            sc.Text(barX + barW * 0.9, axisY, "90 pass", tick, CardInk.Muted, CardAnchor.Middle);
            return axisY + tick * 0.4;
        }

        /// <summary>Dimension mini-bars (0–100 meters with value column) for the compact results card.</summary>
        private static double DimensionBars(BenchmarkCardScene sc, IReadOnlyList<DimensionScore> dims, double x,
            double y, double width, double rowH, double font)
        {
            double labelW = BenchmarkCardScene.TextWidth("Instruction adherence", font) + font * 0.6;
            double valueW = BenchmarkCardScene.TextWidth("100.0", font, true) + font * 0.5;
            double barX = x + labelW;
            double barW = width - labelW - valueW;
            double barH = System.Math.Max(6, System.Math.Round(rowH * 0.3));
            int i = 0;
            foreach (BenchmarkDimension d in AllDimensions)
            {
                double cy = y + i * rowH + rowH / 2;
                double baseline = cy + font * 0.35;
                sc.Text(x, baseline, DimName(d), font, CardInk.Ink2, CardAnchor.Start, false, labelW - font * 0.3);
                DimensionScore score = dims.FirstOrDefault(s => s.Dimension == d);
                if (score == null)
                {
                    sc.Text(barX, baseline, "not measured", font * 0.92, CardInk.Muted);
                }
                else
                {
                    sc.Rect(barX, cy - barH / 2, barW, barH, CardInk.SeriesTrack, barH / 2);
                    sc.Bar(barX, cy - barH / 2, System.Math.Max(barW * Clamp(score.Score, 0, 100) / 100.0, 2), barH,
                        CardInk.Series, System.Math.Min(4, barH / 2)).Tooltip = $"{DimName(d)}: {F1(score.Score)}";
                    sc.Text(x + width, baseline, F1(score.Score), font, CardInk.Ink, CardAnchor.End, true).Tabular =
                        true;
                }

                i++;
            }

            return y + i * rowH;
        }

        /// <summary>
        /// Dimension dot plot on a 50–100 axis: dots encode position, not length, so starting the axis at 50
        /// is honest and gives 90 vs 100 a fifth of the axis. Scores under 50 sit on the left edge; the value
        /// column always prints the true number.
        /// </summary>
        private static void DimensionDots(BenchmarkCardScene sc, IReadOnlyList<DimensionScore> dims, double x,
            double y, double width, double rowH, double font)
        {
            const double axisMin = 50;
            double labelW = BenchmarkCardScene.TextWidth("Task completion", font) + font * 0.8;
            double valueW = BenchmarkCardScene.TextWidth("100.0", font, true) + font * 0.6;
            double r = font * 0.38;
            double ax0 = x + labelW + r;
            double ax1 = x + width - valueW - r;
            double rows = AllDimensions.Length;
            double bottom = y + rows * rowH;

            for (int t = 50; t <= 100; t += 10)
            {
                double gx = ax0 + (ax1 - ax0) * (t - axisMin) / (100 - axisMin);
                bool pass = t == 90;
                sc.Line(gx, y + 4, gx, bottom - 4, pass ? CardInk.Muted : CardInk.Grid, 2);
                sc.Text(gx, bottom + font * 0.95, t.ToString(CultureInfo.InvariantCulture), font * 0.85,
                    CardInk.Muted, CardAnchor.Middle);
            }

            bool anyBelow = false;
            int i = 0;
            foreach (BenchmarkDimension d in AllDimensions)
            {
                double cy = y + i * rowH + rowH / 2;
                double baseline = cy + font * 0.35;
                sc.Text(x, baseline, DimShortName(d), font, CardInk.Ink2, CardAnchor.Start, false, labelW - font * 0.4);
                DimensionScore score = dims.FirstOrDefault(s => s.Dimension == d);
                if (score == null)
                {
                    sc.Text(x + width, baseline, "n/a", font, CardInk.Muted, CardAnchor.End);
                    i++;
                    continue;
                }

                double v = Clamp(score.Score, 0, 100);
                anyBelow |= v < axisMin;
                double px = ax0 + (ax1 - ax0) * (System.Math.Max(v, axisMin) - axisMin) / (100 - axisMin);
                sc.Circle(px, cy, r, CardInk.Series, 4).Tooltip = $"{DimName(d)}: {F1(score.Score)}";
                sc.Text(x + width, baseline, F1(score.Score), font, CardInk.Ink, CardAnchor.End, true).Tabular = true;
                i++;
            }

            if (anyBelow)
            {
                sc.Text(x, bottom + font * 0.95, "<50 on edge", font * 0.85, CardInk.Muted);
            }
        }

        /// <summary>Role-fitness rows: name, verdict and rating on one line, a 0–10 meter below it.</summary>
        private static void RoleRows(BenchmarkCardScene sc, RoleFitness.Result fit, double x, double y, double width,
            double rowH, double font)
        {
            double barH = System.Math.Max(6, System.Math.Round(rowH * 0.2));
            double valueW = BenchmarkCardScene.TextWidth("10.0", font, true);
            for (int i = 0; i < fit.Roles.Count; i++)
            {
                RoleFitness.RoleScore role = fit.Roles[i];
                double top = y + i * rowH;
                double baseline = top + font * 0.95;
                string verdict = role.Assessed ? role.Verdict.ToLowerInvariant() : "not assessed";
                double verdictW = BenchmarkCardScene.TextWidth(verdict, font * 0.85);
                sc.Text(x, baseline, RoleFitness.ShortName(role.Role), font, CardInk.Ink2, CardAnchor.Start, false,
                    width - valueW - verdictW - font * 1.4);
                sc.Text(x + width - valueW - font * 0.6, baseline, verdict, font * 0.85, CardInk.Muted, CardAnchor.End);
                double barY = baseline + font * 0.45;
                sc.Rect(x, barY, width, barH, CardInk.SeriesTrack, barH / 2);
                if (role.Assessed)
                {
                    double frac = Clamp(role.Rating, 0, 10) / 10.0;
                    sc.Bar(x, barY, System.Math.Max(width * frac, 2), barH, CardInk.Series, barH / 2).Tooltip =
                        $"{role.Role}: {F1(role.Rating)}/10, {role.Verdict}";
                    sc.Text(x + width, baseline, F1(role.Rating), font, CardInk.Ink, CardAnchor.End, true).Tabular =
                        true;
                }
                else
                {
                    sc.Text(x + width, baseline, "–", font, CardInk.Muted, CardAnchor.End);
                }
            }
        }

        private static readonly BenchmarkDimension[] AllDimensions =
        {
            BenchmarkDimension.ToolCorrectness, BenchmarkDimension.IntentSequence,
            BenchmarkDimension.TaskCompletion, BenchmarkDimension.Determinism,
            BenchmarkDimension.Reasoning, BenchmarkDimension.InstructionAdherence
        };

        private static string SuiteAndDate(BenchmarkRunMetadata m)
        {
            string suite = string.IsNullOrEmpty(m.SuiteVersion) ? BenchmarkInfo.Version : "suite v" + m.SuiteVersion;
            string date = RunDate(m);
            return string.IsNullOrEmpty(date) ? suite : suite + " · " + date;
        }

        private static string ModelCardMetaLine(BenchmarkReport report)
        {
            BenchmarkRunMetadata m = report.Metadata;
            List<string> parts = new() { SuiteAndDate(m) };
            if (!string.IsNullOrEmpty(m.RunId))
            {
                parts.Add("run " + m.RunId);
            }

            parts.Add($"{report.ScenarioCount} scenarios × {System.Math.Max(1, m.Repetitions)} rep");
            if (!string.IsNullOrEmpty(m.Backend))
            {
                parts.Add(m.Backend);
            }

            return string.Join(" · ", parts);
        }

        /// <summary>The run date as yyyy-MM-dd, from the timestamp or else the run id; empty when neither parses.</summary>
        private static string RunDate(BenchmarkRunMetadata m)
        {
            if (!string.IsNullOrEmpty(m.TimestampUtc) && m.TimestampUtc.Length >= 10 && m.TimestampUtc[4] == '-')
            {
                return m.TimestampUtc.Substring(0, 10);
            }

            string id = m.RunId ?? string.Empty;
            if (id.Length >= 8 && id.Take(8).All(char.IsDigit))
            {
                return $"{id.Substring(0, 4)}-{id.Substring(4, 2)}-{id.Substring(6, 2)}";
            }

            return string.Empty;
        }

        private static string SpeedFooter(BenchmarkReport report)
        {
            return $"{N0(report.TotalTokens)} tokens ({N0(report.TotalCompletionTokens)} generated) · " +
                   $"{F1(report.GenerationTokensPerSecond)} tok/s · {Duration(report.TotalLatencyMs)} · " +
                   $"{report.TotalToolCalls} tool calls, {report.TotalFailedToolCalls} failed · " +
                   "speed is reported, never scored";
        }

        /// <summary>Plain-text summary of the card for screen readers (SVG <c>&lt;desc&gt;</c>).</summary>
        private static string Describe(BenchmarkReport report)
        {
            StringBuilder sb = new();
            sb.Append($"Suite score {F1(report.SuiteBaseScore)} of 100 ({Grade(report.SuiteBaseScore)}), regular tier ")
                .Append(F1(report.RegularBaseScore));
            sb.Append(report.HasExtreme
                ? $", extreme tier {F1(report.ExtremeBaseScore)} with {report.ExtremePassCount} of {report.ExtremeScenarioCount} passing"
                : ", no extreme tier");
            sb.Append($"; {report.PassCount} pass, {report.PartialCount} partial, {report.FailCount} fail.");
            foreach (GroupScore g in report.GroupBreakdown())
            {
                sb.Append($" {g.Group} {F1(g.MeanBase)}");
                if (g.HasExtreme)
                {
                    sb.Append($" (extreme {F1(g.ExtremeMeanBase)})");
                }

                sb.Append('.');
            }

            return sb.ToString();
        }

        private static string Duration(double ms)
        {
            if (!double.IsFinite(ms) || ms <= 0)
            {
                return "0 s";
            }

            double s = ms / 1000.0;
            return s < 120 ? $"{F1(s)} s" : $"{F1(s / 60.0)} min";
        }

        /// <summary>
        /// Inline dimension chart for the Markdown body: 0–100 meters plus the efficiency-bonus row. Styles are
        /// scoped with their own class prefix because inline SVG shares the page's stylesheet.
        /// </summary>
        private static string DimensionSummarySvg(IReadOnlyList<DimensionScore> dims, double meanEfficiencyBonus)
        {
            const double w = 640;
            const double pad = 20;
            const double rowH = 26;
            const double font = 12;
            BenchmarkCardScene sc = new(w, 0, "Scores by dimension");
            double bottom = DimensionBars(sc, dims, pad, 12, w - 2 * pad, rowH, font);
            double cy = bottom + rowH / 2;
            double labelW = BenchmarkCardScene.TextWidth("Instruction adherence", font) + font * 0.6;
            double valueW = BenchmarkCardScene.TextWidth("100.0", font, true) + font * 0.5;
            double barW = w - 2 * pad - labelW - valueW;
            double pct = GoalScore.MaxBonus <= 0 ? 0 : Clamp(meanEfficiencyBonus / GoalScore.MaxBonus * 100.0, 0, 100);
            sc.Text(pad, cy + font * 0.35, "Efficiency bonus", font, CardInk.Ink2);
            sc.Rect(pad + labelW, cy - 4, barW, 8, CardInk.SeriesTrack, 4);
            sc.Bar(pad + labelW, cy - 4, System.Math.Max(barW * pct / 100.0, 2), 8, CardInk.Series);
            sc.Text(w - pad, cy + font * 0.35, $"{F(meanEfficiencyBonus)}/{F(GoalScore.MaxBonus)}", font, CardInk.Ink,
                CardAnchor.End, true);
            sc.Height = bottom + rowH + 12;
            return sc.ToSvg(CardTheme.Auto, "bcdim");
        }

        private static double Clamp(double value, double min, double max)
        {
            if (!double.IsFinite(value))
            {
                return min;
            }

            return value < min ? min : value > max ? max : value;
        }

        private static string F1(double v)
        {
            if (!double.IsFinite(v))
            {
                v = 0;
            }

            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string N0(long v)
        {
            return v.ToString("#,0", CultureInfo.InvariantCulture);
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

            // WHY: INDEX.md keeps its header from the first run, so the extreme sub-score rides inside the
            // existing Score cell instead of adding a column that older rows would not have.
            string extreme = report.HasExtreme ? $" (ext {F(report.ExtremeBaseScore)})" : string.Empty;
            return $"| {m.TimestampUtc} | `{m.ModelId}` | **{F(report.SuiteBaseScore)}**{extreme} | " +
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
            sb.Append(Str("reasoningEffort", m.ReasoningEffort ?? "")).Append(',');
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
            sb.Append(Num("effectiveTokensPerSecond", report.EffectiveTokensPerSecond)).Append(',');
            sb.Append(Num("scenarios", report.ScenarioCount)).Append(',');
            sb.Append(Num("regularBaseScore", report.RegularBaseScore)).Append(',');
            sb.Append(Num("regularScenarios", report.RegularScenarioCount)).Append(',');
            sb.Append(Num("extremeBaseScore", report.ExtremeBaseScore)).Append(',');
            sb.Append(Num("extremeScenarios", report.ExtremeScenarioCount)).Append(',');
            sb.Append(Num("extremePass", report.ExtremePassCount)).Append(',');
            sb.Append("\"groups\":{");
            IReadOnlyList<GroupScore> gjson = report.GroupBreakdown();
            for (int i = 0; i < gjson.Count; i++)
            {
                GroupScore g = gjson[i];
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('"').Append(Escape(g.Group ?? string.Empty)).Append("\":{");
                sb.Append(Num("meanBase", g.MeanBase)).Append(',');
                sb.Append(Num("scenarios", g.Count)).Append(',');
                sb.Append(Num("pass", g.PassCount)).Append(',');
                sb.Append(Num("regularBase", g.RegularMeanBase)).Append(',');
                sb.Append(Num("regularScenarios", g.RegularCount)).Append(',');
                sb.Append(Num("extremeBase", g.ExtremeMeanBase)).Append(',');
                sb.Append(Num("extremeScenarios", g.ExtremeCount)).Append(',');
                sb.Append(Num("extremePass", g.ExtremePassCount));
                sb.Append('}');
            }

            sb.Append("},");
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
                sb.Append(Bool("extreme", r.Extreme)).Append(',');
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

        private static string Verdict(ScenarioResult r)
        {
            return VerdictText(r.Classification);
        }

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

            pct = pct < 0 ? 0 : pct > 100 ? 100 : pct;
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

        /// <summary>Card label of a dimension, short enough for the model card's dot-plot column.</summary>
        private static string DimShortName(BenchmarkDimension d)
        {
            return d switch
            {
                BenchmarkDimension.ToolCorrectness => "Tool use",
                BenchmarkDimension.IntentSequence => "Intent & order",
                BenchmarkDimension.TaskCompletion => "Task completion",
                BenchmarkDimension.Determinism => "Determinism",
                BenchmarkDimension.Reasoning => "Reasoning",
                BenchmarkDimension.InstructionAdherence => "Instructions",
                _ => d.ToString()
            };
        }

        /// <summary>The per-group scorecard table: scenarios, mean base, pass count and the extreme result.</summary>
        private static void AppendGroupTable(StringBuilder sb, IReadOnlyList<GroupScore> groups)
        {
            sb.AppendLine();
            sb.AppendLine("## 🧩 Scores by group");
            sb.AppendLine();
            if (groups.Count == 0)
            {
                sb.AppendLine("_No graded groups._");
                return;
            }

            sb.AppendLine("| Group | Focus | Scenarios | Mean base | Pass | Extreme |");
            sb.AppendLine("|---|---|---:|---:|---:|---|");
            foreach (GroupScore g in groups)
            {
                string extreme = g.HasExtreme
                    ? $"{F(g.ExtremeMeanBase)} · {VerdictText(ClassifyMean(g.ExtremeMeanBase))}" +
                      (g.ExtremeCount > 1 ? $" ({g.ExtremePassCount}/{g.ExtremeCount} pass)" : string.Empty)
                    : "—";
                sb.AppendLine($"| {g.Group} | {BenchmarkInfo.TitleFor(g.Group)} | {g.Count} | {F(g.MeanBase)} | " +
                              $"{g.PassCount}/{g.Count} | {extreme} |");
            }
        }

        /// <summary>The extreme-tier section: sub-score headline plus one row per extreme scenario.</summary>
        private static void AppendExtremeSection(StringBuilder sb, BenchmarkReport report)
        {
            sb.AppendLine();
            if (!report.HasExtreme)
            {
                sb.AppendLine("## 🔥 Extreme tier");
                sb.AppendLine();
                sb.AppendLine("_No extreme-tier scenario was graded in this run._");
                return;
            }

            sb.AppendLine($"## 🔥 Extreme tier — {F(report.ExtremeBaseScore)}/100 " +
                          $"({report.ExtremePassCount}/{report.ExtremeScenarioCount} pass)");
            sb.AppendLine();
            sb.AppendLine("_One deliberately hard scenario per group (G6 has none). The regular tier saturates near " +
                          "100 for frontier models; this sub-score keeps separating them. It also counts in the " +
                          "suite score._");
            sb.AppendLine();
            sb.AppendLine("| Group | Scenario | Mean base | Verdict | Reps | Spread |");
            sb.AppendLine("|---|---|---:|---|---:|---:|");
            foreach (ScenarioSummary s in report.ExtremeScenarios())
            {
                sb.AppendLine($"| {s.Group} | {s.Name} | {F(s.MeanBase)} | {VerdictText(s.Classification)} | " +
                              $"{s.Repetitions} | {F(s.Spread)} |");
            }
        }

        private static BenchmarkClassification ClassifyMean(double meanBase)
        {
            if (meanBase >= GoalScore.PassBase)
            {
                return BenchmarkClassification.Pass;
            }

            return meanBase >= GoalScore.PartialBase
                ? BenchmarkClassification.Partial
                : BenchmarkClassification.Fail;
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
            // WHY: JSON has no NaN/Infinity literal — emit 0 so the document stays parseable.
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
