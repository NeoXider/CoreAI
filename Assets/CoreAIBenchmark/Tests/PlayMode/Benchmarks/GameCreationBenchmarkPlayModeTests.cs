#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        // WHY: Suite 1.15 removes duplicate tool-error deductions and supplies advertised Lua world queries.
        // Suite 1.8 rebuilt G6 on the Roblox API (execute_lua, Enum.Material/Enum.PartType) with a
        // new grader, and the free-build prompt now describes that runtime honestly (section size, the
        // writable Part surface, how Color really composes). The versioning policy says scores compare
        // only within a suite version; every published v1.7 G6 number is the old world_command build.
        private const string SuiteVersion = "1.15";
        private const float FreeBuildTotalBudgetSeconds = 600f;
        private const float FreeBuildCaptureReserveSeconds = 30f;
        private const float FreeBuildTimeoutSeconds = FreeBuildTotalBudgetSeconds - FreeBuildCaptureReserveSeconds;

        /// <summary>
        /// NUnit hard-abort backstop (110 min). Attribute arguments must be compile-time constants, so the
        /// soft-budget clamp below reuses this exact value instead of a second hand-maintained number.
        /// </summary>
        private const int NUnitTimeoutMs = 6_600_000;

        /// <summary>Headroom reserved for report/screenshot/model-card writing after the last scenario.</summary>
        private const double ReportMarginSeconds = 300;

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
        /// Default 6000s (100 min). The effective value is clamped so that budget + report margin (300s)
        /// never exceeds the NUnit [Timeout] (6600s), keeping the graceful path in charge even for
        /// oversized env values.
        /// </summary>
        public const string EnvSuiteBudget = "COREAI_BENCHMARK_SUITE_BUDGET";

        private static double ResolveSuiteBudgetSeconds()
        {
            // Hard ceiling for the soft budget. The rep start-gate reserves the FULL worst case
            // (maxAttempts x this rep's timeout) inside the budget, so scenario work always finishes by
            // the budget itself; only the report/screenshot margin has to fit between the budget and the
            // NUnit hard abort (which writes no artifacts).
            double cap = NUnitTimeoutMs / 1000d - ReportMarginSeconds;

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
                        $"{ReportMarginSeconds:0}s), otherwise the NUnit hard abort could fire before the " +
                        "report is written.");
                    return cap;
                }

                return s;
            }

            return Math.Min(6000, cap); // 100 minutes, kept under the NUnit-backstop ceiling
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

        /// <summary>Per-scenario timeout, capped at ten minutes for a free build.</summary>
        private static float ResolveTimeoutSeconds(GameBenchmarkScenario scenario)
        {
            string raw = Environment.GetEnvironmentVariable(EnvTimeout);
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
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

        [Test]
        public void FreeBuildBudget_NeverExceedsTenMinutesIncludingRetries()
        {
            GameBenchmarkScenario freeBuild = GameFreeBuildScenariosG6.All()[0];
            string previousTimeout = Environment.GetEnvironmentVariable(EnvTimeout);
            string previousRetries = Environment.GetEnvironmentVariable(EnvRetries);
            try
            {
                Environment.SetEnvironmentVariable(EnvTimeout, "1200");
                Environment.SetEnvironmentVariable(EnvRetries, "3");
                Assert.AreEqual(FreeBuildTimeoutSeconds, ResolveTimeoutSeconds(freeBuild));
                Assert.AreEqual(600f, FreeBuildTimeoutSeconds + FreeBuildCaptureReserveSeconds);
                Assert.AreEqual(1, ResolveMaxAttempts(freeBuild));

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
        public void ToolErrorPenalties_DoNotChargeFailuresAlreadyCoveredByCleanCheckpoint()
        {
            RunObservation errors = new() { FailedToolCalls = 2, InvalidCommands = 1 };
            ScenarioGrading bothCovered = new();
            bothCovered.Add("clean_tools", "no failed tool calls or invalid commands", 10, false,
                dimension: BenchmarkDimension.ToolCorrectness);
            ApplyUnscoredToolErrorPenalties(bothCovered, errors);
            Assert.AreEqual(0, bothCovered.Penalties.Count);

            ScenarioGrading failedOnlyCovered = new();
            failedOnlyCovered.Add("clean_tools", "no failed tool calls", 10, false,
                dimension: BenchmarkDimension.ToolCorrectness);
            ApplyUnscoredToolErrorPenalties(failedOnlyCovered, errors);
            Assert.AreEqual(1, failedOnlyCovered.Penalties.Count);
            StringAssert.Contains("invalid world command", failedOnlyCovered.Penalties[0].Reason);

            ScenarioGrading uncovered = new();
            ApplyUnscoredToolErrorPenalties(uncovered, errors);
            Assert.AreEqual(2, uncovered.Penalties.Count);
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
        /// <summary>Retakes a G6 hero image from the saved model-authored prefab without calling the provider.</summary>
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
                yield return CaptureSceneScreenshot(world, model,
                    "Free build (visual) — saved scene", "Model-authored G6 castle captured from its saved prefab.",
                    true, $"{parts} rendered parts", image => png = image);
                Assert.IsNotNull(png, "The saved scene must render a PNG.");
                File.WriteAllBytes(outputPath, png);
            }
            finally
            {
                world.Cleanup();
            }
        }
#endif

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
        [Timeout(NUnitTimeoutMs)] // 110 min — last-resort NUnit backstop; the SOFT suite budget (which still
        // writes artifacts) is the real terminator, clamped in
        // ResolveSuiteBudgetSeconds to this value minus a report margin (300s).
        // The rep start-gate reserves maxAttempts x timeout inside the budget, so
        // scenario work plus report writing always finishes before the hard abort.
        // NUnit's hard abort writes nothing.
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

                GameBenchmarkScenario[] scenarios = AllScenarios();
                // Per-scenario RepsOverride (e.g. G6/G7 always run once) means the true total is not simply
                // scenarios.Length * repetitions — sum each scenario's actual planned rep count instead, or
                // the progress bar/ETA would overshoot 100% whenever any scenario overrides its rep count.
                int totalPlannedRuns = 0;
                foreach (GameBenchmarkScenario s in scenarios)
                {
                    totalPlannedRuns += s.RepsOverride ?? repetitions;
                }

                BenchmarkProgress.Begin(totalPlannedRuns, modelId);

                // Soft whole-suite time budget: a scenario rep only starts when its FULL per-scenario timeout
                // still fits in the remaining budget, so a rep can never start just under the wire and blow
                // through the NUnit [Timeout] mid-run. Once nothing more fits, stop and fall through to
                // writing the report/screenshots for whatever finished. This is graceful — unlike the NUnit
                // [Timeout] which hard-aborts mid-scenario and writes nothing.
                double suiteBudgetSeconds = ResolveSuiteBudgetSeconds();
                System.Diagnostics.Stopwatch suiteClock = System.Diagnostics.Stopwatch.StartNew();
                bool budgetHit = false;

                foreach (GameBenchmarkScenario scenario in scenarios)
                {
                    float timeout = ResolveTimeoutSeconds(scenario);
                    int maxAttempts = ResolveMaxAttempts(scenario);
                    // Scenarios with a RepsOverride (e.g. the G6 castle hero, G7 comprehensive integration)
                    // always run their own fixed count, even when the suite repeats every other scenario
                    // for an averaged score.
                    int scenarioReps = scenario.RepsOverride ?? repetitions;
                    for (int rep = 1; rep <= scenarioReps; rep++)
                    {
                        // Manual Stop button (BenchmarkProgress.RequestStop): break GRACEFULLY between reps so
                        // the report for everything finished so far is still written, instead of losing the
                        // whole run when Play mode is exited mid-scenario. Same graceful path as the budget gate.
                        if (BenchmarkProgress.StopRequested)
                        {
                            budgetHit = true;
                            Debug.LogWarning(
                                $"[Benchmark] Stop requested after {report.Results.Count} scenario result(s); " +
                                "stopping early and writing the report for everything finished so far.");
                            break;
                        }

                        // Start-gate on every rep (not just per scenario). The worst case for the NUnit
                        // backstop is NOT one timeout: the retry loop below reruns a hard-failed attempt
                        // up to maxAttempts times, each allowed the full per-scenario timeout (which the
                        // COREAI_BENCHMARK_TIMEOUT env can raise well past the suite defaults). Reserve
                        // the whole worst case, or a provider that hangs on every attempt blows straight
                        // through the NUnit hard abort with no artifacts written.
                        if (suiteClock.Elapsed.TotalSeconds + maxAttempts * (double)timeout > suiteBudgetSeconds)
                        {
                            budgetHit = true;
                            Debug.LogWarning(
                                $"[Benchmark] Suite time budget ({suiteBudgetSeconds:0}s) would be exceeded by " +
                                $"{scenario.Name} ({maxAttempts} attempt(s) x {timeout:0}s timeout) after " +
                                $"{report.Results.Count} scenario result(s); stopping early and writing the " +
                                "report for everything finished so far.");
                            break;
                        }

                        BenchmarkProgress.StartScenario(
                            $"{scenario.Group} · {scenario.Name}  {Stars(BenchmarkInfo.DifficultyFor(scenario.Group))}" +
                            (scenarioReps > 1 ? $" (run {rep}/{scenarioReps})" : ""),
                            timeout);
                        ScenarioResult captured = null;
                        bool droppedForStop = false;
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

                            // WHY: the stop flag is otherwise only read at the top of the reps loop, so with
                            // retries a pressed Stop against a dead provider would still wait out maxAttempts
                            // full timeouts; exclude the stop-aborted scenario from model scoring instead.
                            if (AbortRetryForStop(BenchmarkProgress.StopRequested, ref captured))
                            {
                                droppedForStop = true;
                                // TODO: Preserve the dropped attempt's token and cost totals without adding it to scored report results.
                                Debug.LogWarning($"[Benchmark] {scenario.Name}: stop requested — skipping " +
                                                 $"remaining retry attempt(s) after attempt {attempt}/{maxAttempts}.");
                                break;
                            }

                            Debug.LogWarning($"[Benchmark] {scenario.Name}: run failed " +
                                             $"({captured.Failure}); retry {attempt}/{maxAttempts - 1}.");
                        }

                        if (!ShouldCountCompletedRepetition(droppedForStop))
                        {
                            continue;
                        }

                        if (captured != null)
                        {
                            captured.Repetition = rep;
                            report.Add(captured);
                            BenchmarkProgress.CompleteScenario(ProgressLine(captured),
                                Stars(BenchmarkInfo.DifficultyFor(scenario.Group)));
                        }
                        else
                        {
                            BenchmarkProgress.CompleteScenario($"⚠ {scenario.Name} — no result",
                                Stars(BenchmarkInfo.DifficultyFor(scenario.Group)));
                        }
                    }

                    if (budgetHit)
                    {
                        break; // the gate covers both loops: stop the scenario loop too
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
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null
                && report.Results.Count > 0)
            {
                yield return CaptureModelCard(report, png => modelCardPng = png);
            }

            string artifactPath = WriteArtifacts(report, modelCardPng);

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
                System.Text.StringBuilder toolTrace = new();
                foreach (ScenarioResult scenarioResult in report.Results)
                {
                    toolTrace.Append(scenarioResult.ToolTraceJsonl);
                }

                if (toolTrace.Length > 0)
                {
                    string traceName = stem + ".tools.jsonl";
                    File.WriteAllText(Path.Combine(dir, traceName), toolTrace.ToString());
                    md += $"\n[Tool-call arguments and result previews (up to 2,000 characters each)]({traceName})\n";
                }

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
                    if (!string.IsNullOrWhiteSpace(hero.LuaReplayScript))
                    {
                        string luaName = stem + "_g6_replay.lua";
                        File.WriteAllText(Path.Combine(dir, luaName), hero.LuaReplayScript);
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

        internal static ScenarioResult FindFreeBuildHeroResult(BenchmarkReport report)
        {
            ScenarioResult plainBuild = null;
            foreach (ScenarioResult r in report.Results)
            {
                if (r.SceneScreenshotPng == null || r.SceneScreenshotPng.Length == 0 ||
                    !string.Equals(r.Group, "G6", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // WHY: in A/B mode the text-only run is listed first, but the hero/replay should show
                // WHY: the vision-enabled build when it produced a usable screenshot.
                if (string.Equals(r.ScenarioId, "g6_free_build_vision", StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }

                if (plainBuild == null && string.Equals(r.ScenarioId, "g6_free_build", StringComparison.OrdinalIgnoreCase))
                {
                    plainBuild = r;
                }
            }

            return plainBuild;
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

            string cleaned = new(chars);
            return cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned;
        }
    }
}
#endif
#endif
