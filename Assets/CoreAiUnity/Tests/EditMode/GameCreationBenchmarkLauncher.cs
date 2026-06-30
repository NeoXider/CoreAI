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
    /// <see cref="GameCreationBenchmarkWindow"/>, and a batchmode <see cref="RunFromCli"/> entry for the
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
        public const string PrefReps = PrefPrefix + "reps";
        public const string PrefRetries = PrefPrefix + "retries";
        public const string PrefTimeout = PrefPrefix + "timeout";

        private const double TimeoutSeconds = 1900d;
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
            RunViaTestRunner(revealOnFinish: true, exitOnFinish: false);
        }

        /// <summary>
        /// Applies the settings persisted by <see cref="GameCreationBenchmarkWindow"/> as env vars, so a
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
            int reps = EditorPrefs.GetInt(PrefReps, 1);
            int retries = EditorPrefs.GetInt(PrefRetries, 1);
            int timeout = EditorPrefs.GetInt(PrefTimeout, 0);
            string groups = GroupsCsv(g1, g2, g3, g4, g5, g6);

            // Override on: empty fields fall back to the project asset (so "model only" works). Off: pass
            // null connection so Configure clears the env vars and the asset is used.
            string model = over ? OrConfigured(EditorPrefs.GetString(PrefModel, ""), ConfiguredModel) : null;
            string baseUrl = over ? OrConfigured(EditorPrefs.GetString(PrefBaseUrl, ""), ConfiguredBaseUrl) : null;
            string apiKey = over ? OrConfigured("", ConfiguredApiKey) : null;

            Configure(
                model: model,
                baseUrl: baseUrl,
                apiKey: apiKey,
                streaming: over ? ConnectionMode(EditorPrefs.GetInt(PrefStreaming, 0)) : (bool?)null,
                nativeTools: over ? ConnectionMode(EditorPrefs.GetInt(PrefNativeTools, 0)) : (bool?)null,
                groupsCsv: groups,
                repetitions: reps,
                retries: retries,
                timeoutSeconds: timeout > 0 ? timeout : (int?)null);
        }

        /// <summary>Tri-state connection toggle: 0 = use config (null), 1 = force On, 2 = force Off.</summary>
        public static bool? ConnectionMode(int mode) => mode == 1 ? true : (mode == 2 ? false : (bool?)null);

        /// <summary>CSV of the enabled benchmark groups; empty string means "all groups".</summary>
        public static string GroupsCsv(bool g1, bool g2, bool g3, bool g4, bool g5, bool g6)
        {
            List<string> on = new();
            if (g2) { on.Add("G2"); }
            if (g1) { on.Add("G1"); }
            if (g3) { on.Add("G3"); }
            if (g4) { on.Add("G4"); }
            if (g5) { on.Add("G5"); }
            if (g6) { on.Add("G6"); }
            return on.Count is 0 or 6 ? "" : string.Join(",", on);
        }

        [MenuItem("CoreAI/Benchmarks/Benchmark Window…", priority = 101)]
        public static void OpenWindow()
        {
            GameCreationBenchmarkWindow.ShowWindow();
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

            WriteComparison(root, latestByModel.Values.ToList(), pinnedModelId: null);
        }

        /// <summary>
        /// Writes COMPARISON.md (with an embedded TerminalBench-style bar chart) + COMPARISON.svg from the
        /// given model summaries. <paramref name="pinnedModelId"/> puts one model first (the two-mode view);
        /// null ranks everything best-first.
        /// </summary>
        public static string WriteComparison(string root, List<ModelSummary> models, string pinnedModelId)
        {
            string md = ModelComparison.Build(models, "Game-Creation Benchmark v1", pinnedModelId);
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

        /// <summary>Deletes a run's report files (.md + .json). Returns true on success.</summary>
        public static bool DeleteRun(RunEntry entry)
        {
            try
            {
                if (entry == null)
                {
                    return false;
                }

                if (File.Exists(entry.JsonPath))
                {
                    File.Delete(entry.JsonPath);
                }

                if (File.Exists(entry.MdPath))
                {
                    File.Delete(entry.MdPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] could not delete run: {ex.Message}");
                return false;
            }
        }

        public static void OpenReport(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
            }
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
        [MenuItem("CoreAI/Benchmarks/Benchmark Window…", validate = true)]
        private static bool NotRunning() => !IsRunning;

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
            SetOrClear(EnvStreaming, streaming.HasValue ? (streaming.Value ? "1" : "0") : null);
            SetOrClear(EnvNativeTools, nativeTools.HasValue ? (nativeTools.Value ? "1" : "0") : null);

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
                model: string.IsNullOrWhiteSpace(model) ? null : model,
                groupsCsv: string.IsNullOrWhiteSpace(groups) ? null : groups,
                repetitions: reps);

            RunViaTestRunner(revealOnFinish: false, exitOnFinish: true);
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

        private static CoreAI.Infrastructure.Llm.CoreAISettingsAsset Asset =>
            CoreAI.Infrastructure.Llm.CoreAISettingsAsset.Instance;

        public static string ConfiguredModel => Asset != null ? Asset.ModelName : "";
        public static string ConfiguredBaseUrl => Asset != null ? Asset.ApiBaseUrl : "";
        public static string ConfiguredApiKey => Asset != null ? Asset.ApiKey : "";

        /// <summary>Fills an empty override field from the project asset so "model only" overrides work.</summary>
        public static string OrConfigured(string field, string configured) =>
            string.IsNullOrWhiteSpace(field) ? configured : field;

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

    /// <summary>
    /// One-screen control panel for the game-creation benchmark: choose the model/connection, pick
    /// which groups run (G1/G2), set repetitions, and launch — no Test Runner navigation required.
    /// </summary>
    public sealed class GameCreationBenchmarkWindow : EditorWindow
    {
        private static readonly string[] ConnModeOptions = { "Default (config)", "On", "Off" };

        private bool _overrideConnection;
        private string _model = "";
        private string _baseUrl = "";
        private string _apiKey = "";
        private int _streamingMode; // 0 = config, 1 = On, 2 = Off
        private int _nativeToolsMode;
        private bool _runG2 = true;
        private bool _runG1 = true;
        private bool _runG3 = true;
        private bool _runG4 = true;
        private bool _runG5 = true;
        private bool _runG6 = false;
        private int _reps = 1;
        private int _retries = 1;
        private int _timeoutSeconds; // 0 = per-scenario default
        private Vector2 _scroll;
        private Vector2 _historyScroll;
        private GUIStyle _titleStyle;
        private int _tab; // 0 = Run, 1 = History
        private List<RunEntry> _runs;
        private readonly HashSet<string> _expanded = new();
        private readonly HashSet<string> _expandedRuns = new();
        private readonly Dictionary<string, Texture2D> _thumbs = new();

        private static readonly string[] DimOrder =
            { "ToolCorrectness", "IntentSequence", "TaskCompletion", "Determinism", "Reasoning", "InstructionAdherence" };

        public static void ShowWindow()
        {
            GameCreationBenchmarkWindow window = GetWindow<GameCreationBenchmarkWindow>(false, "CoreAI Benchmark");
            window.minSize = new Vector2(480, 540);
            window.Show();
        }

        private void OnEnable()
        {
            _overrideConnection = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefOverride, false);
            _model = EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefModel, "");
            _baseUrl = EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefBaseUrl, "");
            _streamingMode = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefStreaming, 0);
            _nativeToolsMode = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefNativeTools, 0);
            _runG2 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG2, true);
            _runG1 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG1, true);
            _runG3 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG3, true);
            _runG4 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG4, true);
            _runG5 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG5, true);
            _runG6 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG6, false);
            _reps = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefReps, 1);
            _retries = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefRetries, 1);
            _timeoutSeconds = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefTimeout, 0);
        }

        private void Persist()
        {
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefOverride, _overrideConnection);
            EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefModel, _model);
            EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefBaseUrl, _baseUrl);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefStreaming, _streamingMode);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefNativeTools, _nativeToolsMode);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG2, _runG2);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG1, _runG1);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG3, _runG3);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG4, _runG4);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG5, _runG5);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG6, _runG6);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefReps, _reps);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefRetries, _retries);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefTimeout, _timeoutSeconds);
        }

        private void OnGUI()
        {
            _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };

            DrawToolbar();

            if (_tab == 0)
            {
                DrawRunTab();
            }
            else
            {
                DrawHistoryTab();
            }

            if (GameCreationBenchmarkLauncher.IsRunning || CoreAI.Benchmarking.BenchmarkProgress.IsRunning)
            {
                Repaint();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Toggle(_tab == 0, "Run", EditorStyles.toolbarButton, GUILayout.Width(70)) && _tab != 0)
                {
                    _tab = 0;
                }

                int historyCount = _runs?.Count ?? 0;
                string historyLabel = historyCount > 0 ? $"History ({historyCount})" : "History";
                if (GUILayout.Toggle(_tab == 1, historyLabel, EditorStyles.toolbarButton, GUILayout.Width(90))
                    && _tab != 1)
                {
                    _tab = 1;
                    RefreshRuns();
                }

                GUILayout.FlexibleSpace();

                if (_tab == 1 && GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    RefreshRuns();
                }

                if (GUILayout.Button("Compare", EditorStyles.toolbarButton, GUILayout.Width(64)))
                {
                    GameCreationBenchmarkLauncher.BuildComparisonReport();
                    RefreshRuns();
                }

                if (GUILayout.Button("Folder", EditorStyles.toolbarButton, GUILayout.Width(54)))
                {
                    string root = GameCreationBenchmarkLauncher.ResultsRoot();
                    if (System.IO.Directory.Exists(root))
                    {
                        EditorUtility.RevealInFinder(root);
                    }
                }
            }
        }

        private void DrawRunTab()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("🎮  Game-Creation Benchmark", _titleStyle);
            EditorGUILayout.LabelField(
                "The model builds a game with execute_lua + world_command; scored 0..100.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(6);

            DrawScenariosSection();
            DrawRunOptionsSection();
            DrawConnectionSection();
            DrawRunSection();
            DrawStatusSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHistoryTab()
        {
            if (_runs == null)
            {
                RefreshRuns();
            }

            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll);
            EditorGUILayout.Space(4);

            if (_runs.Count == 0)
            {
                EditorGUILayout.HelpBox("No runs yet. Run a benchmark to see its history here.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawHistorySummary();

            // Group by model, models ordered by their newest run; each model is a collapsible node.
            foreach (IGrouping<string, RunEntry> model in _runs
                         .GroupBy(r => r.Summary.ModelId)
                         .OrderByDescending(g => g.Max(r => r.Summary.RunId)))
            {
                DrawModelNode(model.Key, model.ToList());
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawModelNode(string modelId, List<RunEntry> runs)
        {
            bool expanded = _expanded.Contains(modelId);
            double best = runs.Max(r => r.Summary.SuiteBase);

            using (new EditorGUILayout.HorizontalScope())
            {
                string latest = runs.Count > 0 ? FormatRunDate(runs[0].Summary.RunId) : "";
                bool now = EditorGUILayout.Foldout(expanded, $"{modelId}   ·  {latest}   ({runs.Count})", true);
                if (now != expanded)
                {
                    if (now)
                    {
                        _expanded.Add(modelId);
                    }
                    else
                    {
                        _expanded.Remove(modelId);
                    }
                }

                GUILayout.FlexibleSpace();
                DrawScoreChip(best);
            }

            if (_expanded.Contains(modelId))
            {
                EditorGUI.indentLevel++;
                foreach (RunEntry e in runs)
                {
                    DrawRunRow(e);
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }
        }

        private void DrawHistorySummary()
        {
            int models = _runs.Select(r => r.Summary.ModelId).Distinct().Count();
            RunEntry best = _runs.OrderByDescending(r => r.Summary.SuiteBase).First();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{_runs.Count} run(s) · {models} model(s)", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Best:", EditorStyles.miniLabel, GUILayout.Width(34));
                    DrawScoreChip(best.Summary.SuiteBase);
                    EditorGUILayout.LabelField($"{best.Summary.ModelId}", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(2);
        }

        private void DrawRunRow(RunEntry e)
        {
            ModelSummary s = e.Summary;
            string key = e.JsonPath;
            bool expanded = _expandedRuns.Contains(key);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(expanded ? "▾" : "▸", EditorStyles.label, GUILayout.Width(16)))
                    {
                        ToggleRun(key, !expanded);
                    }

                    EditorGUILayout.LabelField(FormatRunDate(s.RunId), EditorStyles.miniLabel, GUILayout.Width(112));
                    EditorGUILayout.LabelField(s.ModelId, EditorStyles.miniBoldLabel, GUILayout.Width(170));

                    Rect bar = GUILayoutUtility.GetRect(64, 14, GUILayout.Width(64));
                    EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.18f));
                    Rect fill = new(bar.x, bar.y, bar.width * Mathf.Clamp01((float)(s.SuiteBase / 100.0)), bar.height);
                    EditorGUI.DrawRect(fill, ScoreColor(s.SuiteBase));
                    EditorGUI.LabelField(bar, $" {s.SuiteBase:0.#}", EditorStyles.miniBoldLabel);

                    if (s.GameFitOverall > 0)
                    {
                        EditorGUILayout.LabelField($"🎯{s.GameFitOverall:0.#}", EditorStyles.miniBoldLabel,
                            GUILayout.Width(40));
                    }

                    EditorGUILayout.LabelField($"P{s.Pass}/{s.Partial}/F{s.Fail}", EditorStyles.miniLabel,
                        GUILayout.Width(58));
                    EditorGUILayout.LabelField(FormatTokens(s.TotalTokens), EditorStyles.miniLabel, GUILayout.Width(54));
                    EditorGUILayout.LabelField(FormatDuration(s.TotalLatencyMs), EditorStyles.miniLabel,
                        GUILayout.Width(54));
                    string tps = s.TotalCompletionTokens > 0 ? $"{s.TokensPerSecond:0} tk/s" : "— tk/s";
                    EditorGUILayout.LabelField(tps, EditorStyles.miniLabel, GUILayout.Width(58));

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(48)))
                    {
                        GameCreationBenchmarkLauncher.OpenReport(e.MdPath);
                    }

                    if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(24)))
                    {
                        if (EditorUtility.DisplayDialog("Delete run",
                                $"Delete this benchmark report?\n\n{s.ModelId} — {FormatRunDate(s.RunId)} " +
                                $"({s.SuiteBase:0.#}/100)", "Delete", "Cancel"))
                        {
                            GameCreationBenchmarkLauncher.DeleteRun(e);
                            RefreshRuns();
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                if (expanded)
                {
                    DrawRunDetails(e);
                }
            }
        }

        private void DrawRunDetails(RunEntry e)
        {
            ModelSummary s = e.Summary;
            EditorGUILayout.Space(2);

            // Game-fitness by role — the headline "usable for what" answer, shown first.
            if (s.GameFitOverall > 0 || s.Roles.Count > 0)
            {
                EditorGUILayout.LabelField(
                    $"🎯 Game-fitness {s.GameFitOverall:0.#}/10 — best: {s.BestRole}", EditorStyles.miniBoldLabel);
                foreach (KeyValuePair<string, double> kv in s.Roles.OrderByDescending(r => r.Value))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(kv.Key, EditorStyles.miniLabel, GUILayout.Width(150));
                        Rect b = GUILayoutUtility.GetRect(80, 11, GUILayout.Width(80));
                        EditorGUI.DrawRect(b, new Color(0f, 0f, 0f, 0.18f));
                        EditorGUI.DrawRect(
                            new Rect(b.x, b.y, b.width * Mathf.Clamp01((float)(kv.Value / 10.0)), b.height),
                            ScoreColor(kv.Value * 10));
                        EditorGUILayout.LabelField($"{kv.Value:0.#}/10  {RoleVerdictShort(kv.Value)}",
                            EditorStyles.miniLabel);
                    }
                }

                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Dimensions", EditorStyles.miniBoldLabel);
            }

            foreach (string dim in DimOrder)
            {
                if (s.Dimensions.TryGetValue(dim, out double v))
                {
                    DrawDimMiniBar(DimShort(dim), v);
                }
            }

            EditorGUILayout.LabelField(
                $"Bonus +{s.MeanEfficiencyBonus:0.#} (tokens +{s.MeanTokenBonus:0.#}, time +{s.MeanTimeBonus:0.#}) · " +
                $"tool-errors {s.ToolErrorRate * 100:0.#}% · pass-rate {s.PassRate * 100:0.#}%",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"{s.TotalTokens} tokens ({(s.TotalCompletionTokens > 0 ? s.TotalCompletionTokens + " gen" : "gen n/a")}) · " +
                $"{FormatDuration(s.TotalLatencyMs)} · " +
                $"{(s.TotalCompletionTokens > 0 ? $"{s.TokensPerSecond:0.#} tok/s generation" : "tok/s n/a (older report)")}",
                EditorStyles.miniLabel);

            DrawSceneThumbnails(e);
        }

        /// <summary>Inline thumbnails of the captured Unity scene screenshots for this run.</summary>
        private void DrawSceneThumbnails(RunEntry e)
        {
            string dir = Path.GetDirectoryName(e.MdPath);
            string stem = Path.GetFileNameWithoutExtension(e.MdPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
            {
                return;
            }

            string[] pngs;
            try
            {
                pngs = Directory.GetFiles(dir, stem + "_*.png");
            }
            catch
            {
                return;
            }

            if (pngs.Length == 0)
            {
                return;
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField($"Scene screenshots ({pngs.Length}) — click to open full size",
                EditorStyles.miniBoldLabel);

            const float w = 138f;
            const float pad = 6f;
            float avail = Mathf.Max(w, position.width - 28f);
            int perRow = Mathf.Max(1, (int)(avail / (w + pad)));

            for (int i = 0; i < pngs.Length; i += perRow)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int j = i; j < Mathf.Min(i + perRow, pngs.Length); j++)
                    {
                        Texture2D tex = LoadThumb(pngs[j]);
                        float h = tex != null ? w * tex.height / Mathf.Max(1, tex.width) : w * 0.5625f;
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(w)))
                        {
                            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                            if (tex != null)
                            {
                                GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                            }

                            if (GUI.Button(r, new GUIContent("", "Open " + Path.GetFileName(pngs[j])), GUIStyle.none))
                            {
                                OpenImage(pngs[j]);
                            }

                            EditorGUILayout.LabelField(ScenarioFromPng(pngs[j], stem),
                                EditorStyles.miniLabel, GUILayout.Width(w));
                        }
                    }
                }
            }
        }

        private static void OpenImage(string path)
        {
            try
            {
                Application.OpenURL("file:///" + path.Replace("\\", "/"));
            }
            catch
            {
                EditorUtility.RevealInFinder(path);
            }
        }

        private static string ScenarioFromPng(string png, string stem)
        {
            string fn = Path.GetFileNameWithoutExtension(png);
            return fn.Length > stem.Length + 1 && fn.StartsWith(stem + "_", StringComparison.Ordinal)
                ? fn.Substring(stem.Length + 1)
                : fn;
        }

        private Texture2D LoadThumb(string path)
        {
            if (_thumbs.TryGetValue(path, out Texture2D cached))
            {
                return cached;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D tex = new(2, 2);
                if (tex.LoadImage(bytes))
                {
                    _thumbs[path] = tex;
                    return tex;
                }

                UnityEngine.Object.DestroyImmediate(tex);
            }
            catch
            {
                // ignore — no thumbnail
            }

            _thumbs[path] = null;
            return null;
        }

        private void DrawDimMiniBar(string label, double value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(60));
                Rect bar = GUILayoutUtility.GetRect(140, 11, GUILayout.Width(140));
                EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.18f));
                Rect fill = new(bar.x, bar.y, bar.width * Mathf.Clamp01((float)(value / 100.0)), bar.height);
                EditorGUI.DrawRect(fill, ScoreColor(value));
                EditorGUILayout.LabelField($"{value:0.#}", EditorStyles.miniLabel, GUILayout.Width(40));
            }
        }

        private void ToggleRun(string key, bool on)
        {
            if (on)
            {
                _expandedRuns.Add(key);
            }
            else
            {
                _expandedRuns.Remove(key);
            }
        }

        private static string DimShort(string dim)
        {
            return dim switch
            {
                "ToolCorrectness" => "Tools",
                "IntentSequence" => "Intent",
                "TaskCompletion" => "Task",
                "Determinism" => "Determ",
                "Reasoning" => "Reason",
                "InstructionAdherence" => "Instr",
                _ => dim
            };
        }

        private static string FormatTokens(long tokens)
        {
            return tokens >= 1000 ? $"{tokens / 1000.0:0.#}k tok" : $"{tokens} tok";
        }

        private static string FormatDuration(double ms)
        {
            double sec = ms / 1000.0;
            if (sec < 60)
            {
                return $"{sec:0}s";
            }

            int m = (int)(sec / 60);
            int s = (int)(sec % 60);
            return $"{m}m{s:00}s";
        }

        private void DrawScoreChip(double score)
        {
            Color prev = GUI.color;
            GUI.color = ScoreColor(score);
            GUILayout.Label("●", GUILayout.Width(14));
            GUI.color = prev;
            GUILayout.Label($"{score:0.#}", EditorStyles.miniBoldLabel, GUILayout.Width(36));
        }

        private void RefreshRuns()
        {
            foreach (Texture2D t in _thumbs.Values)
            {
                if (t != null)
                {
                    UnityEngine.Object.DestroyImmediate(t);
                }
            }

            _thumbs.Clear();

            _runs = GameCreationBenchmarkLauncher.ListRuns();
            if (_expanded.Count == 0 && _runs.Count > 0)
            {
                _expanded.Add(_runs[0].Summary.ModelId); // expand the newest model by default
            }
        }

        private void OnDisable()
        {
            foreach (Texture2D t in _thumbs.Values)
            {
                if (t != null)
                {
                    UnityEngine.Object.DestroyImmediate(t);
                }
            }

            _thumbs.Clear();
        }

        private static string RoleVerdictShort(double rating)
        {
            if (rating >= 8.0)
            {
                return "Strong fit";
            }

            if (rating >= 6.5)
            {
                return "Usable";
            }

            return rating >= 4.0 ? "Limited" : "Not suitable";
        }

        private static Color ScoreColor(double score)
        {
            if (score >= 75)
            {
                return new Color(0.30f, 0.72f, 0.40f); // green
            }

            if (score >= 50)
            {
                return new Color(0.92f, 0.74f, 0.25f); // amber
            }

            return new Color(0.86f, 0.36f, 0.34f); // red
        }

        private static string FormatRunDate(string runId)
        {
            // runId = yyyyMMdd_HHmmss
            if (!string.IsNullOrEmpty(runId) && runId.Length == 15 && runId[8] == '_')
            {
                return $"{runId.Substring(0, 4)}-{runId.Substring(4, 2)}-{runId.Substring(6, 2)} " +
                       $"{runId.Substring(9, 2)}:{runId.Substring(11, 2)}";
            }

            return runId ?? "";
        }

        private void DrawScenariosSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Scenarios (easy → hard)", EditorStyles.boldLabel);
                // Ordered from simplest to hardest; difficulty shown on the right.
                _runG2 = GroupToggle(_runG2, "G2 — runtime mechanic authoring (pure Lua)", 1);
                _runG1 = GroupToggle(_runG1, "G1 — build a game (world + Lua)", 2);
                _runG5 = GroupToggle(_runG5, "G5 — strict instruction-following (subtractive)", 3);
                _runG3 = GroupToggle(_runG3, "G3 — reasoning & design (harder, intelligence)", 4);
                _runG4 = GroupToggle(_runG4, "G4 — playable game (simulated playthrough)", 5);
                _runG6 = GroupToggle(_runG6, "G6 - castle free-build (bonus, visual)", 5);
                if (!_runG1 && !_runG2 && !_runG3 && !_runG4 && !_runG5 && !_runG6)
                {
                    EditorGUILayout.HelpBox("Select at least one group.", MessageType.Warning);
                }
            }
        }

        /// <summary>A group toggle with a right-aligned difficulty indicator (1..5 dots).</summary>
        private static bool GroupToggle(bool value, string label, int difficulty)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool v = EditorGUILayout.ToggleLeft(label, value, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                int d = Mathf.Clamp(difficulty, 1, 5);
                EditorGUILayout.LabelField(new string('●', d) + new string('○', 5 - d),
                    EditorStyles.miniLabel, GUILayout.Width(64));
                return v;
            }
        }

        private void DrawRunOptionsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Run options", EditorStyles.boldLabel);
                _reps = EditorGUILayout.IntSlider(
                    new GUIContent("Repetitions", "Runs per scenario; the report keeps the per-scenario median."),
                    _reps, 1, 5);
                _retries = EditorGUILayout.IntSlider(
                    new GUIContent("Transient retries", "Extra attempts on provider/timeout failures only."),
                    _retries, 0, 3);
                _timeoutSeconds = EditorGUILayout.IntField(
                    new GUIContent("Timeout override (s)", "0 = each scenario's own default (200–300s)."),
                    _timeoutSeconds);
                if (_timeoutSeconds < 0)
                {
                    _timeoutSeconds = 0;
                }
            }
        }

        private void DrawConnectionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _overrideConnection = EditorGUILayout.ToggleLeft(
                    new GUIContent("Override connection",
                        "Off = use your CoreAI Settings asset. On = override; empty fields fall back to it."),
                    _overrideConnection);

                if (!_overrideConnection)
                {
                    string m = GameCreationBenchmarkLauncher.ConfiguredModel;
                    string b = GameCreationBenchmarkLauncher.ConfiguredBaseUrl;
                    EditorGUILayout.HelpBox(
                        string.IsNullOrWhiteSpace(m)
                            ? "Using your CoreAI Settings asset (no HTTP model configured — check CoreAISettings)."
                            : $"Using your CoreAI Settings:  {m}\n{b}",
                        string.IsNullOrWhiteSpace(m) ? MessageType.Warning : MessageType.Info);
                }

                using (new EditorGUI.DisabledScope(!_overrideConnection))
                {
                    EditorGUILayout.LabelField("Empty / Default fields fall back to your CoreAI Settings.",
                        EditorStyles.miniLabel);
                    _model = EditorGUILayout.TextField(
                        new GUIContent("Model", "Leave empty to use the configured model."), _model);
                    _baseUrl = EditorGUILayout.TextField(
                        new GUIContent("Base URL", "Leave empty to use the configured base URL."), _baseUrl);
                    _apiKey = EditorGUILayout.PasswordField(
                        new GUIContent("API key", "Leave empty to use the configured key."), _apiKey);
                    _streamingMode = EditorGUILayout.Popup("Streaming", _streamingMode, ConnModeOptions);
                    _nativeToolsMode = EditorGUILayout.Popup("Native tool-calling", _nativeToolsMode, ConnModeOptions);
                }
            }
        }

        private void DrawRunSection()
        {
            EditorGUILayout.Space(2);
            using (new EditorGUI.DisabledScope(
                       GameCreationBenchmarkLauncher.IsRunning || (!_runG1 && !_runG2 && !_runG3 && !_runG4 && !_runG5 && !_runG6)))
            {
                if (GUILayout.Button(GameCreationBenchmarkLauncher.IsRunning ? "Running…" : "▶  Run Benchmark",
                        GUILayout.Height(36)))
                {
                    Persist();
                    GameCreationBenchmarkLauncher.Configure(
                        model: _overrideConnection
                            ? GameCreationBenchmarkLauncher.OrConfigured(_model,
                                GameCreationBenchmarkLauncher.ConfiguredModel)
                            : null,
                        baseUrl: _overrideConnection
                            ? GameCreationBenchmarkLauncher.OrConfigured(_baseUrl,
                                GameCreationBenchmarkLauncher.ConfiguredBaseUrl)
                            : null,
                        apiKey: _overrideConnection
                            ? GameCreationBenchmarkLauncher.OrConfigured(_apiKey,
                                GameCreationBenchmarkLauncher.ConfiguredApiKey)
                            : null,
                        streaming: _overrideConnection
                            ? GameCreationBenchmarkLauncher.ConnectionMode(_streamingMode)
                            : (bool?)null,
                        nativeTools: _overrideConnection
                            ? GameCreationBenchmarkLauncher.ConnectionMode(_nativeToolsMode)
                            : (bool?)null,
                        groupsCsv: BuildGroups(),
                        repetitions: _reps,
                        retries: _retries,
                        timeoutSeconds: _timeoutSeconds > 0 ? _timeoutSeconds : (int?)null);
                    GameCreationBenchmarkLauncher.RunViaTestRunner(revealOnFinish: true, exitOnFinish: false);
                }
            }
        }

        private void DrawStatusSection()
        {
            EditorGUILayout.Space(2);

            if (CoreAI.Benchmarking.BenchmarkProgress.IsRunning)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    int done = CoreAI.Benchmarking.BenchmarkProgress.Completed;
                    int total = CoreAI.Benchmarking.BenchmarkProgress.Total;
                    EditorGUILayout.LabelField($"Progress — {done}/{total}", EditorStyles.boldLabel);

                    Rect bar = EditorGUILayout.GetControlRect(false, 18);
                    EditorGUI.ProgressBar(bar, CoreAI.Benchmarking.BenchmarkProgress.Fraction,
                        CoreAI.Benchmarking.BenchmarkProgress.CurrentLabel);

                    foreach (CoreAI.Benchmarking.ProgressLine line in
                             CoreAI.Benchmarking.BenchmarkProgress.RecentLines(10))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(line.Left, EditorStyles.miniLabel);
                            GUILayout.FlexibleSpace();
                            if (!string.IsNullOrEmpty(line.Right))
                            {
                                EditorGUILayout.LabelField(line.Right, EditorStyles.miniLabel, GUILayout.Width(64));
                            }
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(GameCreationBenchmarkLauncher.LastStatus, MessageType.Info);
            }
        }

        private string BuildGroups() =>
            GameCreationBenchmarkLauncher.GroupsCsv(_runG1, _runG2, _runG3, _runG4, _runG5, _runG6);
    }
}
