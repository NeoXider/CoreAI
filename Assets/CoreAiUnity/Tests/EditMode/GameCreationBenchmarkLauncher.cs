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
        public const string EnvSuiteBudget = "COREAI_BENCHMARK_SUITE_BUDGET";

        /// <summary>Existing run id, or <c>latest</c>, to continue from its durable state or reports.</summary>
        public const string EnvResume = "COREAI_BENCHMARK_RESUME";

        /// <summary>Comma-separated target labels to reopen during resume.</summary>
        public const string EnvOnlyTargets = "COREAI_BENCHMARK_ONLY_TARGETS";

        /// <summary>Target list of a parallel multi-model run; see <see cref="BenchmarkTargetList"/> for the formats.</summary>
        public const string EnvTargets = BenchmarkTargetList.EnvTargets;

        /// <summary>How many targets run at once; empty or 0 means all.</summary>
        public const string EnvParallel = BenchmarkTargetList.EnvParallel;

        /// <summary>Suite-wide <c>reasoning_effort</c> for the single model and for targets without their own.</summary>
        public const string EnvReasoningEffort = BenchmarkTargetList.EnvReasoningEffort;

        public const string ArgModel = "-coreAiBenchmarkModel";
        public const string ArgGroups = "-coreAiBenchmarkGroups";
        public const string ArgReps = "-coreAiBenchmarkReps";
        public const string ArgTargets = "-coreAiBenchmarkTargets";
        public const string ArgParallel = "-coreAiBenchmarkParallel";
        public const string ArgReasoningEffort = "-coreAiBenchmarkReasoningEffort";
        public const string ArgResume = "-coreAiBenchmarkResume";
        public const string ArgOnlyTargets = "-coreAiBenchmarkOnlyTargets";

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

        /// <summary>Saved target rows (JSON); literal API keys are never stored, <c>env:</c> references are.</summary>
        public const string PrefTargets = PrefPrefix + "targets";

        /// <summary>Saved parallel cap; 0 runs every target at once.</summary>
        public const string PrefParallel = PrefPrefix + "parallel";

        /// <summary>Saved suite-wide reasoning effort; empty is the provider default.</summary>
        public const string PrefReasoningEffort = PrefPrefix + "reasoningEffort";

        // Must stay ABOVE the suite's own soft budget (COREAI_BENCHMARK_SUITE_BUDGET) plus
        // report/screenshot time: this launcher-side watchdog is only a last-resort guard against a
        // hung Test Runner, and when it fires the run dies WITHOUT a report.
        // Required ordering across the whole timeout chain:
        //   soft suite budget < NUnit [Timeout] (6 h) < launcher watchdog (6 h 10 min).
        // The first inequality is enforced by the clamp in
        // GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds (budget + report margin
        // <= NUnit timeout; the rep start-gate reserves maxAttempts x timeout inside the budget, so
        // scenario work always ends by the budget itself); this constant only has to stay above
        // the NUnit [Timeout]. 1900s here killed a full Opus 4.8 cloud run mid-suite (slow ~5-7 min
        // turns are normal for large CLI-bridged models, not a hang).
        internal const double TimeoutSeconds = 22_200d;
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
            ConfigureResume(null);
            RunViaTestRunner(true, false);
        }

        /// <summary>Chooses the existing run to continue; null starts a new run.</summary>
        public static void ConfigureResume(string runId, string onlyTargetsCsv = null)
        {
            SetOrClear(EnvResume, runId);
            SetOrClear(EnvOnlyTargets, onlyTargetsCsv);
        }

        /// <summary>Continues an interrupted run using its original manifest settings.</summary>
        public static void ResumeInterruptedRun(string runId)
        {
            if (IsRunning) return;
            try
            {
                PrepareResumeEnvironment(ResultsRoot(), runId);
                RunViaTestRunner(true, false);
            }
            catch (Exception error)
            {
                RestoreResumeEnvironment();
                LastStatus = error.Message;
                Debug.LogWarning("[Benchmark] Cannot resume: " + error.Message);
            }
        }

        private const string ResumeEnvironmentSnapshotKey = PrefPrefix + "resume.environment";

        [InitializeOnLoadMethod]
        private static void RegisterResumeEnvironmentCleanup()
        {
            EditorApplication.playModeStateChanged -= HandleResumePlayModeState;
            EditorApplication.playModeStateChanged += HandleResumePlayModeState;
        }

        /// <summary>Recovers the snapshot after a Play Mode domain reload even if the old callback object was lost.</summary>
        internal static void HandleResumePlayModeState(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode) RestoreResumeEnvironment();
        }

        private static bool IsRunEnvironment(string key) => key.StartsWith("COREAI_BENCHMARK_", StringComparison.Ordinal) ||
            key.StartsWith("COREAI_TEST_", StringComparison.Ordinal);

        /// <summary>Restores the editor's environment after the runner, including across Play Mode domain reloads.</summary>
        internal static void RestoreResumeEnvironment()
        {
            string saved = SessionState.GetString(ResumeEnvironmentSnapshotKey, "");
            if (saved.Length == 0) return;
            Dictionary<string, string> snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(saved);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (IsRunEnvironment((string)entry.Key)) Environment.SetEnvironmentVariable((string)entry.Key, null);
            foreach (KeyValuePair<string, string> entry in snapshot) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            SessionState.EraseString(ResumeEnvironmentSnapshotKey);
        }

        /// <summary>Uses manifest configuration and existing secret sources, never a stale target specification.</summary>
        internal static void PrepareResumeEnvironment(string root, string runId)
        {
            if (SessionState.GetString(ResumeEnvironmentSnapshotKey, "").Length > 0)
                throw new InvalidOperationException("A benchmark resume environment is already active.");
            Dictionary<string, string> snapshot = new(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (IsRunEnvironment((string)entry.Key)) snapshot[(string)entry.Key] = (string)entry.Value;
            SessionState.SetString(ResumeEnvironmentSnapshotKey, Newtonsoft.Json.JsonConvert.SerializeObject(snapshot));
            try
            {
                BenchmarkRunStore store = new(root, runId);
                BenchmarkRunManifest manifest = store.LoadManifest();
                List<BenchmarkTarget> secretSources = new();
                string originalSpec = Environment.GetEnvironmentVariable(EnvTargets);
                if (BenchmarkTargetList.TryParse(originalSpec, out List<BenchmarkTarget> processTargets, out _))
                    secretSources.AddRange(processTargets);
                if (manifest.TargetsFileReference.Length > 0 &&
                    BenchmarkTargetList.TryParse(manifest.TargetsFileReference, out List<BenchmarkTarget> fileTargets, out _))
                    secretSources.AddRange(fileTargets);
                List<BenchmarkTargetRow> sessionRows = LoadSessionTargetRows();
                if (sessionRows != null)
                    foreach (BenchmarkTargetRow row in sessionRows)
                        if (BenchmarkTargetList.TryParse("[" + Newtonsoft.Json.JsonConvert.SerializeObject(row) + "]", out List<BenchmarkTarget> rows, out _))
                            secretSources.AddRange(rows);
                List<BenchmarkTarget> resolved = new();
                foreach (BenchmarkStoredTarget target in manifest.Targets)
                {
                    List<BenchmarkTarget> matching = secretSources.Where(t => t.Model == target.Model && t.Label == target.Label &&
                        (target.ConfigurationDeferred || t.BaseUrl == target.BaseUrl)).ToList();
                    BenchmarkTarget source = matching.LastOrDefault(t => !string.IsNullOrWhiteSpace(t.ApiKey)) ?? matching.LastOrDefault();
                    bool complete = !store.NeedsWork(manifest, target);
                    if (target.ConfigurationDeferred)
                    {
                        if (source == null && !complete) throw new InvalidOperationException($"Target '{target.LabelOrModel}' needs its original legacy target configuration.");
                        resolved.Add(source ?? new BenchmarkTarget(target.Model, target.BaseUrl, target.KeyReference, target.Label));
                        continue;
                    }
                    string key = target.KeyReference;
                    if (target.RequiresExternalKey)
                    {
                        key = source?.ApiKey ?? "";
                        if (!complete && string.IsNullOrWhiteSpace(key) && !target.UsesDefaultSecretSource && !manifest.UseDefaultSecretSource)
                            throw new InvalidOperationException($"Target '{target.LabelOrModel}' needs a literal API key that is unavailable. Restore its original targets file or enter its key in the benchmark window before resuming.");
                    }
                    resolved.Add(new BenchmarkTarget(target.Model, target.BaseUrl, key, target.Label,
                        target.ReasoningEffort, target.ExtraBodyJson, target.Temperature));
                }
                SetOrClear(EnvTargets, BenchmarkTargetList.ToJson(resolved, true));
                foreach (KeyValuePair<string, string> option in manifest.Options)
                    if (option.Key != EnvRetries) SetOrClear(option.Key, option.Value);
                ConfigureResume(runId);
            }
            catch
            {
                RestoreResumeEnvironment();
                throw;
            }
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

            // WHY: this editor session's rows still hold the keys typed in the window; after a restart only the
            // saved rows remain, which carry env: references but no literal keys. An invalid list is not applied.
            List<BenchmarkTargetRow> rows = LoadSessionTargetRows() ?? LoadSavedTargetRows();
            if (TryBuildTargetSpec(rows, out string spec, out string error))
            {
                ConfigureTargets(spec, EditorPrefs.GetInt(PrefParallel, 0));
            }
            else
            {
                Debug.LogWarning($"[Benchmark] Saved target list ignored: {error}");
                ConfigureTargets(null, null);
            }

            string savedEffort = EditorPrefs.GetString(PrefReasoningEffort, "");
            if (BenchmarkTargetList.TryNormalizeReasoningEffort(savedEffort, out string effort))
            {
                ConfigureReasoningEffort(effort);
            }
            else
            {
                Debug.LogWarning($"[Benchmark] Saved {BenchmarkTargetList.ReasoningEffortError(savedEffort)}; " +
                                 "using the provider default.");
                ConfigureReasoningEffort(null);
            }
        }

        /// <summary>
        /// Sets the suite-wide reasoning effort for the next launch, or clears it (provider default) so a value
        /// from an earlier run never leaks into the next one.
        /// </summary>
        public static void ConfigureReasoningEffort(string effort)
        {
            SetOrClear(EnvReasoningEffort, effort);
        }

        /// <summary>One editable row of the window's target list.</summary>
        [Serializable]
        public sealed class BenchmarkTargetRow
        {
            public bool enabled = true;
            public string label = "";
            public string model = "";
            public string baseUrl = "";

            /// <summary>Literal key (kept for the editor session only) or an <c>env:VAR</c> reference.</summary>
            public string apiKey = "";

            /// <summary>This target's reasoning effort; empty uses the suite default.</summary>
            public string reasoningEffort = "";

            /// <summary>Optional temperature from 0 to 2; empty uses the provider/model default.</summary>
            public string temperature = "";

            /// <summary>Provider body fields for this target: one JSON object, or empty.</summary>
            public string extraBody = "";
        }

        [Serializable]
        private sealed class SavedTargetRows
        {
            public List<BenchmarkTargetRow> rows = new();
        }

        /// <summary>The window's saved target rows; a literal API key is never part of them.</summary>
        public static List<BenchmarkTargetRow> LoadSavedTargetRows()
        {
            string json = EditorPrefs.GetString(PrefTargets, "");
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<BenchmarkTargetRow>();
            }

            try
            {
                return JsonUtility.FromJson<SavedTargetRows>(json)?.rows ?? new List<BenchmarkTargetRow>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] Saved target rows are unreadable: {ex.Message}");
                return new List<BenchmarkTargetRow>();
            }
        }

        /// <summary>SessionState key of the rows including literal keys: kept across domain reloads, never written to disk.</summary>
        public const string SessionTargetsKey = PrefPrefix + "targets.session";

        /// <summary>This editor session's rows (literal keys included), or null when the window stored none.</summary>
        public static List<BenchmarkTargetRow> LoadSessionTargetRows()
        {
            string json = SessionState.GetString(SessionTargetsKey, "");
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<SavedTargetRows>(json)?.rows;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Stores <paramref name="rows"/> with their keys for this editor session only.</summary>
        public static void SaveSessionTargetRows(IEnumerable<BenchmarkTargetRow> rows)
        {
            SavedTargetRows session = new();
            session.rows.AddRange(rows ?? Enumerable.Empty<BenchmarkTargetRow>());
            SessionState.SetString(SessionTargetsKey, JsonUtility.ToJson(session));
        }

        /// <summary>Saves <paramref name="rows"/>, dropping literal API keys (env: references are kept).</summary>
        public static void SaveTargetRows(IEnumerable<BenchmarkTargetRow> rows)
        {
            SavedTargetRows saved = new();
            foreach (BenchmarkTargetRow row in rows ?? Enumerable.Empty<BenchmarkTargetRow>())
            {
                string key = (row.apiKey ?? "").Trim();
                saved.rows.Add(new BenchmarkTargetRow
                {
                    enabled = row.enabled,
                    label = row.label ?? "",
                    model = row.model ?? "",
                    baseUrl = row.baseUrl ?? "",
                    apiKey = key.StartsWith(BenchmarkTarget.ApiKeyEnvPrefix, StringComparison.OrdinalIgnoreCase)
                        ? key
                        : "",
                    reasoningEffort = row.reasoningEffort ?? "",
                    temperature = row.temperature ?? "",
                    extraBody = row.extraBody ?? ""
                });
            }

            EditorPrefs.SetString(PrefTargets, JsonUtility.ToJson(saved));
        }

        /// <summary>
        /// Builds the <see cref="EnvTargets"/> value for the enabled rows (literal keys included, for this process
        /// only). An empty spec means a single-model run. Fails with every row problem when a row is invalid.
        /// </summary>
        public static bool TryBuildTargetSpec(IEnumerable<BenchmarkTargetRow> rows, out string spec,
            out string error)
        {
            spec = null;
            List<BenchmarkTarget> targets = new();
            List<string> problems = new();
            int number = 0;
            foreach (BenchmarkTargetRow row in rows ?? Enumerable.Empty<BenchmarkTargetRow>())
            {
                number++;
                if (!row.enabled)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.model))
                {
                    problems.Add($"row {number} has no model id");
                    continue;
                }

                if (!BenchmarkTargetList.IsHttpUrl(row.baseUrl))
                {
                    problems.Add($"row {number} ({row.model.Trim()}) needs an absolute http(s) base URL");
                    continue;
                }

                if (!BenchmarkTargetList.TryNormalizeReasoningEffort(row.reasoningEffort, out string effort))
                {
                    problems.Add($"row {number} ({row.model.Trim()}): " +
                                 BenchmarkTargetList.ReasoningEffortError(row.reasoningEffort));
                    continue;
                }

                if (!TryValidateExtraBody(row.extraBody, out string body, out string bodyError))
                {
                    problems.Add($"row {number} ({row.model.Trim()}): {bodyError}");
                    continue;
                }

                if (!BenchmarkTargetList.TryParseTemperature(row.temperature, out float? temperature))
                {
                    problems.Add($"row {number} ({row.model.Trim()}): temperature must be a number from 0 to 2");
                    continue;
                }

                targets.Add(new BenchmarkTarget(row.model, row.baseUrl, row.apiKey, row.label, effort, body, temperature));
            }

            if (problems.Count > 0)
            {
                error = string.Join("; ", problems);
                return false;
            }

            if (targets.Count > 0)
            {
                // WHY: round-trips through the parser so duplicate names get the same #n labels the suite uses.
                spec = BenchmarkTargetList.ToJson(
                    BenchmarkTargetList.Parse(BenchmarkTargetList.ToJson(targets, true)), true);
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Checks a target's provider body the way the suite will: one JSON object without a CoreAI-owned
        /// field (<c>messages</c>, <c>model</c>, <c>tools</c>, …). <paramref name="normalized"/> is its compact,
        /// key-sorted text, empty for an empty body. The suite additionally rejects a personal <c>session_id</c>.
        /// </summary>
        public static bool TryValidateExtraBody(string json, out string normalized, out string error)
        {
            normalized = "";
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return true;
            }

            try
            {
                normalized = Infrastructure.Llm.OpenAiProviderBodyParameters.NormalizeRawJson(json);
                foreach (JProperty property in BenchmarkJson.ParseObject(normalized).Properties())
                {
                    if (Infrastructure.Llm.OpenAiProviderBodyParameters.IsReserved(property.Name))
                    {
                        normalized = "";
                        error = $"extra body field '{property.Name}' is controlled by CoreAI";
                        return false;
                    }
                }

                return true;
            }
            catch (ArgumentException ex)
            {
                normalized = "";
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Sets the multi-target env vars for the next launch, or clears them for a single-model run so a stale
        /// list never leaks into it. <paramref name="parallel"/> 0/null runs every target at once.
        /// </summary>
        public static void ConfigureTargets(string targetSpec, int? parallel)
        {
            SetOrClear(EnvTargets, targetSpec);
            SetOrClear(EnvParallel, string.IsNullOrWhiteSpace(targetSpec) || parallel is null or <= 0
                ? null
                : parallel.Value.ToString());
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

            // WHY: Effective legacy values must use the same per-group defaults as the consumers
            // (G1-G5 default on, G6/G7 default off) - only when ALL seven were effectively on did the
            // pre-toggle launcher emit an empty CSV that implicitly included G8. A fresh config
            // effectively ran G1-G5 (non-empty CSV, no G8), so it must not gain G8 on migration.
            bool allEnabled =
                EditorPrefs.GetBool(PrefG1, true) &&
                EditorPrefs.GetBool(PrefG2, true) &&
                EditorPrefs.GetBool(PrefG3, true) &&
                EditorPrefs.GetBool(PrefG4, true) &&
                EditorPrefs.GetBool(PrefG5, true) &&
                EditorPrefs.GetBool(PrefG6, false) &&
                EditorPrefs.GetBool(PrefG7, false);

            return allEnabled;
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
            File.WriteAllText(Path.Combine(root, "COMPARISON_groups.svg"),
                ModelComparison.ToGroupHeatmapSvg(models, pinnedModelId));
            File.WriteAllText(Path.Combine(root, "COMPARISON_extreme.svg"),
                ModelComparison.ToExtremeRankingSvg(models, pinnedModelId));
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

                foreach (string file in RunCompanionFiles(dir, stem))
                {
                    File.Delete(file);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] could not delete run: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Files of the run whose report stem is <paramref name="stem"/>: <c>&lt;stem&gt;.*</c> (.json/.md/.svg/
        /// .tools.jsonl) and <c>&lt;stem&gt;_*</c> (model card, hero, per-scenario PNGs, replay script). Files of
        /// another run whose stem starts with this one are excluded: the targets of a multi-target run share
        /// the run id, so <c>BENCHMARK_&lt;id&gt;_qwen</c> and <c>BENCHMARK_&lt;id&gt;_qwen__q8</c> sit side by side.
        /// </summary>
        public static List<string> RunCompanionFiles(string dir, string stem)
        {
            List<string> files = new();
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
            {
                return files;
            }

            List<string> longerStems = Directory.GetFiles(dir, "BENCHMARK_*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(other => other.Length > stem.Length && other.StartsWith(stem, StringComparison.Ordinal))
                .ToList();
            foreach (string pattern in new[] { stem + ".*", stem + "_*" })
            {
                foreach (string file in Directory.GetFiles(dir, pattern))
                {
                    string name = Path.GetFileName(file);
                    bool otherRun = longerStems.Any(other =>
                        name.StartsWith(other + ".", StringComparison.Ordinal) ||
                        name.StartsWith(other + "_", StringComparison.Ordinal));
                    if (!otherRun && !files.Contains(file))
                    {
                        files.Add(file);
                    }
                }
            }

            return files;
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
                // WHY: one parser for the window, editor scripts and the core comparison, so the group and
                // extreme-tier fields reach the heatmap; it also reads pre-2.0 reports.
                return ModelComparison.ParseSummaryJson(File.ReadAllText(jsonPath));
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
        /// <summary>
        /// Batchmode entry point: <c>-executeMethod ...RunHeadless</c>.
        /// <para>
        /// WHY: the suite test is <c>[Explicit]</c>, and Unity's <c>-runTests</c> CLI never selects an
        /// explicit test — a name or category filter returns "No tests were executed". The Test Runner
        /// API does select it, so a headless run goes through the same path the menu uses. Saved editor
        /// prefs are deliberately NOT applied here: a headless run is configured by the
        /// <c>COREAI_TEST_*</c> / <c>COREAI_BENCHMARK_*</c> environment variables alone.
        /// </para>
        /// </summary>
        public static void RunHeadless()
        {
            RunViaTestRunner(false, true);
        }

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
            RestoreResumeEnvironment();
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
        /// <c>-coreAiBenchmarkModel</c> / <c>-coreAiBenchmarkGroups</c> / <c>-coreAiBenchmarkReps</c> /
        /// <c>-coreAiBenchmarkReasoningEffort</c> and, for a parallel multi-model run, <c>-coreAiBenchmarkTargets</c> /
        /// <c>-coreAiBenchmarkParallel</c>, <c>-coreAiBenchmarkResume</c>, and
        /// <c>-coreAiBenchmarkOnlyTargets</c> from the command line (or the COREAI_* env vars) and exits with a status
        /// code. An argument that is absent leaves its env var untouched; an invalid target list or effort exits
        /// with code 2 before Unity runs anything.
        /// </summary>
        public static void RunFromCli()
        {
            if (!TryApplyCliArguments(Environment.GetCommandLineArgs(), out string error))
            {
                Debug.LogError($"[Benchmark] {error}");
                LastStatus = error;
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(2);
                }

                return;
            }

            RunViaTestRunner(false, true);
        }

        /// <summary>
        /// Applies the batchmode arguments as env vars. Targets come from <see cref="ArgTargets"/> or, when it is
        /// absent, from an already set <see cref="EnvTargets"/>; either way the list is validated here.
        /// </summary>
        internal static bool TryApplyCliArguments(string[] args, out string error)
        {
            string model = ReadArg(args, ArgModel);
            string groups = ReadArg(args, ArgGroups);
            string repsRaw = ReadArg(args, ArgReps);
            int? reps = int.TryParse(repsRaw, out int r) ? r : (int?)null;
            string targetsArg = ReadArg(args, ArgTargets);
            string parallelArg = ReadArg(args, ArgParallel);
            string resumeArg = ReadArg(args, ArgResume);
            string onlyTargetsArg = ReadArg(args, ArgOnlyTargets);
            string resume = HasArg(args, ArgResume)
                ? resumeArg.Trim()
                : Environment.GetEnvironmentVariable(EnvResume)?.Trim();
            string onlyTargets = HasArg(args, ArgOnlyTargets)
                ? onlyTargetsArg.Trim()
                : Environment.GetEnvironmentVariable(EnvOnlyTargets)?.Trim();

            if ((HasArg(args, ArgResume) && string.IsNullOrWhiteSpace(resume)) ||
                (!string.IsNullOrEmpty(resume) &&
                 resume.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                                 !(c >= '0' && c <= '9') && c != '_' && c != '-')))
            {
                error = $"{ArgResume}/{EnvResume} must be a run id or 'latest'.";
                return false;
            }

            if ((HasArg(args, ArgOnlyTargets) && string.IsNullOrWhiteSpace(onlyTargets)) ||
                (!string.IsNullOrEmpty(onlyTargets) && onlyTargets.Split(',').Any(string.IsNullOrWhiteSpace)))
            {
                error = $"{ArgOnlyTargets}/{EnvOnlyTargets} must contain comma-separated target labels.";
                return false;
            }

            if (HasArg(args, ArgReps) && (!reps.HasValue || reps.Value < 1))
            {
                error = $"{ArgReps} must be a positive whole number, got '{repsRaw}'.";
                return false;
            }

            string targetSpec = string.IsNullOrWhiteSpace(targetsArg)
                ? Environment.GetEnvironmentVariable(EnvTargets)
                : targetsArg;
            if (!BenchmarkTargetList.TryParse(targetSpec, out List<BenchmarkTarget> targets, out error))
            {
                return false;
            }

            foreach (BenchmarkTarget target in targets)
            {
                if (!string.IsNullOrWhiteSpace(onlyTargets) && !onlyTargets.Split(',').Any(label => label.Trim() == target.DisplayName)) continue;
                if (!TryValidateExtraBody(target.ExtraBodyJson, out _, out string bodyError))
                {
                    error = $"Target '{target.DisplayName}': {bodyError}";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(parallelArg) &&
                (!int.TryParse(parallelArg, out int parallel) || parallel < 0))
            {
                error = $"{ArgParallel} must be a whole number (0 = all targets), got '{parallelArg}'.";
                return false;
            }

            string effortArg = ReadArg(args, ArgReasoningEffort);
            string effortSource = string.IsNullOrWhiteSpace(effortArg)
                ? Environment.GetEnvironmentVariable(EnvReasoningEffort)
                : effortArg;
            if (!BenchmarkTargetList.TryNormalizeReasoningEffort(effortSource, out string effort))
            {
                error = $"{(string.IsNullOrWhiteSpace(effortArg) ? EnvReasoningEffort : ArgReasoningEffort)}: " +
                        BenchmarkTargetList.ReasoningEffortError(effortSource);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(effortArg))
            {
                ConfigureReasoningEffort(effort);
            }

            if (HasArg(args, ArgModel))
            {
                SetOrClear(EnvModel, model);
            }

            if (HasArg(args, ArgGroups))
            {
                SetOrClear(EnvGroups, groups);
            }

            if (HasArg(args, ArgReps))
            {
                SetOrClear(EnvRepetitions, repsRaw.Trim());
            }

            if (HasArg(args, ArgResume))
            {
                SetOrClear(EnvResume, string.Equals(resume, "latest", StringComparison.OrdinalIgnoreCase) ? "latest" : resume);
            }

            if (HasArg(args, ArgOnlyTargets))
            {
                SetOrClear(EnvOnlyTargets, string.Join(",", onlyTargets.Split(',').Select(label => label.Trim())));
            }

            if (!string.IsNullOrWhiteSpace(targetsArg))
            {
                SetOrClear(EnvTargets, targetsArg);
            }

            if (!string.IsNullOrWhiteSpace(parallelArg))
            {
                SetOrClear(EnvParallel, parallelArg.Trim());
            }

            if (targets.Count > 0)
            {
                Debug.Log($"[Benchmark] Multi-target run: {string.Join(", ", targets.Select(t => t.DisplayName))}" +
                          $" (parallel {BenchmarkTargetList.ResolveParallelism(Environment.GetEnvironmentVariable(EnvParallel), targets.Count)}).");
            }

            error = null;
            return true;
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
                    string value = args[i + 1] ?? "";
                    return value.StartsWith("-", StringComparison.Ordinal) ? "" : value;
                }
            }

            return "";
        }

        private static bool HasArg(string[] args, string name)
        {
            return Array.Exists(args, arg => string.Equals(arg, name, StringComparison.Ordinal));
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
