using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scheduling
{
    /// <summary>
    /// Scheduler-level proof (engine-free fakes) that signal-handler records are pooled only when the
    /// handler finished inside its first resume, that pooled records are never live threads, and that
    /// the emergency thread ceiling still bites for a mod genuinely holding many live threads.
    /// </summary>
    [TestFixture]
    public sealed class ModSchedulerRecordPoolEditModeTests
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

            public List<object[]> ResumeArguments { get; } = new();

            public RbxScriptThreadResumeResult Resume(params object[] args)
            {
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
                Status = RbxScriptThreadStatus.Dead;
            }
        }

        private sealed class FakeThreadFactory : IRbxScriptThreadFactory
        {
            public List<FakeScriptThread> Created { get; } = new();

            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                FakeScriptThread thread = new(ownerModId, (FakeThreadPlan)callable);
                Created.Add(thread);
                return thread;
            }
        }

        private static ModScheduler CreateScheduler(out FakeThreadFactory factory)
        {
            factory = new FakeThreadFactory();
            return new ModScheduler(factory, new FakeTimeSource());
        }

        [Test]
        public void SpawnSignal_HandlerThatCompletes_LeavesNoLiveThreadAndReusesOneRecord()
        {
            ModScheduler scheduler = CreateScheduler(out FakeThreadFactory factory);

            for (int fire = 0; fire < 50; fire++)
            {
                scheduler.SpawnSignal("mod-a", new FakeThreadPlan(), new object[] { fire });
                Assert.AreEqual(0, scheduler.LiveThreadCount);
            }

            Assert.AreEqual(50, factory.Created.Count, "every fire still gets its own thread from the factory");
            Assert.AreEqual(1, scheduler.PooledRecordCount,
                "one record cycles through fifty fires instead of fifty records being allocated");
            Assert.AreEqual(49, factory.Created[49].ResumeArguments[0][0],
                "each tenant is resumed with its own arguments");
        }

        [Test]
        public void SpawnSignal_HandlerThatYields_KeepsALiveRecordThatIsNotPooled()
        {
            ModScheduler scheduler = CreateScheduler(out _);

            IRbxScriptThread thread = scheduler.SpawnSignal("mod-a",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            Assert.IsFalse(thread.IsDead);
            Assert.AreEqual(1, scheduler.LiveThreadCount);
            Assert.AreEqual(0, scheduler.PooledRecordCount,
                "a record whose thread is still alive is never offered for reuse");

            scheduler.ScheduleWait(thread);
            scheduler.Advance(0.1d);
            Assert.AreEqual(2, ((FakeScriptThread)thread).ResumeCount, "task.wait inside the handler still resumes it");
            Assert.AreEqual(1, scheduler.LiveThreadCount);
            Assert.AreEqual(0, scheduler.PooledRecordCount);
        }

        [Test]
        public void SpawnSignal_HandlerThatFaults_DoesNotPoolItsRecord()
        {
            ModScheduler scheduler = CreateScheduler(out _);
            RbxError observed = null;
            scheduler.ThreadFaulted += (ownerModId, error) => observed = error;

            scheduler.SpawnSignal("mod-a",
                new FakeThreadPlan(failure: new RbxError(RbxErrorCode.BudgetExceeded,
                    "handler blew its budget", "yield sooner")), Array.Empty<object>());

            Assert.IsNotNull(observed);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, observed.Code);
            Assert.AreEqual(0, scheduler.LiveThreadCount);
            Assert.AreEqual(0, scheduler.PooledRecordCount,
                "a faulted record keeps its GC lifetime; only a clean synchronous finish is pooled");
        }

        [Test]
        public void ReusedRecord_StartsCleanForItsNextTenant()
        {
            ModScheduler scheduler = CreateScheduler(out FakeThreadFactory factory);
            scheduler.Spawn("mod-a", new FakeThreadPlan(), Array.Empty<object>());
            Assert.AreEqual(1, scheduler.PooledRecordCount, "the completed spawn released its record");

            scheduler.Defer("mod-b", new FakeThreadPlan(), new object[] { "deferred-arg" });
            Assert.AreEqual(0, scheduler.PooledRecordCount, "the deferred thread rented the pooled record");
            Assert.AreEqual(1, scheduler.LiveThreadCount);

            scheduler.Advance(0.016d);

            FakeScriptThread deferred = factory.Created[1];
            Assert.AreEqual("mod-b", deferred.OwnerModId);
            Assert.AreEqual(1, deferred.ResumeCount);
            Assert.AreEqual("deferred-arg", deferred.ResumeArguments[0][0],
                "the reused record carries only its new tenant's deferred arguments");
            Assert.AreEqual(0, scheduler.LiveThreadCount);
        }

        [Test]
        public void EmergencyMaxThreads_StillTripsForAModHoldingManyLiveSignalHandlerThreads()
        {
            ModScheduler scheduler = CreateScheduler(out _);
            scheduler.ConfigureActorQuota(ModScheduler.EmergencyMaxThreads + 1, ownerModId => ownerModId);
            List<RbxError> faults = new();
            scheduler.ThreadFaulted += (ownerModId, error) => faults.Add(error);

            for (int index = 0; index < ModScheduler.EmergencyMaxThreads; index++)
            {
                scheduler.SpawnSignal("runaway",
                    new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());
            }

            Assert.AreEqual(ModScheduler.EmergencyMaxThreads, scheduler.LiveThreadCount);
            Assert.IsEmpty(faults);

            IRbxScriptThread refused = scheduler.SpawnSignal("runaway",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            Assert.IsNull(refused);
            Assert.AreEqual(1, faults.Count);
            Assert.AreEqual(RbxErrorCode.ThreadCap, faults[0].Code);
            StringAssert.Contains("emergency", faults[0].RawMessage);
            Assert.AreEqual(ModScheduler.EmergencyMaxThreads, scheduler.LiveThreadCount);
        }

        [Test]
        public void PooledRecords_NeverCountTowardTheEmergencyCap()
        {
            ModScheduler scheduler = CreateScheduler(out _);
            scheduler.ConfigureActorQuota(ModScheduler.EmergencyMaxThreads + 1, ownerModId => ownerModId);
            List<RbxError> faults = new();
            scheduler.ThreadFaulted += (ownerModId, error) => faults.Add(error);

            for (int index = 0; index < ModScheduler.EmergencyMaxThreads + 10; index++)
            {
                scheduler.SpawnSignal("busy", new FakeThreadPlan(), Array.Empty<object>());
            }

            Assert.AreEqual(0, scheduler.LiveThreadCount);
            Assert.AreEqual(1, scheduler.PooledRecordCount);

            IRbxScriptThread admitted = scheduler.SpawnSignal("busy",
                new FakeThreadPlan(completeOnResume: false), Array.Empty<object>());

            Assert.IsNotNull(admitted);
            Assert.IsEmpty(faults, "four thousand finished handlers hide nothing: the cap counts live threads only");
            Assert.AreEqual(1, scheduler.LiveThreadCount);
        }
    }
}
