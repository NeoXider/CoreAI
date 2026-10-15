#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// The per-target scenario loop and the multi-target driver. Several targets (model + OpenAI-compatible
    /// endpoint) run their full loops concurrently inside the one <c>[UnityTest]</c> coroutine through
    /// <see cref="CooperativeScheduler"/>; each owns its client, settings asset, report, progress line and
    /// artifacts. Scenarios that put objects into the Unity scene (<see cref="GameBenchmarkScenario.UsesSharedScene"/>)
    /// and the model-card capture take turns on one <see cref="BenchmarkVisualLane"/>.
    /// </summary>
    public sealed partial class GameCreationBenchmarkPlayModeTests
    {
        /// <summary>
        /// One target's scenario loop: its client, settings, report and progress handle, plus the suite clock,
        /// budget and visual lane every target of the run shares.
        /// </summary>
        internal sealed class TargetSuiteRun
        {
            /// <summary>Name in the report, progress lines and model card (label, else model id).</summary>
            public string DisplayName = "";

            /// <summary>The model id the provider serves; the token estimate uses it.</summary>
            public string TokenizerModelId = "";

            /// <summary>Artifact file stem; null derives it from the report metadata.</summary>
            public string ArtifactStem;

            /// <summary>Artifact directory; null is the project's TestResults/CoreAI/Benchmarks.</summary>
            public string ArtifactDirectory;

            public ILlmClient Client;
            public ICoreAISettings Settings;
            public ITokenCounter TokenCounter;
            public BenchmarkReport Report;
            public BenchmarkRunProgress Progress;
            public IReadOnlyList<GameBenchmarkScenario> Scenarios = Array.Empty<GameBenchmarkScenario>();
            public int Repetitions = 1;
            public double SuiteBudgetSeconds;
            public Stopwatch SuiteClock;
            public BenchmarkVisualLane VisualLane;

            /// <summary>
            /// Takes the visual lane as soon as it is free instead of at the scenario's place in the suite order,
            /// so parallel targets spread their shared-scene builds over the whole run.
            /// </summary>
            public bool SharedSceneFirst;

            public string LogPrefix = "[Benchmark]";
            public ScenarioRunContext ScenarioContext;

            /// <summary>True when the budget gate or a stop ended the loop early.</summary>
            public bool BudgetHit;

            /// <summary>Path of the written Markdown report.</summary>
            public string ArtifactPath = "";
        }

        private readonly struct PlannedRep
        {
            public PlannedRep(GameBenchmarkScenario scenario, int rep, int reps)
            {
                Scenario = scenario;
                Rep = rep;
                Reps = reps;
            }

            public GameBenchmarkScenario Scenario { get; }
            public int Rep { get; }
            public int Reps { get; }
        }

        /// <summary>Settings asset every benchmark target runs with (roundtrip cap, untruncated tool history).</summary>
        internal static CoreAISettingsAsset CreateBenchmarkSettings()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // WHY: a free-build scene (the castle) spawns 24+ objects, more tool-call roundtrips than the default.
            settings.SetMaxToolCallRoundtrips(ResolveBenchmarkRoundtrips());
            // WHY: with the default history cap of 20 a 30+ spawn build forgets the first ~15 objects it placed
            // and re-spawns duplicates; the model must see everything it already built.
            settings.SetMaxToolCallHistoryMessages(0);
            return settings;
        }

        /// <param name="reasoningEffort">The <c>reasoning_effort</c> the client sends; empty for the provider default.</param>
        internal static BenchmarkRunMetadata CreateRunMetadata(string runId, string modelId, string backend,
            bool nativeToolCalling, bool streaming, CoreAISettingsAsset settings, int repetitions,
            string reasoningEffort = "")
        {
            return new BenchmarkRunMetadata
            {
                RunId = runId,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                ModelId = modelId,
                Backend = backend,
                NativeToolCalling = nativeToolCalling,
                Streaming = streaming,
                MaxParallelToolCalls = settings.MaxParallelToolCalls,
                Temperature = 0.1f,
                ReasoningEffort = reasoningEffort ?? "",
                Repetitions = repetitions,
                UnityVersion = Application.unityVersion,
                SuiteVersion = SuiteVersion
            };
        }

        /// <summary>
        /// Planned executions. A per-scenario RepsOverride (e.g. G6/G7 always run once) means the total is not
        /// simply scenarios x repetitions, or the progress bar would overshoot 100%.
        /// </summary>
        internal static int PlannedRunCount(IReadOnlyList<GameBenchmarkScenario> scenarios, int repetitions)
        {
            int total = 0;
            foreach (GameBenchmarkScenario scenario in scenarios)
            {
                total += scenario.RepsOverride ?? repetitions;
            }

            return total;
        }

        /// <summary>
        /// Runs one target's scenarios x reps with the retry loop, stop flag and budget start-gate, adding each
        /// result to the target's report. Shared-scene reps run only while this target holds the visual lane; a
        /// target that cannot take it continues with its other reps and comes back later.
        /// </summary>
        internal static IEnumerator RunTargetSuite(TargetSuiteRun run)
        {
            List<PlannedRep> pending = new();
            foreach (GameBenchmarkScenario scenario in run.Scenarios)
            {
                // WHY: heavy one-off scenarios (G6 hero, G7) keep their RepsOverride even when the suite averages
                // every other scenario over several repetitions.
                int scenarioReps = scenario.RepsOverride ?? run.Repetitions;
                for (int rep = 1; rep <= scenarioReps; rep++)
                {
                    pending.Add(new PlannedRep(scenario, rep, scenarioReps));
                }
            }

            bool holdsLane = false;
            bool waitingForLane = false;
            try
            {
                while (pending.Count > 0)
                {
                    // WHY: stopping between reps still writes the report for everything finished, where leaving
                    // Play mode mid-scenario would lose the whole run.
                    if (run.Progress.StopRequested)
                    {
                        run.BudgetHit = true;
                        Debug.LogWarning(
                            $"{run.LogPrefix} Stop requested after {run.Report.Results.Count} scenario result(s); " +
                            "stopping early and writing the report for everything finished so far.");
                        break;
                    }

                    int index = PickNextRep(run, pending, out holdsLane);
                    if (index < 0)
                    {
                        PlannedRep blocked = pending.Find(p => p.Scenario.UsesSharedScene);
                        if (!FitsBudget(run, blocked.Scenario))
                        {
                            run.BudgetHit = true;
                            LogBudgetStop(run, blocked.Scenario);
                            break;
                        }

                        if (!waitingForLane)
                        {
                            waitingForLane = true;
                            run.Progress.SetStatus($"{blocked.Scenario.Group} · waiting for the visual lane");
                        }

                        yield return null;
                        continue;
                    }

                    waitingForLane = false;
                    PlannedRep planned = pending[index];
                    pending.RemoveAt(index);
                    GameBenchmarkScenario current = planned.Scenario;
                    float timeout = ResolveTimeoutSeconds(current);
                    int maxAttempts = ResolveMaxAttempts(current);

                    // WHY: the gate reserves every retry attempt's full timeout, not one, or a provider that hangs
                    // on each attempt runs past the NUnit hard abort, which writes no artifacts.
                    if (!FitsBudget(run, current))
                    {
                        run.BudgetHit = true;
                        LogBudgetStop(run, current);
                        break;
                    }

                    run.Progress.StartScenario(
                        $"{current.Group} · {current.Name}  {Stars(BenchmarkInfo.DifficultyFor(current.Group))}" +
                        (planned.Reps > 1 ? $" (run {planned.Rep}/{planned.Reps})" : ""),
                        timeout);
                    ScenarioResult captured = null;
                    bool droppedForStop = false;
                    // WHY: only a hard failure that produced no measurement (provider crash, timeout, dropped
                    // connection) is retried; a low score is the measurement and a harness bug must surface.
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        ScenarioResult attemptResult = null;
                        yield return RunScenario(current, run.Client, run.Settings, run.TokenCounter,
                            run.DisplayName, timeout, r => attemptResult = r, run.ScenarioContext);
                        captured = attemptResult;

                        bool hardFailure = captured != null
                                           && !string.IsNullOrEmpty(captured.Failure)
                                           && captured.Attribution != FailureAttribution.Framework;
                        if (!hardFailure)
                        {
                            break;
                        }

                        // WHY: the stop flag is otherwise only read at the top of the reps loop, so with retries a
                        // pressed Stop against a dead provider would still wait out maxAttempts full timeouts;
                        // exclude the stop-aborted scenario from model scoring instead.
                        if (AbortRetryForStop(run.Progress.StopRequested, ref captured))
                        {
                            run.BudgetHit = true;
                            droppedForStop = true;
                            // TODO: Preserve the dropped attempt's token and cost totals without adding it to scored report results.
                            Debug.LogWarning($"{run.LogPrefix} {current.Name}: stop requested — skipping " +
                                             $"remaining retry attempt(s) after attempt {attempt}/{maxAttempts}.");
                            break;
                        }

                        if (run.SuiteClock.Elapsed.TotalSeconds >= run.SuiteBudgetSeconds)
                        {
                            run.BudgetHit = true;
                            break;
                        }

                        if (attempt >= maxAttempts)
                        {
                            break;
                        }

                        Debug.LogWarning($"{run.LogPrefix} {current.Name}: run failed " +
                                         $"({captured.Failure}); retry {attempt}/{maxAttempts - 1}.");
                    }

                    if (holdsLane)
                    {
                        holdsLane = false;
                        run.VisualLane.Exit(run);
                    }

                    if (!ShouldCountCompletedRepetition(droppedForStop))
                    {
                        continue;
                    }

                    if (captured != null)
                    {
                        captured.Repetition = planned.Rep;
                        run.Report.Add(captured);
                        run.Progress.CompleteScenario(ProgressLine(captured),
                            Stars(BenchmarkInfo.DifficultyFor(current.Group)));
                    }
                    else
                    {
                        run.Progress.CompleteScenario($"⚠ {current.Name} — no result",
                            Stars(BenchmarkInfo.DifficultyFor(current.Group)));
                    }
                }
            }
            finally
            {
                // WHY: a stopped, budget-ended or faulted run must not keep the lane or its place in line.
                run.VisualLane?.Leave(run);
            }
        }

        /// <summary>
        /// The next rep to run, or -1 while only shared-scene reps remain and another target holds the visual lane.
        /// Without contention this is always the next rep in suite order, so a single-model run keeps its order.
        /// </summary>
        private static int PickNextRep(TargetSuiteRun run, List<PlannedRep> pending, out bool holdsLane)
        {
            holdsLane = false;
            if (run.VisualLane == null)
            {
                return 0;
            }

            int firstShared = pending.FindIndex(p => p.Scenario.UsesSharedScene);
            int firstPlain = pending.FindIndex(p => !p.Scenario.UsesSharedScene);
            if (firstShared < 0)
            {
                return firstPlain;
            }

            if (run.SharedSceneFirst && run.VisualLane.TryEnter(run, false))
            {
                holdsLane = true;
                return firstShared;
            }

            if (firstPlain >= 0 && firstPlain < firstShared)
            {
                return firstPlain;
            }

            // WHY: queue only when nothing else is left; a target that queued while busy with other work would sit
            // at the head of the line and keep the lane idle until that work finished.
            if (run.VisualLane.TryEnter(run, firstPlain < 0))
            {
                holdsLane = true;
                return firstShared;
            }

            return firstPlain;
        }

        private static bool FitsBudget(TargetSuiteRun run, GameBenchmarkScenario scenario)
        {
            double worstCaseSeconds = ScenarioWorstCaseSeconds(scenario, ResolveTimeoutSeconds(scenario),
                ResolveMaxAttempts(scenario));
            return run.SuiteClock.Elapsed.TotalSeconds + worstCaseSeconds <= run.SuiteBudgetSeconds;
        }

        private static void LogBudgetStop(TargetSuiteRun run, GameBenchmarkScenario scenario)
        {
            float timeout = ResolveTimeoutSeconds(scenario);
            int maxAttempts = ResolveMaxAttempts(scenario);
            Debug.LogWarning(
                $"{run.LogPrefix} Suite time budget ({run.SuiteBudgetSeconds:0}s) would be exceeded by " +
                $"{scenario.Name} ({maxAttempts} attempt(s) x {timeout:0}s timeout, " +
                $"{ScenarioWorstCaseSeconds(scenario, timeout, maxAttempts):0}s planned) after " +
                $"{run.Report.Results.Count} scenario result(s); stopping early and writing the " +
                "report for everything finished so far.");
        }

        /// <summary>
        /// Renders the report's model card while holding the visual lane (ahead of queued scenarios), so no
        /// other target's scene can stand in front of the card camera. This is the single call site of the card
        /// renderer for both the single-model and the multi-target run.
        /// </summary>
        internal static IEnumerator CaptureReportModelCard(BenchmarkReport report, BenchmarkVisualLane lane,
            Action<byte[]> onPng)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                || report.Results.Count == 0)
            {
                yield break;
            }

            object owner = new();
            try
            {
                if (lane != null)
                {
                    yield return lane.Enter(owner, true);
                }

                yield return BenchmarkModelCard.Capture(report, onPng);
            }
            finally
            {
                lane?.Leave(owner);
            }
        }

        /// <summary>Runs a target's loop, then writes its model card and artifacts.</summary>
        internal static IEnumerator RunAndFinishTarget(TargetSuiteRun run)
        {
            try
            {
                yield return RunTargetSuite(run);
            }
            finally
            {
                run.Progress.End();
            }

            byte[] modelCardPng = null;
            yield return CaptureReportModelCard(run.Report, run.VisualLane, png => modelCardPng = png);
            run.ArtifactPath = WriteArtifacts(run.Report, modelCardPng, run.ArtifactStem, run.ArtifactDirectory);
            BenchmarkReport report = run.Report;
            Debug.Log($"{run.LogPrefix} ===== TARGET COMPLETE =====\n" +
                      $"Suite base score: {report.SuiteBaseScore:0.#}/100 (mean bonus {report.MeanBonus:0.#})\n" +
                      $"PASS {report.PassCount} / PARTIAL {report.PartialCount} / FAIL {report.FailCount} " +
                      $"(pass-rate {report.PassRate * 100:0.#}%)\n" +
                      $"Tokens {report.TotalTokens} | total latency {report.TotalLatencyMs:0} ms\n" +
                      $"Report: {run.ArtifactPath}");
        }

        /// <summary>
        /// Runs <paramref name="runs"/> concurrently (at most <paramref name="parallel"/> at once) and returns the
        /// problems that should fail the test: harness faults, targets without results, harness-attributed
        /// scenario failures. A faulted target still gets a report of what it finished.
        /// </summary>
        internal static IEnumerator RunTargets(IReadOnlyList<TargetSuiteRun> runs, int parallel, List<string> problems)
        {
            List<Func<IEnumerator>> routines = new(runs.Count);
            double?[] stopTimes = new double?[runs.Count];
            foreach (TargetSuiteRun run in runs)
            {
                TargetSuiteRun target = run;
                target.ScenarioContext ??= new ScenarioRunContext();
                Func<bool> previousCancel = target.ScenarioContext.ShouldCancel;
                target.ScenarioContext.ShouldCancel = () => target.Progress.StopRequested ||
                    target.SuiteClock.Elapsed.TotalSeconds >= target.SuiteBudgetSeconds ||
                    (previousCancel?.Invoke() ?? false);
                routines.Add(() => RunAndFinishTarget(target));
            }

            yield return CooperativeScheduler.RunAll(routines, parallel, (index, ex) =>
            {
                TargetSuiteRun failed = runs[index];
                problems.Add($"{failed.DisplayName}: harness fault {ex.GetType().Name}: {ex.Message}");
                // WHY: a logged error fails the test at once; the other targets must finish and write first.
                Debug.LogWarning($"{failed.LogPrefix} harness fault, writing a partial report: {ex}");
                failed.Progress.End();
                failed.ArtifactPath = WriteArtifacts(failed.Report, null, failed.ArtifactStem,
                    failed.ArtifactDirectory);
            }, index =>
            {
                TargetSuiteRun target = runs[index];
                double elapsed = target.SuiteClock.Elapsed.TotalSeconds;
                if (target.Progress.StopRequested)
                {
                    stopTimes[index] ??= elapsed;
                }

                double deadline = Math.Min(target.SuiteBudgetSeconds, stopTimes[index] ?? double.PositiveInfinity);
                if (elapsed < deadline + Math.Max(0, target.ScenarioContext.CancellationGraceSeconds) + 1)
                {
                    return null;
                }

                target.BudgetHit = true;
                return new TimeoutException("Target did not finish after Stop or its suite budget and cancellation grace.");
            });

            foreach (TargetSuiteRun run in runs)
            {
                if (!ShouldRequireScenarioResults(run.Progress.StopRequested, run.Report.Results.Count))
                {
                    Debug.LogWarning($"{run.LogPrefix} Stop requested before any scenario completed; " +
                                     "empty partial report saved.");
                    continue;
                }

                if (run.Report.Results.Count == 0)
                {
                    problems.Add($"{run.DisplayName}: produced no scenario results.");
                }

                if (run.Report.FrameworkFailures > 0)
                {
                    problems.Add($"{run.DisplayName}: a scenario failed inside the harness (not the model). " +
                                 "See its report for details.");
                }
            }
        }

        /// <summary>
        /// The multi-target entry: one HTTP client, settings asset, report and progress run per target, all run
        /// together under the shared suite budget, with the visual lane serializing scene-building scenarios.
        /// </summary>
        private static IEnumerator RunMultiTargetSuite(IReadOnlyList<BenchmarkTarget> targets)
        {
            int parallel = BenchmarkTargetList.ResolveParallelism(
                Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvParallel), targets.Count);
            string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            double budget = ResolveSuiteBudgetSeconds(targets.Count);
            int repetitions = ResolveRepetitions();
            BenchmarkVisualLane visualLane = new();
            Stopwatch suiteClock = Stopwatch.StartNew();
            List<TargetSuiteRun> runs = new();
            List<PlayModeProductionLikeLlmHandle> handles = new();
            List<CoreAISettingsAsset> settingsAssets = new();
            List<string> problems = new();

            if (!BenchmarkTargetList.TryNormalizeReasoningEffort(
                    Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvReasoningEffort),
                    out string defaultEffort))
            {
                Assert.Fail($"[Benchmark] {BenchmarkTargetList.EnvReasoningEffort}: " +
                            BenchmarkTargetList.ReasoningEffortError(
                                Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvReasoningEffort)));
            }

            Debug.Log($"[Benchmark] Multi-target run {runId}: {targets.Count} target(s), up to {parallel} at a " +
                      $"time, suite budget {budget:0}s, reasoning effort " +
                      $"{(defaultEffort.Length > 0 ? defaultEffort : "provider default")} unless a target sets its own.");
            BenchmarkProgress.BeginSession();
            try
            {
                foreach (BenchmarkTarget target in targets)
                {
                    PlayModeProductionLikeLlmHandle handle;
                    string reason;
                    bool created;
                    try
                    {
                        created = PlayModeProductionLikeLlmFactory.TryCreateForEndpoint(target.BaseUrl, target.Model,
                            target.ResolveApiKey(), 0.1f, 300, out handle, out reason, target.ExtraBodyJson,
                            target.ReasoningEffort, defaultEffort);
                    }
                    catch (ArgumentException ex)
                    {
                        // WHY: an invalid provider body is a configuration error; fail before any target runs.
                        created = false;
                        handle = null;
                        reason = ex.Message;
                    }

                    if (!created)
                    {
                        Assert.Fail($"[Benchmark] Target '{target.DisplayName}': {reason}");
                    }

                    handles.Add(handle);
                    CoreAISettingsAsset settings = CreateBenchmarkSettings();
                    settingsAssets.Add(settings);
                    GameBenchmarkScenario[] scenarios = AllScenarios();
                    string backend = target.HasDistinctLabel
                        ? $"{handle.ResolvedBackend} (model {target.Model})"
                        : handle.ResolvedBackend.ToString();
                    BenchmarkReport report = new()
                    {
                        Metadata = CreateRunMetadata(runId, target.DisplayName, backend,
                            handle.ResolvedConfig?.NativeTools ?? handle.Client.SupportsNativeToolCalling,
                            handle.ResolvedConfig?.Streaming ?? false, settings, repetitions,
                            handle.ResolvedConfig?.ReasoningEffort)
                    };
                    string logPrefix = $"[Benchmark][{target.DisplayName}]";
                    runs.Add(new TargetSuiteRun
                    {
                        DisplayName = target.DisplayName,
                        TokenizerModelId = target.Model,
                        ArtifactStem = ArtifactStem(runId, target.Model, target.Label),
                        Client = handle.Client,
                        Settings = settings,
                        TokenCounter = new BpeTokenCounter(),
                        Report = report,
                        Progress = BenchmarkProgress.BeginRun(target.DisplayName,
                            PlannedRunCount(scenarios, repetitions), true),
                        Scenarios = scenarios,
                        Repetitions = repetitions,
                        SuiteBudgetSeconds = budget,
                        SuiteClock = suiteClock,
                        VisualLane = visualLane,
                        SharedSceneFirst = true,
                        LogPrefix = logPrefix,
                        ScenarioContext = new ScenarioRunContext
                        {
                            TokenizerModelId = target.Model,
                            LogPrefix = logPrefix,
                            RequireTraceId = true,
                            Parallel = true
                        }
                    });
                }

                RaiseSharedEndpointConnectionLimits(targets);
                yield return RunTargets(runs, parallel, problems);
            }
            finally
            {
                BenchmarkProgress.EndSession();
                foreach (PlayModeProductionLikeLlmHandle handle in handles)
                {
                    handle.Dispose();
                }

                foreach (CoreAISettingsAsset settings in settingsAssets)
                {
                    if (settings != null)
                    {
                        UnityEngine.Object.DestroyImmediate(settings);
                    }
                }
            }

            Debug.Log($"[Benchmark] ===== MULTI-TARGET RUN {runId} COMPLETE =====\n" +
                      string.Join("\n", runs.ConvertAll(r =>
                          $"{r.DisplayName}: {r.Report.SuiteBaseScore:0.#}/100, {r.Report.Results.Count} result(s)" +
                          $"{(r.BudgetHit ? " (stopped early)" : "")} — {r.ArtifactPath}")));
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        /// <summary>
        /// Lets every target behind one host keep its own connection. WHY: the HTTP stack limits concurrent
        /// connections per host and port (two by default on Mono), so a third target on the same provider would
        /// wait for a free connection and the run would silently serialize.
        /// </summary>
        private static void RaiseSharedEndpointConnectionLimits(IReadOnlyList<BenchmarkTarget> targets)
        {
            Dictionary<string, int> perEndpoint = new(StringComparer.OrdinalIgnoreCase);
            foreach (BenchmarkTarget target in targets)
            {
                string authority = new Uri(target.BaseUrl).GetLeftPart(UriPartial.Authority);
                perEndpoint[authority] = perEndpoint.TryGetValue(authority, out int count) ? count + 1 : 1;
            }

            foreach (KeyValuePair<string, int> endpoint in perEndpoint)
            {
                try
                {
                    ServicePoint point = ServicePointManager.FindServicePoint(new Uri(endpoint.Key));
                    int wanted = endpoint.Value * 2 + 2;
                    if (point.ConnectionLimit < wanted)
                    {
                        point.ConnectionLimit = wanted;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] could not raise the connection limit for {endpoint.Key}: " +
                                     ex.Message);
                }
            }
        }
    }
}
#endif
#endif
