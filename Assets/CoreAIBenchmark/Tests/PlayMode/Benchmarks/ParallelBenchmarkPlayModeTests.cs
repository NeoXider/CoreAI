#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;
using RunTarget = CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.TargetSuiteRun;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Covers the parallel multi-target benchmark: the coroutine stepper and scheduler, the visual lane, and whole
    /// target loops driven by scripted LLM clients (overlap, per-target results and artifacts, provider failure
    /// isolation, stop, budget, parallel cap, shared-scene exclusion).
    /// </summary>
    public sealed class ParallelBenchmarkPlayModeTests
    {
        private static readonly string[] IsolatedEnvVars =
        {
            "COREAI_BENCHMARK_TIMEOUT", "COREAI_BENCHMARK_RETRIES", "COREAI_BENCHMARK_MAX_OUTPUT_TOKENS",
            "COREAI_BENCHMARK_ROUNDTRIPS", PlayModeOpenAiTestConfig.EnvBaseUrl, PlayModeOpenAiTestConfig.EnvModel,
            PlayModeOpenAiTestConfig.EnvExtraBodyJson, PlayModeOpenAiTestConfig.EnvStreaming,
            PlayModeOpenAiTestConfig.EnvNativeTools, PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort
        };

        private readonly Dictionary<string, string> _savedEnv = new();
        private readonly List<UnityEngine.Object> _ownedAssets = new();
        private string _artifactDir;

        [SetUp]
        public void SetUp()
        {
            foreach (string name in IsolatedEnvVars)
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }

            _artifactDir = Path.Combine(Path.GetTempPath(), "CoreAiParallelBenchmark_" + Path.GetRandomFileName());
        }

        [TearDown]
        public void TearDown()
        {
            foreach (KeyValuePair<string, string> saved in _savedEnv)
            {
                Environment.SetEnvironmentVariable(saved.Key, saved.Value);
            }

            foreach (UnityEngine.Object asset in _ownedAssets)
            {
                if (asset != null)
                {
                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }

            _ownedAssets.Clear();
            BenchmarkProgress.EndSession();
            if (Directory.Exists(_artifactDir))
            {
                Directory.Delete(_artifactDir, true);
            }
        }

        [Test]
        public void Stepper_FlattensNestedEnumeratorsAndKeepsTheirYields()
        {
            List<string> trace = new();
            CoroutineStepper stepper = new(Outer(trace));

            stepper.Step();
            Assert.IsNull(stepper.PendingWait);
            CollectionAssert.AreEqual(new[] { "outer-start", "inner-1" }, trace);

            stepper.Step();
            CollectionAssert.AreEqual(new[] { "outer-start", "inner-1", "inner-2", "outer-end" }, trace);
            Assert.IsInstanceOf<WaitForEndOfFrame>(stepper.PendingWait);
            Assert.IsTrue(stepper.WaitsForEndOfFrame);
            Assert.IsFalse(stepper.IsReadyForUpdate);

            stepper.Step();
            Assert.IsTrue(stepper.IsDone);
            Assert.IsNull(stepper.Error);
        }

        [Test]
        public void Stepper_ThrowInNestedLevel_EndsOnlyThisRoutineAndRunsOuterFinally()
        {
            bool outerFinallyRan = false;
            CoroutineStepper stepper = new(Guarded(() => outerFinallyRan = true));

            stepper.Step();
            Assert.IsFalse(stepper.IsDone);
            stepper.Step();

            Assert.IsTrue(stepper.IsDone);
            Assert.IsInstanceOf<InvalidOperationException>(stepper.Error);
            Assert.IsTrue(outerFinallyRan, "Abandoning the routine must run its finally blocks (lane release).");
        }

        [Test]
        public void Stepper_Dispose_RunsFinallyOfEverySuspendedLevel()
        {
            bool outerFinallyRan = false;
            CoroutineStepper stepper = new(Guarded(() => outerFinallyRan = true, throwInside: false));
            stepper.Step();
            stepper.Dispose();

            Assert.IsTrue(outerFinallyRan);
            Assert.IsTrue(stepper.IsDone);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_StepsRoutinesInTheSameFrames()
        {
            List<int>[] frames = { new(), new(), new() };
            List<Func<IEnumerator>> routines = new();
            for (int i = 0; i < frames.Length; i++)
            {
                List<int> log = frames[i];
                routines.Add(() => RecordFrames(log, 4));
            }

            yield return CooperativeScheduler.RunAll(routines, 0, (_, ex) => Assert.Fail(ex.ToString()));

            CollectionAssert.AreEqual(frames[0], frames[1], "Concurrent routines advance together, frame by frame.");
            CollectionAssert.AreEqual(frames[0], frames[2]);
            Assert.AreEqual(4, frames[0].Count);
            Assert.AreEqual(3, frames[0][3] - frames[0][0], "Each yield null resumes on the next frame.");
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_ParallelCapOfOneRunsRoutinesInTurn()
        {
            List<int> first = new();
            List<int> second = new();
            List<Func<IEnumerator>> routines = new()
            {
                () => RecordFrames(first, 3),
                () => RecordFrames(second, 3)
            };

            yield return CooperativeScheduler.RunAll(routines, 1, (_, ex) => Assert.Fail(ex.ToString()));

            Assert.Greater(second[0], first[first.Count - 1], "With a cap of one the second routine starts afterwards.");
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_AFaultingRoutineDoesNotStopTheOthers()
        {
            List<int> survivor = new();
            List<int> faulted = new();
            List<Func<IEnumerator>> routines = new()
            {
                () => ThrowAfterFrames(2),
                () => RecordFrames(survivor, 5)
            };

            yield return CooperativeScheduler.RunAll(routines, 0, (index, _) => faulted.Add(index));

            CollectionAssert.AreEqual(new[] { 0 }, faulted);
            Assert.AreEqual(5, survivor.Count);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_ResumesEndOfFrameWaitersInTheSameFrame()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("WaitForEndOfFrame does not resume in Editor batch mode or without graphics.");
            }

            int before = -1;
            int after = -1;
            List<Func<IEnumerator>> routines = new() { () => EndOfFrameProbe(f => before = f, f => after = f) };

            yield return CooperativeScheduler.RunAll(routines, 0, (_, ex) => Assert.Fail(ex.ToString()));

            Assert.AreEqual(before, after, "A WaitForEndOfFrame yield resumes at the end of the same frame.");
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_BatchMode_EndOfFrameDoesNotSuspendOtherTargets()
        {
            if (!Application.isBatchMode)
            {
                Assert.Ignore("This regression covers Editor batch mode.");
            }

            int resumed = -1;
            List<int> survivor = new();
            List<Func<IEnumerator>> routines = new()
            {
                () => EndOfFrameProbe(_ => { }, frame => resumed = frame),
                () => RecordFrames(survivor, 4)
            };
            yield return CooperativeScheduler.RunAll(routines, 0, (_, ex) => Assert.Fail(ex.ToString()));
            Assert.GreaterOrEqual(resumed, 0);
            Assert.AreEqual(4, survivor.Count);
        }

        [Test, Timeout(1000)]
        public void Stepper_NestedSequenceWithoutFrameYields_ReturnsControlToScheduler()
        {
            using CoroutineStepper stepper = new(NestedWithoutFrameYield());
            stepper.Step();
            Assert.IsFalse(stepper.IsDone);
            Assert.IsNull(stepper.PendingWait);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_InterruptsCustomWait_AndReleasesVisualWaiter()
        {
            BenchmarkVisualLane lane = new();
            object holder = new();
            object waiter = new();
            Assert.IsTrue(lane.TryEnter(holder, false));
            List<int> faults = new();
            List<int> survivor = new();
            int checks = 0;
            List<Func<IEnumerator>> routines = new()
            {
                () => lane.Enter(waiter),
                () => WaitForever(),
                () => RecordFrames(survivor, 5)
            };
            yield return CooperativeScheduler.RunAll(routines, 0, (index, _) => faults.Add(index),
                index => index < 2 && ++checks > 4 ? new TimeoutException("scripted deadline") : null);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, faults);
            Assert.AreEqual(0, lane.QueueLength);
            Assert.AreSame(holder, lane.Holder);
            Assert.AreEqual(5, survivor.Count);
            lane.Exit(holder);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator Scheduler_ReadinessFaultAndUnsupportedDelay_DoNotStopOtherTargets()
        {
            List<int> faults = new();
            List<int> survivor = new();
            List<Func<IEnumerator>> routines = new()
            {
                () => InvalidWait(true),
                () => InvalidWait(false),
                () => RecordFrames(survivor, 4)
            };
            yield return CooperativeScheduler.RunAll(routines, 0, (index, _) => faults.Add(index));
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, faults);
            Assert.AreEqual(4, survivor.Count);
        }

        [Test, Timeout(15000)]
        public void DisposingCapturesAtRenderWait_ReleasesCamerasAndRestoresLighting()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("Capture allocation requires a graphics device.");
            }

            BenchmarkReport report = new() { Metadata = new BenchmarkRunMetadata { ModelId = "probe" } };
            VisualBenchmarkWorldExecutor world = new();
            try
            {
                List<Func<IEnumerator>> captures = new()
                {
                    () => BenchmarkModelCard.Capture(report, _ => { }),
                    () => CaptureModelCard(report, _ => { }),
                    () => CaptureSceneScreenshot(world, "probe", "probe", "probe", false, null, _ => { })
                };
                foreach (Func<IEnumerator> create in captures)
                {
                    int cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                    UnityEngine.Rendering.AmbientMode ambientMode = RenderSettings.ambientMode;
                    Color ambientLight = RenderSettings.ambientLight;
                    using CoroutineStepper capture = new(create());
                    capture.Step();
                    Assert.IsNull(capture.Error);
                    Assert.IsFalse(capture.IsDone, "The capture must suspend with its render rig allocated.");
                    Assert.Greater(UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                        cameras);
                    capture.Dispose();
                    Assert.IsNull(capture.Error);
                    Assert.AreEqual(cameras,
                        UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
                    Assert.AreEqual(ambientMode, RenderSettings.ambientMode);
                    Assert.AreEqual(ambientLight, RenderSettings.ambientLight);
                }
            }
            finally
            {
                world.Cleanup();
            }
        }

        [Test]
        public void VisualLane_AdmitsOneHolderAndServesWaitersInOrder()
        {
            BenchmarkVisualLane lane = new();
            object a = new();
            object b = new();
            object c = new();

            Assert.IsTrue(lane.TryEnter(a, false));
            Assert.IsFalse(lane.TryEnter(b, true));
            Assert.IsFalse(lane.TryEnter(c, true));
            Assert.Throws<InvalidOperationException>(() => lane.Exit(b));

            lane.Exit(a);
            Assert.IsFalse(lane.TryEnter(c, true), "c queued after b and may not overtake it.");
            Assert.IsFalse(lane.TryEnter(a, false), "An opportunistic attempt never jumps the line.");
            Assert.IsTrue(lane.TryEnter(b, true));
            lane.Exit(b);
            Assert.IsTrue(lane.TryEnter(c, true));
            lane.Exit(c);

            Assert.AreEqual(3, lane.Holds.Count);
            for (int i = 1; i < lane.Holds.Count; i++)
            {
                Assert.GreaterOrEqual(lane.Holds[i].StartTimestamp, lane.Holds[i - 1].EndTimestamp,
                    "Holds never overlap.");
            }
        }

        [Test]
        public void VisualLane_PriorityWaiterGoesAheadOfQueuedScenarios()
        {
            BenchmarkVisualLane lane = new();
            object building = new();
            object queuedScenario = new();
            object modelCard = new();

            Assert.IsTrue(lane.TryEnter(building, false));
            Assert.IsFalse(lane.TryEnter(queuedScenario, true));
            Assert.IsFalse(lane.TryEnter(modelCard, true, true));
            lane.Exit(building);

            Assert.IsFalse(lane.TryEnter(queuedScenario, true));
            Assert.IsTrue(lane.TryEnter(modelCard, true, true));
        }

        [Test]
        public void VisualLane_LeaveFreesTheHeadOfTheLineAndTheLane()
        {
            BenchmarkVisualLane lane = new();
            object holder = new();
            object stopped = new();
            object next = new();

            Assert.IsTrue(lane.TryEnter(holder, false));
            Assert.IsFalse(lane.TryEnter(stopped, true));
            Assert.IsFalse(lane.TryEnter(next, true));
            lane.Leave(stopped);
            lane.Leave(holder);

            Assert.IsNull(lane.Holder);
            Assert.IsTrue(lane.TryEnter(next, true), "A waiter that stopped must not block the line.");
        }

        [Test]
        public void ToolCallObserver_StrictTrace_DropsUntracedCallsOfOtherTargets()
        {
            ScenarioToolCallObserver strict = new("GameMaster", "g2", "bench-g2-mine", true);
            ScenarioToolCallObserver lenient = new("GameMaster", "g2", "bench-g2-mine");
            LlmToolCallRecord untraced = new()
            {
                Info = new LlmToolCallInfo("", "GameMaster", "c1", "execute_lua", "{\"code\":\"x()\"}"),
                Status = "completed"
            };
            LlmToolCallRecord mine = new()
            {
                Info = new LlmToolCallInfo("bench-g2-mine-t1", "GameMaster", "c2", "execute_lua", "{}"),
                Status = "completed"
            };

            foreach (ScenarioToolCallObserver observer in new[] { strict, lenient })
            {
                observer.Record(untraced);
                observer.Record(mine);
            }

            Assert.AreEqual(1, strict.ToolCalls);
            Assert.AreEqual(2, lenient.ToolCalls, "The single-model run keeps its untraced-call fallback.");
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TwoTargets_RunConcurrently_WithSeparateClientsReportsAndArtifacts()
        {
            ProbeLog log = new();
            BenchmarkVisualLane lane = new();
            ProbeLlmClient alphaClient = new("bench-probe-alpha-", 250);
            ProbeLlmClient betaClient = new("bench-probe-beta-", 250);
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "shared-model", alphaClient, lane, log, Plain(log, "alpha", 3));
            RunTarget beta = MakeRun("beta", "shared-model", betaClient, lane, log, Plain(log, "beta", 3));
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);

            CollectionAssert.IsEmpty(problems);
            Assert.IsTrue(log.AnyOverlap("alpha", "beta"), "The two targets' scenarios must overlap in time.");
            AssertOwnResults(alpha, 3);
            AssertOwnResults(beta, 3);
            Assert.AreEqual(0, alphaClient.ForeignCalls, "A target's client only serves that target's requests.");
            Assert.AreEqual(0, betaClient.ForeignCalls);
            Assert.GreaterOrEqual(alphaClient.Calls, 3);
            Assert.GreaterOrEqual(betaClient.Calls, 3);

            Assert.AreNotEqual(alpha.ArtifactStem, beta.ArtifactStem,
                "Two targets serving the same model id must not collide on artifact names.");
            foreach (RunTarget run in new[] { alpha, beta })
            {
                Assert.IsTrue(File.Exists(Path.Combine(_artifactDir, run.ArtifactStem + ".json")), run.DisplayName);
                Assert.IsTrue(File.Exists(Path.Combine(_artifactDir, run.ArtifactStem + ".md")), run.DisplayName);
                StringAssert.Contains(run.DisplayName,
                    File.ReadAllText(Path.Combine(_artifactDir, run.ArtifactStem + ".json")));
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator ProviderFailure_OnOneTarget_DoesNotAffectTheOther()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "1");
            ProbeLog log = new();
            BenchmarkVisualLane lane = new();
            ProbeLlmClient broken = new("bench-probe-alpha-", 50, "HTTP 503 Service Unavailable");
            ProbeLlmClient healthy = new("bench-probe-beta-", 150);
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "model-a", broken, lane, log, Plain(log, "alpha", 2));
            RunTarget beta = MakeRun("beta", "model-b", healthy, lane, log, Plain(log, "beta", 2));
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);

            CollectionAssert.IsEmpty(problems);
            Assert.AreEqual(2, alpha.Report.Results.Count);
            foreach (ScenarioResult result in alpha.Report.Results)
            {
                Assert.AreEqual(FailureAttribution.Environment, result.Attribution);
                StringAssert.Contains("503", result.Failure);
            }

            Assert.AreEqual(4, broken.Calls, "Exactly one benchmark retry follows each scripted failure.");
            Assert.AreEqual(2, beta.Report.Results.Count);
            foreach (ScenarioResult result in beta.Report.Results)
            {
                Assert.AreEqual(FailureAttribution.None, result.Attribution, result.Failure);
                Assert.IsTrue(string.IsNullOrEmpty(result.Failure), result.Failure);
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator CheckpointedTarget_ResumeSkipsCompletedRepetitionsAndMergesBothTiersAndVisualLane()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "0");
            BenchmarkProgress.BeginSession();
            ProbeLog baselineLog = new();
            ProbeLlmClient baselineClient = new("bench-probe-alpha-", 0);
            RunTarget baseline = MakeRun("alpha", "model", baselineClient, new BenchmarkVisualLane(), baselineLog,
                ResumeProbes(baselineLog));
            EnableCheckpoint(baseline, new BenchmarkRunStore(_artifactDir, "baseline"));
            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(baseline);
            Assert.AreEqual(5, baseline.Report.Results.Count);

            ProbeLog interruptedLog = new();
            ProbeLlmClient interruptedClient = new("bench-probe-alpha-", 0);
            RunTarget interrupted = MakeRun("alpha", "model", interruptedClient, new BenchmarkVisualLane(), interruptedLog,
                ResumeProbes(interruptedLog));
            BenchmarkRunStore store = new(_artifactDir, "interrupted");
            EnableCheckpoint(interrupted, store);
            int graded = 0;
            interruptedLog.AfterGrade = _ =>
            {
                if (++graded == 2) interrupted.Progress.RequestStop();
            };
            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(interrupted);
            Assert.IsTrue(interrupted.BudgetHit);
            Assert.AreEqual(2, interruptedClient.Calls);
            BenchmarkTargetState saved = store.Load("alpha");
            Assert.AreEqual(2, saved.Results.Count, "Each completed result must be durable before final reports exist.");
            Assert.IsTrue(saved.Results.Any(r => r.Group == "G6"), "The lane runs first and must checkpoint independently.");
            Assert.IsFalse(File.Exists(Path.Combine(_artifactDir, interrupted.ArtifactStem + ".json")));
            Dictionary<string, string> originalRows = saved.Results.ToDictionary(
                r => r.ScenarioId + "/" + r.Repetition, r => Newtonsoft.Json.JsonConvert.SerializeObject(r));

            ProbeLog resumedLog = new();
            ProbeLlmClient resumedClient = new("bench-probe-alpha-", 0);
            RunTarget resumed = MakeRun("alpha", "model", resumedClient, new BenchmarkVisualLane(), resumedLog,
                ResumeProbes(resumedLog));
            EnableCheckpoint(resumed, store, saved);
            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(resumed);

            Assert.AreEqual(3, resumedClient.Calls, "Only the unfinished regular/extreme repetitions execute after reload.");
            Assert.AreEqual(0, resumed.VisualLane.Holds.Count, "The completed G6 lane result must be skipped.");
            CollectionAssert.AreEqual(baseline.Report.Results.Select(ResultIdentity), resumed.Report.Results.Select(ResultIdentity));
            CollectionAssert.AreEqual(baseline.Report.Results.Select(r => r.Score.Base), resumed.Report.Results.Select(r => r.Score.Base));
            CollectionAssert.AreEqual(baseline.Report.Results.Select(r => r.Extreme), resumed.Report.Results.Select(r => r.Extreme));
            CollectionAssert.AreEqual(baseline.Report.Results.Select(r => r.PromptTokens), resumed.Report.Results.Select(r => r.PromptTokens));
            Assert.AreEqual(baseline.Report.RegularBaseScore, resumed.Report.RegularBaseScore);
            Assert.AreEqual(baseline.Report.ExtremeBaseScore, resumed.Report.ExtremeBaseScore);
            Assert.AreEqual(baseline.Report.SuiteBaseScore, resumed.Report.SuiteBaseScore);
            foreach (ScenarioResult result in resumed.Report.Results)
            {
                if (originalRows.TryGetValue(ResultIdentity(result), out string original))
                    Assert.AreEqual(original, Newtonsoft.Json.JsonConvert.SerializeObject(result), "A finalized row must remain unchanged.");
            }
            Assert.AreEqual(5, store.Load("alpha").Results.Count);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator CheckpointedTarget_ResumeReexecutesEnvironmentResultAndKeepsFinalizedRow()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "0");
            BenchmarkProgress.BeginSession();
            ProbeLlmClient broken = new("bench-probe-alpha-", 0, "HTTP 503 Service Unavailable");
            RunTarget interrupted = MakeRun("alpha", "model", broken, new BenchmarkVisualLane(), null, Plain(null, "alpha", 2));
            BenchmarkRunStore store = new(_artifactDir, "environment");
            EnableCheckpoint(interrupted, store);
            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(interrupted);
            BenchmarkTargetState saved = store.Load("alpha");
            Assert.AreEqual(2, saved.Results.Count);
            Assert.IsTrue(saved.Results.All(r => r.Attribution == FailureAttribution.Environment));
            saved.Results[0].Attribution = FailureAttribution.Model;
            string finalized = Newtonsoft.Json.JsonConvert.SerializeObject(saved.Results[0]);
            store.Save("alpha", saved);

            ProbeLlmClient healthy = new("bench-probe-alpha-", 0);
            RunTarget resumed = MakeRun("alpha", "model", healthy, new BenchmarkVisualLane(), null, Plain(null, "alpha", 2));
            EnableCheckpoint(resumed, store, store.Load("alpha"));
            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(resumed);
            Assert.AreEqual(1, healthy.Calls);
            Assert.AreEqual(2, resumed.Report.Results.Count);
            Assert.AreEqual(finalized, Newtonsoft.Json.JsonConvert.SerializeObject(resumed.Report.Results[0]));
            Assert.AreEqual(FailureAttribution.None, resumed.Report.Results[1].Attribution);
            Assert.IsEmpty(resumed.Report.Results[1].Failure);
            Assert.AreEqual(FailureAttribution.None, store.Load("alpha").Results[1].Attribution);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TypedTotalDeadline_OnLiveProviderStream_IsGradedWithoutRetry()
        {
            yield return RunTypedTimeoutScenario(LlmTimeoutKind.TotalDeadline, FailureAttribution.Model, 1);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TypedResponseWait_OnAcceptedProviderStream_IsEnvironmentAndRetried()
        {
            yield return RunTypedTimeoutScenario(LlmTimeoutKind.ResponseWait, FailureAttribution.Environment, 2);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TypedTransportTimeout_OnAcceptedProviderStream_IsEnvironmentAndRetried()
        {
            yield return RunTypedTimeoutScenario(LlmTimeoutKind.Transport, FailureAttribution.Environment, 2);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TypedResponseWait_AfterUsableTurn_IsEnvironmentAndNotReplayedInPlace()
        {
            // WHY: the continuation path needs a free-build scenario, which the suite never replays in place;
            // WHY: the Environment result is re-run by a resume instead, so one attempt is the contract here.
            yield return RunTypedTimeoutScenario(LlmTimeoutKind.ResponseWait, FailureAttribution.Environment, 1,
                afterUsableTurn: true);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator TypedTotalDeadline_AfterUsableTurn_IsGradedWithoutRetry()
        {
            yield return RunTypedTimeoutScenario(LlmTimeoutKind.TotalDeadline, FailureAttribution.Model, 1,
                afterUsableTurn: true);
        }

        private IEnumerator RunTypedTimeoutScenario(LlmTimeoutKind kind, FailureAttribution expected, int attempts,
            bool afterUsableTurn = false)
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "1");
            BenchmarkProgress.BeginSession();
            TypedTimeoutLlmClient client = new(kind, afterUsableTurn);
            RunTarget run = MakeRun("alpha", "model", client, new BenchmarkVisualLane(), null,
                new[] { new ProbeScenario(null, "alpha", 1, false, false,
                    timeoutSeconds: afterUsableTurn ? 120 : 20, freeBuild: afterUsableTurn) });

            yield return GameCreationBenchmarkPlayModeTests.RunTargetSuite(run);

            Assert.AreEqual(attempts, client.Calls, "Only Environment failures may replay a scenario.");
            Assert.AreEqual(afterUsableTurn ? attempts : 0, client.SuccessfulTurns);
            Assert.AreEqual(1, run.Report.Results.Count);
            ScenarioResult result = run.Report.Results[0];
            Assert.AreEqual(expected, result.Attribution, result.Failure);
            Assert.IsNotEmpty(result.Failure);
            Assert.AreEqual(expected == FailureAttribution.Model ? 1 : 0, run.Report.ScenarioCount,
                "An accepted-stream total deadline must remain in the graded aggregate.");
            Assert.AreEqual(expected == FailureAttribution.Environment ? 1 : 0, run.Report.EnvironmentFailures);
            if (expected == FailureAttribution.Model && afterUsableTurn)
            {
                Assert.AreEqual(1, result.ToolCalls);
                Assert.Greater(result.Score.Base, 0, "A completed usable turn must retain its work grade.");
            }
            else if (expected == FailureAttribution.Model)
            {
                Assert.AreEqual(0, result.ToolCalls);
                Assert.AreEqual(0, result.Score.Base, "A deadline without executed work earns zero points.");
                Assert.AreEqual(0, result.Score.HardCap);
                Assert.AreEqual(BenchmarkClassification.Fail, result.Score.Classification);
                Assert.AreEqual(0, run.Report.SuiteBaseScore);
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator ProviderIgnoringCancellation_OwnTimeoutFinishesAndRetriesAreBounded()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "1");
            HangingLlmClient broken = new();
            BenchmarkVisualLane lane = new();
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "model-a", broken, lane, null,
                new[] { new ProbeScenario(null, "alpha", 1, true, false, 0.05f) });
            RunTarget beta = MakeRun("beta", "model-b", new ProbeLlmClient("bench-probe-beta-", 10),
                lane, null, Plain(null, "beta", 1));
            alpha.ScenarioContext.CancellationGraceSeconds = 0.02;
            List<string> problems = new();
            try
            {
                yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);
                CollectionAssert.IsEmpty(problems);
                Assert.AreEqual(2, broken.Calls);
                Assert.IsTrue(broken.Token.IsCancellationRequested);
                Assert.AreEqual(1, alpha.Report.Results.Count);
                Assert.AreEqual(FailureAttribution.Environment, alpha.Report.Results[0].Attribution);
                StringAssert.Contains("timed out", alpha.Report.Results[0].Failure);
                AssertOwnResults(beta, 1);
                Assert.IsNull(lane.Holder);
                Assert.AreEqual(0, lane.QueueLength);
            }
            finally
            {
                broken.Release();
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator ProviderIgnoringCancellation_StopPreventsEveryRetryIncludingLastAttempt()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "0");
            HangingLlmClient broken = new();
            BenchmarkVisualLane lane = new();
            BenchmarkProgress.BeginSession();
            RunTarget run = MakeRun("alpha", "model-a", broken, lane, null, Plain(null, "alpha", 2));
            run.ScenarioContext.CancellationGraceSeconds = 0.02;
            broken.OnCall = () => run.Progress.RequestStop();
            List<string> problems = new();
            try
            {
                yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { run }, 0, problems);
                CollectionAssert.IsEmpty(problems);
                Assert.AreEqual(1, broken.Calls);
                Assert.IsTrue(broken.Token.IsCancellationRequested);
                Assert.AreEqual(0, run.Report.Results.Count, "A Stop-aborted final attempt is not scored.");
                Assert.IsTrue(run.BudgetHit);
                Assert.IsFalse(BenchmarkProgress.Runs()[0].IsActive);
                Assert.IsTrue(File.Exists(Path.Combine(_artifactDir, run.ArtifactStem + ".json")));
            }
            finally
            {
                broken.Release();
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator ProviderIgnoringCancellation_ElapsedSoftBudgetCancelsActiveAttempt()
        {
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_RETRIES", "3");
            HangingLlmClient broken = new();
            BenchmarkVisualLane lane = new();
            BenchmarkProgress.BeginSession();
            RunTarget run = MakeRun("alpha", "model-a", broken, lane, null, Plain(null, "alpha", 2));
            run.ScenarioContext.CancellationGraceSeconds = 0.02;
            broken.OnCall = () => run.SuiteBudgetSeconds = run.SuiteClock.Elapsed.TotalSeconds;
            List<string> problems = new();
            try
            {
                yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { run }, 0, problems);
                CollectionAssert.IsEmpty(problems);
                Assert.AreEqual(1, broken.Calls, "The elapsed budget prevents remaining retries.");
                Assert.IsTrue(broken.Token.IsCancellationRequested);
                Assert.IsTrue(run.BudgetHit);
                Assert.AreEqual(1, run.Report.Results.Count);
                Assert.IsNull(lane.Holder);
                Assert.IsFalse(BenchmarkProgress.Runs()[0].IsActive);
            }
            finally
            {
                broken.Release();
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator SharedSceneScenarios_TakeTurnsWhileOtherWorkContinues()
        {
            ProbeLog log = new() { Lane = new BenchmarkVisualLane() };
            ProbeLlmClient alphaClient = new("bench-probe-alpha-", 300);
            ProbeLlmClient betaClient = new("bench-probe-beta-", 300);
            BenchmarkProgress.BeginSession();
            List<GameBenchmarkScenario> alphaScenarios = new() { new ProbeScenario(log, "alpha", 0, true, true) };
            alphaScenarios.AddRange(Plain(log, "alpha", 2));
            List<GameBenchmarkScenario> betaScenarios = new() { new ProbeScenario(log, "beta", 0, true, true) };
            betaScenarios.AddRange(Plain(log, "beta", 2));
            RunTarget alpha = MakeRun("alpha", "model-a", alphaClient, log.Lane, log, alphaScenarios);
            RunTarget beta = MakeRun("beta", "model-b", betaClient, log.Lane, log, betaScenarios);
            log.Runs["alpha"] = alpha;
            log.Runs["beta"] = beta;
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);

            CollectionAssert.IsEmpty(problems);
            CollectionAssert.IsEmpty(log.Violations);
            Assert.AreEqual(3, alpha.Report.Results.Count);
            Assert.AreEqual(3, beta.Report.Results.Count);

            ProbeInterval alphaShared = log.Intervals.Find(i => i.Owner == "alpha" && i.Shared);
            ProbeInterval betaShared = log.Intervals.Find(i => i.Owner == "beta" && i.Shared);
            Assert.IsFalse(alphaShared.Overlaps(betaShared), "Shared-scene scenarios never run at the same time.");
            ProbeInterval first = alphaShared.Start < betaShared.Start ? alphaShared : betaShared;
            string other = first.Owner == "alpha" ? "beta" : "alpha";
            Assert.IsTrue(log.Intervals.Exists(i => i.Owner == other && !i.Shared && i.Overlaps(first)),
                "While one target holds the visual lane, the other keeps running its other scenarios.");

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.AreEqual(2, log.VisualChecks, "Both visual probes inspected the scene.");
                Assert.IsNotNull(alpha.Report.Results.Find(r => r.ScenarioId == "probe-alpha-0").SceneScreenshotPng);
                Assert.IsNotNull(beta.Report.Results.Find(r => r.ScenarioId == "probe-beta-0").SceneScreenshotPng);
            }
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator StoppingOneTarget_EndsOnlyThatRun()
        {
            ProbeLog log = new();
            BenchmarkVisualLane lane = new();
            ProbeLlmClient alphaClient = new("bench-probe-alpha-", 150);
            ProbeLlmClient betaClient = new("bench-probe-beta-", 150);
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "model-a", alphaClient, lane, log, Plain(log, "alpha", 3));
            RunTarget beta = MakeRun("beta", "model-b", betaClient, lane, log, Plain(log, "beta", 3));
            // WHY: request Stop after grading so this test covers stopping between completed scenarios.
            // WHY: active-provider cancellation is covered separately without racing async continuations.
            log.AfterGrade = probe =>
            {
                if (probe.Owner == "alpha")
                {
                    alpha.Progress.RequestStop();
                }
            };
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);

            CollectionAssert.IsEmpty(problems);
            Assert.IsTrue(alpha.BudgetHit);
            Assert.AreEqual(1, alpha.Report.Results.Count, "The stopped target ends after its current scenario.");
            Assert.IsFalse(beta.BudgetHit);
            Assert.AreEqual(3, beta.Report.Results.Count);
            Assert.IsTrue(File.Exists(Path.Combine(_artifactDir, alpha.ArtifactStem + ".json")),
                "A stopped target still writes its partial report.");
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator BudgetGate_StopsEveryTargetBeforeAScenarioCanOverrun()
        {
            ProbeLog log = new();
            BenchmarkVisualLane lane = new();
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "model-a", new ProbeLlmClient("bench-probe-alpha-", 10), lane, log,
                Plain(log, "alpha", 2));
            RunTarget beta = MakeRun("beta", "model-b", new ProbeLlmClient("bench-probe-beta-", 10), lane, log,
                Plain(log, "beta", 2));
            alpha.SuiteBudgetSeconds = 1;
            beta.SuiteBudgetSeconds = 1;
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 0, problems);

            Assert.IsTrue(alpha.BudgetHit);
            Assert.IsTrue(beta.BudgetHit);
            Assert.AreEqual(0, alpha.Report.Results.Count + beta.Report.Results.Count);
            Assert.AreEqual(2, problems.Count, "A target that measured nothing is reported, not silently passed.");
            CollectionAssert.IsEmpty(log.Intervals);
        }

        [UnityTest, Timeout(15000)]
        public IEnumerator ParallelCapOfOne_RunsTargetsOneAfterAnother()
        {
            ProbeLog log = new();
            BenchmarkVisualLane lane = new();
            BenchmarkProgress.BeginSession();
            RunTarget alpha = MakeRun("alpha", "model-a", new ProbeLlmClient("bench-probe-alpha-", 100), lane, log,
                Plain(log, "alpha", 2));
            RunTarget beta = MakeRun("beta", "model-b", new ProbeLlmClient("bench-probe-beta-", 100), lane, log,
                Plain(log, "beta", 2));
            List<string> problems = new();

            yield return GameCreationBenchmarkPlayModeTests.RunTargets(new[] { alpha, beta }, 1, problems);

            CollectionAssert.IsEmpty(problems);
            Assert.IsFalse(log.AnyOverlap("alpha", "beta"));
            Assert.AreEqual(2, alpha.Report.Results.Count);
            Assert.AreEqual(2, beta.Report.Results.Count);
        }

        [Test]
        public void SuiteBudget_DefaultsAndClampKeepTheReportMarginBeforeTheHardAbort()
        {
            string previous = Environment.GetEnvironmentVariable(GameCreationBenchmarkPlayModeTests.EnvSuiteBudget);
            try
            {
                double nunit = GameCreationBenchmarkPlayModeTests.NUnitTimeoutMs / 1000d;
                Environment.SetEnvironmentVariable(GameCreationBenchmarkPlayModeTests.EnvSuiteBudget, null);
                Assert.AreEqual(6000, GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds(1));
                Assert.AreEqual(nunit - GameCreationBenchmarkPlayModeTests.ReportMarginFor(9),
                    GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds(9));

                Environment.SetEnvironmentVariable(GameCreationBenchmarkPlayModeTests.EnvSuiteBudget, "999999");
                for (int targets = 1; targets <= 9; targets++)
                {
                    double budget = GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds(targets);
                    Assert.LessOrEqual(budget + GameCreationBenchmarkPlayModeTests.ReportMarginFor(targets), nunit);
                }

                Environment.SetEnvironmentVariable(GameCreationBenchmarkPlayModeTests.EnvSuiteBudget, "14400");
                Assert.AreEqual(14400, GameCreationBenchmarkPlayModeTests.ResolveSuiteBudgetSeconds(9));
                Assert.GreaterOrEqual(nunit, 6 * 3600, "The NUnit backstop must fit a six-hour multi-target run.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(GameCreationBenchmarkPlayModeTests.EnvSuiteBudget, previous);
            }
        }

        [Test]
        public void ArtifactStem_AddsADistinctLabelAndKeepsTheSingleModelName()
        {
            Assert.AreEqual("BENCHMARK_20261015_101500_qwen3.5-4b",
                GameCreationBenchmarkPlayModeTests.ArtifactStem("20261015_101500", "qwen3.5-4b"));
            Assert.AreEqual("BENCHMARK_20261015_101500_qwen3.5-4b",
                GameCreationBenchmarkPlayModeTests.ArtifactStem("20261015_101500", "qwen3.5-4b", "qwen3.5-4b"));
            Assert.AreEqual("BENCHMARK_20261015_101500_vendor-model__q8-bridge",
                GameCreationBenchmarkPlayModeTests.ArtifactStem("20261015_101500", "vendor/model", "q8 bridge"));
        }

        [Test]
        public void ReasoningEffort_ConfigAndTargetListAgreeOnNamesAndLevels()
        {
            CollectionAssert.AreEqual(BenchmarkTargetList.ReasoningEfforts, PlayModeOpenAiTestConfig.ReasoningEfforts);
            Assert.AreEqual(BenchmarkTargetList.EnvReasoningEffort, PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort);
            Assert.AreEqual(BenchmarkTargetList.ReasoningEffortBodyKey, PlayModeOpenAiTestConfig.ReasoningEffortBodyKey);
        }

        [Test]
        public void ComposeProviderBody_OwnEffortBeatsOwnBodyBeatsDefaultBeatsSharedBody()
        {
            const string shared = "{\"reasoning_effort\":\"low\",\"top_k\":5}";
            const string own = "{\"reasoning_effort\":\"medium\",\"top_k\":20}";

            Newtonsoft.Json.Linq.JObject all = Newtonsoft.Json.Linq.JObject.Parse(
                PlayModeOpenAiTestConfig.ComposeProviderBody(shared, own, "HIGH", "none"));
            Assert.AreEqual("high", (string)all["reasoning_effort"]);
            Assert.AreEqual(20, (int)all["top_k"]);

            Assert.AreEqual("medium", Effort(PlayModeOpenAiTestConfig.ComposeProviderBody(shared, own, "", "none")));
            Assert.AreEqual("none", Effort(PlayModeOpenAiTestConfig.ComposeProviderBody(shared, "", null, "none")));
            Assert.AreEqual("low", Effort(PlayModeOpenAiTestConfig.ComposeProviderBody(shared, null, null, "")));
            Assert.AreEqual("", PlayModeOpenAiTestConfig.ComposeProviderBody(null, null, null, null));
            Assert.AreEqual("{\"reasoning_effort\":\"xhigh\"}",
                PlayModeOpenAiTestConfig.ComposeProviderBody("", "", "xhigh", ""));
        }

        [TestCase("{\"messages\":[]}", "")]
        [TestCase("{\"top_k\":", "")]
        [TestCase("[1,2]", "")]
        [TestCase("{\"session_id\":\"student-42\"}", "")]
        [TestCase("", "max")]
        public void ComposeProviderBody_RejectsUnsafeBodiesAndUnknownEfforts(string body, string effort)
        {
            Assert.Throws<ArgumentException>(() =>
                PlayModeOpenAiTestConfig.ComposeProviderBody("", body, effort, ""));
        }

        [Test]
        public void ResolveForEndpoint_MergesTargetBodyAndEffortOverTheSharedBodyOfTheSameEndpoint()
        {
            string[] names =
            {
                PlayModeOpenAiTestConfig.EnvBaseUrl, PlayModeOpenAiTestConfig.EnvModel,
                PlayModeOpenAiTestConfig.EnvExtraBodyJson, PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort
            };
            Dictionary<string, string> saved = new();
            foreach (string name in names)
            {
                saved[name] = Environment.GetEnvironmentVariable(name);
            }

            try
            {
                Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBaseUrl, "http://127.0.0.1:65530/v1");
                Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvModel, "shared-model");
                Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvExtraBodyJson, "{\"top_k\":5}");

                PlayModeOpenAiTestConfig.ResolvedConfig same = PlayModeOpenAiTestConfig.ResolveForEndpoint(
                    "http://127.0.0.1:65530/v1/", "m", "", "{\"min_p\":0.1}", "high", "low");
                Newtonsoft.Json.Linq.JObject body = Newtonsoft.Json.Linq.JObject.Parse(same.ExtraBodyJson);
                Assert.AreEqual(5, (int)body["top_k"]);
                Assert.AreEqual(0.1, (double)body["min_p"], 1e-9);
                Assert.AreEqual("high", same.ReasoningEffort);

                PlayModeOpenAiTestConfig.ResolvedConfig other = PlayModeOpenAiTestConfig.ResolveForEndpoint(
                    "http://127.0.0.1:65531/v1", "m", "", null, null, "low");
                Assert.AreEqual("{\"reasoning_effort\":\"low\"}", other.ExtraBodyJson,
                    "Another endpoint never receives the shared body, only the suite default.");
                Assert.AreEqual("low", other.ReasoningEffort);

                Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort, " XHigh ");
                string effort = PlayModeOpenAiTestConfig.ResolveBenchmarkReasoningEffortDefault();
                Assert.AreEqual("xhigh", effort);
                PlayModeOpenAiTestConfig.ResolvedConfig single = PlayModeOpenAiTestConfig.WithReasoningEffortDefault(
                    PlayModeOpenAiTestConfig.Resolve(), effort);
                Assert.AreEqual("xhigh", single.ReasoningEffort, "The single-model run gets the suite default too.");
                StringAssert.Contains("\"top_k\":5", single.ExtraBodyJson);

                Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort, "turbo");
                ArgumentException invalid = Assert.Throws<ArgumentException>(() =>
                    PlayModeOpenAiTestConfig.ResolveBenchmarkReasoningEffortDefault());
                StringAssert.Contains(PlayModeOpenAiTestConfig.EnvBenchmarkReasoningEffort, invalid.Message);
            }
            finally
            {
                foreach (KeyValuePair<string, string> entry in saved)
                {
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }
            }
        }

        [Test]
        public void RunMetadata_RecordsTheReasoningEffortInTheReport()
        {
            CoreAISettingsAsset settings = GameCreationBenchmarkPlayModeTests.CreateBenchmarkSettings();
            _ownedAssets.Add(settings);
            BenchmarkReport report = new()
            {
                Metadata = GameCreationBenchmarkPlayModeTests.CreateRunMetadata("20261015_101500", "m", "Scripted",
                    true, false, settings, 1, "high")
            };

            Assert.AreEqual("high", report.Metadata.ReasoningEffort);
            StringAssert.Contains("\"reasoningEffort\":\"high\"", BenchmarkReportFormatter.ToJson(report));
            Assert.AreEqual("", GameCreationBenchmarkPlayModeTests.CreateRunMetadata("r", "m", "b", true, false,
                settings, 1).ReasoningEffort);
        }

#if UNITY_EDITOR
        [Test]
        public async Task BenchmarkClient_TargetSamplingReachesWireUnchanged(
            [Values("unset", "typed", "raw")] string temperatureMode, [Values(false, true)] bool streaming)
        {
            string extraBody = "{\"top_p\":0.95,\"top_k\":100,\"min_p\":0,\"presence_penalty\":0," +
                               (temperatureMode == "raw" ? "\"temperature\":0.7," : "") +
                               "\"repeat_penalty\":1.03,\"chat_template_kwargs\":{\"enable_thinking\":false}}";
            string spec = "[{\"model\":\"MiniCPM-V-4_7\",\"baseUrl\":\"http://offline.invalid/v1\"," +
                          (temperatureMode == "typed" ? "\"temperature\":0.7," : "") +
                          "\"extraBody\":" + extraBody + "}]";
            float? expectedTemperature = temperatureMode == "unset" ? null : 0.7f;
            BenchmarkTarget target = BenchmarkTargetList.Parse(spec)[0];
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvExtraBodyJson, "{}");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvStreaming, streaming ? "true" : "false");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvNativeTools, "true");
            SamplingCaptureHandler capture = new();
            Func<HttpClient> previous = MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory;
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(capture, false);
            try
            {
                Assert.IsTrue(PlayModeProductionLikeLlmFactory.TryCreateForEndpoint(target.BaseUrl, target.Model,
                    "", target.Temperature, 5, out PlayModeProductionLikeLlmHandle handle, out string reason,
                    target.ExtraBodyJson), reason);
                using (handle)
                {
                    Assert.AreEqual(target.Temperature, handle.ResolvedConfig.Temperature);
                    Assert.AreEqual(expectedTemperature, handle.Temperature);
                    await CompleteSamplingRequest(handle, streaming);
                    Newtonsoft.Json.Linq.JObject body = capture.Body;
                    Assert.IsNotNull(body);
                    Assert.AreEqual(streaming, (bool?)body["stream"] == true);
                    Assert.AreEqual(target.Model, (string)body["model"]);
                    if (expectedTemperature.HasValue)
                    {
                        Assert.That((double)body["temperature"], Is.EqualTo(0.7).Within(0.000001));
                    }
                    else
                    {
                        Assert.IsNull(body["temperature"], "An unset target must use the provider/model default.");
                    }

                    foreach (Newtonsoft.Json.Linq.JProperty property in
                             Newtonsoft.Json.Linq.JObject.Parse(extraBody).Properties())
                    {
                        Assert.IsTrue(Newtonsoft.Json.Linq.JToken.DeepEquals(property.Value, body[property.Name]),
                            property.Name + " must reach the wire unchanged when no typed override is configured.");
                    }

                    CoreAISettingsAsset settings = GameCreationBenchmarkPlayModeTests.CreateBenchmarkSettings();
                    _ownedAssets.Add(settings);
                    BenchmarkRunMetadata metadata = GameCreationBenchmarkPlayModeTests.CreateRunMetadata("r", "m",
                        "HTTP", true, streaming, settings, 1,
                        temperature: handle.Temperature);
                    Assert.AreEqual(expectedTemperature, metadata.Temperature);
                }
            }
            finally
            {
                MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = previous;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task BenchmarkClient_ClassicRunOmitsTemperature(bool streaming)
        {
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBaseUrl, "http://offline.invalid/v1");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvModel, "benchmark");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvExtraBodyJson, "{}");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvStreaming, streaming ? "true" : "false");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvNativeTools, "true");
            SamplingCaptureHandler capture = new();
            Func<HttpClient> previous = MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory;
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(capture, false);
            try
            {
                Assert.IsTrue(PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp, null, 5,
                    out PlayModeProductionLikeLlmHandle handle, out string reason), reason);
                using (handle)
                {
                    await CompleteSamplingRequest(handle, streaming);
                    Assert.AreEqual(streaming, (bool?)capture.Body["stream"] == true);
                    Assert.IsNull(handle.Temperature);
                    Assert.IsNull(capture.Body["temperature"]);
                }
            }
            finally
            {
                MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = previous;
            }
        }

        private static async Task CompleteSamplingRequest(PlayModeProductionLikeLlmHandle handle, bool streaming)
        {
            LlmCompletionRequest request = new() { UserPayload = "hello" };
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in handle.Client.CompleteStreamingAsync(request))
                {
                    Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
                }
            }
            else
            {
                LlmCompletionResult result = await handle.Client.CompleteAsync(request);
                Assert.IsTrue(result.Ok, result.Error);
            }
        }

        private sealed class SamplingCaptureHandler : HttpMessageHandler
        {
            public Newtonsoft.Json.Linq.JObject Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Body = Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync());
                bool streaming = (bool?)Body["stream"] == true;
                string response = streaming
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
                };
            }
        }
#endif

        private static string Effort(string body)
        {
            return (string)Newtonsoft.Json.Linq.JObject.Parse(body)["reasoning_effort"];
        }

        private RunTarget MakeRun(string owner, string model, ILlmClient client, BenchmarkVisualLane lane,
            ProbeLog log, IEnumerable<GameBenchmarkScenario> scenarios)
        {
            CoreAISettingsAsset settings = GameCreationBenchmarkPlayModeTests.CreateBenchmarkSettings();
            _ownedAssets.Add(settings);
            List<GameBenchmarkScenario> list = new(scenarios);
            string prefix = $"[Benchmark][{owner}]";
            return new RunTarget
            {
                DisplayName = owner,
                TokenizerModelId = model,
                ArtifactStem = GameCreationBenchmarkPlayModeTests.ArtifactStem("20261015_101500", model, owner),
                ArtifactDirectory = _artifactDir,
                Client = client,
                Settings = settings,
                TokenCounter = new BpeTokenCounter(),
                Report = new BenchmarkReport
                {
                    Metadata = GameCreationBenchmarkPlayModeTests.CreateRunMetadata("20261015_101500", owner,
                        "Scripted", true, false, settings, 1)
                },
                Progress = BenchmarkProgress.BeginRun(owner, list.Count, true),
                Scenarios = list,
                Repetitions = 1,
                SuiteBudgetSeconds = 3600,
                SuiteClock = Stopwatch.StartNew(),
                VisualLane = lane,
                SharedSceneFirst = true,
                LogPrefix = prefix,
                ScenarioContext = new ScenarioRunContext
                {
                    TokenizerModelId = model,
                    LogPrefix = prefix,
                    RequireTraceId = true,
                    Parallel = true
                }
            };
        }

        private static void AssertOwnResults(RunTarget run, int expected)
        {
            Assert.AreEqual(expected, run.Report.Results.Count, run.DisplayName);
            foreach (ScenarioResult result in run.Report.Results)
            {
                Assert.AreEqual(run.DisplayName, result.ModelId);
                StringAssert.StartsWith($"probe-{run.DisplayName}-", result.ScenarioId);
                Assert.IsTrue(string.IsNullOrEmpty(result.Failure), result.Failure);
            }
        }

        private static List<GameBenchmarkScenario> Plain(ProbeLog log, string owner, int count)
        {
            List<GameBenchmarkScenario> list = new();
            for (int i = 1; i <= count; i++)
            {
                list.Add(new ProbeScenario(log, owner, i, false, false));
            }

            return list;
        }

        private static List<GameBenchmarkScenario> ResumeProbes(ProbeLog log)
        {
            return new List<GameBenchmarkScenario>
            {
                new ProbeScenario(log, "alpha", 1, false, false, repsOverride: null),
                new ProbeScenario(log, "alpha", 2, false, false, repsOverride: null, extreme: true),
                new ProbeScenario(log, "alpha", 3, true, false, group: "G6")
            };
        }

        private static string ResultIdentity(ScenarioResult result) => result.ScenarioId + "/" + result.Repetition;

        private static void EnableCheckpoint(RunTarget run, BenchmarkRunStore store, BenchmarkTargetState state = null)
        {
            run.Repetitions = 2;
            run.Report.Metadata.Repetitions = 2;
            run.Plan = run.Scenarios.SelectMany(scenario => Enumerable.Range(1, scenario.RepsOverride ?? run.Repetitions)
                .Select(repetition => new BenchmarkPlannedScenario
                {
                    Id = scenario.Id, Group = scenario.Group, Extreme = scenario.IsExtreme, Repetition = repetition
                })).ToList();
            run.CheckpointStore = store;
            run.CheckpointSlug = "alpha";
            run.Checkpoint = state ?? new BenchmarkTargetState { Metadata = run.Report.Metadata };
            run.Checkpoint.Segments.Add(new BenchmarkRunSegment { StartedUtc = DateTime.UtcNow.ToString("o") });
            run.Report = run.Checkpoint.ToReport();
            run.Progress = BenchmarkProgress.BeginRun(run.DisplayName, run.Plan.Count, true);
            store.Save(run.CheckpointSlug, run.Checkpoint);
        }

        private static IEnumerator Outer(List<string> trace)
        {
            trace.Add("outer-start");
            yield return Inner(trace);
            trace.Add("outer-end");
            yield return new WaitForEndOfFrame();
        }

        private static IEnumerator Inner(List<string> trace)
        {
            trace.Add("inner-1");
            yield return null;
            trace.Add("inner-2");
        }

        private static IEnumerator Guarded(Action onFinally, bool throwInside = true)
        {
            try
            {
                yield return Throwing(throwInside);
            }
            finally
            {
                onFinally();
            }
        }

        private static IEnumerator Throwing(bool throwInside)
        {
            yield return null;
            if (throwInside)
            {
                throw new InvalidOperationException("boom");
            }

            yield return null;
        }

        private static IEnumerator RecordFrames(List<int> log, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                log.Add(Time.frameCount);
                if (i < steps - 1)
                {
                    yield return null;
                }
            }
        }

        private static IEnumerator ThrowAfterFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
            }

            throw new InvalidOperationException("scripted fault");
        }

        private static IEnumerator EndOfFrameProbe(Action<int> before, Action<int> after)
        {
            yield return null;
            before(Time.frameCount);
            yield return new WaitForEndOfFrame();
            after(Time.frameCount);
        }

        private static IEnumerator NestedWithoutFrameYield()
        {
            while (true)
            {
                yield return EmptyRoutine();
            }
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }

        private static IEnumerator WaitForever()
        {
            yield return new WaitUntil(() => false);
        }

        private static IEnumerator InvalidWait(bool unsupportedDelay)
        {
            yield return unsupportedDelay
                ? (object)new WaitForSeconds(100)
                : new WaitUntil(() => throw new InvalidOperationException("scripted readiness fault"));
        }

        /// <summary>A provider that ignores cancellation until explicitly released by test cleanup.</summary>
        private sealed class HangingLlmClient : ILlmClient
        {
            private readonly TaskCompletionSource<LlmCompletionResult> _pending = new();
            public Action OnCall;
            public int Calls;
            public CancellationToken Token;

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                Token = cancellationToken;
                OnCall?.Invoke();
                return _pending.Task;
            }

            public void Release()
            {
                _pending.TrySetResult(new LlmCompletionResult { Ok = true, Content = "done" });
            }
        }

        /// <summary>Produces an accepted-stream delta and a typed deadline without network calls or delays.</summary>
        private sealed class TypedTimeoutLlmClient : ILlmClient
        {
            private readonly LlmTimeoutKind _kind;
            private readonly bool _afterUsableTurn;
            private bool _nextTurnIsUsable;
            public int SuccessfulTurns { get; private set; }
            public int Calls { get; private set; }

            public TypedTimeoutLlmClient(LlmTimeoutKind kind, bool afterUsableTurn = false)
            {
                _kind = kind;
                _afterUsableTurn = afterUsableTurn;
                _nextTurnIsUsable = afterUsableTurn;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (TryUsableTurn())
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = true, Content = "Built one part", ExecutedToolCalls = CompletedTools()
                    });
                return Task.FromException<LlmCompletionResult>(Failure(request));
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                request.ProviderResponseState.RecordResponse();
                if (TryUsableTurn())
                {
                    yield return new LlmStreamChunk
                    {
                        Text = "Built one part", IsDone = true, ExecutedToolCalls = CompletedTools()
                    };
                    yield break;
                }
                yield return new LlmStreamChunk { ReasoningText = "Still working", GenerationInProgress = true };
                throw Failure(request);
            }

            private bool TryUsableTurn()
            {
                if (!_nextTurnIsUsable) return false;
                _nextTurnIsUsable = false;
                SuccessfulTurns++;
                return true;
            }

            private static LlmToolCallTrace[] CompletedTools() => new[]
            {
                new LlmToolCallTrace("execute_lua", true, 1, "native", "Built one part")
            };

            private LlmClientException Failure(LlmCompletionRequest request)
            {
                Calls++;
                _nextTurnIsUsable = _afterUsableTurn;
                request.ProviderResponseState.RecordResponse();
                request.ProviderResponseState.RecordTimeout(_kind);
                return new LlmClientException("Opaque scripted failure", LlmErrorCode.Timeout, timeoutKind: _kind);
            }
        }

        /// <summary>Scripted provider: waits, answers "done" (or fails), and flags requests of another target.</summary>
        private sealed class ProbeLlmClient : ILlmClient
        {
            private readonly string _tracePrefix;
            private readonly int _delayMs;
            private readonly string _failure;
            private int _calls;
            private int _foreignCalls;

            public ProbeLlmClient(string tracePrefix, int delayMs, string failure = null)
            {
                _tracePrefix = tracePrefix;
                _delayMs = delayMs;
                _failure = failure;
            }

            public int Calls => Volatile.Read(ref _calls);

            public int ForeignCalls => Volatile.Read(ref _foreignCalls);

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _calls);
                if (!(request?.TraceId ?? "").StartsWith(_tracePrefix, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _foreignCalls);
                }

                return _failure != null
                    ? new LlmCompletionResult { Ok = false, Error = _failure }
                    : new LlmCompletionResult { Ok = true, Content = "done", PromptTokens = 40, CompletionTokens = 2 };
            }
        }

        private readonly struct ProbeInterval
        {
            public ProbeInterval(string owner, bool shared, long start, long end)
            {
                Owner = owner;
                Shared = shared;
                Start = start;
                End = end;
            }

            public string Owner { get; }
            public bool Shared { get; }
            public long Start { get; }
            public long End { get; }

            public bool Overlaps(ProbeInterval other)
            {
                return Start < other.End && other.Start < End;
            }
        }

        /// <summary>Records when each probe ran and what it could see of the shared scene.</summary>
        private sealed class ProbeLog
        {
            private readonly Dictionary<ProbeScenario, long> _starts = new();
            public readonly List<ProbeInterval> Intervals = new();
            public readonly List<string> Violations = new();
            public readonly Dictionary<string, RunTarget> Runs = new();
            public BenchmarkVisualLane Lane;
            public int VisualChecks;
            public Action<ProbeScenario> AfterGrade;

            public void Begin(ProbeScenario probe, BenchmarkEnvironment env)
            {
                _starts[probe] = Stopwatch.GetTimestamp();
                CheckLane(probe, "start");
                if (env.World is VisualBenchmarkWorldExecutor visual)
                {
                    GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = $"probe-cube-{probe.Owner}";
                    cube.transform.SetParent(visual.Root, false);
                    CheckScene(probe);
                }
            }

            public void End(ProbeScenario probe, BenchmarkEnvironment env)
            {
                Intervals.Add(new ProbeInterval(probe.Owner, probe.UsesSharedScene, _starts[probe],
                    Stopwatch.GetTimestamp()));
                CheckLane(probe, "grade");
                if (env.World is VisualBenchmarkWorldExecutor)
                {
                    CheckScene(probe);
                    VisualChecks++;
                }

                AfterGrade?.Invoke(probe);
            }

            public bool AnyOverlap(string a, string b)
            {
                foreach (ProbeInterval first in Intervals)
                {
                    if (first.Owner != a)
                    {
                        continue;
                    }

                    foreach (ProbeInterval second in Intervals)
                    {
                        if (second.Owner == b && first.Overlaps(second))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private void CheckLane(ProbeScenario probe, string phase)
            {
                if (Lane != null && probe.UsesSharedScene &&
                    (!Runs.TryGetValue(probe.Owner, out RunTarget run) || !ReferenceEquals(Lane.Holder, run)))
                {
                    Violations.Add($"{probe.Id} {phase}: ran without holding the visual lane");
                }
            }

            private void CheckScene(ProbeScenario probe)
            {
                foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                {
                    if (t.name.StartsWith("probe-cube-", StringComparison.Ordinal) &&
                        t.name != $"probe-cube-{probe.Owner}")
                    {
                        Violations.Add($"{probe.Id}: another target's object '{t.name}' is in the scene");
                    }
                }

                int agentCameras = UnityEngine.Object.FindObjectsByType<CoreAiAgentCamera>(FindObjectsSortMode.None).Length;
                if (agentCameras != 1)
                {
                    Violations.Add($"{probe.Id}: {agentCameras} agent cameras exist, camera_capture could pick another target's");
                }
            }
        }

        /// <summary>A minimal scenario: answer once; records its interval and, when shared, its lane ownership.</summary>
        private sealed class ProbeScenario : GameBenchmarkScenario
        {
            private readonly ProbeLog _log;
            private readonly int _index;
            private readonly bool _shared;
            private readonly bool _visual;
            private readonly float _timeoutSeconds;
            private readonly int? _repsOverride;
            private readonly bool _extreme;
            private readonly string _group;
            private readonly bool _freeBuild;

            public ProbeScenario(ProbeLog log, string owner, int index, bool shared, bool visual,
                float timeoutSeconds = 20f, int? repsOverride = 1, bool extreme = false, string group = "G2",
                bool freeBuild = false)
            {
                _log = log;
                Owner = owner;
                _index = index;
                _shared = shared;
                _visual = visual;
                _timeoutSeconds = timeoutSeconds;
                _repsOverride = repsOverride;
                _extreme = extreme;
                _group = group;
                _freeBuild = freeBuild;
            }

            public string Owner { get; }
            public override string Id => $"probe-{Owner}-{_index}";
            public override string Name => $"Probe {Owner} {_index}";
            public override string Group => _group;
            public override string Goal => "Reply with the single word done.";
            public override float TimeoutSeconds => _timeoutSeconds;
            public override int? RepsOverride => _repsOverride;
            public override bool IsExtreme => _extreme;
            public override bool FreeBuildLayout => _freeBuild;
            public override bool CaptureScene => _visual;
            public override bool UsesSharedScene => _shared || _visual;

            public override void Prepare(BenchmarkEnvironment env)
            {
                _log?.Begin(this, env);
            }

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.LuaTool())
                    .BuildDetached();
            }

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                _log?.End(this, env);
                ScenarioGrading grading = new();
                grading.Add("answered", "the model answered", 10, run.Turns >= 1);
                return grading;
            }
        }
    }
}
#endif
#endif
