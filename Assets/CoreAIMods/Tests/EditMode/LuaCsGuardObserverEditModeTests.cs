#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the guard observability seam: an <see cref="ILuaCsGuardObserver"/>
    /// supplied through production composition must see exactly one cost record per guarded
    /// execution — including trips and re-entrant calls — while a null observer changes nothing.
    /// </summary>
    [TestFixture]
    public sealed class LuaCsGuardObserverEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The guard bridges the async VM to sync call sites via <c>GetAwaiter().GetResult()</c>; with a
        /// main-thread <see cref="SynchronizationContext"/> installed those continuations would deadlock.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class RecordingObserver : ILuaCsGuardObserver
        {
            public readonly List<LuaCsGuardExecutionRecord> Records = new();

            public void OnGuardedExecutionCompleted(in LuaCsGuardExecutionRecord record)
            {
                Records.Add(record);
            }
        }

        [Test]
        public void ObserverThroughProductionConstructor_ReceivesExactlyOneRecordPerExecute()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(2000, 200_000, guardObserver: observer);

            LuaValue[] chunkResults = env.RunChunk(state,
                "local x = 0\n" +
                "for i = 1, 100 do x = x + i end\n" +
                "return x",
                guard);
            LuaFunction function = env.RunChunk(state, "return function(a) return a * 2 end")[0].Read<LuaFunction>();
            LuaValue[] callResults = guard.Execute(state, function, CancellationToken.None, new LuaValue(21d));
            int genericResult = guard.Execute<int>(state, state.Load("return 6 * 7", "seam_probe"));

            Assert.AreEqual(3, observer.Records.Count,
                "Each Execute overload (chunk, function call, generic) must emit exactly one record.");
            foreach (LuaCsGuardExecutionRecord record in observer.Records)
            {
                Assert.IsTrue(record.Completed, "A normal finish must report Completed == true.");
                Assert.AreEqual(LuaCsGuardTripKind.None, record.TrippedBudget,
                    "A normal finish must not report a budget trip.");
                Assert.GreaterOrEqual(record.Steps, 0, "Steps is a count and can never be negative.");
                Assert.GreaterOrEqual(record.ElapsedTicks, 0, "Elapsed wall-clock ticks must be reported.");
            }

            // WHY: only the loop is long enough to charge anything. `return a * 2` and `return 6 * 7`
            // are shorter than one hook batch, so they legitimately report zero — see
            // ShortBodyBelowTheHookBatch_ReportsZeroSteps for why that is the contract, not a bug.
            Assert.Greater(observer.Records[0].Steps, 0, "A loop body must charge guarded instructions.");

            Assert.AreEqual(5050, (int)chunkResults[0].Read<double>());
            Assert.AreEqual(42, (int)callResults[0].Read<double>());
            Assert.AreEqual(42, genericResult);
        }

        [Test]
        public void Steps_GrowWithLongerLoop()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(2000, 200_000, guardObserver: observer);

            env.RunChunk(state, "local x = 0\nfor i = 1, 100 do x = x + 1 end\nreturn x", guard);
            env.RunChunk(state, "local x = 0\nfor i = 1, 10000 do x = x + 1 end\nreturn x", guard);

            Assert.AreEqual(2, observer.Records.Count);
            Assert.Greater(observer.Records[0].Steps, 0,
                "Even the short loop must charge guarded instructions.");
            Assert.Greater(observer.Records[1].Steps, observer.Records[0].Steps,
                "A 100x longer loop must charge more guarded instructions.");
        }

        [Test]
        public void ThrowingClosure_StillProducesRecord_WithCompletedFalseAndNoTrip()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(2000, 200_000, guardObserver: observer);

            Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "error('boom')", guard));

            Assert.AreEqual(1, observer.Records.Count,
                "An execution that ended by throwing must still produce exactly one record.");
            Assert.IsFalse(observer.Records[0].Completed,
                "An execution that ended by throwing must report Completed == false.");
            Assert.AreEqual(LuaCsGuardTripKind.None, observer.Records[0].TrippedBudget,
                "A mod's own error() is not a guard trip and must not be classified as one.");
        }

        [Test]
        [Timeout(15000)]
        public void StepBudgetTrip_ReportsTrippedBudgetSteps()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(60_000, 5_000, 0, guardObserver: observer);

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "for i = 1, 5000000 do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"));
            Assert.AreEqual(1, observer.Records.Count);
            Assert.IsFalse(observer.Records[0].Completed);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, observer.Records[0].TrippedBudget,
                "A step-budget trip must report TrippedBudget == Steps.");
        }

        [Test]
        [Timeout(15000)]
        public void TimeoutTrip_ReportsTrippedBudgetTimeout()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(150, 5_000_000_000L, 0, guardObserver: observer);

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "while true do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("exceeded"));
            Assert.AreEqual(1, observer.Records.Count);
            Assert.IsFalse(observer.Records[0].Completed);
            Assert.AreEqual(LuaCsGuardTripKind.Timeout, observer.Records[0].TrippedBudget,
                "A wall-clock trip must report TrippedBudget == Timeout.");
        }

        [Test]
        [Timeout(15000)]
        public void NestedExecutions_EachProduceOwnRecordWithoutClobbering()
        {
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            RecordingObserver innerObserver = new();
            LuaCsExecutionGuard nestedGuard = new(2000, 10_000, guardObserver: innerObserver);
            LuaState state = null;
            LuaFunction noop = null;
            registry.Register("nested", new System.Func<double>(() =>
            {
                LuaValue[] r = nestedGuard.Execute(state, noop, CancellationToken.None);
                return r.Length > 0 ? r[0].Read<double>() : 0d;
            }));
            state = env.Create(registry);
            // WHY: the inner body loops instead of returning a constant. A one-instruction body is
            // shorter than the hook batch and would charge zero steps, which would make the
            // non-clobbering assertion below vacuous — two zeroes cannot clobber each other.
            noop = env.RunChunk(state,
                "return function() local s = 0 for i = 1, 200 do s = s + i end return s end")[0]
                .Read<LuaFunction>();

            RecordingObserver outerObserver = new();
            LuaCsExecutionGuard outerGuard = new(2000, 5_000, guardObserver: outerObserver);
            Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "nested()\n" +
                    "local x = 0\n" +
                    "for i = 1, 100000 do x = x + 1 end\n" +
                    "return x",
                    outerGuard));

            Assert.AreEqual(1, innerObserver.Records.Count,
                "The nested execution must produce its own record.");
            Assert.IsTrue(innerObserver.Records[0].Completed);
            Assert.AreEqual(LuaCsGuardTripKind.None, innerObserver.Records[0].TrippedBudget);
            Assert.Greater(innerObserver.Records[0].Steps, 0);

            Assert.AreEqual(1, outerObserver.Records.Count,
                "The outer execution must produce its own record.");
            Assert.IsFalse(outerObserver.Records[0].Completed);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, outerObserver.Records[0].TrippedBudget);
            Assert.Greater(outerObserver.Records[0].Steps, 0,
                "The outer record must carry the outer hook's own steps, not the inner call's.");
        }

        [Test]
        public void NoObserver_BehaviourAndResultsUnchanged()
        {
            const string source =
                "local x = 0\n" +
                "for i = 1, 100 do x = x + i end\n" +
                "return x";

            LuaCsSecureEnvironment plainEnv = new();
            LuaValue[] plainResult = plainEnv.RunChunk(
                plainEnv.Create(), source, new LuaCsExecutionGuard(2000, 200_000));

            LuaCsSecureEnvironment observedEnv = new();
            RecordingObserver observer = new();
            LuaValue[] observedResult = observedEnv.RunChunk(
                observedEnv.Create(), source, new LuaCsExecutionGuard(2000, 200_000, guardObserver: observer));

            Assert.AreEqual(5050, (int)plainResult[0].Read<double>());
            Assert.AreEqual((int)plainResult[0].Read<double>(), (int)observedResult[0].Read<double>(),
                "Supplying an observer must not change the guarded result.");
            Assert.AreEqual(1, observer.Records.Count);
        }

        [Test]
        public void ProductionEngine_PassesObserverToCreatedGuards()
        {
            RecordingObserver observer = new();
            LuaCsScriptEngine engine = new(guardObserver: observer);
            IScriptState state = engine.CreateState();
            IScriptExecutionGuard guard = engine.CreateGuard(new ExecutionBudget(2000, 200_000));

            object[] results = engine.RunChunk(state,
                "local x = 0\n" +
                "for i = 1, 100 do x = x + i end\n" +
                "return x",
                guard);

            Assert.AreEqual(1, observer.Records.Count,
                "A guard created through the production engine must report to the engine observer.");
            Assert.IsTrue(observer.Records[0].Completed);
            Assert.AreEqual(LuaCsGuardTripKind.None, observer.Records[0].TrippedBudget);
            Assert.Greater(observer.Records[0].Steps, 0);
            // WHY: IScriptEngine.RunChunk returns SCRIPT values boxed as object, not CLR numbers.
            // Unwrap through the engine's own marshaller, the way the seam tests already do.
            Assert.AreEqual(5050d, engine.Marshaller.ToHostValue(results[0]));
        }

        [Test]
        public void ShortBodyBelowTheHookBatch_ReportsZeroSteps()
        {
            // WHY: this is the seam's granularity, stated out loud because the whole point of the seam
            // is measurement. The count hook fires once per HookInstructionBatch instructions and
            // charges the whole batch, so a body shorter than one batch never fires it and is reported
            // as zero — not as "a few". Anything deriving a frame budget from these records must treat
            // Steps as a batch-granular lower bound, never as an exact instruction count.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(2000, 200_000, guardObserver: observer);

            int result = guard.Execute<int>(state, state.Load("return 6 * 7", "seam_short_body"));

            Assert.AreEqual(42, result, "the execution itself must still be correct");
            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(0, observer.Records[0].Steps,
                "a body shorter than one hook batch charges nothing; the seam must not invent a number");
            Assert.IsTrue(observer.Records[0].Completed);
        }
    }
}
#endif
