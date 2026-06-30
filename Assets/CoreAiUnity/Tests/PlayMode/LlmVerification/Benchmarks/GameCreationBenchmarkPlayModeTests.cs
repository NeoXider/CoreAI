#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Entry point for the game-creation benchmark (G1 + G2). Runs every scenario through the resolved
    /// live model, grades each 0..100 via the portable scoring core, and writes
    /// <c>TestResults/CoreAI/Benchmarks/&lt;runId&gt;/BENCHMARK_RESULTS.{md,json}</c>.
    /// <para>
    /// Gated on a configured provider (<see cref="PlayModeOpenAiTestConfig"/> env vars / local config or a
    /// local LLMUnity model); <see cref="Assert.Ignore(string)"/> when unconfigured. This is a benchmark,
    /// not a pass/fail correctness gate: it only fails on a HARNESS (framework) failure, never on a low
    /// model score — the score is the measurement.
    /// </para>
    /// </summary>
    public sealed class GameCreationBenchmarkPlayModeTests
    {
        private const string SuiteVersion = "1.6";

        /// <summary>Env var (CSV of group ids, e.g. "G1,G2") to restrict which scenarios run. Empty = all.</summary>
        public const string EnvGroups = "COREAI_BENCHMARK_GROUPS";

        /// <summary>Env var (int) for how many times each scenario runs; the report keeps the per-scenario median.</summary>
        public const string EnvRepetitions = "COREAI_BENCHMARK_REPS";

        /// <summary>Env var (seconds) overriding the per-scenario wall-clock timeout. 0/unset = per-scenario default.</summary>
        public const string EnvTimeout = "COREAI_BENCHMARK_TIMEOUT";

        /// <summary>Env var (int) for the per-request tool-call roundtrip cap. 0/unset = default 40.</summary>
        public const string EnvRoundtrips = "COREAI_BENCHMARK_ROUNDTRIPS";

        /// <summary>
        /// Env var (seconds) for the SOFT whole-suite time budget. When the elapsed wall-clock crosses this,
        /// the suite stops starting new scenarios, then writes the report/screenshots for everything finished
        /// so far — unlike the NUnit [Timeout], which hard-aborts and produces NO artifacts. Default 300s
        /// (5 min). The NUnit [Timeout] is set well above this as a last-resort backstop.
        /// </summary>
        public const string EnvSuiteBudget = "COREAI_BENCHMARK_SUITE_BUDGET";

        private static double ResolveSuiteBudgetSeconds()
        {
            string raw = Environment.GetEnvironmentVariable(EnvSuiteBudget);
            if (!string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 30)
            {
                return s;
            }

            return 300; // 5 minutes
        }

        private static int ResolveBenchmarkRoundtrips()
        {
            string raw = Environment.GetEnvironmentVariable(EnvRoundtrips);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int value) && value >= 1)
            {
                return value;
            }

            return 40;
        }

        /// <summary>
        /// Env var (int) for extra attempts on a hard FAILURE — any crash/fault/timeout that produced no
        /// measurement: provider 5xx, dropped connection, model-load crash, "model has crashed", or a
        /// timeout. Default 1 extra attempt. A run that COMPLETED but scored low is never retried (a low
        /// score is the measurement), and harness (Framework) bugs are not retried either. This is
        /// distinct from repetitions, which re-run successful scenarios for median stability.
        /// </summary>
        public const string EnvRetries = "COREAI_BENCHMARK_RETRIES";

        private static GameBenchmarkScenario[] AllScenarios()
        {
            List<GameBenchmarkScenario> all = new(BenchmarkSuiteCatalog.All());

            string groupsCsv = Environment.GetEnvironmentVariable(EnvGroups);
            if (!string.IsNullOrWhiteSpace(groupsCsv))
            {
                HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
                foreach (string g in groupsCsv.Split(','))
                {
                    string trimmed = g.Trim();
                    if (trimmed.Length > 0)
                    {
                        wanted.Add(trimmed);
                    }
                }

                all.RemoveAll(s => !wanted.Contains(s.Group));
            }

            // Run (and display) from easiest to hardest, using the SAME canonical group difficulty the editor
            // RUN tab shows, so ordering and the rating indicator agree everywhere.
            all.Sort((a, b) =>
            {
                int d = BenchmarkInfo.DifficultyFor(a.Group).CompareTo(BenchmarkInfo.DifficultyFor(b.Group));
                if (d != 0)
                {
                    return d;
                }

                int gcmp = string.CompareOrdinal(a.Group, b.Group);
                return gcmp != 0 ? gcmp : string.CompareOrdinal(a.Id, b.Id);
            });

            return all.ToArray();
        }

        private static int ResolveRepetitions()
        {
            string raw = Environment.GetEnvironmentVariable(EnvRepetitions);
            if (int.TryParse(raw, out int n) && n >= 1 && n <= 9)
            {
                return n;
            }

            return 1;
        }

        /// <summary>Per-scenario timeout: the env override when set (1..1200s), else the scenario's own default.</summary>
        private static float ResolveTimeoutSeconds(GameBenchmarkScenario scenario)
        {
            string raw = Environment.GetEnvironmentVariable(EnvTimeout);
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 1f && s <= 1200f)
            {
                return s;
            }

            return scenario.TimeoutSeconds;
        }

        /// <summary>Total attempts per repetition on a transient failure (1 + retries, clamped 1..4).</summary>
        private static int ResolveMaxAttempts()
        {
            string raw = Environment.GetEnvironmentVariable(EnvRetries);
            int retries = int.TryParse(raw, out int n) && n >= 0 && n <= 3 ? n : 1;
            return 1 + retries;
        }

        [UnityTest]
        [Timeout(600000)] // 10 min — last-resort NUnit backstop; the SOFT 5-min suite budget (which still
                          // writes artifacts) is the real terminator. NUnit's hard abort writes nothing.
        [Category("Benchmark")]
        [Explicit("Live game-creation benchmark; run manually with a configured model.")]
        public IEnumerator GameCreationBenchmark_Suite()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, 0.1f, 300, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // Free-build scenes (the castle) spawn 24+ objects, which needs more tool-call roundtrips than the
            // default. Overridable via env.
            settings.SetMaxToolCallRoundtrips(ResolveBenchmarkRoundtrips());
            // CRITICAL for free-build: do NOT truncate tool-call history (0 = unlimited). With the default cap
            // of 20, a 30+ spawn build forgets the first ~15 objects it placed and re-spawns duplicates. The
            // model must see everything it has already built to avoid repeating itself.
            settings.SetMaxToolCallHistoryMessages(0);
            BenchmarkReport report = new();

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                ITokenCounter tokenCounter = new BpeTokenCounter();
                // Model id: from the resolved env/file config, else the project CoreAISettings asset (the
                // backend-driven path carries no ResolvedConfig), else the backend name as a last resort.
                string modelId = handle.ResolvedConfig?.Model;
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    modelId = CoreAISettingsAsset.Instance != null
                        ? CoreAISettingsAsset.Instance.ModelName
                        : null;
                }

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    modelId = handle.ResolvedBackend.ToString();
                }

                int repetitions = ResolveRepetitions();

                report.Metadata = new BenchmarkRunMetadata
                {
                    RunId = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    ModelId = modelId,
                    Backend = handle.ResolvedBackend.ToString(),
                    // From the resolved config when present, else the actual client capability (the
                    // asset-driven path carries no ResolvedConfig). Streaming is forced off per-agent for
                    // determinism, so it is reported as false here regardless of the provider default.
                    NativeToolCalling = handle.ResolvedConfig?.NativeTools ?? handle.Client.SupportsNativeToolCalling,
                    Streaming = handle.ResolvedConfig?.Streaming ?? false,
                    MaxParallelToolCalls = settings.MaxParallelToolCalls,
                    Temperature = 0.1f,
                    Repetitions = repetitions,
                    UnityVersion = Application.unityVersion,
                    SuiteVersion = SuiteVersion
                };

                int maxAttempts = ResolveMaxAttempts();
                GameBenchmarkScenario[] scenarios = AllScenarios();
                BenchmarkProgress.Begin(scenarios.Length * repetitions, modelId);

                // Soft whole-suite time budget: when crossed, stop starting NEW scenarios and fall through to
                // writing the report/screenshots for whatever finished. This is graceful — unlike the NUnit
                // [Timeout] which hard-aborts mid-scenario and writes nothing.
                double suiteBudgetSeconds = ResolveSuiteBudgetSeconds();
                System.Diagnostics.Stopwatch suiteClock = System.Diagnostics.Stopwatch.StartNew();
                bool budgetHit = false;

                foreach (GameBenchmarkScenario scenario in scenarios)
                {
                    if (suiteClock.Elapsed.TotalSeconds >= suiteBudgetSeconds)
                    {
                        budgetHit = true;
                        Debug.LogWarning(
                            $"[Benchmark] Suite time budget ({suiteBudgetSeconds:0}s) reached after " +
                            $"{report.Results.Count} scenario result(s); stopping early and writing the report " +
                            "for everything finished so far.");
                        break;
                    }

                    float timeout = ResolveTimeoutSeconds(scenario);
                    // Non-repeatable scenarios (e.g. the G6 castle hero — a one-off visual) always run once,
                    // even when the suite repeats every other scenario for an averaged score.
                    int scenarioReps = scenario.Repeatable ? repetitions : 1;
                    for (int rep = 1; rep <= scenarioReps; rep++)
                    {
                        BenchmarkProgress.StartScenario(
                            $"{scenario.Group} · {scenario.Name}  {Stars(BenchmarkInfo.DifficultyFor(scenario.Group))}" +
                            (scenarioReps > 1 ? $" (run {rep}/{scenarioReps})" : ""));
                        ScenarioResult captured = null;
                        // Retry on ANY hard failure that produced no measurement — provider/model crash,
                        // failed-to-load, timeout, dropped connection — so a crash never counts as a model
                        // failure. A run that COMPLETED but scored low is NOT retried (that is the
                        // measurement); harness (Framework) bugs are NOT retried (fail fast to surface them).
                        for (int attempt = 1; attempt <= maxAttempts; attempt++)
                        {
                            ScenarioResult attemptResult = null;
                            yield return RunScenario(scenario, handle.Client, settings, tokenCounter, modelId,
                                timeout, r => attemptResult = r);
                            captured = attemptResult;

                            bool hardFailure = captured != null
                                               && !string.IsNullOrEmpty(captured.Failure)
                                               && captured.Attribution != FailureAttribution.Framework;
                            if (!hardFailure || attempt >= maxAttempts)
                            {
                                break;
                            }

                            Debug.LogWarning($"[Benchmark] {scenario.Name}: run failed " +
                                             $"({captured.Failure}); retry {attempt}/{maxAttempts - 1}.");
                        }

                        if (captured != null)
                        {
                            captured.Repetition = rep;
                            report.Add(captured);
                            BenchmarkProgress.CompleteScenario(ProgressLine(captured), Stars(BenchmarkInfo.DifficultyFor(scenario.Group)));
                        }
                        else
                        {
                            BenchmarkProgress.CompleteScenario($"⚠ {scenario.Name} — no result",
                                Stars(BenchmarkInfo.DifficultyFor(scenario.Group)));
                        }
                    }
                }
            }
            finally
            {
                BenchmarkProgress.End();
                handle.Dispose();
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }

            // A suite-level "model card" (radar of the six dimensions + game-fitness bars) so two models'
            // results are comparable at a glance — rendered with a throwaway camera while still in Play mode.
            byte[] modelCardPng = null;
            if (UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null
                && report.Results.Count > 0)
            {
                yield return GameCreationBenchmarkHarness.CaptureModelCard(report, png => modelCardPng = png);
            }

            string artifactPath = WriteArtifacts(report, modelCardPng);

            Debug.Log($"[Benchmark] ===== SUITE COMPLETE =====\n" +
                      $"Suite base score: {report.SuiteBaseScore:0.#}/100 (mean bonus {report.MeanBonus:0.#})\n" +
                      $"PASS {report.PassCount} / PARTIAL {report.PartialCount} / FAIL {report.FailCount} " +
                      $"(pass-rate {report.PassRate * 100:0.#}%)\n" +
                      $"Tokens {report.TotalTokens} | total latency {report.TotalLatencyMs:0} ms\n" +
                      $"Report: {artifactPath}");

            Assert.Greater(report.Results.Count, 0, "Benchmark produced no scenario results.");
            Assert.AreEqual(0, report.FrameworkFailures,
                "A scenario failed inside the harness (not the model). See artifact for details.");
        }

        /// <summary>
        /// Difficulty on the single 1–10 scale (from <see cref="BenchmarkInfo.GroupDifficulty10"/>) rendered
        /// as 5 half-dots plus "d/10", matching the editor RUN-tab indicator exactly so the two never differ.
        /// </summary>
        private static string Stars(int difficulty10)
        {
            int d = difficulty10 < 1 ? 1 : (difficulty10 > 10 ? 10 : difficulty10);
            int half = d / 2;
            return $"{new string('●', half)}{new string('○', 5 - half)} {d}/10";
        }

        private static string ProgressLine(ScenarioResult r)
        {
            if (r.Attribution == FailureAttribution.Environment)
            {
                return $"⚠ {r.ScenarioName} — provider/env failure (excluded)";
            }

            string glyph = r.Classification switch
            {
                BenchmarkClassification.Pass => "✅",
                BenchmarkClassification.Partial => "🟡",
                _ => "❌"
            };
            return $"{glyph} {r.ScenarioName} — {r.Score.Base:0.#}";
        }

        private static string WriteArtifacts(BenchmarkReport report, byte[] modelCardPng = null)
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string dir = Path.Combine(projectRoot, "TestResults", "CoreAI", "Benchmarks");
                Directory.CreateDirectory(dir);

                // Date + model in the filename so reports are self-identifying and never overwrite.
                string stem = $"BENCHMARK_{report.Metadata.RunId}_{SanitizeForFileName(report.Metadata.ModelId)}";
                string mdName = stem + ".md";
                string svgName = stem + ".svg";
                string mdPath = Path.Combine(dir, mdName);
                string heroScenarioId = null;

                // Visual results card, embedded near the top of the Markdown report.
                File.WriteAllText(Path.Combine(dir, svgName), BenchmarkReportFormatter.ToSvg(report));
                string md = EmbedResultsImage(BenchmarkReportFormatter.ToMarkdown(report), svgName);

                // The rendered model card (radar + role bars) leads the report when available.
                if (modelCardPng != null && modelCardPng.Length > 0)
                {
                    string cardName = stem + "_modelcard.png";
                    try
                    {
                        File.WriteAllBytes(Path.Combine(dir, cardName), modelCardPng);
                        md = EmbedResultsImage(md, cardName);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Benchmark] failed to write model card: {ex.Message}");
                    }
                }

                ScenarioResult hero = FindFreeBuildHeroResult(report);
                if (hero != null)
                {
                    string heroName = stem + "_g6_free_build_hero.png";
                    try
                    {
                        File.WriteAllBytes(Path.Combine(dir, heroName), hero.SceneScreenshotPng);
                        md = EmbedResultsImage(md, heroName, "free-build hero",
                            "_Hero: G6 free-build visual scene, preserving the model-authored layout._");
                        heroScenarioId = hero.ScenarioId;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Benchmark] failed to write castle hero screenshot: {ex.Message}");
                    }
                }

                md += WriteSceneScreenshots(report, dir, stem, heroScenarioId);
                File.WriteAllText(mdPath, md);
                File.WriteAllText(Path.Combine(dir, stem + ".json"), BenchmarkReportFormatter.ToJson(report));

                // Append a one-line row to the rolling index so many runs stay easy to scan/compare.
                string indexPath = Path.Combine(dir, "INDEX.md");
                if (!File.Exists(indexPath))
                {
                    File.WriteAllText(indexPath, BenchmarkReportFormatter.IndexHeader());
                }

                File.AppendAllText(indexPath, BenchmarkReportFormatter.IndexRow(report, mdName) + "\n");

                return mdPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] failed to write artifacts: {ex.Message}");
                return "(not written)";
            }
        }

        private static ScenarioResult FindFreeBuildHeroResult(BenchmarkReport report)
        {
            foreach (ScenarioResult r in report.Results)
            {
                if (r.SceneScreenshotPng == null || r.SceneScreenshotPng.Length == 0)
                {
                    continue;
                }

                if (string.Equals(r.Group, "G6", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.ScenarioId, "g6_free_build", StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }

            return null;
        }

        /// <summary>Writes each captured scene screenshot as a PNG and returns a Markdown section linking them.</summary>
        private static string WriteSceneScreenshots(BenchmarkReport report, string dir, string stem,
            string skipScenarioId = null)
        {
            string section = "";
            foreach (ScenarioResult r in report.Results)
            {
                if (!string.IsNullOrEmpty(skipScenarioId)
                    && string.Equals(r.ScenarioId, skipScenarioId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (r.SceneScreenshotPng == null || r.SceneScreenshotPng.Length == 0)
                {
                    continue;
                }

                string png = $"{stem}_{r.ScenarioId}.png";
                try
                {
                    File.WriteAllBytes(Path.Combine(dir, png), r.SceneScreenshotPng);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] failed to write screenshot: {ex.Message}");
                    continue;
                }

                if (section.Length == 0)
                {
                    section = "\n\n---\n## 🖼 Scene screenshots\n\n" +
                              "_Each object is shaped by its role (capsule = player, sphere = enemy, puck = coin, " +
                              "post = goal). Expected objects are coloured and marked ✓; unexpected/extra ones are " +
                              "red ✗; objects the model never built appear as faint grey ghosts marked ✗. The header " +
                              "shows the score and verdict._\n";
                }

                string verdict = r.Classification switch
                {
                    BenchmarkClassification.Pass => "✅ PASS",
                    BenchmarkClassification.Partial => "🟡 PARTIAL",
                    _ => "❌ FAIL"
                };
                section += $"\n### {r.Group} · {r.ScenarioName} — {r.Score.Base:0}/100 {verdict}\n";
                if (!string.IsNullOrEmpty(r.WhatItChecks))
                {
                    section += $"_{r.WhatItChecks}_\n";
                }

                section += $"\n![scene]({png})\n";
            }

            return section;
        }

        /// <summary>Inserts the SVG results-card image link right after the report's H1 title.</summary>
        private static string EmbedResultsImage(string markdown, string imageName, string altText = "results",
            string caption = null)
        {
            int firstBreak = markdown.IndexOf('\n');
            string image = $"\n\n![{altText}]({imageName})\n";
            if (!string.IsNullOrEmpty(caption))
            {
                image += caption + "\n";
            }

            return firstBreak < 0
                ? markdown + image
                : markdown.Substring(0, firstBreak + 1) + image + markdown.Substring(firstBreak + 1);
        }

        /// <summary>Reduces a model id (which may contain '/', ':', '@', spaces) to a safe file-name fragment.</summary>
        private static string SanitizeForFileName(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return "unknown-model";
            }

            char[] chars = modelId.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                            || c == '.' || c == '-' || c == '_';
                if (!safe)
                {
                    chars[i] = '-';
                }
            }

            string cleaned = new string(chars);
            return cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned;
        }
    }
}
#endif
#endif
