#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the two guarantees a runaway one-shot chunk must give: it is cut by the
    /// budget it actually exceeded (never by the allocation backstop reacting to the runtime's own
    /// transient garbage), and it does not hold the host frame while it runs.
    /// </summary>
    [TestFixture]
    public sealed class LuaCsGuardFrameAndAllocationEditModeTests
    {
        private const string UnboundedArithmeticLoop =
            "local i = 0\n" +
            "while true do i = i + 1 end\n" +
            "return i";

        private const string LongArithmeticLoop =
            "local s = 0\n" +
            "for i = 1, 2000000 do s = s + i end\n" +
            "return s";

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

        /// <summary>
        /// Counts frame releases and completes immediately, so a test can drive the asynchronous path
        /// synchronously without a running player loop.
        /// </summary>
        private sealed class CountingFrameYielder : IScriptFrameYielder
        {
            public int Count;

            public ValueTask YieldFrameAsync(CancellationToken cancellationToken)
            {
                Count++;
                return default;
            }
        }

        [Test]
        public void SampledGarbageGrowth_IsNotAMemoryBudgetTrip()
        {
            LuaCsAllocationBudget budget = default;
            budget.Reset(8 * 1024 * 1024);

            // WHY: 64 MB allocated and dropped on the floor. The cheap sampled reading the backstop takes
            // is garbage-inclusive and process-wide, so it may well cross the 8 MB budget here — and that
            // is exactly the reading that must NOT decide the trip. This is the defect that cut a
            // pure-arithmetic Lua runaway with EXCEEDED_MEMORY_BUDGET instead of its own wall clock.
            for (int i = 0; i < 1024; i++)
            {
                byte[] garbage = new byte[64 * 1024];
                garbage[0] = (byte)i;
            }

            Assert.IsFalse(budget.IsExceeded(),
                "growth that does not survive a collection is the runtime's garbage, not the script's cost");
        }

        [Test]
        public void RetainedGrowthBeyondBudget_IsAMemoryBudgetTrip()
        {
            LuaCsAllocationBudget budget = default;
            budget.Reset(8 * 1024 * 1024);

            List<byte[]> retained = new();
            for (int i = 0; i < 512; i++)
            {
                retained.Add(new byte[64 * 1024]);
            }

            Assert.IsTrue(budget.IsExceeded(),
                "growth that survives a collection is live and must still trip the anti-bomb backstop");
            GC.KeepAlive(retained);
        }

        [Test]
        [Timeout(60000)]
        public void UnboundedArithmeticLoop_TripsTimeout_NotTheAllocationBackstop()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(
                1000,
                5_000_000_000L,
                LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget,
                guardObserver: observer);

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, UnboundedArithmeticLoop, guard));

            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Timeout, observer.Records[0].TrippedBudget,
                "a loop that retains nothing must be cut by the wall clock it actually exceeded");
            Assert.AreNotEqual(LuaCsGuardTripKind.Memory, observer.Records[0].TrippedBudget,
                "the allocation backstop must not pre-empt the wall clock on a script that allocates nothing");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(ex),
                "a wall-clock trip must never be classified as a memory-budget trip");
        }

        [Test]
        [Timeout(60000)]
        public void SmallStepBudget_TripsSteps_NotTheAllocationBackstop()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(
                60_000,
                5_000,
                LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget,
                guardObserver: observer);

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, UnboundedArithmeticLoop, guard));

            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, observer.Records[0].TrippedBudget,
                "a long timeout with a small step budget must be cut by the step budget");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(ex));
        }

        [Test]
        [Timeout(60000)]
        public void ConcatenationBomb_StillTripsTheMemoryBudget()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(
                20_000,
                5_000_000_000L,
                8 * 1024 * 1024,
                guardObserver: observer);

            // WHY: the anti-bomb guarantee the confirmation must not trade away. Doubling concatenation
            // has no library call site to cap and its result string is LIVE, so it survives the
            // confirming collection and must still be cut.
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "local s = 'x'\nwhile true do s = s .. s end\nreturn s", guard));

            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Memory, observer.Records[0].TrippedBudget,
                "a live doubling string must still trip the allocation backstop");
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(ex),
                "the trip must stay classified by type so a mod cannot forge or suppress it");
        }

        [Test]
        [Timeout(120000)]
        public async Task AsyncExecute_ReleasesTheFrameRepeatedly_WhileSyncExecuteNeverDoes()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            LuaCsExecutionGuard guard = new(60_000, 5_000_000_000L, 0);
            CountingFrameYielder yielder = new();

            LuaValue[] asyncResults = await guard.ExecuteAsync(
                state,
                state.Load(LongArithmeticLoop, "frame_yield_probe"),
                yielder,
                CancellationToken.None);

            Assert.Greater(yielder.Count, 1,
                "a chunk that runs for many milliseconds must hand the frame back more than once");

            int afterAsync = yielder.Count;
            LuaValue[] syncResults = guard.Execute(
                state,
                state.Load(LongArithmeticLoop, "frame_yield_probe_sync"));

            Assert.AreEqual(afterAsync, yielder.Count,
                "the synchronous entry must never arm a yielder: its caller is blocked and could not resume it");
            Assert.AreEqual(
                syncResults[0].Read<double>(),
                asyncResults[0].Read<double>(),
                "yielding must not change what the chunk computes");
        }

        [Test]
        [Timeout(60000)]
        public void AsyncExecute_HonoursCancellation()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            LuaCsExecutionGuard guard = new(60_000, 5_000_000_000L, 0);
            CountingFrameYielder yielder = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.ThrowsAsync<LuaCanceledException>(async () =>
                await guard.ExecuteAsync(
                    state,
                    state.Load(UnboundedArithmeticLoop, "cancel_probe"),
                    yielder,
                    cancellation.Token));

            // WHY: the state must be usable afterwards — a cancelled async run that left its hook behind
            // would silently keep charging the next execution's budget.
            LuaValue[] results = guard.Execute(state, state.Load("return 6 * 7", "cancel_probe_after"));
            Assert.AreEqual(42, (int)results[0].Read<double>());
        }

        [Test]
        [Timeout(60000)]
        public void AsyncExecute_NestedSynchronousCall_RestoresTheOuterGuardHook()
        {
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            RecordingObserver innerObserver = new();
            LuaCsExecutionGuard nestedGuard = new(20_000, 100_000, 0, guardObserver: innerObserver);
            LuaState state = null;
            LuaFunction inner = null;
            registry.Register("nested", new Func<double>(() =>
            {
                LuaValue[] r = nestedGuard.Execute(state, inner, CancellationToken.None);
                return r.Length > 0 ? r[0].Read<double>() : 0d;
            }));
            state = env.Create(registry);
            inner = env.RunChunk(state,
                "return function() local s = 0 for i = 1, 200 do s = s + i end return s end")[0]
                .Read<LuaFunction>();

            RecordingObserver outerObserver = new();
            LuaCsExecutionGuard outerGuard = new(20_000, 5_000, 0, guardObserver: outerObserver);
            CountingFrameYielder yielder = new();

            Assert.ThrowsAsync<LuaRuntimeException>(async () =>
                await outerGuard.ExecuteAsync(
                    state,
                    state.Load(
                        "nested()\n" +
                        "local x = 0\n" +
                        "for i = 1, 100000 do x = x + 1 end\n" +
                        "return x",
                        "nested_async_probe"),
                    yielder,
                    CancellationToken.None));

            Assert.AreEqual(1, innerObserver.Records.Count);
            Assert.IsTrue(innerObserver.Records[0].Completed);
            Assert.AreEqual(1, outerObserver.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, outerObserver.Records[0].TrippedBudget,
                "the nested call must re-arm the outer hook on exit, not leave the outer chunk unguarded");
        }

        [Test]
        [Timeout(60000)]
        public async Task EngineRunChunkAsync_MatchesRunChunk()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            CountingFrameYielder yielder = new();

            object[] syncResults = engine.RunChunk(state, "return 40 + 2");
            object[] asyncResults = await engine.RunChunkAsync(state, "return 40 + 2", frameYielder: yielder);

            Assert.AreEqual(
                engine.Marshaller.ToHostValue(syncResults[0]),
                engine.Marshaller.ToHostValue(asyncResults[0]),
                "the asynchronous chunk entry must return the same values as the synchronous one");
        }
    }
}
#endif
