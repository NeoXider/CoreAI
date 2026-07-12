using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Benchmarking;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;
using RunEntry = CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunEntry;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Convenient launchers for the live game-creation benchmark (the <c>[Explicit]</c> PlayMode suite
    /// <c>GameCreationBenchmark_Suite</c>): a <b>CoreAI/Benchmarks</b> editor menu, an interactive
    /// <see cref="GameCreationBenchmarkWindowUitk"/>, and a batchmode <see cref="RunFromCli"/> entry for the
    /// terminal / CI / multi-model matrix. All three drive the same suite via <see cref="TestRunnerApi"/>
    /// and read the same config, so results are identical regardless of how it was launched.
    /// <para>
    /// Lives in the editor-only test assembly (not the shipped package) so a hard reference to
    /// <c>UnityEditor.TestRunner</c> never reaches package consumers.
    /// </para>
    /// </summary>
    public static class GameCreationBenchmarkLauncher
    {
        public const string SuiteTestName =
            "CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.GameCreationBenchmark_Suite";

        // Connection config consumed by PlayModeOpenAiTestConfig (kept in sync by name).
        public const string EnvBaseUrl = "COREAI_TEST_BASE_URL";
        public const string EnvApiKey = "COREAI_TEST_API_KEY";
        public const string EnvModel = "COREAI_TEST_MODEL";
        public const string EnvStreaming = "COREAI_TEST_STREAMING";
        public const string EnvNativeTools = "COREAI_TEST_NATIVE_TOOLS";

        // Benchmark shaping consumed by the suite.
        public const string EnvGroups = "COREAI_BENCHMARK_GROUPS";
        public const string EnvRepetitions = "COREAI_BENCHMARK_REPS";
        public const string EnvTimeout = "COREAI_BENCHMARK_TIMEOUT";
        public const string EnvRetries = "COREAI_BENCHMARK_RETRIES";

        // EditorPrefs keys shared by the window and the one-click menu, so the menu reuses window settings.
        public const string PrefPrefix = "CoreAI.Benchmark.";
        public const string PrefOverride = PrefPrefix + "override";
        public const string PrefModel = PrefPrefix + "model";
        public const string PrefBaseUrl = PrefPrefix + "baseUrl";
        public const string PrefStreaming = PrefPrefix + "streaming";
        public const string PrefNativeTools = PrefPrefix + "nativeTools";
        public const string PrefG2 = PrefPrefix + "g2";
        public const string PrefG1 = PrefPrefix + "g1";
        public const string PrefG3 = PrefPrefix + "g3";
        public const string PrefG4 = PrefPrefix + "g4";
        public const string PrefG5 = PrefPrefix + "g5";
        public const string PrefG6 = PrefPrefix + "g6";
        public const string PrefG7 = PrefPrefix + "g7";
        public const string PrefG8 = PrefPrefix + "g8";
        public const string PrefReps = PrefPrefix + "reps";
        public const string PrefRetries = PrefPrefix + "retries";
        public const string PrefTimeout = PrefPrefix + "timeout";

        // Must stay ABOVE the suite's own soft budget (COREAI_BENCHMARK_SUITE_BUDGET, default
        // 6000s) plus report/screenshot time: this launcher-side watchdog is only a last-resort
        // guard against a hung Test Runner, and when it fires the run dies WITHOUT a report.
        // Required ordering across the whole timeout chain:
        //   soft suite budget < NUnit [Timeout] (6600s) < launcher watchdog (7200s).
        // The first inequality is enforced by the clamp in
        // GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds (budget + report margin
        // 300s <= 6600s; the rep start-gate reserves maxAttempts x timeout inside the budget, so
        // scenario work always ends by the budget itself); this constant only has to stay above
        // the NUnit [Timeout]. 1900s here killed a full Opus 4.8 cloud run mid-suite (slow ~5-7 min
        // turns are normal for large CLI-bridged models, not a hang).
        private const double TimeoutSeconds = 7200d;
        private static double _startedAt;
        private static TestRunnerApi _api;
        private static ICallbacks _callbacks;

        /// <summary>Latest human-readable status line, surfaced by the window.</summary>
        public static string LastStatus { get; private set; } = "Idle.";

        public static bool IsRunning { get; private set; }

        [MenuItem("CoreAI/Benchmarks/Run Game-Creation Benchmark", priority = 100)]
        public static void RunFromMenu()
        {
            // One-click run honors the last settings chosen in the Benchmark Window (groups, reps, and
            // connection override). Set them once in the window, then this menu reuses them.
            ApplySavedConfig();
            RunViaTestRunner(true, false);
        }

        /// <summary>
        /// Applies the settings persisted by <see cref="GameCreationBenchmarkWindowUitk"/> as env vars, so a
        /// one-click run matches what the window would do. The API key is intentionally not persisted —
        /// when an override needs a key, launch from the window (or set <c>COREAI_TEST_API_KEY</c>).
        /// </summary>
        public static void ApplySavedConfig()
        {
            bool over = EditorPrefs.GetBool(PrefOverride, false);
            bool g2 = EditorPrefs.GetBool(PrefG2, true);
            bool g1 = EditorPrefs.GetBool(PrefG1, true);
            bool g3 = EditorPrefs.GetBool(PrefG3, true);
            bool g4 = EditorPrefs.GetBool(PrefG4, true);
            bool g5 = EditorPrefs.GetBool(PrefG5, true);
            bool g6 = EditorPrefs.GetBool(PrefG6, false);
            bool g7 = EditorPrefs.GetBool(PrefG7, false);
            bool g8 = LoadSavedG8Preference();
            int reps = EditorPrefs.GetInt(PrefReps, 1);
            int retries = EditorPrefs.GetInt(PrefRetries, 1);
            int timeout = EditorPrefs.GetInt(PrefTimeout, 0);
            string groups = GroupsCsv(g1, g2, g3, g4, g5, g6, g7, g8);

            // Override on: empty fields fall back to the project asset (so "model only" works). Off: pass
            // null connection so Configure clears the env vars and the asset is used.
            string model = over ? OrConfigured(EditorPrefs.GetString(PrefModel, ""), ConfiguredModel) : null;
            string baseUrl = over ? OrConfigured(EditorPrefs.GetString(PrefBaseUrl, ""), ConfiguredBaseUrl) : null;
            string apiKey = over ? OrConfigured("", ConfiguredApiKey) : null;

            Configure(
                model,
                baseUrl,
                apiKey,
                over ? ConnectionMode(EditorPrefs.GetInt(PrefStreaming, 0)) : (bool?)null,
                over ? ConnectionMode(EditorPrefs.GetInt(PrefNativeTools, 0)) : (bool?)null,
                groups,
                reps,
                retries,
                timeout > 0 ? timeout : (int?)null);
        }

        /// <summary>Tri-state connection toggle: 0 = use config (null), 1 = force On, 2 = force Off.</summary>
        public static bool? ConnectionMode(int mode)
        {
            return mode == 1 ? true : mode == 2 ? false : (bool?)null;
        }

        /// <summary>Loads G8 without expanding an existing saved subset created before the G8 toggle.</summary>
        internal static bool LoadSavedG8Preference()
        {
            if (EditorPrefs.HasKey(PrefG8))
            {
                return EditorPrefs.GetBool(PrefG8);
            }

            string[] priorGroupPrefs = { PrefG1, PrefG2, PrefG3, PrefG4, PrefG5, PrefG6, PrefG7 };
            bool anySaved = false;
            bool allEnabled = true;
            foreach (string pref in priorGroupPrefs)
            {
                bool saved = EditorPrefs.HasKey(pref);
                anySaved |= saved;
                allEnabled &= saved && EditorPrefs.GetBool(pref);
            }

            // WHY: A brand-new configuration still means the full suite, while any explicit legacy
            // WHY: subset stays unchanged unless all seven prior groups were explicitly enabled.
            return !anySaved || allEnabled;
        }

        /// <summary>All benchmark group ids the suite knows, in the launcher's toggle order.</summary>
        private static readonly string[] AllGroupIds = { "G2", "G1", "G3", "G4", "G5", "G6", "G7", "G8" };

        /// <summary>CSV of the enabled benchmark groups; empty string means "all groups".</summary>
        public static string GroupsCsv(bool g1, bool g2, bool g3, bool g4, bool g5, bool g6, bool g7, bool g8)
        {
            // WHY: G8 is part of this list on purpose — before it was included here, any subset run
            // silently dropped G8 (and shrank the suite-score denominator) because only an EMPTY csv
            // ("all groups") could reach it.
            bool[] flags = { g2, g1, g3, g4, g5, g6, g7, g8 };
            List<string> on = new();
            for (int i = 0; i < AllGroupIds.Length; i++)
            {
                if (flags[i])
                {
                    on.Add(AllGroupIds[i]);
                }
            }

            if (on.Count == 0 || on.Count == AllGroupIds.Length)
            {
                return "";
            }

            List<string> off = new();
            foreach (string id in AllGroupIds)
            {
                if (!on.Contains(id))
                {
                    off.Add(id);
                }
            }

            Debug.Log($"[Benchmark] Group subset: running {string.Join(",", on)}; " +
                      $"excluded {string.Join(",", off)} (suite score is averaged over the selected groups only).");
            return string.Join(",", on);
        }

        [MenuItem("CoreAI/Benchmarks/Open Latest Results", priority = 120)]
        public static void OpenLatestResults()
        {
            string latest = FindLatestResult();
            if (string.IsNullOrEmpty(latest))
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark",
                    "No benchmark results found yet. Run the benchmark first.", "OK");
                return;
            }

            EditorUtility.RevealInFinder(latest);
        }

        [MenuItem("CoreAI/Benchmarks/Open Benchmark Index", priority = 121)]
        public static void OpenIndex()
        {
            string index = Path.Combine(ResultsRoot(), "INDEX.md");
            if (!File.Exists(index))
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark",
                    "No index yet — run the benchmark at least once.", "OK");
                return;
            }

            EditorUtility.RevealInFinder(index);
        }

        [MenuItem("CoreAI/Benchmarks/Stop Running Benchmark (save partial)", priority = 90)]
        public static void StopRunningBenchmark()
        {
            if (!BenchmarkProgress.IsRunning)
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark", "No benchmark is currently running.", "OK");
                return;
            }

            // Cooperative stop: the suite loop breaks between scenario reps and still writes the report for
            // everything finished so far (partial), instead of the run vanishing with no artifacts.
            BenchmarkProgress.RequestStop();
            Debug.Log("[Benchmark] Stop requested — the suite will finish the current scenario, then save a "
                      + "partial report and exit.");
        }

        [MenuItem("CoreAI/Benchmarks/Stop Running Benchmark (save partial)", validate = true)]
        public static bool StopRunningBenchmarkValidate()
        {
            return BenchmarkProgress.IsRunning && !BenchmarkProgress.StopRequested;
        }

        [MenuItem("CoreAI/Benchmarks/Build Model Comparison Report", priority = 140)]
        public static void BuildComparisonReport()
        {
            string root = ResultsRoot();
            if (!Directory.Exists(root))
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark", "No results yet.", "OK");
                return;
            }

            // Newest run per model, parsed from the machine-readable JSON reports.
            Dictionary<string, ModelSummary> latestByModel = new();
            foreach (FileInfo f in new DirectoryInfo(root).GetFiles("BENCHMARK_*.json"))
            {
                ModelSummary s = TryParseSummary(f.FullName);
                if (s == null)
                {
                    continue;
                }

                if (!latestByModel.TryGetValue(s.ModelId, out ModelSummary prev)
                    || string.CompareOrdinal(s.RunId, prev.RunId) > 0)
                {
                    latestByModel[s.ModelId] = s;
                }
            }

            if (latestByModel.Count == 0)
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark", "No parseable JSON reports found.", "OK");
                return;
            }

            WriteComparison(root, latestByModel.Values.ToList(), null);
        }

        /// <summary>
        /// Writes COMPARISON.md (with an embedded TerminalBench-style bar chart) + COMPARISON.svg from the
        /// given model summaries. <paramref name="pinnedModelId"/> puts one model first (the two-mode view);
        /// null ranks everything best-first.
        /// </summary>
        public static string WriteComparison(string root, List<ModelSummary> models, string pinnedModelId)
        {
            string md = ModelComparison.Build(models, BenchmarkInfo.TitleWithVersion, pinnedModelId);
            string svg = ModelComparison.ToComparisonSvg(models, pinnedModelId);
            string mdPath = Path.Combine(root, "COMPARISON.md");
            File.WriteAllText(mdPath, md);
            File.WriteAllText(Path.Combine(root, "COMPARISON.svg"), svg);
            Debug.Log($"[Benchmark] Comparison of {models.Count} model(s): {mdPath}");
            EditorUtility.RevealInFinder(mdPath);
            return mdPath;
        }

        /// <summary>One past benchmark run on disk: its parsed summary plus the report file paths.</summary>
        public sealed class RunEntry
        {
            public ModelSummary Summary;
            public string MdPath;
            public string JsonPath;
        }

        /// <summary>All past runs found on disk, newest first (parsed from the JSON reports).</summary>
        public static List<RunEntry> ListRuns()
        {
            List<RunEntry> list = new();
            string root = ResultsRoot();
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (FileInfo f in new DirectoryInfo(root).GetFiles("BENCHMARK_*.json"))
            {
                ModelSummary s = TryParseSummary(f.FullName);
                if (s == null)
                {
                    continue;
                }

                string md = f.FullName.Substring(0, f.FullName.Length - ".json".Length) + ".md";
                list.Add(new RunEntry { Summary = s, JsonPath = f.FullName, MdPath = md });
            }

            return list.OrderByDescending(e => e.Summary.RunId).ToList();
        }

        /// <summary>
        /// Deletes a run's report files: .md + .json plus every companion artifact sharing the same
        /// stem (.svg chart, _modelcard.png, _g6_free_build_hero.png, per-scenario _gN_*.png screenshots).
        /// Returns true on success.
        /// </summary>
        public static bool DeleteRun(RunEntry entry)
        {
            try
            {
                if (entry == null || string.IsNullOrEmpty(entry.JsonPath))
                {
                    return false;
                }

                string dir = Path.GetDirectoryName(entry.JsonPath);
                string stem = Path.GetFileNameWithoutExtension(entry.JsonPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
                {
                    return false;
                }

                // "<stem>.*" catches .json/.md/.svg; "<stem>_*" catches modelcard/hero/per-scenario PNGs.
                // Matching on the exact stem boundary (not a raw prefix) avoids deleting an unrelated run
                // whose stem happens to start with this one's characters.
                foreach (string pattern in new[] { stem + ".*", stem + "_*" })
                {
                    foreach (string file in Directory.GetFiles(dir, pattern))
                    {
                        File.Delete(file);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] could not delete run: {ex.Message}");
                return false;
            }
        }

        /// <summary>Deletes all saved benchmark runs. Returns the number of runs successfully deleted.</summary>
        public static int DeleteAllRuns()
        {
            List<RunEntry> runs = ListRuns();
            if (runs.Count == 0)
            {
                return 0;
            }

            int deleted = 0;
            foreach (RunEntry entry in runs)
            {
                if (DeleteRun(entry))
                {
                    deleted++;
                }
            }

            return deleted;
        }

        public static void OpenReport(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
            }
        }

        /// <summary>
        /// Parses one benchmark report JSON into a <see cref="ModelSummary"/> (null when the file is not
        /// a parseable report). Public so editor scripts can build custom comparisons from hand-picked
        /// report files via <see cref="WriteComparison"/> instead of the newest-per-model default.
        /// </summary>
        public static ModelSummary ParseSummary(string jsonPath)
        {
            return TryParseSummary(jsonPath);
        }

        private static ModelSummary TryParseSummary(string jsonPath)
        {
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(jsonPath));
                JObject meta = (JObject)root["metadata"];
                JObject sum = (JObject)root["summary"];
                if (meta == null || sum == null)
                {
                    return null;
                }

                ModelSummary s = new()
                {
                    ModelId = (string)meta["modelId"] ?? "unknown",
                    RunId = (string)meta["runId"] ?? "",
                    TimestampUtc = (string)meta["timestampUtc"] ?? "",
                    Repetitions = (int?)meta["repetitions"] ?? 1,
                    SuiteBase = (double?)sum["suiteBaseScore"] ?? 0,
                    PassRate = (double?)sum["passRate"] ?? 0,
                    Pass = (int?)sum["pass"] ?? 0,
                    Partial = (int?)sum["partial"] ?? 0,
                    Fail = (int?)sum["fail"] ?? 0,
                    TotalTokens = (long?)sum["totalTokens"] ?? 0,
                    TotalCompletionTokens = (long?)sum["totalCompletionTokens"] ?? 0,
                    TotalGenerationMs = (double?)sum["totalGenerationMs"] ?? 0,
                    TotalLatencyMs = (double?)sum["totalLatencyMs"] ?? 0,
                    MeanEfficiencyBonus = (double?)sum["meanEfficiencyBonus"] ?? 0,
                    MeanTokenBonus = (double?)sum["meanTokenBonus"] ?? 0,
                    MeanTimeBonus = (double?)sum["meanTimeBonus"] ?? 0
                };

                long totalCalls = (long?)sum["totalToolCalls"] ?? 0;
                long failedCalls = (long?)sum["failedToolCalls"] ?? 0;
                s.ToolErrorRate = totalCalls == 0 ? 0 : (double)failedCalls / totalCalls;

                s.GameFitOverall = (double?)sum["gameFitOverall"] ?? 0;
                s.BestRole = (string)sum["bestRole"] ?? "";
                if (sum["roles"] is JObject roles)
                {
                    foreach (KeyValuePair<string, JToken> kv in roles)
                    {
                        s.Roles[kv.Key] = (double?)kv.Value ?? 0;
                    }
                }

                if (sum["dimensions"] is JObject dims)
                {
                    foreach (KeyValuePair<string, JToken> kv in dims)
                    {
                        s.Dimensions[kv.Key] = (double?)kv.Value ?? 0;
                    }
                }

                return s;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] could not parse {Path.GetFileName(jsonPath)}: {ex.Message}");
                return null;
            }
        }

        [MenuItem("CoreAI/Benchmarks/Run Game-Creation Benchmark", validate = true)]
        private static bool NotRunning()
        {
            return !IsRunning;
        }

        /// <summary>Project-root <c>TestResults/CoreAI/Benchmarks</c> directory (created lazily by the suite).</summary>
        public static string ResultsRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, "TestResults", "CoreAI", "Benchmarks");
        }

        /// <summary>Newest <c>BENCHMARK_&lt;date&gt;_&lt;model&gt;.md</c> report file, or null if none yet.</summary>
        public static string FindLatestResult()
        {
            string root = ResultsRoot();
            if (!Directory.Exists(root))
            {
                return null;
            }

            return new DirectoryInfo(root)
                .GetFiles("BENCHMARK_*.md")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        /// <summary>
        /// Applies optional connection + shaping overrides as process env vars, then launches the suite.
        /// Pass null/empty to leave a value to the resolved config (env / local file / settings asset).
        /// </summary>
        public static void Configure(
            string model = null, string baseUrl = null, string apiKey = null,
            bool? streaming = null, bool? nativeTools = null,
            string groupsCsv = null, int? repetitions = null, int? retries = null, int? timeoutSeconds = null)
        {
            // Connection vars: set when provided, otherwise CLEAR them — so an empty/disabled field falls
            // back to the project CoreAISettings asset instead of leaving a stale value from a prior run.
            SetOrClear(EnvModel, model);
            SetOrClear(EnvBaseUrl, baseUrl);
            SetOrClear(EnvApiKey, apiKey);
            SetOrClear(EnvStreaming, streaming.HasValue ? streaming.Value ? "1" : "0" : null);
            SetOrClear(EnvNativeTools, nativeTools.HasValue ? nativeTools.Value ? "1" : "0" : null);

            Environment.SetEnvironmentVariable(EnvGroups, groupsCsv ?? "");
            SetOrClear(EnvRepetitions, repetitions?.ToString());
            SetOrClear(EnvRetries, retries?.ToString());
            SetOrClear(EnvTimeout, timeoutSeconds is > 0 ? timeoutSeconds.Value.ToString() : null);
        }

        /// <summary>Clears every per-run connection env var so the project CoreAISettings asset is used.</summary>
        public static void ClearConnectionOverrides()
        {
            SetOrClear(EnvModel, null);
            SetOrClear(EnvBaseUrl, null);
            SetOrClear(EnvApiKey, null);
            SetOrClear(EnvStreaming, null);
            SetOrClear(EnvNativeTools, null);
        }

        private static void SetOrClear(string key, string value)
        {
            Environment.SetEnvironmentVariable(key, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        /// <summary>
        /// Executes the suite through the Test Runner. <paramref name="revealOnFinish"/> opens the result
        /// folder; <paramref name="exitOnFinish"/> quits the editor with a status code (batchmode/CI).
        /// </summary>
        public static void RunViaTestRunner(bool revealOnFinish, bool exitOnFinish)
        {
            if (IsRunning)
            {
                Debug.LogWarning("[Benchmark] A run is already in progress.");
                return;
            }

            IsRunning = true;
            LastStatus = "Running…";
            _startedAt = EditorApplication.timeSinceStartup;

            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new Callbacks(revealOnFinish, exitOnFinish);
            _api.RegisterCallbacks(_callbacks);
            EditorApplication.update += Watchdog;

            ExecutionSettings settings = new(new Filter
            {
                testMode = TestMode.PlayMode,
                testNames = new[] { SuiteTestName }
            });

            Debug.Log($"[Benchmark] Launching {SuiteTestName} …");
            _api.Execute(settings);
        }

        private static void Cleanup()
        {
            EditorApplication.update -= Watchdog;
            IsRunning = false;
            if (_api != null)
            {
                if (_callbacks != null)
                {
                    _api.UnregisterCallbacks(_callbacks);
                }

                UnityEngine.Object.DestroyImmediate(_api);
                _api = null;
                _callbacks = null;
            }
        }

        /// <summary>
        /// Batchmode entry: <c>Unity.exe -batchmode -projectPath … -executeMethod
        /// CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunFromCli</c>. Reads
        /// <c>-coreAiBenchmarkModel</c> / <c>-coreAiBenchmarkGroups</c> / <c>-coreAiBenchmarkReps</c> from
        /// the command line (or the COREAI_* env vars) and exits with a status code.
        /// </summary>
        public static void RunFromCli()
        {
            string[] args = Environment.GetCommandLineArgs();
            string model = ReadArg(args, "-coreAiBenchmarkModel");
            string groups = ReadArg(args, "-coreAiBenchmarkGroups");
            string repsRaw = ReadArg(args, "-coreAiBenchmarkReps");
            int? reps = int.TryParse(repsRaw, out int r) ? r : (int?)null;

            Configure(
                string.IsNullOrWhiteSpace(model) ? null : model,
                groupsCsv: string.IsNullOrWhiteSpace(groups) ? null : groups,
                repetitions: reps);

            RunViaTestRunner(false, true);
        }

        private static void Watchdog()
        {
            if (EditorApplication.timeSinceStartup - _startedAt < TimeoutSeconds)
            {
                return;
            }

            Cleanup();
            LastStatus = "Timed out.";
            Debug.LogError($"[Benchmark] Timed out after {TimeoutSeconds:0} seconds.");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(124);
            }
        }

        // --- Project CoreAISettings asset (used when no override / for empty override fields) ---

        private static Infrastructure.Llm.CoreAISettingsAsset Asset =>
            Infrastructure.Llm.CoreAISettingsAsset.Instance;

        public static string ConfiguredModel => Asset != null ? Asset.ModelName : "";
        public static string ConfiguredBaseUrl => Asset != null ? Asset.ApiBaseUrl : "";
        public static string ConfiguredApiKey => Asset != null ? Asset.ApiKey : "";

        /// <summary>Fills an empty override field from the project asset so "model only" overrides work.</summary>
        public static string OrConfigured(string field, string configured)
        {
            return string.IsNullOrWhiteSpace(field) ? configured : field;
        }

        private static string ReadArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1] ?? "";
                }
            }

            return "";
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly bool _reveal;
            private readonly bool _exit;

            public Callbacks(bool reveal, bool exit)
            {
                _reveal = reveal;
                _exit = exit;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Cleanup();

                string state = result?.ResultState ?? "";
                bool skipped = result != null && result.SkipCount > 0 && result.PassCount == 0 && result.FailCount == 0;
                LastStatus = skipped
                    ? "Skipped — no model configured (see Console)."
                    : $"Finished: {state} (passed {result?.PassCount}, failed {result?.FailCount}).";
                Debug.Log($"[Benchmark] {LastStatus}");

                string latest = FindLatestResult();
                if (!string.IsNullOrEmpty(latest))
                {
                    Debug.Log($"[Benchmark] Report: {latest}");
                    if (_reveal)
                    {
                        EditorUtility.RevealInFinder(latest);
                    }
                }

                if (_exit)
                {
                    bool failed = result != null &&
                                  (result.FailCount > 0 || state.StartsWith("Failed", StringComparison.Ordinal));
                    EditorApplication.Exit(failed ? 1 : 0);
                }
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }
    }
}
