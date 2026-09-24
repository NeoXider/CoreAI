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
                bool completeOnResume = true, RbxError failure = null, RbxError terminalFault = null)
            {
                OnResume = onResume;
                CompleteOnResume = completeOnResume;
                Failure = failure;
                TerminalFault = terminalFault;
            }

            public Action<FakeScriptThread, object[]> OnResume { get; }

            public bool CompleteOnResume { get; }

            /// <summary>
            /// The error the resume reports; settable so a plan's own <see cref="OnResume"/> can fail
            /// with what the scheduler raised inside it, the way an uncaught Lua error ends a thread.
            /// </summary>
            public RbxError Failure { get; set; }

            /// <summary>Error the fake adapter reports after a "successful" resume that ended the thread.</summary>
            public RbxError TerminalFault { get; }
        }

        private sealed class FakeScriptThread : IRbxScriptThread, IRbxScriptThreadTerminalFault
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

            public RbxError TerminalFault => IsDead ? _plan.TerminalFault : null;

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
        public void SignalDrain_QuotaSaturatedFirstSubscriber_StillInvokesLaterSubscriber()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(1, ownerModId => ownerModId);
            scheduler.Spawn("saturated-actor",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxScriptSignal signal = new("QuotaIsolation");
            signal.BindScheduler(scheduler);
            int laterInvocations = 0;
            List<SchedulerPhase> phases = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) => phases.Add(phase);
            signal.Connect((Action<object[]>)(_ => scheduler.SpawnSignal(
                "saturated-actor", new FakeThreadPlan(), Array.Empty<object>())));
            signal.Connect((Action<object[]>)(_ => laterInvocations++));
            signal.Fire();

            RbxError error = Assert.Throws<RbxError>(() => scheduler.Advance(0d));

            Assert.AreEqual(RbxErrorCode.ThreadCap, error.Code);
            Assert.AreEqual(1, laterInvocations,
                "one actor's admission refusal must not discard another queued signal invocation");
            CollectionAssert.AreEqual(AllPhases, phases,
                "a fault nobody observes is rethrown only after the whole frame has run");
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
                "InputProcessing",
                "defer-InputProcessing",
                "PreRender",
                "defer-PreRender"
            }, order);
            Assert.AreEqual(8, factory.Created.Count);
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
        public void R4_8_M2_13_TaskCancelDeadThreadIsANoOp()
        {
            ModScheduler scheduler = CreateScheduler(out FakeTimeSource timeSource,
                out FakeThreadFactory factory);
            FakeScriptThread thread = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(), Array.Empty<object>());
            Assert.IsTrue(thread.IsDead, "the spawned thread ran to completion");

            // WHY a no-op and not BAD_ARGUMENT: the mirror (reference/engine/libraries/task.yaml,
            // task.cancel) names only the currently executing thread and a thread that resumed
            // another coroutine as uncancellable, and task.cancel closes the thread like
            // coroutine.close, which accepts a dead coroutine. `task.cancel(self._t)` in a Destroy
            // method must not abort the teardown just because the task already ran.
            Assert.DoesNotThrow(() => scheduler.Cancel(thread));
            Assert.DoesNotThrow(() => scheduler.Cancel(thread),
                "cancelling the same finished thread twice is still a no-op");

            Assert.AreEqual(0, thread.KillCount, "a finished thread is never killed again");
            Assert.AreEqual(0, scheduler.LiveThreadCount);
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(0d, timeSource.CurrentTime);
        }

        [Test]
        public void R4_8_M2_13_TaskCancelOfFinishedDelayIsANoOpAndLeavesOtherWorkAlone()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread ran = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan(), Array.Empty<object>());
            FakeScriptThread pending = (FakeScriptThread)scheduler.Delay("mod-a", 1d,
                new FakeThreadPlan(), Array.Empty<object>());
            scheduler.Advance(0.016d);
            Assert.IsTrue(ran.IsDead);

            Assert.DoesNotThrow(() => scheduler.Cancel(ran));
            scheduler.Advance(1d);

            Assert.AreEqual(1, pending.ResumeCount,
                "cancelling a finished task must not touch another pending task");
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void R4_8_M2_13_NegativeTwin_TaskCancelOfTheRunningThreadStillRaises()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            RbxError observed = null;
            FakeThreadPlan selfCancelling = new((FakeScriptThread thread, object[] args) =>
            {
                try
                {
                    scheduler.Cancel(thread);
                }
                catch (RbxError error)
                {
                    observed = error;
                }
            });

            scheduler.Spawn("mod-a", selfCancelling, Array.Empty<object>());

            Assert.IsNotNull(observed);
            Assert.AreEqual(RbxErrorCode.BadArgument, observed.Code);
            Assert.AreEqual("task.cancel cannot cancel the currently running thread",
                observed.RawMessage);
        }

        [Test]
        public void R4_8_M2_13_CancelOfAThreadKilledOutsideTheSchedulerDropsItsRecordSilently()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => faults.Add(ownerModId);
            FakeScriptThread waiting = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleWait(waiting, 0d);
            waiting.Kill();

            Assert.DoesNotThrow(() => scheduler.Cancel(waiting));
            scheduler.Advance(0.016d);

            Assert.AreEqual(0, scheduler.LiveThreadCount);
            Assert.IsEmpty(faults, "an explicit cancel is not a fault, even of a thread that died elsewhere");
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
            Assert.IsFalse(resumedFrames.ContainsKey("InputProcessing"));
            Assert.IsFalse(resumedFrames.ContainsKey("PreRender"));

            scheduler.Advance(0.016d);

            Assert.AreEqual(2L, resumedFrames["Delayed"]);
            Assert.AreEqual(2L, resumedFrames["Heartbeat"]);
            Assert.AreEqual(2L, resumedFrames["InputProcessing"]);
            Assert.AreEqual(2L, resumedFrames["PreRender"]);
            Assert.AreEqual(7, resumedFrames.Count);
            Assert.AreEqual(8, factory.Created.Count);
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
                    case SchedulerPhase.InputProcessing:
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
                "InputProcessing",
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
                SchedulerPhase.InputProcessing,
                SchedulerPhase.PreRender
            }, phases);
            CollectionAssert.AreEqual(
                new[] { 0.033d, 0.033d, 0.033d, 0.033d, 0.033d, 0.033d }, deltas);
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

            // WHY the throw survives with no subscriber: a fault nobody observes must stay loud, but it
            // is rethrown only after the frame, so the other mod's sibling runs in the SAME frame
            // instead of being orphaned until the next one (M2-02).
            RbxError thrown = Assert.Throws<RbxError>(() => scheduler.Advance(0.016d));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thrown.Code);
            Assert.AreEqual(1, faulting.ResumeCount);
            Assert.AreEqual(1, faulting.KillCount);
            Assert.AreEqual(1, sibling.ResumeCount,
                "the different-mod sibling resumes in the frame where the fault happened");
            Assert.IsTrue(sibling.IsDead);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, sibling.ResumeCount, "the sibling is never resumed twice");
            Assert.AreEqual(1, faulting.ResumeCount, "the faulted thread is never resumed again");
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
            Assert.AreEqual(1, sibling.ResumeCount,
                "the different-mod sibling resumes in the same delayed slot as the fault");
            Assert.IsTrue(sibling.IsDead);

            scheduler.Advance(0.016d);

            Assert.AreEqual(1, sibling.ResumeCount, "the sibling is never resumed twice");
            Assert.AreEqual(1, faulting.ResumeCount, "the faulted thread is never resumed again");
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
            Assert.AreEqual(2, sibling.ResumeCount,
                "the succeeded completion of the other mod is promoted in the same frame");
            Assert.AreEqual("value", sibling.ResumeArguments[1][0]);
            Assert.AreEqual(0, scheduler.CompletionWaitCount);

            scheduler.Advance(0.016d);

            Assert.AreEqual(2, sibling.ResumeCount, "the sibling is never resumed twice");
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

        private static readonly SchedulerPhase[] AllPhases =
        {
            SchedulerPhase.PreAnimation,
            SchedulerPhase.PreSimulation,
            SchedulerPhase.PostSimulation,
            SchedulerPhase.Heartbeat,
            SchedulerPhase.InputProcessing,
            SchedulerPhase.PreRender
        };

        [Test]
        public void M2_02_FaultingModEveryFrame_HealthyModKeepsEveryPhaseAndEachFaultIsAttributedOnce()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
                faults.Add(ownerModId + ":" + error.Code);
            RbxScriptSignal stepped = new("RunService.Stepped");
            RbxScriptSignal changed = new("Part.Changed");
            RbxScriptSignal heartbeat = new("RunService.Heartbeat");

            // WHY three triggers in one mod: each of them used to abort the whole frame on its own (a
            // handler that throws out of its invocation, a self-sustaining Changed cascade, and a
            // waiting thread finished outside the scheduler), and each must cost only mod-a.
            ConnectOwned(registry, scheduler, stepped, "mod-a",
                _ => throw new RbxError(RbxErrorCode.ContextViolation,
                    "mod-a handler exploded", "fix the handler"));
            ConnectThreadHandler(registry, scheduler, changed, "mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => changed.Fire("Color")));
            int heartbeats = 0;
            ConnectThreadHandler(registry, scheduler, heartbeat, "mod-b",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => heartbeats++));
            int waits = 0;
            scheduler.Spawn("mod-b", new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
            {
                waits++;
                scheduler.ScheduleWait(thread);
            }, false), Array.Empty<object>());
            waits = 0;
            List<SchedulerPhase> phases = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                phases.Add(phase);
                if (phase == SchedulerPhase.PreAnimation)
                {
                    FakeScriptThread doomed = (FakeScriptThread)scheduler.Spawn("mod-a",
                        new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
                    scheduler.ScheduleWait(doomed, 0d);
                    doomed.Kill();
                }
                else if (phase == SchedulerPhase.PreSimulation)
                {
                    stepped.Fire(scheduler.CurrentTime, delta);
                    changed.Fire("Color");
                }
                else if (phase == SchedulerPhase.Heartbeat)
                {
                    heartbeat.Fire(delta);
                }
            };

            const int frames = 5;
            for (int frame = 0; frame < frames; frame++)
            {
                Assert.DoesNotThrow(() => scheduler.Advance(1d / 60d),
                    "one mod's fault must never abort the frame for every other mod");
            }

            Assert.AreEqual(frames, heartbeats, "mod-b's Heartbeat handler ran every frame");
            Assert.AreEqual(frames, waits, "mod-b's task.wait loop resumed every frame");
            Assert.AreEqual(frames * AllPhases.Length, phases.Count, "every phase of every frame ran");
            Assert.AreEqual(frames * 3, faults.Count,
                "each trigger is reported exactly once per frame: " + string.Join(", ", faults));
            Assert.AreEqual(frames, faults.FindAll(fault => fault == "mod-a:SignalCascade").Count);
            Assert.AreEqual(frames, faults.FindAll(fault => fault == "mod-a:ContextViolation").Count);
            Assert.AreEqual(frames, faults.FindAll(fault => fault == "mod-a:BadArgument").Count,
                "the dead-thread resume is a mod-a fault, not a frame abort");
            Assert.AreEqual(1, scheduler.LiveThreadCount, "only mod-b's waiting loop is still alive");
        }

        [Test]
        public void M2_02_CascadeIsAttributedToTheFiringMod_NotToAModThatOnlyListens()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            List<string> faults = new();
            RbxError cascade = null;
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                faults.Add(ownerModId);
                cascade = error;
            };
            RbxScriptSignal changed = new("Part.Changed");
            ConnectThreadHandler(registry, scheduler, changed, "mod-loop",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => changed.Fire("Color")));
            int listenerRuns = 0;
            ConnectThreadHandler(registry, scheduler, changed, "mod-listener",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => listenerRuns++));
            changed.Fire("Color");

            Assert.DoesNotThrow(() => scheduler.Advance(0d));

            CollectionAssert.AreEqual(new[] { "mod-loop" }, faults,
                "only the mod whose handler keeps re-firing is faulted; a listener is a victim");
            Assert.AreEqual(RbxErrorCode.SignalCascade, cascade.Code);
            StringAssert.Contains("Part.Changed -> Part.Changed", cascade.RawMessage);
            Assert.AreEqual(ModScheduler.MaxSignalGenerations, listenerRuns,
                "the listener received every generation up to the cap");
        }

        [Test]
        public void M2_02_OwnerlessHandlerFault_GoesToHostFaultedAndSiblingsStillRun()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            List<string> hostFaults = new();
            List<string> modFaults = new();
            scheduler.HostFaulted += (string source, Exception exception) =>
                hostFaults.Add(source + ": " + exception.Message);
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => modFaults.Add(ownerModId);
            RbxScriptSignal signal = new("Workspace.ChildAdded");
            signal.BindScheduler(scheduler);
            signal.Connect((Action<object[]>)(_ =>
                throw new InvalidOperationException("ownerless handler exploded")));
            int siblingRuns = 0;
            ConnectOwned(registry, scheduler, signal, "mod-b", _ => siblingRuns++);
            signal.Fire();

            Assert.DoesNotThrow(() => scheduler.Advance(0d));

            Assert.AreEqual(1, hostFaults.Count);
            StringAssert.Contains("Workspace.ChildAdded handler", hostFaults[0]);
            StringAssert.Contains("ownerless handler exploded", hostFaults[0]);
            Assert.IsEmpty(modFaults, "a connection no mod owns never faults a mod");
            Assert.AreEqual(1, siblingRuns);
        }

        [Test]
        public void M2_02_ThrowingThreadResume_IsAThreadFaultOfItsOwner()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<RbxError> faults = new();
            List<string> owners = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                owners.Add(ownerModId);
                faults.Add(error);
            };
            FakeScriptThread broken = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                    throw new InvalidOperationException("adapter blew up")), Array.Empty<object>());
            FakeScriptThread healthy = (FakeScriptThread)scheduler.Delay("mod-b", 0d,
                new FakeThreadPlan(), Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.Advance(0.016d));

            CollectionAssert.AreEqual(new[] { "mod-a" }, owners);
            StringAssert.Contains("InvalidOperationException", faults[0].RawMessage);
            StringAssert.Contains("adapter blew up", faults[0].RawMessage);
            Assert.AreEqual(1, broken.KillCount);
            Assert.AreEqual(1, healthy.ResumeCount, "the next thread of the same slot still ran");
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_02_ThrowingHostCallback_IsReportedAndTheRestOfTheSlotRunsThisFrame()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> hostFaults = new();
            scheduler.HostFaulted += (string source, Exception exception) => hostFaults.Add(source);
            bool laterCallbackRan = false;
            scheduler.ScheduleHostCallback(0d, () => throw new InvalidOperationException("debris failed"));
            scheduler.ScheduleHostCallback(0d, () => laterCallbackRan = true);
            FakeScriptThread delayed = (FakeScriptThread)scheduler.Delay("mod-b", 0d,
                new FakeThreadPlan(), Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.Advance(0.016d));

            CollectionAssert.AreEqual(new[] { "host callback" }, hostFaults);
            Assert.IsTrue(laterCallbackRan, "a later host callback of the same slot runs this frame");
            Assert.AreEqual(1, delayed.ResumeCount, "the delayed thread of the same slot runs this frame");

            scheduler.Advance(0.016d);
            Assert.AreEqual(1, hostFaults.Count, "a throwing host callback is dropped after one attempt");
        }

        [Test]
        public void M2_02_ThrowingThreadFaultedSubscriber_DoesNotHideTheFaultFromTheNextSubscriber()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> hostFaults = new();
            List<string> observed = new();
            scheduler.HostFaulted += (string source, Exception exception) => hostFaults.Add(source);
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
                throw new InvalidOperationException("first subscriber is broken");
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => observed.Add(ownerModId);
            scheduler.Delay("mod-a", 0d, new FakeThreadPlan(failure: new RbxError(
                RbxErrorCode.BudgetExceeded, "mod-a ran away", "yield sooner")), Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.Advance(0.016d));

            CollectionAssert.AreEqual(new[] { "mod-a" }, observed);
            CollectionAssert.AreEqual(new[] { "ThreadFaulted subscriber" }, hostFaults);
        }

        [Test]
        public void M8_02_ThrowingPhaseSubscriber_DoesNotStarveLaterSubscribersOrPhases()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> hostFaults = new();
            scheduler.HostFaulted += (string source, Exception exception) =>
                hostFaults.Add(source + ": " + exception.Message);
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.Heartbeat)
                {
                    throw new InvalidOperationException("tween pump exploded");
                }
            };
            List<SchedulerPhase> laterSubscriber = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) => laterSubscriber.Add(phase);
            int delayedRuns = 0;
            scheduler.Spawn("mod-a", new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
            {
                delayedRuns++;
                scheduler.ScheduleWait(thread);
            }, false), Array.Empty<object>());
            delayedRuns = 0;

            const int frames = 3;
            for (int frame = 0; frame < frames; frame++)
            {
                Assert.DoesNotThrow(() => scheduler.Advance(0.016d));
            }

            Assert.AreEqual(frames * AllPhases.Length, laterSubscriber.Count,
                "the subscriber after the throwing one sees every phase of every frame");
            Assert.AreEqual(frames, delayedRuns);
            Assert.AreEqual(frames, hostFaults.Count);
            StringAssert.Contains("PhaseReached(Heartbeat) subscriber", hostFaults[0]);
            StringAssert.Contains("tween pump exploded", hostFaults[0]);
        }

        [Test]
        public void M8_02_NegativeTwin_UnobservedPhaseSubscriberFault_IsRethrownOnlyAfterTheFrame()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.Heartbeat && scheduler.FrameIndex == 1)
                {
                    throw new InvalidOperationException("nobody observes this");
                }
            };
            List<SchedulerPhase> laterSubscriber = new();
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) => laterSubscriber.Add(phase);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => scheduler.Advance(0.016d));

            Assert.AreEqual("nobody observes this", thrown.Message);
            CollectionAssert.AreEqual(AllPhases, laterSubscriber,
                "the unobserved fault is still loud, but only after InputProcessing and PreRender ran");
            Assert.DoesNotThrow(() => scheduler.Advance(0d),
                "the held fault belongs to one frame and is not rethrown by the next");
        }

        [Test]
        public void M2_11_DeferFromAPreRenderHandlerRunsInTheSameFrame()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            RbxScriptSignal preRender = new("RunService.PreRender");
            List<string> order = new();
            ConnectThreadHandler(registry, scheduler, preRender, "mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    order.Add("handler-frame-" + scheduler.FrameIndex);
                    scheduler.Defer("mod-a", new FakeThreadPlan((FakeScriptThread deferred,
                        object[] deferredArgs) => order.Add("deferred-frame-" + scheduler.FrameIndex)),
                        Array.Empty<object>());
                }));
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.PreRender && scheduler.FrameIndex == 1)
                {
                    preRender.Fire(delta);
                }
            };

            scheduler.Advance(1d / 60d);

            CollectionAssert.AreEqual(new[] { "handler-frame-1", "deferred-frame-1" }, order,
                "task.defer resumes at the end of the current resumption point, not in the next frame");
        }

        [Test]
        public void M2_11_DeferFromAPostSimulationHandlerRunsBeforeTheDelayedThreads()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            RbxScriptSignal postSimulation = new("RunService.PostSimulation");
            List<string> order = new();
            ConnectThreadHandler(registry, scheduler, postSimulation, "mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    order.Add("handler");
                    scheduler.Defer("mod-a", new FakeThreadPlan((FakeScriptThread deferred,
                        object[] deferredArgs) => order.Add("deferred")), Array.Empty<object>());
                }));
            scheduler.Delay("mod-b", 0d, new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                order.Add("delayed")), Array.Empty<object>());
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.PostSimulation)
                {
                    postSimulation.Fire(delta);
                }
            };

            scheduler.Advance(1d / 60d);

            CollectionAssert.AreEqual(new[] { "handler", "deferred", "delayed" }, order);
        }

        [Test]
        public void M2_11_NegativeTwin_SelfDeferringThreadIsBoundedPerResumptionPoint()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            int runs = 0;
            FakeThreadPlan again = null;
            again = new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
            {
                runs++;
                scheduler.Defer("mod-a", again, Array.Empty<object>());
            });
            scheduler.Defer("mod-a", again, Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.Advance(0.016d));

            // WHY eight: frame entry plus one after each of the six phases and the delayed slot (R4.2).
            Assert.AreEqual(8 * ModScheduler.MaxDrainRoundsPerResumptionPoint, runs,
                "every resumption point of the frame drains a bounded number of rounds");
            Assert.AreEqual(1, scheduler.LiveThreadCount, "the leftover deferral waits for the next point");
        }

        [Test]
        public void M2_12_FanOut_DefaultBudgetFaultsTheOwnerOnceAndOtherModsStillRun()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            ModConnectionRegistry registry = new();
            List<string> faults = new();
            RbxError budgetFault = null;
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                faults.Add(ownerModId + ":" + error.Code);
                budgetFault = error;
            };
            RbxScriptSignal bindable = new("BindableEvent.Event");
            long invocations = 0;
            for (int index = 0; index < 4; index++)
            {
                ConnectThreadHandler(registry, scheduler, bindable, "mod-fan",
                    new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                    {
                        invocations++;
                        bindable.Fire();
                    }));
            }

            RbxScriptSignal heartbeat = new("RunService.Heartbeat");
            int heartbeats = 0;
            ConnectThreadHandler(registry, scheduler, heartbeat, "mod-b",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) => heartbeats++));
            scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.Heartbeat)
                {
                    heartbeat.Fire(delta);
                }
            };
            bindable.Fire();

            Assert.DoesNotThrow(() => scheduler.Advance(1d / 60d));

            // WHY this bound: four connections that each re-fire once reach 4^10 (about 1.4 million)
            // invocations inside the ten-generation depth cap; width needs its own budget.
            Assert.LessOrEqual(invocations, ModScheduler.DefaultMaxSignalInvocationsPerOwner);
            Assert.Greater(invocations, 1000L, "the fan-out ran until the budget, not only one level");
            CollectionAssert.AreEqual(new[] { "mod-fan:BudgetExceeded" }, faults,
                "the owner is faulted exactly once for the whole overflow");
            StringAssert.Contains(ModScheduler.DefaultMaxSignalInvocationsPerOwner.ToString(),
                budgetFault.RawMessage);
            Assert.AreEqual(1, heartbeats, "another mod's Heartbeat still ran in the same frame");
        }

        [Test]
        public void M2_12_ConfiguredBudget_DropsOnlyTheOverBudgetOwnerAndResetsEveryResumptionPoint()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureSignalBudget(10, 1000);
            ModConnectionRegistry registry = new();
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
                faults.Add(ownerModId + ":" + error.Code);
            RbxScriptSignal noisy = new("Noisy.Event");
            RbxScriptSignal quiet = new("Quiet.Event");
            int noisyRuns = 0;
            int quietRuns = 0;
            ConnectOwned(registry, scheduler, noisy, "mod-noisy", _ => noisyRuns++);
            ConnectOwned(registry, scheduler, quiet, "mod-quiet", _ => quietRuns++);
            for (int index = 0; index < 25; index++)
            {
                noisy.Fire(index);
            }

            for (int index = 0; index < 5; index++)
            {
                quiet.Fire(index);
            }

            scheduler.Advance(0d);

            Assert.AreEqual(10, noisyRuns, "only the budgeted invocations of the noisy owner ran");
            Assert.AreEqual(5, quietRuns, "another owner's invocations are never dropped");
            CollectionAssert.AreEqual(new[] { "mod-noisy:BudgetExceeded" }, faults);

            for (int index = 0; index < 5; index++)
            {
                noisy.Fire(index);
            }

            scheduler.Advance(0d);

            Assert.AreEqual(15, noisyRuns, "the budget window restarts at every resumption point");
            Assert.AreEqual(1, faults.Count);
        }

        [Test]
        public void M2_12_OwnerlessFanOut_SharesOneHostBudgetAndIsReportedAsAHostFault()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureSignalBudget(500, 100000);
            List<string> hostFaults = new();
            List<string> modFaults = new();
            scheduler.HostFaulted += (string source, Exception exception) =>
                hostFaults.Add(source + ": " + exception.Message);
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => modFaults.Add(ownerModId);
            RbxScriptSignal bindable = new("BindableEvent.Event");
            bindable.BindScheduler(scheduler);
            long invocations = 0;
            for (int index = 0; index < 4; index++)
            {
                bindable.Connect((Action<object[]>)(_ =>
                {
                    invocations++;
                    bindable.Fire();
                }));
            }

            bindable.Fire();

            Assert.DoesNotThrow(() => scheduler.Advance(0d));

            Assert.AreEqual(500L, invocations, "connections no mod owns share one fan-out budget");
            Assert.AreEqual(1, hostFaults.Count);
            StringAssert.Contains("connections owned by no mod exceeded 500", hostFaults[0]);
            Assert.IsEmpty(modFaults);
        }

        [Test]
        public void M2_12_QueueCeiling_DropsOverflowAndFaultsTheListenerOnce()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureSignalBudget(1000, 8);
            ModConnectionRegistry registry = new();
            List<string> faults = new();
            RbxError overflow = null;
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                faults.Add(ownerModId);
                overflow = error;
            };
            RbxScriptSignal signal = new("Busy.Event");
            int runs = 0;
            ConnectOwned(registry, scheduler, signal, "mod-a", _ => runs++);
            for (int index = 0; index < 12; index++)
            {
                signal.Fire(index);
            }

            Assert.DoesNotThrow(() => scheduler.Advance(0d));

            Assert.AreEqual(8, runs);
            CollectionAssert.AreEqual(new[] { "mod-a" }, faults);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, overflow.Code);
            StringAssert.Contains("signal queue is full", overflow.RawMessage);
        }

        [Test]
        public void M2_17_InfiniteWaitParksTheThreadUntilItIsCancelled()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread sleeper = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.ScheduleWait(sleeper, double.PositiveInfinity));
            scheduler.Advance(1e9d);
            scheduler.Advance(1e9d);

            Assert.AreEqual(1, sleeper.ResumeCount, "task.wait(math.huge) never resumes on its own");
            Assert.AreEqual(1, scheduler.LiveThreadCount);
            Assert.AreEqual(0, scheduler.TimedEntryCount, "a parked thread holds no heap entry");

            scheduler.Cancel(sleeper);

            Assert.AreEqual(1, sleeper.KillCount);
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_17_InfiniteDelayAndSignalTimeoutParkAndDieWithTheirOwner()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread parkedDelay = (FakeScriptThread)scheduler.Delay("mod-a",
                double.PositiveInfinity, new FakeThreadPlan(), Array.Empty<object>());
            FakeScriptThread signalWaiter = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleSignalWait(signalWaiter, double.PositiveInfinity,
                () => new object[] { "timed out" });
            bool hostCallbackRan = false;
            scheduler.ScheduleHostCallback(double.PositiveInfinity, () => hostCallbackRan = true);

            scheduler.Advance(1e9d);

            Assert.AreEqual(0, parkedDelay.ResumeCount);
            Assert.AreEqual(1, signalWaiter.ResumeCount, "an infinite signal timeout never fires");
            Assert.IsFalse(hostCallbackRan);
            Assert.AreEqual(0, scheduler.TimedEntryCount);

            scheduler.ResumeSignalWait(signalWaiter, new object[] { "fired" });
            Assert.AreEqual("fired", signalWaiter.ResumeArguments[1][0],
                "the signal itself still resumes a waiter with an infinite timeout");

            Assert.AreEqual(2, scheduler.KillOwnedBy("mod-a"));
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_17_NegativeTwin_NaNIsRefusedAndNegativeInfinityMeansNow()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            RbxError delayError = Assert.Throws<RbxError>(() => scheduler.Delay("mod-a", double.NaN,
                new FakeThreadPlan(), Array.Empty<object>()));
            Assert.AreEqual(RbxErrorCode.BadArgument, delayError.Code);
            StringAssert.Contains("NaN", delayError.RawMessage);
            FakeScriptThread caller = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            Assert.Throws<RbxError>(() => scheduler.ScheduleWait(caller, double.NaN));

            scheduler.ScheduleWait(caller, double.NegativeInfinity);
            scheduler.Advance(0d);

            Assert.AreEqual(2, caller.ResumeCount, "a negative duration resumes at the next slot");
        }

        [Test]
        public void M2_01_ThreadEndedInsideASuccessfulResumeWithATerminalFault_IsReported()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> faults = new();
            List<string> successes = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
                faults.Add(ownerModId + ":" + error.Code);
            scheduler.ThreadResumeSucceeded += (string ownerModId, bool completed) =>
                successes.Add(ownerModId);
            RbxError lifetimeCap = new(RbxErrorCode.BudgetExceeded,
                "thread exceeded its lifetime step cap", "yield more often");
            FakeScriptThread capped = (FakeScriptThread)scheduler.Delay("mod-a", 0d,
                new FakeThreadPlan(terminalFault: lifetimeCap), Array.Empty<object>());
            FakeScriptThread normal = (FakeScriptThread)scheduler.Delay("mod-b", 0d,
                new FakeThreadPlan(), Array.Empty<object>());

            Assert.DoesNotThrow(() => scheduler.Advance(0.016d));

            CollectionAssert.AreEqual(new[] { "mod-a:BudgetExceeded" }, faults,
                "a thread the adapter ended inside a successful resume is not dropped silently");
            CollectionAssert.AreEqual(new[] { "mod-b" }, successes,
                "a normal completion is a success, the capped thread is not");
            Assert.IsTrue(capped.IsDead);
            Assert.IsTrue(normal.IsDead);
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void ThreadResumeSucceeded_IsOwnerScopedAndSkipsFaults()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> successes = new();
            List<string> hostFaults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => { };
            scheduler.HostFaulted += (string source, Exception exception) => hostFaults.Add(source);
            scheduler.ThreadResumeSucceeded += (string ownerModId, bool completed) =>
                throw new InvalidOperationException("broken success subscriber");
            scheduler.ThreadResumeSucceeded += (string ownerModId, bool completed) =>
                successes.Add(ownerModId + ":" + (completed ? "completed" : "yielded"));

            FakeScriptThread yielder = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.Spawn("mod-b", new FakeThreadPlan(), Array.Empty<object>());
            scheduler.Spawn("mod-c", new FakeThreadPlan(failure: new RbxError(
                RbxErrorCode.BudgetExceeded, "mod-c failed", "yield sooner")), Array.Empty<object>());
            scheduler.ScheduleWait(yielder, 0d);
            scheduler.Advance(0.016d);

            CollectionAssert.AreEqual(new[] { "mod-a:yielded", "mod-b:completed", "mod-a:yielded" },
                successes, "one event per fault-free resume, never for the faulted mod-c");
            Assert.AreEqual(3, hostFaults.Count,
                "a throwing success subscriber is contained and reported on every call");
            Assert.AreEqual("ThreadResumeSucceeded subscriber", hostFaults[0]);
        }

        [Test]
        public void M2_26_KillOwnedByThousandsOfDelayedThreads_ScansQueuedWorkLinearly()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(ModScheduler.EmergencyMaxThreads, ownerModId => ownerModId);
            const int perMod = ModScheduler.EmergencyMaxThreads / 2;
            List<FakeScriptThread> killed = new();
            List<FakeScriptThread> survivors = new();
            for (int index = 0; index < perMod; index++)
            {
                killed.Add((FakeScriptThread)scheduler.Delay("mod-a", 10d, new FakeThreadPlan(),
                    Array.Empty<object>()));
                survivors.Add((FakeScriptThread)scheduler.Delay("mod-b", 10d, new FakeThreadPlan(),
                    Array.Empty<object>()));
            }

            Assert.AreEqual(perMod, scheduler.KillOwnedBy("mod-a"));

            // WHY a work counter: the old KillOwnedBy rescanned every heap and the deferred queue once
            // per killed thread, perMod * 2 * perMod entry visits (about 8.4 million here).
            Assert.LessOrEqual(scheduler.QueuedWorkScanCount, 4L * ModScheduler.EmergencyMaxThreads);
            Assert.LessOrEqual(scheduler.TimedEntryCount, 2 * perMod + 128,
                "stale entries never outnumber live ones by more than the compaction slack");

            scheduler.Advance(10d);

            Assert.IsTrue(killed.TrueForAll(thread => thread.ResumeCount == 0),
                "a lazily removed entry never resumes its killed thread");
            Assert.IsTrue(survivors.TrueForAll(thread => thread.ResumeCount == 1));
            Assert.AreEqual(0, scheduler.LiveThreadCount);
            Assert.AreEqual(0, scheduler.TimedEntryCount);
        }

        [Test]
        public void M2_26_CancelChurnAndSignalFirstWaits_KeepTheHeapsBounded()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            for (int index = 0; index < 10000; index++)
            {
                IRbxScriptThread delayed = scheduler.Delay("mod-a", 3600d, new FakeThreadPlan(),
                    Array.Empty<object>());
                scheduler.Cancel(delayed);
            }

            Assert.LessOrEqual(scheduler.TimedEntryCount, 65,
                "cancelled long delays are compacted instead of piling up for an hour");
            Assert.LessOrEqual(scheduler.QueuedWorkScanCount, 2L * 10000);

            FakeScriptThread waiter = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            for (int index = 0; index < 1000; index++)
            {
                scheduler.ScheduleSignalWait(waiter, 3600d, () => new object[] { "timed out" });
                scheduler.ResumeSignalWait(waiter, new object[] { index });
            }

            Assert.AreEqual(1001, waiter.ResumeCount);
            Assert.LessOrEqual(scheduler.TimedEntryCount, 65,
                "timeouts of signal waits the signal already resumed are compacted too");
        }

        [Test]
        public void M2_07_ThreadRetired_IsRaisedOnceForEveryWayAThreadEnds()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<IRbxScriptThread> retired = new();
            scheduler.ThreadRetired += thread => retired.Add(thread);
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => { };

            IRbxScriptThread completed = scheduler.Spawn("mod-a", new FakeThreadPlan(),
                Array.Empty<object>());
            IRbxScriptThread cancelled = scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            IRbxScriptThread faulted = scheduler.Spawn("mod-a", new FakeThreadPlan(
                failure: new RbxError(RbxErrorCode.BadArgument, "handler failed", "fix it")),
                Array.Empty<object>());
            IRbxScriptThread killedWithOwner = scheduler.Delay("mod-b", 10d, new FakeThreadPlan(),
                Array.Empty<object>());
            IRbxScriptThread deferred = scheduler.Defer("mod-a", new FakeThreadPlan(),
                Array.Empty<object>());
            IRbxScriptThread parked = scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            scheduler.Cancel(cancelled);
            Assert.AreEqual(1, scheduler.KillOwnedBy("mod-b"));
            scheduler.Advance(0d);

            CollectionAssert.AreEquivalent(
                new[] { completed, cancelled, faulted, killedWithOwner, deferred }, retired,
                "completion, cancel, fault, owner kill and a deferred run each retire their thread");
            Assert.AreEqual(retired.Count, new HashSet<IRbxScriptThread>(retired).Count,
                "no thread is retired twice");
            CollectionAssert.DoesNotContain(retired, parked, "a parked thread is still live");
            Assert.AreEqual(1, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_07_ThrowingThreadRetiredSubscriber_IsContainedAndTheNextOneStillRuns()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> hostFaults = new();
            int observed = 0;
            scheduler.HostFaulted += (string source, Exception exception) => hostFaults.Add(source);
            scheduler.ThreadRetired += thread =>
                throw new InvalidOperationException("broken retire subscriber");
            scheduler.ThreadRetired += thread => observed++;

            Assert.DoesNotThrow(() => scheduler.Spawn("mod-a", new FakeThreadPlan(),
                Array.Empty<object>()));

            Assert.AreEqual(1, observed);
            CollectionAssert.AreEqual(new[] { "ThreadRetired subscriber" }, hostFaults);
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_14_SpawnOfAParkedHandle_ResumesItNowWithTheNewArguments()
        {
            ModScheduler scheduler = CreateScheduler(out _, out FakeThreadFactory factory);
            FakeScriptThread parked = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), new object[] { "first" });

            IRbxScriptThread returned = scheduler.Spawn("mod-a", parked, new object[] { "again" });

            Assert.AreSame(parked, returned, "task.spawn(thread) hands back the thread it was given");
            Assert.AreEqual(2, parked.ResumeCount);
            Assert.AreEqual("again", parked.ResumeArguments[1][0]);
            Assert.AreEqual(1, factory.Created.Count, "no second thread was created");
            Assert.AreEqual(1, scheduler.LiveThreadCount);
        }

        [Test]
        public void M2_14_DeferAndDelayOfAPendingHandle_MoveItAndTheOldSlotNeverFires()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread delayed = (FakeScriptThread)scheduler.Delay("mod-a", 5d,
                new FakeThreadPlan(completeOnResume: false), new object[] { "delay" });
            FakeScriptThread deferred = (FakeScriptThread)scheduler.Defer("mod-a",
                new FakeThreadPlan(completeOnResume: false), new object[] { "defer" });

            Assert.AreSame(delayed, scheduler.Defer("mod-a", delayed, new object[] { "to-defer" }));
            Assert.AreSame(deferred, scheduler.Delay("mod-a", 1d, deferred, new object[] { "to-delay" }));
            scheduler.Advance(0d);

            Assert.AreEqual(1, delayed.ResumeCount, "the delayed thread ran at the next resumption point");
            Assert.AreEqual("to-defer", delayed.ResumeArguments[0][0]);
            Assert.AreEqual(0, deferred.ResumeCount, "the deferred thread left the deferred queue");

            scheduler.Advance(1d);

            Assert.AreEqual(1, deferred.ResumeCount);
            Assert.AreEqual("to-delay", deferred.ResumeArguments[0][0]);

            scheduler.Advance(5d);

            Assert.AreEqual(1, delayed.ResumeCount, "the abandoned delay slot never resumes it again");
            Assert.AreEqual(1, deferred.ResumeCount);
            Assert.AreEqual(2, scheduler.LiveThreadCount, "both threads are parked, not lost");
        }

        [Test]
        public void M2_14_RedeferredHandle_RunsInItsNewQueueSlotAndNeverInTheAbandonedOne()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> order = new();
            FakeScriptThread target = (FakeScriptThread)scheduler.Defer("mod-a",
                new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                {
                    string tag = (string)args[0];
                    order.Add(tag);
                    if (tag == "later")
                    {
                        scheduler.Defer("mod-a", thread, new object[] { "again" });
                    }
                }, false), new object[] { "old" });

            scheduler.Spawn("mod-a", target, new object[] { "now" });
            scheduler.Defer("mod-a", new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                order.Add("other")), Array.Empty<object>());
            scheduler.Defer("mod-a", target, new object[] { "later" });
            scheduler.Advance(0d);

            // WHY this order: the thread's first queue slot was abandoned when task.spawn ran it, and it
            // was deferred again after "other". Resuming it from the abandoned slot would run "later"
            // before "other", and its own re-defer would then run in the same round instead of the next.
            CollectionAssert.AreEqual(new[] { "now", "other", "later", "again" }, order);
            Assert.AreEqual(3, target.ResumeCount,
                "the spawn plus the two deferred resumes, never one from the abandoned slot");
        }

        [Test]
        public void M2_14_NegativeTwin_WaitingDeadForeignAndRunningHandlesAreRefused()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread waiting = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleWait(waiting, 1d);
            FakeScriptThread finished = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(), Array.Empty<object>());
            FakeScriptThread foreign = (FakeScriptThread)scheduler.Spawn("mod-b",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            RbxError runningError = null;
            scheduler.Defer("mod-a", new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
            {
                runningError = Assert.Throws<RbxError>(() =>
                    scheduler.Spawn("mod-a", thread, Array.Empty<object>()));
            }), Array.Empty<object>());

            RbxError waitingError = Assert.Throws<RbxError>(() =>
                scheduler.Spawn("mod-a", waiting, Array.Empty<object>()));
            RbxError deadError = Assert.Throws<RbxError>(() =>
                scheduler.Defer("mod-a", finished, Array.Empty<object>()));
            RbxError foreignError = Assert.Throws<RbxError>(() =>
                scheduler.Delay("mod-a", 0d, foreign, Array.Empty<object>()));
            scheduler.Advance(0d);

            StringAssert.Contains("scheduler wait", waitingError.RawMessage);
            StringAssert.Contains("dead thread", deadError.RawMessage);
            StringAssert.Contains("mod-b", foreignError.RawMessage);
            Assert.IsNotNull(runningError, "task.spawn of the running thread must raise");
            StringAssert.Contains("running thread", runningError.RawMessage);
            Assert.AreEqual(1, waiting.ResumeCount, "a refused reschedule leaves the wait alone");

            scheduler.Advance(1d);

            Assert.AreEqual(2, waiting.ResumeCount, "the wait still resumes its thread exactly once");
            Assert.AreEqual(1, foreign.ResumeCount);
        }

        [Test]
        public void MP_10_SpawnSignalOnBehalfOfAnActor_IsChargedToThatActorsBudgetNotTheOwners()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(2, ownerModId => "host");
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => faults.Add(ownerModId);

            IRbxScriptThread first = scheduler.SpawnSignal("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>(),
                "client-a", 2, out RbxError firstRefusal);
            IRbxScriptThread second = scheduler.SpawnSignal("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>(),
                "client-a", 2, out RbxError secondRefusal);
            IRbxScriptThread third = scheduler.SpawnSignal("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>(),
                "client-a", 2, out RbxError thirdRefusal);

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsNull(firstRefusal);
            Assert.IsNull(secondRefusal);
            Assert.IsNull(third, "the sender's third in-flight handler is refused");
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, thirdRefusal.Code);
            StringAssert.Contains("client-a", thirdRefusal.RawMessage);
            Assert.IsEmpty(faults, "a refused remote-induced start is not the handler owner's fault");
            Assert.AreEqual(2, scheduler.CountInducedThreads("client-a"));

            Assert.DoesNotThrow(() => scheduler.Spawn("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>()));
            Assert.DoesNotThrow(() => scheduler.Spawn("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>()),
                "the owner's own quota of two is untouched by the sender's threads");
            RbxError hostCap = Assert.Throws<RbxError>(() => scheduler.Spawn("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>()));
            Assert.AreEqual(RbxErrorCode.ThreadCap, hostCap.Code);

            IRbxScriptThread otherSender = scheduler.SpawnSignal("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>(),
                "client-b", 2, out RbxError otherRefusal);
            Assert.IsNotNull(otherSender, "another sender has its own budget");
            Assert.IsNull(otherRefusal);

            scheduler.Cancel(first);

            Assert.AreEqual(1, scheduler.CountInducedThreads("client-a"));
            Assert.IsNotNull(scheduler.SpawnSignal("host-mod", new FakeThreadPlan(),
                Array.Empty<object>(), "client-a", 2, out RbxError _));
            Assert.AreEqual(1, scheduler.CountInducedThreads("client-a"),
                "a handler that finished synchronously releases its charge at once");
            Assert.AreEqual(4, scheduler.KillOwnedBy("host-mod"),
                "the owner's teardown kills its two own threads and the two induced ones it runs");
            Assert.AreEqual(0, scheduler.CountInducedThreads("client-a"));
            Assert.AreEqual(0, scheduler.CountInducedThreads("client-b"));
        }

        [Test]
        public void MP_10_SignalsFiredOnBehalfOfAnActor_TagOnlyTheHandlerInvocationTheyQueue()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            RbxScriptSignal signal = new("RemoteEvent.OnServerEvent");
            signal.BindScheduler(scheduler);
            List<string> seen = new();
            signal.Connect((Action<object[]>)(_ =>
            {
                seen.Add(scheduler.CurrentSignalQuotaActorId ?? "none");
                scheduler.Spawn("host-mod", new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
                    seen.Add("in-thread:" + (scheduler.CurrentSignalQuotaActorId ?? "none"))),
                    Array.Empty<object>());
            }));

            string previous = scheduler.BeginSignalsOnBehalfOf("client-a");
            signal.Fire();
            scheduler.EndSignalsOnBehalfOf(previous);
            signal.Fire();
            scheduler.Advance(0d);

            CollectionAssert.AreEqual(
                new[] { "client-a", "in-thread:none", "none", "in-thread:none" }, seen,
                "the sender reaches the invocation it fired, never the code that invocation runs");
            Assert.IsNull(scheduler.CurrentSignalQuotaActorId);
        }

        [Test]
        public void A4_01_ThreadsAHandlerChargedToASenderStarts_AreChargedToThatSender_NotTheOwner()
        {
            // WHY: a handler a remote call started was charged to the sender, but the task.delay it
            // made was charged to the handler's owner, so one client firing at a handler that
            // schedules work filled the host's whole thread quota (A4-01).
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(2, ownerModId => "host");
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => faults.Add(ownerModId);
            List<string> outcomes = new();
            FakeThreadPlan handler = new((FakeScriptThread thread, object[] args) =>
            {
                for (int index = 0; index < 3; index++)
                {
                    try
                    {
                        scheduler.Delay("host-mod", 60d, new FakeThreadPlan(), Array.Empty<object>());
                        outcomes.Add("started");
                    }
                    catch (RbxError error)
                    {
                        outcomes.Add(RbxError.ToWireName(error.Code) + ":" + error.RawMessage);
                    }
                }
            }, completeOnResume: false);

            IRbxScriptThread started = scheduler.SpawnSignal("host-mod", handler, Array.Empty<object>(),
                "client-a", 3, out RbxError refusal);

            Assert.IsNotNull(started);
            Assert.IsNull(refusal);
            Assert.AreEqual(3, outcomes.Count);
            Assert.AreEqual("started", outcomes[0]);
            Assert.AreEqual("started", outcomes[1]);
            StringAssert.StartsWith("BUDGET_EXCEEDED:", outcomes[2],
                "the third thread would be the sender's fourth in flight");
            StringAssert.Contains("client-a", outcomes[2]);
            StringAssert.Contains("task.delay", outcomes[2]);
            Assert.AreEqual(3, scheduler.CountInducedThreads("client-a"),
                "the handler and the two delayed threads it started are all the sender's");
            Assert.IsEmpty(faults, "a refusal the handler caught is nobody's fault");
            Assert.DoesNotThrow(() => scheduler.Spawn("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>()));
            Assert.DoesNotThrow(() => scheduler.Spawn("host-mod",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>()),
                "the owner's own quota of two is untouched by what the sender's handler scheduled");

            Assert.AreEqual(5, scheduler.KillOwnedBy("host-mod"));
            Assert.AreEqual(0, scheduler.CountInducedThreads("client-a"),
                "every inherited charge is released with its thread");
        }

        [Test]
        public void A4_01_SpawnAndDeferInsideAChargedHandler_InheritTheCharge_AndSoDoesTheirOwnWork()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(8, ownerModId => "host");
            FakeThreadPlan grandchild = new(completeOnResume: false);
            FakeThreadPlan child = new((FakeScriptThread thread, object[] args) =>
                scheduler.Spawn("host-mod", grandchild, Array.Empty<object>()), completeOnResume: false);
            FakeThreadPlan handler = new((FakeScriptThread thread, object[] args) =>
            {
                scheduler.Spawn("host-mod", child, Array.Empty<object>());
                scheduler.Defer("host-mod", new FakeThreadPlan(completeOnResume: false),
                    Array.Empty<object>());
            }, completeOnResume: false);

            scheduler.SpawnSignal("host-mod", handler, Array.Empty<object>(), "client-a", 32,
                out RbxError _);
            scheduler.Advance(0d);

            Assert.AreEqual(4, scheduler.CountInducedThreads("client-a"),
                "the handler, its spawned child, that child's own spawn and the deferred thread");
            Assert.AreEqual(4, scheduler.LiveThreadCount);
            scheduler.Spawn("host-mod", new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            Assert.AreEqual(4, scheduler.CountInducedThreads("client-a"),
                "a thread the host starts itself stays the host's");
        }

        [Test]
        public void A4_01_AHandlerThatDiesOfItsSendersRefusal_IsNotItsOwnersFault()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => faults.Add(ownerModId);
            FakeThreadPlan scheduling = new((FakeScriptThread thread, object[] args) =>
                scheduler.Delay("host-mod", 60d, new FakeThreadPlan(), Array.Empty<object>()));
            scheduler.SpawnSignal("host-mod", scheduling, Array.Empty<object>(), "client-a", 2,
                out RbxError _);
            Assert.AreEqual(1, scheduler.CountInducedThreads("client-a"),
                "the first handler finished; the thread it delayed is still in flight on its sender");
            RbxError caught = null;
            FakeThreadPlan dying = null;
            dying = new FakeThreadPlan((FakeScriptThread thread, object[] args) =>
            {
                try
                {
                    scheduler.Spawn("host-mod", new FakeThreadPlan(), Array.Empty<object>());
                }
                catch (RbxError error)
                {
                    caught = error;
                    dying.Failure = error;
                }
            }, completeOnResume: false);

            IRbxScriptThread handler = scheduler.SpawnSignal("host-mod", dying, Array.Empty<object>(),
                "client-a", 2, out RbxError _);

            Assert.IsNotNull(caught, "the second handler and the delayed thread fill the budget of two");
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, caught.Code);
            Assert.IsTrue(handler.IsDead, "the refusal went uncaught and ended the handler");
            Assert.IsEmpty(faults,
                "a handler that died because its sender's budget was full did nothing wrong");
            Assert.AreEqual(1, scheduler.CountInducedThreads("client-a"),
                "the dead handler released its charge; only the delayed thread is still in flight");
        }

        [Test]
        public void A4_01_Negative_AChargedHandlerThatFailsForItsOwnReason_IsStillItsOwnersFault()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<RbxError> faults = new();
            scheduler.ThreadFaulted += (string ownerModId, RbxError error) => faults.Add(error);
            FakeThreadPlan broken = new(completeOnResume: false,
                failure: RbxError.BadArgument("attempt to index nil", "check the value first"));
            FakeThreadPlan forgedExcuse = new(completeOnResume: false,
                failure: new RbxError(RbxErrorCode.BudgetExceeded,
                    "actor 'client-a' already has 1 threads in flight that its remote calls started; "
                    + "task.spawn in mod 'host-mod' did not start another one",
                    "wait for earlier remote calls to finish before sending more"));

            scheduler.SpawnSignal("host-mod", broken, Array.Empty<object>(), "client-a", 4,
                out RbxError _);
            scheduler.SpawnSignal("host-mod", forgedExcuse, Array.Empty<object>(), "client-a", 4,
                out RbxError _);

            Assert.AreEqual(2, faults.Count,
                "only a refusal the scheduler raised inside the thread excuses its death; an error "
                + "that merely reads like one is the owner's own");
            Assert.AreEqual(RbxErrorCode.BadArgument, faults[0].Code);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, faults[1].Code);
            Assert.AreEqual(0, scheduler.CountInducedThreads("client-a"));
        }

        [Test]
        public void A4_01_ARefusedInheritedStart_IsCountedAndRaisedWithItsSender()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            List<string> refusedSenders = new();
            scheduler.InducedThreadRefused += (string sender, RbxError refusal) =>
                refusedSenders.Add(sender + ":" + RbxError.ToWireName(refusal.Code));
            FakeThreadPlan handler = new((FakeScriptThread thread, object[] args) =>
            {
                for (int index = 0; index < 3; index++)
                {
                    try
                    {
                        scheduler.Defer("host-mod", new FakeThreadPlan(), Array.Empty<object>());
                    }
                    catch (RbxError)
                    {
                    }
                }
            }, completeOnResume: false);

            scheduler.SpawnSignal("host-mod", handler, Array.Empty<object>(), "client-a", 2,
                out RbxError _);

            Assert.AreEqual(2L, scheduler.InducedThreadRefusals);
            CollectionAssert.AreEqual(new[] { "client-a:BUDGET_EXCEEDED", "client-a:BUDGET_EXCEEDED" },
                refusedSenders);
        }

        [Test]
        public void M2_19_RollbackUnfinishedYield_ReturnsAWaitingRecordToRunningAndDropsItsSlots()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            FakeScriptThread waiter = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleWait(waiter, 0.5d);
            Assert.Throws<RbxError>(() => scheduler.ScheduleWait(waiter, 1d),
                "without a rollback the leftover wait refuses every later one");

            Assert.IsTrue(scheduler.RollbackUnfinishedYield(waiter));
            scheduler.ScheduleWait(waiter, 1d);
            scheduler.Advance(0.5d);

            Assert.AreEqual(1, waiter.ResumeCount, "the rolled-back wait's slot is stale");

            scheduler.Advance(0.5d);

            Assert.AreEqual(2, waiter.ResumeCount, "the new wait resumes it");

            FakeScriptThread signalWaiter = (FakeScriptThread)scheduler.Spawn("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.ScheduleSignalWait(signalWaiter, 1d, () => new object[] { "timeout" });

            Assert.IsTrue(scheduler.RollbackUnfinishedYield(signalWaiter));
            scheduler.ResumeSignalWait(signalWaiter, new object[] { "late fire" });
            scheduler.Advance(1d);

            Assert.AreEqual(1, signalWaiter.ResumeCount,
                "neither the old signal nor the old timeout resumes a rolled-back waiter");
            Assert.IsFalse(scheduler.RollbackUnfinishedYield(signalWaiter),
                "a record that waits for nothing has nothing to roll back");
            Assert.IsFalse(scheduler.RollbackUnfinishedYield(waiter));
        }

        [Test]
        public void M2_20_ThreadCapRefusal_NamesThreadsParkedOutsideTheScheduler()
        {
            ModScheduler scheduler = CreateScheduler(out _, out _);
            scheduler.ConfigureActorQuota(2, ownerModId => "actor-a");
            scheduler.Spawn("mod-a", new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            scheduler.Spawn("mod-a", new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            RbxError parkedCap = Assert.Throws<RbxError>(() => scheduler.Spawn("mod-a",
                new FakeThreadPlan(), Array.Empty<object>()));

            Assert.AreEqual(RbxErrorCode.ThreadCap, parkedCap.Code);
            StringAssert.Contains("live scheduler threads quota reached (limit 2)", parkedCap.RawMessage);
            StringAssert.Contains("2 of them are parked", parkedCap.RawMessage);
            StringAssert.Contains("task.cancel", parkedCap.Fix);

            ModScheduler waitingScheduler = CreateScheduler(out _, out _);
            waitingScheduler.ConfigureActorQuota(2, ownerModId => "actor-a");
            for (int index = 0; index < 2; index++)
            {
                FakeScriptThread waiting = (FakeScriptThread)waitingScheduler.Spawn("mod-a",
                    new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
                waitingScheduler.ScheduleWait(waiting, 10d);
            }

            RbxError waitingCap = Assert.Throws<RbxError>(() => waitingScheduler.Spawn("mod-a",
                new FakeThreadPlan(), Array.Empty<object>()));

            Assert.AreEqual(RbxErrorCode.ThreadCap, waitingCap.Code);
            StringAssert.DoesNotContain("parked", waitingCap.RawMessage,
                "threads waiting in the scheduler are not parked");
        }

        private static RbxScriptConnection ConnectOwned(ModConnectionRegistry registry,
            ModScheduler scheduler, RbxScriptSignal signal, string ownerModId, Action<object[]> handler)
        {
            signal.BindScheduler(scheduler);
            RbxScriptConnection connection = signal.Connect(handler);
            registry.Track(ownerModId, 1, connection);
            return connection;
        }

        /// <summary>Production-shaped handler: every invocation runs as a scheduler-owned thread.</summary>
        private static RbxScriptConnection ConnectThreadHandler(ModConnectionRegistry registry,
            ModScheduler scheduler, RbxScriptSignal signal, string ownerModId, FakeThreadPlan plan)
        {
            return ConnectOwned(registry, scheduler, signal, ownerModId,
                args => scheduler.SpawnSignal(ownerModId, plan, args));
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
