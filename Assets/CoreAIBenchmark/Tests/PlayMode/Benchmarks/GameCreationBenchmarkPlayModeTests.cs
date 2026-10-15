#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Entry point for the game-creation benchmark. Runs every scenario through the resolved live model, grades
    /// each 0..100 via the portable scoring core, and writes
    /// <c>TestResults/CoreAI/Benchmarks/BENCHMARK_&lt;runId&gt;_&lt;model&gt;.{md,json}</c>. With
    /// <see cref="BenchmarkTargetList.EnvTargets"/> set it runs several models at once instead, one report each
    /// (see <c>GameCreationBenchmarkParallelSuite.cs</c>).
    /// <para>
    /// Gated on a configured provider (<see cref="PlayModeOpenAiTestConfig"/> env vars / local config or a
    /// local LLMUnity model); <see cref="Assert.Ignore(string)"/> when unconfigured. This is a benchmark,
    /// not a pass/fail correctness gate: it only fails on a HARNESS (framework) failure, never on a low
    /// model score — the score is the measurement.
    /// </para>
    /// </summary>
    public sealed partial class GameCreationBenchmarkPlayModeTests
    {
        // WHY: Suite 2.0 adds one extreme scenario to every group except G6 (reported as a separate extreme
        // sub-score), regrades G6 on build geometry and closes grader loopholes in G1-G5, G7 and G8, so no
        // 2.0 score is comparable with 1.x.
        // WHY: Suite 1.16 makes each tool-error checkpoint declare what it covers (G4 clean_tool now also fails on
        // a failed call that never ran Lua) and accounts for a turn cut by the deadline. Rows whose G4 had no such
        // failed call are unchanged and were carried over from 1.15.
        // WHY: Suite 1.15 removes duplicate tool-error deductions and supplies advertised Lua world queries.
        // Suite 1.8 rebuilt G6 on the Roblox API (execute_lua, Enum.Material/Enum.PartType) with a
        // new grader, and the free-build prompt now describes that runtime honestly (section size, the
        // writable Part surface, how Color really composes). The versioning policy says scores compare
        // only within a suite version; every published v1.7 G6 number is the old world_command build.
        private const string SuiteVersion = "2.0";
        private const float FreeBuildTotalBudgetSeconds = 600f;

        /// <summary>
        /// Planning allowance for the G6 screenshot and prefab export after the build. The capture is not
        /// time-limited; the start-gate reserves this on top of the build and the cancel grace.
        /// </summary>
        private const float FreeBuildCaptureReserveSeconds = 30f;
        private const float FreeBuildTimeoutSeconds = FreeBuildTotalBudgetSeconds - FreeBuildCaptureReserveSeconds;

        /// <summary>
        /// NUnit hard-abort backstop (6 h), sized for a multi-target run of the full suite on slow bridged
        /// models. Attribute arguments must be compile-time constants, so the soft-budget clamp below reuses
        /// this exact value instead of a second hand-maintained number. The launcher watchdog must stay above it.
        /// </summary>
        internal const int NUnitTimeoutMs = 21_600_000;

        /// <summary>Headroom reserved for report/screenshot/model-card writing after the last scenario.</summary>
        private const double ReportMarginSeconds = 300;

        /// <summary>Extra report headroom per additional target: its model card and artifacts are written in turn.</summary>
        private const double ReportMarginPerExtraTargetSeconds = 30;

        /// <summary>Soft budget of a single-model run when <see cref="EnvSuiteBudget"/> is unset (100 minutes).</summary>
        private const double DefaultSingleTargetBudgetSeconds = 6000;

        /// <summary>Env var (CSV of group ids, e.g. "G1,G2") to restrict which scenarios run. Empty = all.</summary>
        public const string EnvGroups = "COREAI_BENCHMARK_GROUPS";

        /// <summary>Env var (int) for how many times each scenario runs; the report keeps the per-scenario mean.</summary>
        public const string EnvRepetitions = "COREAI_BENCHMARK_REPS";

        /// <summary>Env var (seconds) overriding the per-scenario wall-clock timeout. 0/unset = per-scenario default.</summary>
        public const string EnvTimeout = "COREAI_BENCHMARK_TIMEOUT";

        /// <summary>Env var (int) for the per-request tool-call roundtrip cap. 0/unset = default 40.</summary>
        public const string EnvRoundtrips = "COREAI_BENCHMARK_ROUNDTRIPS";

        /// <summary>
        /// Env var for the G6 free-build vision mode: "off" (default, text-only build), "image" (the model
        /// gets a camera tool to SEE and refine its build — vision-capable models only), or "both" (run the
        /// text-only build AND an image-feedback build so their scores can be compared). Also settable from
        /// the benchmark window dropdown.
        /// </summary>
        public const string EnvVisionMode = "COREAI_BENCHMARK_VISION_MODE";

        /// <summary>
        /// Env var (seconds) for the SOFT whole-suite time budget. A scenario rep only STARTS when its
        /// WORST case — every retry attempt running the full per-scenario timeout — still fits inside this
        /// budget; once nothing more fits, the suite stops and writes the report/screenshots for everything
        /// finished so far — unlike the NUnit [Timeout], which hard-aborts and produces NO artifacts.
        /// A multi-target run shares one budget, measured from the start of the run, across all targets.
        /// Default: 6000s (100 min) for a single model, the full ceiling for a multi-target run. The effective
        /// value is clamped so that budget + report margin (300s, plus 30s per extra target) never exceeds the
        /// NUnit [Timeout] (6 h), keeping the graceful path in charge even for oversized env values.
        /// </summary>
        public const string EnvSuiteBudget = "COREAI_BENCHMARK_SUITE_BUDGET";

        /// <summary>Report headroom between the soft budget and the NUnit hard abort for a run of <paramref name="targetCount"/> targets.</summary>
        internal static double ReportMarginFor(int targetCount)
        {
            return ReportMarginSeconds + ReportMarginPerExtraTargetSeconds * Math.Max(0, targetCount - 1);
        }

        internal static double ResolveSuiteBudgetSeconds(int targetCount = 1)
        {
            // Hard ceiling for the soft budget. The rep start-gate reserves the FULL worst case
            // (maxAttempts x this rep's timeout) inside the budget, so scenario work always finishes by
            // the budget itself; only the report/screenshot margin has to fit between the budget and the
            // NUnit hard abort (which writes no artifacts).
            double margin = ReportMarginFor(targetCount);
            double cap = NUnitTimeoutMs / 1000d - margin;

            string raw = Environment.GetEnvironmentVariable(EnvSuiteBudget);
            if (!string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 30)
            {
                if (s > cap)
                {
                    Debug.LogWarning(
                        $"[Benchmark] {EnvSuiteBudget}={s:0}s exceeds the safe ceiling; clamped to {cap:0}s " +
                        $"(NUnit [Timeout] {NUnitTimeoutMs / 1000d:0}s - report margin " +
                        $"{margin:0}s), otherwise the NUnit hard abort could fire before the " +
                        "report is written.");
                    return cap;
                }

                return s;
            }

            // WHY: a single model keeps its historical 100-minute default; parallel targets are typically slow
            // bridged models, and the shared budget is only a graceful upper bound, so they get the ceiling.
            return targetCount > 1 ? cap : Math.Min(DefaultSingleTargetBudgetSeconds, cap);
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
        /// distinct from repetitions, which re-run successful scenarios for mean stability.
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

        /// <summary>
        /// Per-scenario model timeout; a free build is capped at the 600 s budget minus the capture reserve.
        /// <c>COREAI_BENCHMARK_TIMEOUT</c> overrides regular scenarios and the free build, never an extreme-tier
        /// scenario, whose own longer budget is part of its task.
        /// </summary>
        private static float ResolveTimeoutSeconds(GameBenchmarkScenario scenario)
        {
            string raw = Environment.GetEnvironmentVariable(EnvTimeout);
            // WHY: the flat override is sized for regular scenarios; applied to an extreme scenario it cut
            // its stated time and turned hard tasks into timeouts.
            if (!scenario.IsExtreme && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 1f && s <= 1200f)
            {
                return scenario.FreeBuildLayout ? Math.Min(s, FreeBuildTimeoutSeconds) : s;
            }

            return scenario.FreeBuildLayout
                ? Math.Min(scenario.TimeoutSeconds, FreeBuildTimeoutSeconds)
                : scenario.TimeoutSeconds;
        }

        /// <summary>Total attempts per repetition on a transient failure (1 + retries, clamped 1..4).</summary>
        private static int ResolveMaxAttempts()
        {
            string raw = Environment.GetEnvironmentVariable(EnvRetries);
            int retries = int.TryParse(raw, out int n) && n >= 0 && n <= 3 ? n : 1;
            return 1 + retries;
        }

        private static int ResolveMaxAttempts(GameBenchmarkScenario scenario)
        {
            // WHY: A second full attempt would turn one ten-minute G6 build into a twenty-minute run.
            return scenario.FreeBuildLayout ? 1 : ResolveMaxAttempts();
        }

        /// <summary>
        /// Wall-clock one scenario rep may occupy, as the suite start-gate plans it: every attempt's timeout
        /// plus the cancel grace the runner waits after it, and for a free build the capture/export reserve.
        /// </summary>
        internal static double ScenarioWorstCaseSeconds(GameBenchmarkScenario scenario, float timeout,
            int maxAttempts)
        {
            return maxAttempts * (timeout + CancelGraceSeconds) +
                   (scenario.FreeBuildLayout ? FreeBuildCaptureReserveSeconds : 0.0);
        }

        [Test]
        public void FreeBuildBudget_ResolverClampsTheModelTimeUnderTheReserve()
        {
            GameBenchmarkScenario freeBuild = GameFreeBuildScenariosG6.All()[0];
            string previousTimeout = Environment.GetEnvironmentVariable(EnvTimeout);
            string previousRetries = Environment.GetEnvironmentVariable(EnvRetries);
            try
            {
                Environment.SetEnvironmentVariable(EnvTimeout, "1200");
                Environment.SetEnvironmentVariable(EnvRetries, "3");
                float timeout = ResolveTimeoutSeconds(freeBuild);
                Assert.AreEqual(FreeBuildTotalBudgetSeconds - FreeBuildCaptureReserveSeconds, timeout);
                Assert.AreEqual(1, ResolveMaxAttempts(freeBuild));
                Assert.AreEqual(timeout + CancelGraceSeconds + FreeBuildCaptureReserveSeconds,
                    ScenarioWorstCaseSeconds(freeBuild, timeout, ResolveMaxAttempts(freeBuild)), 1e-6,
                    "The start-gate must plan the cancel grace and the capture reserve, not only the model time.");

                Environment.SetEnvironmentVariable(EnvTimeout, "90");
                Assert.AreEqual(90f, ResolveTimeoutSeconds(freeBuild));
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvTimeout, previousTimeout);
                Environment.SetEnvironmentVariable(EnvRetries, previousRetries);
            }
        }

        [Test]
        public void TimeoutOverride_AppliesToRegularScenariosButNotToExtremeOnes()
        {
            GameBenchmarkScenario regular = Array.Find(GameInstructionScenariosG5.All(), s => !s.IsExtreme);
            GameBenchmarkScenario g5Extreme = Array.Find(GameInstructionScenariosG5.All(), s => s.IsExtreme);
            GameBenchmarkScenario g8Extreme = Array.Find(GameObserveActScenariosG8.All(), s => s.IsExtreme);
            Assert.IsNotNull(regular);
            Assert.IsNotNull(g5Extreme);
            Assert.IsNotNull(g8Extreme);
            string previousTimeout = Environment.GetEnvironmentVariable(EnvTimeout);
            try
            {
                Environment.SetEnvironmentVariable(EnvTimeout, "60");
                Assert.AreEqual(60f, ResolveTimeoutSeconds(regular));
                Assert.AreEqual(g5Extreme.TimeoutSeconds, ResolveTimeoutSeconds(g5Extreme));
                Assert.AreEqual(g8Extreme.TimeoutSeconds, ResolveTimeoutSeconds(g8Extreme));

                Environment.SetEnvironmentVariable(EnvTimeout, "1200");
                Assert.AreEqual(1200f, ResolveTimeoutSeconds(regular));
                Assert.AreEqual(g5Extreme.TimeoutSeconds, ResolveTimeoutSeconds(g5Extreme),
                    "the override neither shrinks nor stretches an extreme scenario's own budget");
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvTimeout, previousTimeout);
            }
        }

        [Test]
        public void FreeBuildContinuation_RequiresProgressAndRemainingTime()
        {
            Assert.IsTrue(GameCreationBenchmarkHarness.CanContinueFreeBuild(8, 0, 0, 68, 600));
            Assert.IsTrue(GameCreationBenchmarkHarness.CanContinueFreeBuild(16, 8, 1, 540, 600));
            Assert.IsFalse(GameCreationBenchmarkHarness.CanContinueFreeBuild(8, 8, 1, 100, 600));
            Assert.IsFalse(GameCreationBenchmarkHarness.CanContinueFreeBuild(16, 8, 1, 560, 600));
            Assert.IsFalse(GameCreationBenchmarkHarness.CanContinueFreeBuild(16, 8,
                GameCreationBenchmarkHarness.MaxFreeBuildContinuations, 100, 600));
        }

        [Test]
        public void BalancedEnemyGrader_AcceptsFourDistinctHpValuesSummingTo400()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                GameBenchmarkScenario scenario = Array.Find(GameReasoningScenariosG3.All(),
                    candidate => candidate.Id == "g3_balanced_enemies");
                Assert.IsNotNull(scenario);
                BenchmarkEnvironment env = new(settings);
                scenario.Prepare(env);
                env.Lua.Seed("logic_define('enemy_hp', function(name) " +
                             "local hp = { Enemy1=70, Enemy2=85, Enemy3=105, Enemy4=140 }; " +
                             "return hp[name] or 0 end)");
                Assert.IsTrue(env.Lua.LogicSlots.IsOverridden("enemy_hp"), env.Lua.LastError);
                for (int i = 1; i <= 4; i++)
                {
                    env.World.Commands.Add(new RecordedWorldCommand
                    {
                        Action = "spawn",
                        TargetName = $"Enemy{i}"
                    });
                }

                ScenarioGrading grade = scenario.Grade(env, new RunObservation { ToolCalls = 5 });
                Assert.IsTrue(grade.Checkpoints.Find(c => c.Id == "in_range").Passed);
                Assert.IsTrue(grade.Checkpoints.Find(c => c.Id == "distinct").Passed);
                Assert.IsTrue(grade.Checkpoints.Find(c => c.Id == "sum_400").Passed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void LuaWorldQueries_SeeSeededAndRecordedSceneObjects()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                BenchmarkEnvironment env = new(settings);
                env.World.SeedObjects("TowerA", "Debris1");
                LuaTool.LuaResult first = env.Lua.ExecuteAsync(
                    "local names=coreai_world_find('Debris'); local pos=coreai_world_pos('TowerA'); " +
                    "if #names==1 and names[1]=='Debris1' and pos.x==0 and " +
                    "coreai_world_exists('TowerA') then return 'ok' else return 'bad' end",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(first.Success, first.Error);
                Assert.AreEqual("ok", first.Output);

                env.World.TryExecute(new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                    JsonPayload = "{\"action\":\"destroy\",\"targetName\":\"Debris1\"}"
                });
                LuaTool.LuaResult second = env.Lua.ExecuteAsync(
                    "if not coreai_world_exists('Debris1') and #coreai_world_find('Debris')==0 " +
                    "and coreai_world_pos('Debris1')==nil then return 'removed' else return 'bad' end",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(second.Success, second.Error);
                Assert.AreEqual("removed", second.Output);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ToolErrorPenalties_FollowTheCoverageEachRealCheckpointDeclares()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                // WHY: G4 clean_tool scores failed calls but not invalid world commands, so the invalid
                // command must surface as a separate penalty.
                foreach (GameBenchmarkScenario scenario in GamePlaythroughScenariosG4.All())
                {
                    BenchmarkEnvironment env = new(settings);
                    scenario.Prepare(env);
                    AddInvalidWorldCommand(env);
                    RunObservation failedCallWithoutLua = new()
                    {
                        ToolCalls = 2, FailedToolCalls = 1, InvalidCommands = env.World.InvalidCommandCount
                    };
                    ScenarioGrading grade = scenario.Grade(env, failedCallWithoutLua);
                    ApplyUnscoredToolErrorPenalties(grade, failedCallWithoutLua);

                    Assert.AreEqual(0, env.Lua.FailedExecutions, scenario.Id);
                    BenchmarkCheckpoint clean = grade.Checkpoints.Find(c => c.Id == "clean_tool");
                    Assert.IsNotNull(clean, scenario.Id);
                    Assert.IsFalse(clean.Passed,
                        $"{scenario.Id}: a failed call that never ran Lua must still be charged.");
                    Assert.AreEqual(1, grade.Penalties.Count, scenario.Id);
                    StringAssert.Contains("invalid world command", grade.Penalties[0].Reason);
                }

                // WHY: G3 clean_tool scores both, so no separate penalty may be added - nothing is charged twice.
                GameBenchmarkScenario g3 = Array.Find(GameReasoningScenariosG3.All(),
                    candidate => candidate.Id == "g3_balanced_enemies");
                Assert.IsNotNull(g3);
                BenchmarkEnvironment g3Env = new(settings);
                g3.Prepare(g3Env);
                AddInvalidWorldCommand(g3Env);
                RunObservation errors = new()
                {
                    ToolCalls = 3, FailedToolCalls = 2, InvalidCommands = g3Env.World.InvalidCommandCount
                };
                Assert.AreEqual(1, errors.InvalidCommands);
                ScenarioGrading g3Grade = g3.Grade(g3Env, errors);
                ApplyUnscoredToolErrorPenalties(g3Grade, errors);
                Assert.IsFalse(g3Grade.Checkpoints.Find(c => c.Id == "clean_tool").Passed);
                Assert.AreEqual(0, g3Grade.Penalties.Count);

                ScenarioGrading uncovered = new();
                ApplyUnscoredToolErrorPenalties(uncovered, errors);
                Assert.AreEqual(2, uncovered.Penalties.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static void AddInvalidWorldCommand(BenchmarkEnvironment env)
        {
            env.World.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = "{\"action\":\"\"}"
            });
        }

        [Test]
        public void FreeBuildGoal_AddsCastlePacingOnlyToTheDefaultCastleGoal()
        {
            const string subjectVar = "COREAI_BENCHMARK_FREEBUILD_SUBJECT";
            const string promptVar = "COREAI_BENCHMARK_FREEBUILD_PROMPT";
            string previousSubject = Environment.GetEnvironmentVariable(subjectVar);
            string previousPrompt = Environment.GetEnvironmentVariable(promptVar);
            const string timeNote = GameFreeBuildScenariosG6.FreeBuildTimeBudgetNote;
            try
            {
                Environment.SetEnvironmentVariable(subjectVar, null);
                Environment.SetEnvironmentVariable(promptVar, null);
                // WHY: the default castle goal ends with the clock, the castle pacing and the stop rule, in that order.
                StringAssert.EndsWith(GameFreeBuildScenariosG6.FreeBuildTimeBudgetClockNote +
                                      GameFreeBuildScenariosG6.FreeBuildCastlePacingNote +
                                      GameFreeBuildScenariosG6.FreeBuildTimeBudgetStopNote,
                    GameFreeBuildScenariosG6.All()[0].Goal);

                Environment.SetEnvironmentVariable(subjectVar, "a futuristic city");
                string generic = GameFreeBuildScenariosG6.All()[0].Goal;
                string genericSuffix = GameFreeBuildScenariosG6.FreeBuildTimeBudgetClockNote +
                                       GameFreeBuildScenariosG6.FreeBuildGenericPacingNote +
                                       GameFreeBuildScenariosG6.FreeBuildTimeBudgetStopNote;
                StringAssert.EndsWith(genericSuffix, generic);
                StringAssert.DoesNotContain(GameFreeBuildScenariosG6.FreeBuildCastlePacingNote, generic);
                string[] castleWords = { "castle", "curtain wall", "keep", "tower", "gate", "silhouette" };
                foreach (string castleWord in castleWords)
                {
                    StringAssert.DoesNotContain(castleWord, genericSuffix.ToLowerInvariant());
                }

                const string customPrompt = "Build a lighthouse on a sea cliff.";
                Environment.SetEnvironmentVariable(promptVar, customPrompt);
                Assert.AreEqual(customPrompt + timeNote, GameFreeBuildScenariosG6.All()[0].Goal);
                StringAssert.DoesNotContain("castle", timeNote.ToLowerInvariant());
            }
            finally
            {
                Environment.SetEnvironmentVariable(subjectVar, previousSubject);
                Environment.SetEnvironmentVariable(promptVar, previousPrompt);
            }
        }

        /// <summary>Streams two chunks; each MoveNext spends <see cref="BurnMs"/> so turn time is measurable.</summary>
        private sealed class TimedStreamingLlm : ILlmClient
        {
            internal const int BurnMs = 40;

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "done" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                Burn(BurnMs);
                yield return new LlmStreamChunk { Text = "part " };
                Burn(BurnMs);
                yield return new LlmStreamChunk { IsDone = true, PromptTokens = 100, CompletionTokens = 10 };
            }

            private static void Burn(int ms)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    Thread.Sleep(1);
                }
            }
        }

        [Test]
        public void TokenAccounting_CutTurnIsTimedAndMarksTheTotalsPartial()
        {
            SessionCapturingLlmClient capture = new(new TimedStreamingLlm());
            LlmCompletionRequest request = new() { UserPayload = "build" };

            IAsyncEnumerator<LlmStreamChunk> completed = capture.CompleteStreamingAsync(request).GetAsyncEnumerator();
            while (completed.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
            }

            completed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            double completedMs = capture.GenerationMs;

            // WHY: disposing after the first chunk is what the deadline does to a streaming turn.
            IAsyncEnumerator<LlmStreamChunk> cut = capture.CompleteStreamingAsync(request).GetAsyncEnumerator();
            Assert.IsTrue(cut.MoveNextAsync().AsTask().GetAwaiter().GetResult());
            cut.DisposeAsync().AsTask().GetAwaiter().GetResult();
            capture.Seal();

            Assert.AreEqual(2, capture.Turns.Count);
            Assert.AreEqual(1, capture.CutTurnCount);
            Assert.IsFalse(capture.Turns[0].Cut);
            Assert.IsTrue(capture.Turns[1].Cut);
            Assert.AreEqual("part ", capture.Turns[1].Assistant);
            Assert.GreaterOrEqual(capture.GenerationMs, completedMs + TimedStreamingLlm.BurnMs - 1,
                "The cut turn's generation time must be counted.");
            StringAssert.Contains("CUT:", capture.BuildTranscript("goal"));

            // WHY: the lifecycle observer's arguments for the cut turn lift the estimate above provider usage.
            const int estimatedCompletion = 40;
            TokenAccounting tokens = ResolveTokenAccounting(capture, estimatedCompletion, 0);
            Assert.IsTrue(tokens.Partial);
            Assert.IsFalse(tokens.FromProvider);
            Assert.AreEqual(100, tokens.PromptTokens);
            Assert.AreEqual(estimatedCompletion, tokens.CompletionTokens);
            Assert.AreEqual(capture.GenerationMs / 1000.0, tokens.GenerationSeconds, 1e-9);
            Assert.Less(tokens.TokensPerSecond, estimatedCompletion / (completedMs / 1000.0),
                "Tokens that include the cut turn must not be divided by the completed turns' time alone.");

            string banner = FormatFreeBuildHeroStats(5, 12, 0, 1000, tokens);
            StringAssert.Contains("partial token estimate", banner);
            StringAssert.Contains("~" + tokens.TokensPerSecond.ToString("0.#"), banner);

            SessionCapturingLlmClient complete = new(new TimedStreamingLlm());
            IAsyncEnumerator<LlmStreamChunk> only = complete.CompleteStreamingAsync(request).GetAsyncEnumerator();
            while (only.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
            }

            only.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TokenAccounting exact = ResolveTokenAccounting(complete, 5, 0);
            Assert.IsFalse(exact.Partial);
            Assert.IsTrue(exact.FromProvider);
            Assert.AreEqual(10, exact.CompletionTokens);
        }

        [Test]
        public async Task ProviderResponseEvidence_ResetsBetweenTurnsAndSealFreezesIt()
        {
            ResponseEvidenceLlm provider = new();
            SessionCapturingLlmClient capture = new(provider);
            await foreach (LlmStreamChunk chunk in capture.CompleteStreamingAsync(new LlmCompletionRequest())) { }
            Assert.IsTrue(capture.ProviderResponseObserved);
            provider.OpenResponse = false;
            IAsyncEnumerator<LlmStreamChunk> waiting = capture.CompleteStreamingAsync(new LlmCompletionRequest())
                .GetAsyncEnumerator();
            Assert.IsTrue(await waiting.MoveNextAsync());
            Assert.IsFalse(capture.ProviderResponseObserved, "A later request cannot inherit an earlier HTTP success.");
            capture.Seal();
            provider.LastState.RecordResponse();
            Assert.IsFalse(capture.ProviderResponseObserved, "Late updates cannot change a sealed scenario.");
            await waiting.DisposeAsync();
        }

        [Test]
        public async Task ProviderResponseEvidence_ParallelCapturesRemainIndependent()
        {
            ResponseEvidenceLlm open = new();
            ResponseEvidenceLlm waiting = new() { OpenResponse = false };
            SessionCapturingLlmClient first = new(open);
            SessionCapturingLlmClient second = new(waiting);
            await foreach (LlmStreamChunk chunk in first.CompleteStreamingAsync(new LlmCompletionRequest())) { }
            await foreach (LlmStreamChunk chunk in second.CompleteStreamingAsync(new LlmCompletionRequest())) { }
            Assert.IsTrue(first.ProviderResponseObserved);
            Assert.IsFalse(second.ProviderResponseObserved);
            first.Seal();
            open.LastState.ResetAttempt();
            Assert.IsTrue(first.ProviderResponseObserved);
        }

        [Test]
        public void ProviderFailure_RawTransportFaultsAreEnvironmentFailures()
        {
            Assert.AreEqual(FailureAttribution.Environment, ClassifyException(new System.IO.IOException("connection reset")));
            Assert.AreEqual(FailureAttribution.Environment, ClassifyException(new OperationCanceledException("cancelled",
                new System.Net.Http.HttpRequestException("send failed"))));
            Assert.AreEqual(FailureAttribution.Framework, ClassifyException(new InvalidOperationException("grader failed")));
        }

        [Test]
        public async Task ProviderResponseEvidence_CapturesHttpFailureBeforeCancellationWrappingIsLost()
        {
            ResponseEvidenceLlm provider = new() { FailAfterFirstChunk = true };
            SessionCapturingLlmClient capture = new(provider);
            IAsyncEnumerator<LlmStreamChunk> iterator = capture.CompleteStreamingAsync(new LlmCompletionRequest())
                .GetAsyncEnumerator();
            Assert.IsTrue(await iterator.MoveNextAsync());
            bool cancelled = false;
            try { await iterator.MoveNextAsync(); }
            catch (OperationCanceledException) { cancelled = true; }
            await iterator.DisposeAsync();
            Assert.IsTrue(cancelled);
            Assert.IsTrue(capture.TransportFailed);
            StringAssert.Contains("503", capture.FirstProviderError);
            Assert.AreEqual(FailureAttribution.Environment,
                BenchmarkProviderFailurePolicy.ClassifyDeadline(true, capture.ProviderResponseObserved, capture.TransportFailed));
        }

        private sealed class ResponseEvidenceLlm : ILlmClient
        {
            public bool OpenResponse = true;
            public bool FailAfterFirstChunk;
            public LlmProviderResponseState LastState;
            public bool SupportsNativeToolCalling => true;
            public void SetTools(IReadOnlyList<ILlmTool> tools) { }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LastState = request.ProviderResponseState;
                LastState.ResetAttempt();
                if (OpenResponse) LastState.RecordResponse();
                yield return new LlmStreamChunk();
                await Task.CompletedTask;
                if (FailAfterFirstChunk)
                    throw new OperationCanceledException("cancelled", new LlmClientException("HTTP 503",
                        LlmErrorCode.BackendUnavailable, 503));
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        [Test]
        public void BenchmarkOutputCap_ResolvesOptionalPerCallLimit()
        {
            string previous = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_MAX_OUTPUT_TOKENS");
            try
            {
                Environment.SetEnvironmentVariable("COREAI_BENCHMARK_MAX_OUTPUT_TOKENS", "2048");
                Assert.AreEqual(2048, ResolveBenchmarkMaxOutputTokens());
                Environment.SetEnvironmentVariable("COREAI_BENCHMARK_MAX_OUTPUT_TOKENS", "0");
                Assert.IsNull(ResolveBenchmarkMaxOutputTokens());
            }
            finally
            {
                Environment.SetEnvironmentVariable("COREAI_BENCHMARK_MAX_OUTPUT_TOKENS", previous);
            }
        }

        [Test]
        public void ScenarioToolCallObserver_RetainsCallsFromAnUnfinishedStream()
        {
            ScenarioToolCallObserver observer = new("GameMaster", "g6_free_build_vision");
            LlmToolCallInfo matching = new("trace", "GameMaster", "call-1", "execute_lua",
                "{\"code\":\"build()\"}");
            LlmToolCallInfo failed = new("trace", "GameMaster", "call-3", "execute_lua",
                "{\"code\":\"bad()\"}");
            LlmToolCallInfo otherRole = new("trace", "Builder", "call-2", "world_command",
                "{\"action\":\"spawn\"}");
            observer.Record(new LlmToolCallRecord { Info = matching, Status = "started" });
            observer.Record(new LlmToolCallRecord { Info = otherRole, Status = "completed" });
            observer.Record(new LlmToolCallRecord { Info = matching, Status = "completed" });
            observer.Record(new LlmToolCallRecord { Info = failed, Status = "failed" });

            Assert.AreEqual(2, observer.ToolCalls);
            Assert.AreEqual(1, observer.FailedToolCalls);
            StringAssert.Contains("execute_lua", observer.CompletionText);
            StringAssert.Contains("build()", observer.CompletionText);
            string replay = observer.BuildLuaReplayScript("bunny", "g6_free_build_vision");
            StringAssert.Contains("run_chunk(1, function()", replay);
            StringAssert.Contains("build()", replay);
            StringAssert.Contains("bad()", replay);
            Assert.Less(replay.IndexOf("build()", StringComparison.Ordinal),
                replay.IndexOf("bad()", StringComparison.Ordinal));
            string[] traceLines = observer.ToolTraceJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(2, traceLines.Length);
            StringAssert.Contains("g6_free_build_vision", traceLines[0]);
            StringAssert.Contains("build()", traceLines[0]);
            StringAssert.Contains("bad()", traceLines[1]);
        }

        [Test]
        public void ScenarioToolCallObserver_IgnoresCallsFromAnotherRunsTrace()
        {
            ScenarioToolCallObserver observer = new("GameMaster", "g6_free_build", "bench-g6_free_build-run2");
            observer.Record(new LlmToolCallRecord
            {
                Info = new LlmToolCallInfo("bench-g6_free_build-run2-t1", "GameMaster", "c1", "execute_lua",
                    "{\"code\":\"mine()\"}"),
                Status = "completed"
            });
            observer.Record(new LlmToolCallRecord
            {
                Info = new LlmToolCallInfo("bench-g6_free_build-run1-t3", "GameMaster", "c2", "execute_lua",
                    "{\"code\":\"orphan()\"}"),
                Status = "failed"
            });
            observer.Record(new LlmToolCallRecord
            {
                Info = new LlmToolCallInfo("", "GameMaster", "c3", "execute_lua", "{\"code\":\"untraced()\"}"),
                Status = "completed"
            });

            Assert.AreEqual(2, observer.ToolCalls);
            Assert.AreEqual(0, observer.FailedToolCalls);
            StringAssert.DoesNotContain("orphan()", observer.CompletionText);
        }

        [Test]
        public void VisionFreeBuild_IsSelectedAsHeroAndExportsLua()
        {
            BenchmarkReport report = new();
            ScenarioResult plain = new()
            {
                Group = "G6",
                ScenarioId = "g6_free_build",
                SceneScreenshotPng = new byte[] { 2 },
                LuaReplayScript = "plain build"
            };
            report.Add(plain);
            ScenarioResult expected = new()
            {
                Group = "G6",
                ScenarioId = "g6_free_build_vision",
                SceneScreenshotPng = new byte[] { 1 },
                LuaReplayScript = "run_chunk(1, function() end)"
            };
            report.Add(expected);
            Assert.AreSame(expected, FindFreeBuildHeroResult(report));
        }

        [Test]
        public void FreeBuildHero_SkipsAFailedBuild()
        {
            BenchmarkReport report = new();
            ScenarioResult plain = new()
            {
                Group = "G6",
                ScenarioId = "g6_free_build",
                SceneScreenshotPng = new byte[] { 2 }
            };
            report.Add(plain);
            report.Add(new ScenarioResult
            {
                Group = "G6",
                ScenarioId = "g6_free_build_vision",
                SceneScreenshotPng = new byte[] { 1 },
                Attribution = FailureAttribution.Environment,
                Failure = "provider error: HTTP 500"
            });
            Assert.AreSame(plain, FindFreeBuildHeroResult(report));

            BenchmarkReport failedOnly = new();
            failedOnly.Add(new ScenarioResult
            {
                Group = "G6",
                ScenarioId = "g6_free_build",
                SceneScreenshotPng = new byte[] { 3 },
                Failure = "grading failed: boom"
            });
            Assert.IsNull(FindFreeBuildHeroResult(failedOnly));
        }

        [TestCase("A task was canceled.", true)]
        [TestCase("The operation was cancelled.", true)]
        [TestCase("HTTP 504 Gateway Timeout", false)]
        [TestCase("connection timed out", false)]
        [TestCase("provider timeout", false)]
        public void CancellationClassification_DistinguishesLocalStopFromTransportTimeout(
            string error, bool expected)
        {
            Assert.AreEqual(expected, IsCancellationError(error));
        }

        [TestCase(true, true, true, true)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        public void CleanBudgetCancellation_RequiresOwnDeadlineAndBuiltScene(
            bool scenarioCancelled, bool sceneWasBuilt, bool cancellationResult, bool expected)
        {
            Assert.AreEqual(expected,
                IsCleanBudgetCancellation(scenarioCancelled, sceneWasBuilt, cancellationResult));
        }

        [Test]
        public void ProviderCancellation_RemainsAnEnvironmentErrorBeforeOurDeadline()
        {
            const string providerError = "A task was canceled.";
            Assert.IsTrue(IsCancellationError(providerError));
            Assert.IsTrue(LooksTransient(providerError));
            Assert.IsFalse(IsCleanBudgetCancellation(false, true, IsCancellationError(providerError)));
        }

        [Test]
        public void VisualSceneDetection_IncludesPartsBuiltOutsideWorldCommand()
        {
            VisualBenchmarkWorldExecutor world = new();
            try
            {
                Assert.IsFalse(world.HasRenderableScene);
                GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
                part.transform.SetParent(world.Root, false);
                part.transform.position = new Vector3(20f, 0f, 0f);
                Assert.AreEqual(0, world.ObjectCount,
                    "A Lua-built part must not rely on the world_command object counter.");
                Assert.IsTrue(world.HasRenderableScene,
                    "Lua-built geometry must trigger the G6 screenshot and prefab export.");
                Assert.That(world.ComputeBounds().center.x, Is.EqualTo(20f).Within(0.01f),
                    "The hero camera must frame Lua-built geometry rather than an empty origin.");
            }
            finally
            {
                world.Cleanup();
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Retakes a G6 hero image from the saved model-authored prefab without calling the provider. An optional
        /// UTF-8 file named by <c>COREAI_BENCHMARK_RECAPTURE_TEXT_FILE</c> supplies the banner text so the retake
        /// matches the original hero: line 1 the score line, line 2 the stats line, line 3 the caption (a blank
        /// or missing line keeps the default).
        /// </summary>
        [UnityTest]
        [Explicit("Set COREAI_BENCHMARK_RECAPTURE_PREFAB and COREAI_BENCHMARK_RECAPTURE_OUTPUT")]
        public IEnumerator CaptureSavedCastlePrefab_ForReport()
        {
            string prefabPath = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_RECAPTURE_PREFAB");
            string outputPath = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_RECAPTURE_OUTPUT");
            if (string.IsNullOrWhiteSpace(prefabPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                Assert.Ignore("No saved castle requested for screenshot recapture.");
            }

            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab, "The requested saved castle prefab must exist.");
            VisualBenchmarkWorldExecutor world = new();
            try
            {
                UnityEngine.Object.Instantiate(prefab, world.Root);
                byte[] png = null;
                string model = Environment.GetEnvironmentVariable("COREAI_TEST_MODEL") ?? prefab.name;
                int parts = world.Root.GetComponentsInChildren<Renderer>(true).Length;
                string[] text = ReadRecaptureText();
                yield return CaptureSceneScreenshot(world, model,
                    RecaptureLine(text, 0, "Free build (visual) — saved scene"),
                    RecaptureLine(text, 2, "Model-authored G6 castle captured from its saved prefab."),
                    true, RecaptureLine(text, 1, $"{parts} rendered parts"), image => png = image);
                Assert.IsNotNull(png, "The saved scene must render a PNG.");
                File.WriteAllBytes(outputPath, png);
            }
            finally
            {
                world.Cleanup();
            }
        }

        /// <summary>
        /// Retakes a G6 hero image by replaying the report's <c>_g6_replay.lua</c> in a fresh Roblox-API world, so the
        /// runtime-generated textures and materials look as they did in the run (a saved prefab keeps only colours).
        /// Reads <c>COREAI_BENCHMARK_RECAPTURE_LUA</c>, <c>COREAI_BENCHMARK_RECAPTURE_OUTPUT</c>, the optional
        /// <c>COREAI_BENCHMARK_RECAPTURE_TEXT_FILE</c> described on <see cref="CaptureSavedCastlePrefab_ForReport"/> and
        /// <c>COREAI_TEST_MODEL</c> for the headline.
        /// </summary>
        [UnityTest]
        [Explicit("Set COREAI_BENCHMARK_RECAPTURE_LUA and COREAI_BENCHMARK_RECAPTURE_OUTPUT")]
        public IEnumerator CaptureSavedCastleReplay_ForReport()
        {
            string luaPath = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_RECAPTURE_LUA");
            string outputPath = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_RECAPTURE_OUTPUT");
            if (string.IsNullOrWhiteSpace(luaPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                Assert.Ignore("No castle replay requested for screenshot recapture.");
            }

            Assert.IsTrue(File.Exists(luaPath), "The requested replay script must exist.");
            VisualBenchmarkWorldExecutor world = new() { HideLabels = true };
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            RbxBenchmarkWorld rbx = null;
            try
            {
                rbx = new RbxBenchmarkWorld(settings, world.Root);
                Assert.IsTrue(rbx.TryEvaluateLua(File.ReadAllText(luaPath, System.Text.Encoding.UTF8), out string output),
                    "The replay must finish synchronously; output: " + output);
                int parts = rbx.Measure().Parts;
                Assert.Greater(parts, 0, "The replay must build at least one castle part.");
                yield return null;
                byte[] png = null;
                string model = Environment.GetEnvironmentVariable("COREAI_TEST_MODEL") ?? "replay";
                string[] text = ReadRecaptureText();
                yield return CaptureSceneScreenshot(world, model,
                    RecaptureLine(text, 0, "Free build (visual) — replayed scene"),
                    RecaptureLine(text, 2, "Model-authored G6 castle replayed from its recorded Lua."),
                    true, RecaptureLine(text, 1, $"{parts} parts"), image => png = image);
                Assert.IsNotNull(png, "The replayed scene must render a PNG.");
                File.WriteAllBytes(outputPath, png);
            }
            finally
            {
                rbx?.Dispose();
                world.Cleanup();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static string[] ReadRecaptureText()
        {
            string file = Environment.GetEnvironmentVariable("COREAI_BENCHMARK_RECAPTURE_TEXT_FILE");
            return string.IsNullOrWhiteSpace(file) || !File.Exists(file)
                ? Array.Empty<string>()
                : File.ReadAllLines(file, System.Text.Encoding.UTF8);
        }

        private static string RecaptureLine(string[] lines, int index, string fallback)
        {
            return index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) ? lines[index].Trim() : fallback;
        }
#endif

        [Test]
        public void HeroTitle_KeepsLongLocalModelLabelOnOneLine()
        {
            float size = FitHeroTitle("local/qwen3.8-flash-coder-iq1_m", out string text);
            Assert.AreEqual("local/qwen3.8-flash-coder-iq1_m", text,
                "A 31-character label fits one line; it used to wrap to a lone 'm'.");
            Assert.That(size, Is.GreaterThan(0.0086f), "The label must not be set smaller than the old wrapped tier.");
            Assert.That(size * text.Length, Is.LessThanOrEqualTo(0.30001f), "The line must stay inside the width budget.");

            Assert.AreEqual(0.0112f, FitHeroTitle("codex/GPT-6.1 Sol", out string shortText), 1e-6f);
            Assert.AreEqual("codex/GPT-6.1 Sol", shortText);

            string veryLong = "provider/some-extremely-long-model-identifier-with-many-hyphenated-parts-v2";
            float wrapped = FitHeroTitle(veryLong, out string wrappedText);
            Assert.That(wrappedText, Does.Contain("\n"), "A name over the single-line limit wraps.");
            Assert.AreEqual(0.0064f, wrapped, 1e-6f);
        }

        [Test]
        public void StopAtRetryBoundary_ClearsUnretriedHardFailureBeforeScoring()
        {
            ScenarioResult captured = new() { Failure = "timeout" };

            Assert.IsTrue(AbortRetryForStop(true, ref captured));
            Assert.IsNull(captured);

            captured = new ScenarioResult { Failure = "timeout" };
            Assert.IsFalse(AbortRetryForStop(false, ref captured));
            Assert.IsNotNull(captured);
        }

        [Test]
        public void StopDroppedRepetition_IsNotCountedAsCompleted()
        {
            Assert.IsFalse(ShouldCountCompletedRepetition(true));
            Assert.IsTrue(ShouldCountCompletedRepetition(false));
        }

        [Test]
        public void StopWithNoResults_SkipsNonEmptyReportRequirement()
        {
            Assert.IsFalse(ShouldRequireScenarioResults(true, 0));
            Assert.IsTrue(ShouldRequireScenarioResults(false, 0));
            Assert.IsTrue(ShouldRequireScenarioResults(true, 1));
        }

        // ==================== RbxBenchmarkWorld.TimedExecuteLuaTool ====================

        /// <summary>
        /// Drives the wrapper over the exact shape <c>LuaTool.CreateAIFunction</c> builds —
        /// <c>AIFunctionFactory.Create</c> over a <c>Task&lt;string&gt;</c> delegate — so the assertion
        /// covers what MEAI really hands back (a <c>JsonElement</c>, not a <c>string</c>), not a stand-in.
        /// </summary>
        [Test]
        public void TimedExecuteLuaTool_StampsTimeLeftIntoMeaiMarshalledResult()
        {
            Func<string, CancellationToken, Task<string>> body =
                (code, cancellationToken) => Task.FromResult("{\"Success\":true,\"Output\":\"3\"}");
            MEAI.AIFunction inner = MEAI.AIFunctionFactory.Create(body, new MEAI.AIFunctionFactoryOptions
            {
                Name = "execute_lua",
                Description = "probe"
            });
            MEAI.AIFunction timed =
                RbxBenchmarkWorld.TimedExecuteLuaTool.WithTimeLeft(inner, () => "~412s left");

            object result = timed
                .InvokeAsync(new MEAI.AIFunctionArguments { ["code"] = "return 3" })
                .AsTask().GetAwaiter().GetResult();

            JObject payload = JObject.Parse(result?.ToString() ?? "");
            Assert.AreEqual("~412s left", (string)payload["TimeLeft"]);
            Assert.IsTrue((bool)payload["Success"]);
            Assert.AreEqual("3", (string)payload["Output"]);
        }

        [Test]
        public void TimedExecuteLuaTool_Stamp_AcceptsStringAndJsonStringElement()
        {
            const string json = "{\"Success\":false,\"Error\":\"boom\"}";
            System.Text.Json.JsonElement element = System.Text.Json.JsonSerializer.SerializeToElement(json);

            foreach (object raw in new object[] { json, element })
            {
                string shape = raw.GetType().Name;
                object stamped = RbxBenchmarkWorld.TimedExecuteLuaTool.Stamp(raw, () => " 9s left ");

                Assert.IsInstanceOf<string>(stamped, shape);
                JObject payload = JObject.Parse((string)stamped);
                Assert.AreEqual("9s left", (string)payload["TimeLeft"], shape);
                Assert.IsFalse((bool)payload["Success"], shape);
                Assert.AreEqual("boom", (string)payload["Error"], shape);
            }
        }

        [Test]
        public void TimedExecuteLuaTool_Stamp_LeavesResultUntouchedWithoutNoteOrJsonObject()
        {
            System.Text.Json.JsonElement element =
                System.Text.Json.JsonSerializer.SerializeToElement("{\"Success\":true}");

            Assert.IsInstanceOf<System.Text.Json.JsonElement>(
                RbxBenchmarkWorld.TimedExecuteLuaTool.Stamp(element, null));
            Assert.IsInstanceOf<System.Text.Json.JsonElement>(
                RbxBenchmarkWorld.TimedExecuteLuaTool.Stamp(element, () => " "));
            Assert.AreEqual("plain text",
                RbxBenchmarkWorld.TimedExecuteLuaTool.Stamp("plain text", () => "9s left"));
        }

        [UnityTest]
        [Timeout(NUnitTimeoutMs)] // 6 h — last-resort NUnit backstop; the SOFT suite budget (which still
        // writes artifacts) is the real terminator, clamped in
        // ResolveSuiteBudgetSeconds to this value minus a report margin.
        // The rep start-gate reserves maxAttempts x timeout inside the budget, so
        // scenario work plus report writing always finishes before the hard abort.
        // NUnit's hard abort writes nothing.
        [Category("Benchmark")]
        [Explicit("Live game-creation benchmark; run manually with a configured model.")]
        public IEnumerator GameCreationBenchmark_Suite()
        {
            string targetSpec = Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvTargets);
            string resumeId = ResolveResumeId();
            BenchmarkRunStore resumeStore = resumeId.Length > 0 ? new BenchmarkRunStore(DefaultResultsDirectory(), resumeId) : null;
            bool localResume = resumeStore?.HasManifest == true && resumeStore.LoadManifest().LocalBackend.Length > 0;
            if (localResume)
            {
                BenchmarkRunManifest saved = resumeStore.LoadManifest();
                foreach (KeyValuePair<string, string> option in saved.Options)
                    if (option.Key != EnvRetries) SetSavedOption(option.Key, option.Value);
                saved.Validate(SuiteVersion, DurablePlan(AllScenarios(), saved.Repetitions));
            }
            if (resumeId.Length > 0 && !localResume)
            {
                if (!BenchmarkTargetList.TryParse(targetSpec, out List<BenchmarkTarget> supplied, out string parseError))
                    throw new InvalidOperationException(parseError);
                List<BenchmarkTarget> resumedTargets = ResolveResumeTargets(resumeId, supplied);
                yield return RunMultiTargetSuite(resumedTargets);
                yield break;
            }
            if (!localResume && !string.IsNullOrWhiteSpace(targetSpec))
            {
                if (!BenchmarkTargetList.TryParse(targetSpec, out List<BenchmarkTarget> targets, out string targetError))
                {
                    Assert.Fail(targetError);
                }

                if (targets.Count > 0)
                {
                    yield return RunMultiTargetSuite(targets);
                    yield break;
                }
            }

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, null, 0, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            CoreAISettingsAsset settings = CreateBenchmarkSettings();
            BenchmarkReport report = new();
            BenchmarkVisualLane visualLane = new();
            TargetSuiteRun singleRun = null;

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

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

                // From the resolved config when present, else the actual client capability (the asset-driven
                // path carries no ResolvedConfig). Streaming is forced off per-agent for determinism, so it is
                // reported as false here regardless of the provider default.
                report.Metadata = CreateRunMetadata(localResume ? resumeId : DateTime.Now.ToString("yyyyMMdd_HHmmss"), modelId,
                    handle.ResolvedBackend.ToString(),
                    handle.ResolvedConfig?.NativeTools ?? handle.Client.SupportsNativeToolCalling,
                    handle.ResolvedConfig?.Streaming ?? false, settings, repetitions,
                    handle.ResolvedConfig?.ReasoningEffort, handle.Temperature);

                GameBenchmarkScenario[] scenarios = AllScenarios();
                BenchmarkProgress.BeginSession();
                BenchmarkRunProgress progress =
                    BenchmarkProgress.BeginRun(modelId, PlannedRunCount(scenarios, repetitions), false);

                TargetSuiteRun run = new()
                {
                    DisplayName = modelId,
                    TokenizerModelId = modelId,
                    Client = handle.Client,
                    Settings = settings,
                    TokenCounter = new BpeTokenCounter(),
                    Report = report,
                    Progress = progress,
                    Scenarios = scenarios,
                    Repetitions = repetitions,
                    SuiteBudgetSeconds = ResolveSuiteBudgetSeconds(),
                    SuiteClock = System.Diagnostics.Stopwatch.StartNew(),
                    VisualLane = visualLane,
                    ScenarioContext = new ScenarioRunContext { ArtifactId = ArtifactStem(report.Metadata.RunId, modelId) }
                };
                singleRun = run;
                string endpoint = handle.ResolvedConfig?.BaseUrl ?? CoreAISettingsAsset.Instance?.ApiBaseUrl;
                if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://localhost";
                string resolvedKey = handle.ResolvedConfig?.ApiKey ?? CoreAISettingsAsset.Instance?.ApiKey ?? "";
                string keySource = !string.IsNullOrEmpty(resolvedKey) && Environment.GetEnvironmentVariable("COREAI_TEST_API_KEY") == resolvedKey
                    ? "env:COREAI_TEST_API_KEY" : resolvedKey;
                BenchmarkTarget durableTarget = new(modelId, endpoint, keySource, reasoningEffort: report.Metadata.ReasoningEffort,
                    extraBodyJson: handle.ResolvedConfig?.ExtraBodyJson ?? "", temperature: handle.Temperature);
                BenchmarkRunManifest manifest = CreateManifest(report.Metadata.RunId, new[] { durableTarget }, scenarios, repetitions);
                manifest.UseDefaultSecretSource = true;
                manifest.ParallelExecution = false;
                manifest.Options["COREAI_TEST_STREAMING"] = report.Metadata.Streaming ? "1" : "0";
                manifest.Options["COREAI_TEST_NATIVE_TOOLS"] = report.Metadata.NativeToolCalling ? "1" : "0";
                manifest.LocalBackend = handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity ? handle.ResolvedBackend.ToString() : "";
                BenchmarkRunStore store = new(DefaultResultsDirectory(), report.Metadata.RunId);
                if (localResume)
                {
                    BenchmarkRunManifest saved = store.LoadManifest();
                    if (saved.LocalBackend != manifest.LocalBackend)
                        throw new InvalidOperationException("Cannot resume: local benchmark backend changed.");
                    saved.Targets[0].Validate(manifest.Targets[0]);
                    manifest = saved;
                }
                else
                {
                    if (store.HasManifest) throw new InvalidOperationException("A benchmark run with this timestamp already exists.");
                    store.SaveManifest(manifest);
                }
                AttachCheckpoint(run, store, manifest, manifest.Targets[0].Slug, localResume);
                report = run.Report;
                if (!run.SkipCompleted) yield return RunTargetSuite(run);
                SaveCheckpoint(run, segmentFinished: true);
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

            if (singleRun?.SkipCompleted == true) yield break;

            // A suite-level "model card" (radar of the six dimensions + game-fitness bars) so two models'
            // results are comparable at a glance — rendered with a throwaway camera while still in Play mode.
            byte[] modelCardPng = null;
            yield return CaptureReportModelCard(report, visualLane, png => modelCardPng = png);

            string artifactPath = WriteArtifacts(report, modelCardPng);
            if (artifactPath != "(not written)" && singleRun != null)
                SaveCheckpoint(singleRun, singleRun.Plan.All(p => singleRun.Checkpoint.IsFinalized(p.Id, p.Repetition)), true);

            Debug.Log($"[Benchmark] ===== SUITE COMPLETE =====\n" +
                      $"Suite base score: {report.SuiteBaseScore:0.#}/100 (mean bonus {report.MeanBonus:0.#})\n" +
                      $"PASS {report.PassCount} / PARTIAL {report.PartialCount} / FAIL {report.FailCount} " +
                      $"(pass-rate {report.PassRate * 100:0.#}%)\n" +
                      $"Tokens {report.TotalTokens} | total latency {report.TotalLatencyMs:0} ms\n" +
                      $"Report: {artifactPath}");

            if (!ShouldRequireScenarioResults(BenchmarkProgress.StopRequested, report.Results.Count))
            {
                Debug.LogWarning(
                    "[Benchmark] Stop requested before any scenario completed; empty partial report saved.");
                yield break;
            }

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
            int d = difficulty10 < 1 ? 1 : difficulty10 > 10 ? 10 : difficulty10;
            int half = d / 2;
            return $"{new string('●', half)}{new string('○', 5 - half)} {d}/10";
        }

        private static bool AbortRetryForStop(bool stopRequested, ref ScenarioResult captured)
        {
            if (!stopRequested)
            {
                return false;
            }

            captured = null;
            return true;
        }

        private static bool ShouldCountCompletedRepetition(bool droppedForStop)
        {
            return !droppedForStop;
        }

        private static bool ShouldRequireScenarioResults(bool stopRequested, int resultCount)
        {
            return !stopRequested || resultCount > 0;
        }

        private static string ProgressLine(ScenarioResult r)
        {
            if (r.Attribution == FailureAttribution.Environment)
            {
                return $"⚠ {r.ScenarioName} — provider/env failure (excluded)";
            }

            if (r.Attribution == FailureAttribution.NotGraded)
            {
                return $"⚪ {r.ScenarioName} — custom prompt (not scored)";
            }

            string glyph = r.Classification switch
            {
                BenchmarkClassification.Pass => "✅",
                BenchmarkClassification.Partial => "🟡",
                _ => "❌"
            };
            return $"{glyph} {r.ScenarioName} — {r.Score.Base:0.#}";
        }

        /// <summary>Project-root <c>TestResults/CoreAI/Benchmarks</c>, where every report lands.</summary>
        internal static string DefaultResultsDirectory()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, "TestResults", "CoreAI", "Benchmarks");
        }

        /// <summary>
        /// Artifact file stem: run date + model, so reports are self-identifying and never overwrite. A target
        /// label that differs from the model id is appended after a double underscore, which keeps two targets
        /// serving the same model apart.
        /// </summary>
        internal static string ArtifactStem(string runId, string modelId, string label = null)
        {
            string stem = $"BENCHMARK_{runId}_{SanitizeForFileName(modelId)}";
            return string.IsNullOrWhiteSpace(label) ||
                   string.Equals(label.Trim(), modelId?.Trim(), StringComparison.Ordinal)
                ? stem
                : $"{stem}__{SanitizeForFileName(label)}";
        }

        internal static string WriteArtifacts(BenchmarkReport report, byte[] modelCardPng = null, string stem = null,
            string directory = null)
        {
            try
            {
                string dir = string.IsNullOrWhiteSpace(directory) ? DefaultResultsDirectory() : directory;
                Directory.CreateDirectory(dir);

                stem ??= ArtifactStem(report.Metadata.RunId, report.Metadata.ModelId);
                string mdName = stem + ".md";
                string svgName = stem + ".svg";
                string mdPath = Path.Combine(dir, mdName);
                string heroScenarioId = null;

                // Visual results card, embedded near the top of the Markdown report.
                BenchmarkRunStore.AtomicWrite(Path.Combine(dir, svgName), BenchmarkReportFormatter.ToSvg(report));
                string md = EmbedResultsImage(BenchmarkReportFormatter.ToMarkdown(report), svgName);
                System.Text.StringBuilder toolTrace = new(report.PreservedToolTraceJsonl);
                foreach (ScenarioResult scenarioResult in report.Results)
                {
                    toolTrace.Append(scenarioResult.ToolTraceJsonl);
                }

                if (toolTrace.Length > 0)
                {
                    string traceName = stem + ".tools.jsonl";
                    BenchmarkRunStore.AtomicWrite(Path.Combine(dir, traceName), toolTrace.ToString());
                    md += $"\n[Tool-call arguments and result previews (up to 2,000 characters each)]({traceName})\n";
                }

                // WHY: the vector card needs no GPU, so a headless run still gets a model card.
                string cardSvgName = stem + "_modelcard.svg";
                BenchmarkRunStore.AtomicWrite(Path.Combine(dir, cardSvgName), BenchmarkReportFormatter.ToModelCardSvg(report));

                // The rendered model card leads the report when available, else its vector twin.
                if (modelCardPng == null || modelCardPng.Length == 0)
                {
                    md = EmbedResultsImage(md, cardSvgName);
                }
                else
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
                    if (!string.IsNullOrWhiteSpace(hero.LuaReplayScript))
                    {
                        string luaName = stem + "_g6_replay.lua";
                        BenchmarkRunStore.AtomicWrite(Path.Combine(dir, luaName), hero.LuaReplayScript);
                        md += $"\n[Replay the model's complete G6 Lua calls]({luaName})\n";
                    }

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
                BenchmarkRunStore.AtomicWrite(mdPath, md);
                BenchmarkRunStore.AtomicWrite(Path.Combine(dir, stem + ".json"), BenchmarkReportFormatter.ToJson(report));

                // Append a one-line row to the rolling index so many runs stay easy to scan/compare.
                string indexPath = Path.Combine(dir, "INDEX.md");
                if (!File.Exists(indexPath))
                {
                    BenchmarkRunStore.AtomicWrite(indexPath, BenchmarkReportFormatter.IndexHeader());
                }

                List<string> indexLines = File.ReadAllLines(indexPath).Where(line => !line.Contains("](" + mdName + ")")).ToList();
                indexLines.Add(BenchmarkReportFormatter.IndexRow(report, mdName));
                BenchmarkRunStore.AtomicWrite(indexPath, string.Join("\n", indexLines) + "\n");

                return mdPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] failed to write artifacts: {ex.Message}");
                return "(not written)";
            }
        }

        internal static ScenarioResult FindFreeBuildHeroResult(BenchmarkReport report) =>
            BenchmarkArtifactRecovery.FindFreeBuildHeroResult(report.Results);

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

                if ((r.SceneScreenshotPng == null || r.SceneScreenshotPng.Length == 0) && string.IsNullOrEmpty(r.ImportedSceneMarkdown))
                {
                    continue;
                }

                string png = $"{stem}_{r.ScenarioId}.png";
                try
                {
                    if (r.SceneScreenshotPng != null && r.SceneScreenshotPng.Length > 0)
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

                if (!string.IsNullOrEmpty(r.ImportedSceneMarkdown))
                {
                    section += r.ImportedSceneMarkdown;
                    continue;
                }
                section += "\n" + BenchmarkReportFormatter.SceneHeading(r) + "\n";
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

            string cleaned = new(chars);
            return cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned;
        }
    }
}
#endif
#endif
