using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scheduling
{
    /// <summary>Deterministic C# conformance tests for the engine-free MVP2 scheduler core.</summary>
    [TestFixture]
    public sealed class ModSchedulerEditModeTests
    {
        private sealed class FakeTimeSource : IRbxTimeSource
        {
            public double CurrentTime { get; private set; }

            public void Advance(double deltaSeconds)
            {
                CurrentTime += deltaSeconds;
            }
        }

        private sealed class FakeThreadPlan
        {
            public FakeThreadPlan(Action<FakeScriptThread, object[]> onResume = null,
                bool completeOnResume = true, RbxError failure = null)
            {
                OnResume = onResume;
                CompleteOnResume = completeOnResume;
                Failure = failure;
            }

            public Action<FakeScriptThread, object[]> OnResume { get; }

            public bool CompleteOnResume { get; }

            public RbxError Failure { get; }
        }

        private sealed class FakeScriptThread : IRbxScriptThread
        {
            private readonly FakeThreadPlan _plan;

            public FakeScriptThread(string ownerModId, FakeThreadPlan plan)
            {
                OwnerModId = ownerModId;
                _plan = plan;
                Status = RbxScriptThreadStatus.Suspended;
            }

            public string OwnerModId { get; }

            public RbxScriptThreadStatus Status { get; private set; }

            public bool IsDead => Status == RbxScriptThreadStatus.Dead;

            public int ResumeCount { get; private set; }

            public int KillCount { get; private set; }

            public List<object[]> ResumeArguments { get; } = new();

            public RbxScriptThreadResumeResult Resume(params object[] args)
            {
                if (IsDead)
                {
                    throw new InvalidOperationException("dead fake thread resumed");
                }

                Status = RbxScriptThreadStatus.Running;
                ResumeCount++;
                object[] captured = args == null ? Array.Empty<object>() : (object[])args.Clone();
                ResumeArguments.Add(captured);
                _plan.OnResume?.Invoke(this, captured);

                if (_plan.Failure != null)
                {
                    Status = RbxScriptThreadStatus.Suspended;
                    return RbxScriptThreadResumeResult.Failure(_plan.Failure);
                }

                if (!IsDead)
                {
                    Status = _plan.CompleteOnResume
                        ? RbxScriptThreadStatus.Dead
                        : RbxScriptThreadStatus.Suspended;
                }

                return RbxScriptThreadResumeResult.Success();
            }

            public void Kill()
            {
                if (IsDead)
                {
                    return;
                }

                KillCount++;
                Status = RbxScriptThreadStatus.Dead;
            }
        }

        private sealed class FakeThreadFactory : IRbxScriptThreadFactory
        {
            public List<FakeScriptThread> Created { get; } = new();

            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                FakeThreadPlan plan = callable as FakeThreadPlan;
                if (plan == null)
                {
                    throw new InvalidOperationException("test callable must be a FakeThreadPlan");
                }

                FakeScriptThread thread = new(ownerModId, plan);
                Created.Add(thread);
                return thread;
            }
        }

        [Test]
        public void R4_8_TaskWaitWithoutDurationResumesNextFrameAndReturnsElapsed()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeThreadPlan plan = new(completeOnResume: false);
            FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a", plan,
                Array.Empty<object>());

            scheduler.ScheduleWait(thread);
            Assert.AreEqual(1, thread.ResumeCount);

            scheduler.Advance(0.125d);

            Assert.AreEqual(2, thread.ResumeCount);
            Assert.AreEqual(0.125d, (double)thread.ResumeArguments[1][0], 0.000001d);
            Assert.AreEqual(0.125d, timeSource.CurrentTime, 0.000001d);
            Assert.AreEqual(1, factory.Created.Count);
        }

        [Test]
        public void R4_8_TaskWaitDurationResumesWithinOneFrameDelta()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleWait(thread, 0.5d);

            scheduler.Advance(0.2d);
            scheduler.Advance(0.2d);
            Assert.AreEqual(1, thread.ResumeCount);

            scheduler.Advance(0.2d);

            double elapsed = (double)thread.ResumeArguments[1][0];
            Assert.GreaterOrEqual(elapsed, 0.5d);
            Assert.LessOrEqual(elapsed, 0.7d);
            Assert.AreEqual(0.6d, timeSource.CurrentTime, 0.000001d);
            Assert.AreEqual(1, factory.Created.Count);
        }

        [Test]
        public void R4_8_TaskWaitCanBeScheduledByTheCurrentlyRunningThread()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeThreadPlan plan = new((FakeScriptThread thread, object[] args) =>
            {
                if (thread.ResumeCount == 1)
                {
                    scheduler.ScheduleWait(thread);
                }
            }, false);

            FakeScriptThread caller = (FakeScriptThread)scheduler.Spawn("mod-a", plan,
                Array.Empty<object>());
            scheduler.Advance(0.1d);

            Assert.AreEqual(2, caller.ResumeCount);
            Assert.AreEqual(0.1d, (double)caller.ResumeArguments[1][0], 0.000001d);
            Assert.AreEqual(0.1d, timeSource.CurrentTime, 0.000001d);
            Assert.AreEqual(1, factory.Created.Count);
        }

        [Test]
        public void R4_8_TaskSpawnRunsToFirstYieldSynchronously()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            FakeThreadPlan plan = new((FakeScriptThread thread, object[] args) => order.Add("spawn"),
                false);

            IRbxScriptThread returned = scheduler.Spawn("mod-a", plan, new object[] { "argument" });

            Assert.AreEqual(new[] { "spawn" }, order);
            Assert.AreEqual(RbxScriptThreadStatus.Suspended, returned.Status);
            Assert.AreEqual("argument", factory.Created[0].ResumeArguments[0][0]);
            Assert.AreEqual(0d, timeSource.CurrentTime);
        }

        [Test]
        public void R4_8_TaskDeferDoesNotRunBeforeCurrentDrainFinishes()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            FakeThreadPlan nested = new((FakeScriptThread thread, object[] args) => order.Add("nested"));
            FakeThreadPlan first = new((FakeScriptThread thread, object[] args) =>
            {
                order.Add("first-start");
                scheduler.Defer("mod-a", nested, Array.Empty<object>());
                order.Add("first-end");
            });
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.PreSimulation)
                {
                    order.Add("pre-simulation");
                }
            };

            scheduler.Defer("mod-a", first, Array.Empty<object>());
            Assert.IsEmpty(order);

            scheduler.Advance(0.016d);

            CollectionAssert.AreEqual(
                new[] { "first-start", "first-end", "nested", "pre-simulation" }, order);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskDeferRunsAfterTheCurrentResumptionPoint()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();

            scheduler.Defer("mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => order.Add("A")),
                Array.Empty<object>());
            order.Add("B");

            CollectionAssert.AreEqual(new[] { "B" }, order);
            scheduler.Advance(0.016d);
            CollectionAssert.AreEqual(new[] { "B", "A" }, order);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskDeferDrainsAfterEveryScriptResumePoint()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                string name = phase.ToString();
                order.Add(name);
                scheduler.Defer("mod-a",
                    new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                        order.Add("defer-" + name)), Array.Empty<object>());
            };
            scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    order.Add("Delayed");
                    scheduler.Defer("mod-a",
                        new FakeThreadPlan((FakeScriptThread deferred, object[] deferredArgs) =>
                            order.Add("defer-Delayed")), Array.Empty<object>());
                }), Array.Empty<object>());

            scheduler.Advance(0.016d);

            CollectionAssert.AreEqual(new[]
            {
                "PreAnimation",
                "defer-PreAnimation",
                "PreSimulation",
                "defer-PreSimulation",
                "PostSimulation",
                "defer-PostSimulation",
                "Delayed",
                "defer-Delayed",
                "Heartbeat",
                "defer-Heartbeat",
                "PreRender",
                "defer-PreRender"
            }, order);
            Assert.AreEqual(7, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskCancelWaitingThreadPreventsResume()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleWait(thread, 0d);

            scheduler.Cancel(thread);
            scheduler.Advance(0.1d);

            Assert.AreEqual(1, thread.ResumeCount);
            Assert.AreEqual(1, thread.KillCount);
            Assert.IsTrue(thread.IsDead);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0.1d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskCancelDeadThreadRaisesBadArgument()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(), Array.Empty<object>());

            RbxError error = Assert.Throws<RbxError>(() => scheduler.Cancel(thread));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("dead thread", error.RawMessage);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0d, timeSource.CurrentTime);
        }

        [Test]
        public void R4_8_TaskDelayZeroResumesOnTheNextHeartbeatSlot()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            SchedulerPhase? phaseAtResume = null;
            SchedulerPhase? latestPhase = null;
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) => latestPhase = phase;
            FakeThreadPlan plan = new((FakeScriptThread thread, object[] args) =>
                phaseAtResume = latestPhase);

            FakeScriptThread delayed = (FakeScriptThread)scheduler.Delay("mod-a", 0d, plan,
                Array.Empty<object>());
            Assert.AreEqual(0, delayed.ResumeCount);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, delayed.ResumeCount);
            Assert.AreEqual(SchedulerPhase.PostSimulation, phaseAtResume);
            Assert.AreEqual(SchedulerPhase.PreRender, latestPhase);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskDelayZeroUsesTheNextStageRelativeDelayedSlot()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            Dictionary<string, long> resumedFrames = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (scheduler.FrameIndex != 1)
                {
                    return;
                }

                string name = phase.ToString();
                scheduler.Delay("mod-a", 0d,
                    new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                        resumedFrames.Add(name, scheduler.FrameIndex)), Array.Empty<object>());
            };
            scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                    scheduler.Delay("mod-a", 0d,
                        new FakeThreadPlan((FakeScriptThread nested, object[] nestedArgs) =>
                            resumedFrames.Add("Delayed", scheduler.FrameIndex)),
                        Array.Empty<object>())), Array.Empty<object>());

            scheduler.Advance(0.016d);

            Assert.IsTrue(resumedFrames.ContainsKey("PreAnimation"));
            Assert.IsTrue(resumedFrames.ContainsKey("PreSimulation"));
            Assert.IsTrue(resumedFrames.ContainsKey("PostSimulation"));
            Assert.AreEqual(1L, resumedFrames["PreAnimation"]);
            Assert.AreEqual(1L, resumedFrames["PreSimulation"]);
            Assert.AreEqual(1L, resumedFrames["PostSimulation"]);
            Assert.IsFalse(resumedFrames.ContainsKey("Delayed"));
            Assert.IsFalse(resumedFrames.ContainsKey("Heartbeat"));
            Assert.IsFalse(resumedFrames.ContainsKey("PreRender"));

            scheduler.Advance(0.016d);

            Assert.AreEqual(2L, resumedFrames["Delayed"]);
            Assert.AreEqual(2L, resumedFrames["Heartbeat"]);
            Assert.AreEqual(2L, resumedFrames["PreRender"]);
            Assert.AreEqual(6, resumedFrames.Count);
            Assert.AreEqual(7, factory.Created.Count);
            Assert.AreEqual(0.032d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_TaskWaitZeroUsesTheNextStageRelativeDelayedSlot()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            Dictionary<string, long> resumedFrames = new();

            FakeScriptThread CreateCaller(string name)
            {
                return (FakeScriptThread)scheduler.Spawn("mod-a",
                    new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                    {
                        if (thread.ResumeCount == 2)
                        {
                            resumedFrames.Add(name, scheduler.FrameIndex);
                        }
                    }, false), Array.Empty<object>());
            }

            FakeScriptThread preAnimation = CreateCaller("PreAnimation");
            FakeScriptThread preSimulation = CreateCaller("PreSimulation");
            FakeScriptThread postSimulation = CreateCaller("PostSimulation");
            FakeScriptThread heartbeat = CreateCaller("Heartbeat");
            FakeScriptThread preRender = CreateCaller("PreRender");
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (scheduler.FrameIndex != 1)
                {
                    return;
                }

                switch (phase)
                {
                    case SchedulerPhase.PreAnimation:
                        scheduler.ScheduleWait(preAnimation, 0d);
                        break;
                    case SchedulerPhase.PreSimulation:
                        scheduler.ScheduleWait(preSimulation, 0d);
                        break;
                    case SchedulerPhase.PostSimulation:
                        scheduler.ScheduleWait(postSimulation, 0d);
                        break;
                    case SchedulerPhase.Heartbeat:
                        scheduler.ScheduleWait(heartbeat, 0d);
                        break;
                    case SchedulerPhase.PreRender:
                        scheduler.ScheduleWait(preRender, 0d);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
                }
            };
            scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    if (thread.ResumeCount == 1)
                    {
                        scheduler.ScheduleWait(thread, 0d);
                    }
                    else
                    {
                        resumedFrames.Add("Delayed", scheduler.FrameIndex);
                    }
                }, false), Array.Empty<object>());

            scheduler.Advance(0.016d);

            Assert.IsTrue(resumedFrames.ContainsKey("PreAnimation"));
            Assert.IsTrue(resumedFrames.ContainsKey("PreSimulation"));
            Assert.IsTrue(resumedFrames.ContainsKey("PostSimulation"));
            Assert.AreEqual(1L, resumedFrames["PreAnimation"]);
            Assert.AreEqual(1L, resumedFrames["PreSimulation"]);
            Assert.AreEqual(1L, resumedFrames["PostSimulation"]);
            Assert.IsFalse(resumedFrames.ContainsKey("Delayed"));
            Assert.IsFalse(resumedFrames.ContainsKey("Heartbeat"));
            Assert.IsFalse(resumedFrames.ContainsKey("PreRender"));

            scheduler.Advance(0.016d);

            Assert.AreEqual(2L, resumedFrames["Delayed"]);
            Assert.AreEqual(2L, resumedFrames["Heartbeat"]);
            Assert.AreEqual(2L, resumedFrames["PreRender"]);
            Assert.AreEqual(6, resumedFrames.Count);
            Assert.AreEqual(6, factory.Created.Count);
            Assert.AreEqual(0.032d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_2_DelayedThreadsResumeBeforeHeartbeat()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) => order.Add(phase.ToString());
            scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => order.Add("Delayed")),
                Array.Empty<object>());

            scheduler.Advance(0.02d);

            CollectionAssert.AreEqual(new[]
            {
                "PreAnimation",
                "PreSimulation",
                "PostSimulation",
                "Delayed",
                "Heartbeat",
                "PreRender"
            }, order);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0.02d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_2_FramePhasesFollowCanonicalOrderAndDelta()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<SchedulerPhase> phases = new();
            List<double> deltas = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                phases.Add(phase);
                deltas.Add(delta);
            };

            scheduler.Advance(0.033d);

            CollectionAssert.AreEqual(new[]
            {
                SchedulerPhase.PreAnimation,
                SchedulerPhase.PreSimulation,
                SchedulerPhase.PostSimulation,
                SchedulerPhase.Heartbeat,
                SchedulerPhase.PreRender
            }, phases);
            CollectionAssert.AreEqual(
                new[] { 0.033d, 0.033d, 0.033d, 0.033d, 0.033d }, deltas);
            Assert.AreEqual(0, factory.Created.Count);
            Assert.AreEqual(0.033d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void DEV3_DeferredFaultDoesNotOrphanDifferentModSibling()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            RbxError failure = new(RbxErrorCode.BudgetExceeded,
                "mod-a failed during a deferred drain",
                "yield before exhausting the scheduler slice");
            FakeScriptThread faulting = (FakeScriptThread)scheduler.Defer("mod-a",
                new FakeThreadPlan(failure: failure), Array.Empty<object>());
            FakeScriptThread sibling = (FakeScriptThread)scheduler.Defer("mod-b",
                new FakeThreadPlan(), Array.Empty<object>());

            RbxError thrown = Assert.Throws<RbxError>(() => scheduler.Advance(0.016d));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thrown.Code);
            Assert.AreEqual(1, faulting.ResumeCount);
            Assert.AreEqual(1, faulting.KillCount);
            Assert.AreEqual(0, sibling.ResumeCount);
            Assert.IsFalse(sibling.IsDead);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, sibling.ResumeCount);
            Assert.IsTrue(sibling.IsDead);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.032d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void DEV3_DelayedFaultDoesNotOrphanDifferentModSibling()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            RbxError failure = new(RbxErrorCode.BudgetExceeded,
                "mod-a failed during a delayed batch",
                "yield before exhausting the scheduler slice");
            FakeScriptThread faulting = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan(failure: failure), Array.Empty<object>());
            FakeScriptThread sibling = (FakeScriptThread)scheduler.Delay("mod-b", 0d,
                new FakeThreadPlan(), Array.Empty<object>());

            RbxError thrown = Assert.Throws<RbxError>(() => scheduler.Advance(0.016d));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thrown.Code);
            Assert.AreEqual(1, faulting.ResumeCount);
            Assert.AreEqual(1, faulting.KillCount);
            Assert.AreEqual(0, sibling.ResumeCount);
            Assert.IsFalse(sibling.IsDead);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, sibling.ResumeCount);
            Assert.IsTrue(sibling.IsDead);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.032d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void DEV3_BudgetKillTargetsOnlyOwningModAndOtherModRunsSameFrame()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> ran = new();
            string faultedMod = null;
            RbxError observedError = null;
            int ownerKillCount = -1;
            RbxError budgetError = new(RbxErrorCode.BudgetExceeded,
                "mod-a exceeded its scheduler slice",
                "reduce work per resumption or yield sooner");
            FakeScriptThread runaway = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan(failure: budgetError), Array.Empty<object>());
            FakeScriptThread modASibling = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => ran.Add("mod-a")),
                Array.Empty<object>());
            FakeScriptThread modB = (FakeScriptThread)scheduler.Delay("mod-b", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => ran.Add("mod-b")),
                Array.Empty<object>());
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                faultedMod = ownerModId;
                observedError = error;
                ownerKillCount = scheduler.KillOwnedBy(ownerModId);
            };

            scheduler.Advance(0.016d);

            CollectionAssert.AreEqual(new[] { "mod-b" }, ran);
            Assert.AreEqual("mod-a", faultedMod);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, observedError.Code);
            Assert.AreEqual(1, ownerKillCount);
            Assert.IsTrue(runaway.IsDead);
            Assert.IsTrue(modASibling.IsDead);
            Assert.AreEqual(0, modASibling.ResumeCount);
            Assert.AreEqual(1, modB.ResumeCount);
            Assert.AreEqual(3, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void R4_8_ScheduleWaitUntilResumesAtTheNextDeferredDrain()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            FakeScriptThread caller = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    if (thread.ResumeCount == 2)
                    {
                        order.Add("completion");
                    }
                }, false), Array.Empty<object>());
            RbxSchedulerCompletion completion = new();
            scheduler.ScheduleWaitUntil(caller, completion);
            scheduler.Advance(0.01d);
            Assert.AreEqual(1, caller.ResumeCount);

            completion.Complete("value", 7d);
            scheduler.SignalCompletion(completion);
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.PreSimulation)
                {
                    order.Add("pre-simulation");
                }
            };
            scheduler.Advance(0.01d);

            CollectionAssert.AreEqual(new[] { "completion", "pre-simulation" }, order);
            Assert.AreEqual("value", caller.ResumeArguments[1][0]);
            Assert.AreEqual(7d, caller.ResumeArguments[1][1]);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0.02d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void DEV3_CompletionFaultDoesNotOrphanDifferentModSibling()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread faulting = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            FakeScriptThread sibling = (FakeScriptThread)scheduler.Spawn("mod-b",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxSchedulerCompletion faulted = new();
            RbxSchedulerCompletion succeeded = new();
            scheduler.ScheduleWaitUntil(faulting, faulted);
            scheduler.ScheduleWaitUntil(sibling, succeeded);
            faulted.Fail(new RbxError(RbxErrorCode.BudgetExceeded,
                "mod-a failed while promoting completion",
                "yield before exhausting the scheduler slice"));
            scheduler.SignalCompletion(faulted);
            succeeded.Complete("value");
            scheduler.SignalCompletion(succeeded);

            RbxError thrown = Assert.Throws<RbxError>(() => scheduler.Advance(0.016d));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thrown.Code);
            Assert.AreEqual(1, faulting.KillCount);
            Assert.AreEqual(1, sibling.ResumeCount);
            Assert.AreEqual(1, scheduler.CompletionWaitCount);

            scheduler.Advance(0.016d);

            Assert.AreEqual(2, sibling.ResumeCount);
            Assert.AreEqual("value", sibling.ResumeArguments[1][0]);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.032d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void F2_CompletionPromotionTouchesOnlySignaledEntries()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            const int waitCount = 200;
            const int completedCount = 20;
            List<FakeScriptThread> threads = new();
            List<RbxSchedulerCompletion> completions = new();
            for (int index = 0; index < waitCount; index++)
            {
                FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a",
                    new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
                RbxSchedulerCompletion completion = new();
                scheduler.ScheduleWaitUntil(thread, completion);
                threads.Add(thread);
                completions.Add(completion);
            }

            for (int index = 0; index < completedCount; index++)
            {
                completions[index].Complete(index);
                scheduler.SignalCompletion(completions[index]);
            }

            scheduler.Advance(0.016d);

            Assert.AreEqual(completedCount, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(waitCount - completedCount, scheduler.CompletionWaitCount);
            for (int index = 0; index < waitCount; index++)
            {
                int expectedResumeCount = index < completedCount ? 2 : 1;
                Assert.AreEqual(expectedResumeCount, threads[index].ResumeCount);
            }

            Assert.AreEqual(waitCount, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void F2_FaultedCompletionPromotionDoesNotScanPendingSchedulerWork()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            PopulatePendingSchedulerWork(scheduler, 200);
            FakeScriptThread faulting = (FakeScriptThread)scheduler.Spawn("mod-fault",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxSchedulerCompletion completion = new();
            scheduler.ScheduleWaitUntil(faulting, completion);
            completion.Fail(new RbxError(RbxErrorCode.BudgetExceeded,
                "completion failed during complexity regression",
                "keep failure promotion proportional to ready completions"));
            scheduler.SignalCompletion(completion);

            RbxError thrown = Assert.Throws<RbxError>(() => scheduler.Advance(0.016d));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thrown.Code);
            Assert.AreEqual(1, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(1, faulting.KillCount);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
            Assert.AreEqual(201, factory.Created.Count);
        }

        [Test]
        public void F2_CanceledCompletionPromotionDoesNotScanPendingSchedulerWork()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            PopulatePendingSchedulerWork(scheduler, 200);
            FakeScriptThread canceled = (FakeScriptThread)scheduler.Spawn("mod-cancel",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxSchedulerCompletion completion = new();
            scheduler.ScheduleWaitUntil(canceled, completion);
            completion.Cancel();
            scheduler.SignalCompletion(completion);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(1, canceled.KillCount);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
            Assert.AreEqual(201, factory.Created.Count);
        }

        [Test]
        public void F2_CompletionPromotionUsesRegistrationOrder()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            FakeScriptThread first = CreateCompletionCaller(scheduler, "first", order);
            FakeScriptThread second = CreateCompletionCaller(scheduler, "second", order);
            FakeScriptThread third = CreateCompletionCaller(scheduler, "third", order);
            RbxSchedulerCompletion firstCompletion = new();
            RbxSchedulerCompletion secondCompletion = new();
            RbxSchedulerCompletion thirdCompletion = new();
            scheduler.ScheduleWaitUntil(first, firstCompletion);
            scheduler.ScheduleWaitUntil(second, secondCompletion);
            scheduler.ScheduleWaitUntil(third, thirdCompletion);

            thirdCompletion.Complete();
            scheduler.SignalCompletion(thirdCompletion);
            firstCompletion.Complete();
            scheduler.SignalCompletion(firstCompletion);
            secondCompletion.Complete();
            scheduler.SignalCompletion(secondCompletion);

            scheduler.Advance(0.016d);

            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, order);
            Assert.AreEqual(3, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(3, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void F2_CrossSnapshotCompletionOrderFollowsSignalReadiness()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            List<string> order = new();
            FakeScriptThread earlier = CreateCompletionCaller(scheduler, "earlier", order);
            FakeScriptThread later = CreateCompletionCaller(scheduler, "later", order);
            RbxSchedulerCompletion earlierCompletion = new();
            RbxSchedulerCompletion laterCompletion = new();
            scheduler.ScheduleWaitUntil(earlier, earlierCompletion);
            scheduler.ScheduleWaitUntil(later, laterCompletion);
            laterCompletion.Complete();
            scheduler.SignalCompletion(laterCompletion);

            using (Barrier snapshotBarrier = new(2))
            {
                Exception signalError = null;
                Thread signalThread = new(() =>
                {
                    try
                    {
                        if (!snapshotBarrier.SignalAndWait(TimeSpan.FromSeconds(5d)))
                        {
                            throw new TimeoutException("completion snapshot barrier timed out");
                        }

                        earlierCompletion.Complete();
                        scheduler.SignalCompletion(earlierCompletion);
                        if (!snapshotBarrier.SignalAndWait(TimeSpan.FromSeconds(5d)))
                        {
                            throw new TimeoutException("completion signal barrier timed out");
                        }
                    }
                    catch (Exception error)
                    {
                        signalError = error;
                    }
                });
                scheduler.CompletionSnapshotCaptured = () =>
                {
                    scheduler.CompletionSnapshotCaptured = null;
                    Assert.IsTrue(snapshotBarrier.SignalAndWait(TimeSpan.FromSeconds(5d)));
                    Assert.IsTrue(snapshotBarrier.SignalAndWait(TimeSpan.FromSeconds(5d)));
                };
                signalThread.Start();
                bool joined = false;
                try
                {
                    scheduler.Advance(0.016d);
                }
                finally
                {
                    joined = signalThread.Join(TimeSpan.FromSeconds(5d));
                    scheduler.CompletionSnapshotCaptured = null;
                }

                Assert.IsTrue(joined);
                Assert.IsNull(signalError);
            }

            CollectionAssert.AreEqual(new[] { "later", "earlier" }, order);
            Assert.AreEqual(2, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        [Test]
        public void F2_LateCompletionSignalsAfterCancelAndUnloadAreDiscarded()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread canceled = (FakeScriptThread)scheduler.Spawn("mod-cancel",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            FakeScriptThread unloaded = (FakeScriptThread)scheduler.Spawn("mod-unload",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxSchedulerCompletion canceledCompletion = new();
            RbxSchedulerCompletion unloadedCompletion = new();
            scheduler.ScheduleWaitUntil(canceled, canceledCompletion);
            scheduler.ScheduleWaitUntil(unloaded, unloadedCompletion);

            scheduler.Cancel(canceled);
            Assert.AreEqual(1, scheduler.KillOwnedBy("mod-unload"));
            canceledCompletion.Complete();
            scheduler.SignalCompletion(canceledCompletion);
            unloadedCompletion.Complete();
            scheduler.SignalCompletion(unloadedCompletion);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, canceled.ResumeCount);
            Assert.AreEqual(1, unloaded.ResumeCount);
            Assert.IsTrue(canceled.IsDead);
            Assert.IsTrue(unloaded.IsDead);
            Assert.AreEqual(0, scheduler.CompletionPromotionTouchCount);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);
            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreEqual(0.016d, timeSource.CurrentTime, 0.000001d);
        }

        private static FakeScriptThread CreateCompletionCaller(ModScheduler scheduler, string name,
            List<string> order)
        {
            return (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    if (thread.ResumeCount == 2)
                    {
                        order.Add(name);
                    }
                }, false), Array.Empty<object>());
        }

        private static void PopulatePendingSchedulerWork(ModScheduler scheduler, int count)
        {
            for (int index = 0; index < count; index++)
            {
                switch (index % 3)
                {
                    case 0:
                        scheduler.Defer("mod-pending", new FakeThreadPlan(), Array.Empty<object>());
                        break;
                    case 1:
                        scheduler.Delay("mod-pending", 1000d, new FakeThreadPlan(),
                            Array.Empty<object>());
                        break;
                    case 2:
                        FakeScriptThread waiting = (FakeScriptThread)scheduler.Spawn("mod-pending",
                            new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
                        scheduler.ScheduleWait(waiting, 1000d);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private static ModScheduler CreateScheduler(out FakeTimeSource timeSource,
            out FakeThreadFactory factory)
        {
            timeSource = new FakeTimeSource();
            factory = new FakeThreadFactory();
            return new ModScheduler(factory, timeSource);
        }
    }
}
