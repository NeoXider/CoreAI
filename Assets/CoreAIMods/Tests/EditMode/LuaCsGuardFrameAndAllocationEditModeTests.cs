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
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: a test here waits on a Task from the calling thread (Assert.ThrowsAsync/CatchAsync, or
        /// a blocking read of a Task local). Under Unity's SynchronizationContext the awaited
        /// continuation is posted back to the very thread the wait is holding, and the EditMode batch
        /// stops with no results file - silence, not a failure.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

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
            // WHY the collection before Reset: the budget's baseline is a deliberately garbage-INCLUSIVE
            // GC.GetTotalMemory(false) sample, and LuaCsAllocationBudget documents the consequence -
            // live growth is understated by whatever garbage was already on the heap, so a trip can be
            // late but never false. That is the right bias for a backstop and must not change. It does
            // mean a single IsExceeded() call only trips when the baseline was taken on a reasonably
            // clean heap. This fixture shares a process with ~4800 other tests and runs without a
            // domain reload, so without this the assertion measures whichever neighbours ran first: it
            // passed alone and failed twice in the full sweep once new tests upstream left more garbage
            // behind. Collecting here makes the test measure the rule it states, not its neighbours.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

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

        private const long MB = 1024 * 1024;

        /// <summary>
        /// Retains a fresh 512 KB string per iteration (64 MB if never cut) while dropping about 1 MB of
        /// garbage beside each one, so a garbage-inclusive sample keeps raising suspicions that a
        /// collection then clears — the exact shape the old re-baselining forgave, one confirmation at a
        /// time, all the way to the end of the loop.
        /// </summary>
        private const string RetentionWithGarbageChurn =
            "local keep = {}\n" +
            "local chunk = string.rep('k', 262144)\n" +
            "for i = 1, 128 do\n" +
            "  keep[i] = chunk .. i\n" +
            "  local garbage = string.rep('g', 262144) .. i\n" +
            "end\n" +
            "return #keep";

        private static void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [Test]
        public void ClearedSuspicion_NeverMovesTheTripReferenceUp()
        {
            // WHY (audit M2-05): a cleared suspicion used to re-baseline the trip reference to the
            // post-collection reading, forgiving the live growth it had just measured. Two confirmations
            // of 10 MB each then both passed a 16 MB budget, and a doubling bomb walked a 256 MB budget up
            // to a 1 GB string. The readings are explicit so the rule is pinned, not the GC's timing.
            LuaCsAllocationBudget budget = default;
            budget.Reset(16 * MB, 100 * MB);

            Assert.IsFalse(budget.IsExceeded(120 * MB, () => 110 * MB),
                "10 MB of live growth is within the 16 MB budget; the other 10 MB sampled was garbage");
            Assert.AreEqual(100 * MB, budget.BaselineBytes,
                "a cleared suspicion must never raise the trip reference");
            Assert.IsTrue(budget.IsExceeded(124 * MB, () => 120 * MB),
                "20 MB of live growth since the execution started exceeds 16 MB, however many " +
                "confirmations it was split across");
        }

        [Test]
        public void ClearedSuspicion_ReArmsOnlyAfterAQuarterBudgetOfFreshGrowth()
        {
            // WHY: never raising the trip reference must not turn every later sample into a forced full
            // collection. What a cleared suspicion raises is the SUSPICION line: the next confirmation
            // waits for a quarter budget of sampled growth past the post-collection reading, and never
            // sits below the budget line itself.
            LuaCsAllocationBudget budget = default;
            budget.Reset(16 * MB, 100 * MB);
            int confirmations = 0;

            Assert.IsFalse(budget.IsExceeded(130 * MB, () =>
            {
                confirmations++;
                return 114 * MB;
            }));
            Assert.AreEqual(1, confirmations);
            Assert.AreEqual(118 * MB, budget.SuspicionBytes,
                "re-armed a quarter budget (4 MB) above the 114 MB post-collection reading");

            Assert.IsFalse(budget.IsExceeded(118 * MB, () =>
            {
                confirmations++;
                return 114 * MB;
            }));
            Assert.AreEqual(1, confirmations, "a sample at the re-armed line must not force a collection");

            Assert.IsTrue(budget.IsExceeded(119 * MB, () =>
            {
                confirmations++;
                return 117 * MB;
            }), "past the line, a confirmation that finds 17 MB of live growth trips");
            Assert.AreEqual(2, confirmations);

            budget.Reset(16 * MB, 100 * MB);
            Assert.IsFalse(budget.IsExceeded(117 * MB, () => 101 * MB));
            Assert.AreEqual(116 * MB, budget.SuspicionBytes,
                "a clearance far below the budget line keeps the exact budget line as the next suspicion");
        }

        [Test]
        public void PostCollectionReadingBelowTheBaseline_LowersTheTripReference()
        {
            // WHY: the baseline is a garbage-INCLUSIVE sample, so a collection can show the real start
            // was lower. Moving the reference DOWN only makes a trip earlier for growth that really
            // happened inside this execution; that is the one direction it may move.
            LuaCsAllocationBudget budget = default;
            budget.Reset(16 * MB, 100 * MB);

            Assert.IsFalse(budget.IsExceeded(120 * MB, () => 90 * MB));
            Assert.AreEqual(90 * MB, budget.BaselineBytes);
            Assert.IsTrue(budget.IsExceeded(110 * MB, () => 107 * MB),
                "17 MB of live growth past the truer 90 MB reference trips a 16 MB budget");
        }

        [Test]
        [Timeout(60000)]
        public void LinearRetentionWithGarbageChurn_TripsTheGuardBudget_InsteadOfRatchetingPastIt()
        {
            CollectGarbage();
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(
                30_000,
                5_000_000_000L,
                16 * MB,
                guardObserver: observer);

            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
                env.RunChunk(state, RetentionWithGarbageChurn, guard),
                "64 MB of retained growth under a 16 MB budget must be cut, however much garbage hides it");

            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Memory, observer.Records[0].TrippedBudget);
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(ex));
        }

        [Test]
        [Timeout(60000)]
        public void CoroutineResume_LinearRetentionWithGarbageChurn_TripsTheMemoryBudget()
        {
            // WHY (audit M2-05): a scheduler thread's resume hook sampled no allocation at all, so this
            // resume kept all 64 MB under a 16 MB budget. It is the same backstop, sampled per
            // millisecond of execution instead of per instruction batch.
            CollectGarbage();
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state, "return function()\n" + RetentionWithGarbageChurn + "\nend");

            IScriptCoroutine co = engine.CreateCoroutine(state, fn[0],
                new ExecutionBudget(timeoutMs: 30_000, maxSteps: 50_000_000, maxAllocatedBytes: 16 * MB));
            ScriptResumeResult result = co.Resume();

            Assert.IsFalse(result.Ok, "the resume must be cut, not keep 64 MB under a 16 MB budget");
            StringAssert.Contains(LuaCsExecutionGuard.MemoryBudgetTripMarker, result.Error,
                "error was: " + result.Error);
            Assert.AreEqual(LuaCsGuardTripKind.Memory, ((LuaCsScriptCoroutine)co).Handle.LastTrip);
        }

        [Test]
        [Timeout(60000)]
        public void CoroutineResume_OneDoublingPerResume_IsChargedToTheResumeThatAllocates()
        {
            // WHY: the per-resume baseline must be read before the resume's first instruction. A
            // baseline taken lazily at the first sample would already contain the one long concat a
            // resume starts with, so a thread doubling its string once per resume would grow forever
            // without a single resume being charged. Here the resume that builds the 32 MB string must be
            // cut; five doublings from 1M chars would otherwise end at a 64 MB string.
            CollectGarbage();
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state,
                "return function()\n" +
                "  local s = string.rep('x', 1000000)\n" +
                "  for r = 1, 5 do\n" +
                "    s = s .. s\n" +
                "    coroutine.yield(#s)\n" +
                "  end\n" +
                "  return #s\n" +
                "end");

            IScriptCoroutine co = engine.CreateCoroutine(state, fn[0],
                new ExecutionBudget(timeoutMs: 30_000, maxSteps: 50_000_000, maxAllocatedBytes: 16 * MB));
            ScriptResumeResult result = default;
            int resumes = 0;
            while (co.CanResume && resumes < 6)
            {
                result = co.Resume();
                resumes++;
            }

            Assert.IsFalse(result.Ok,
                "one of the resumes must be cut for its own doubling — ran " + resumes + " resumes");
            StringAssert.Contains(LuaCsExecutionGuard.MemoryBudgetTripMarker, result.Error,
                "error was: " + result.Error);
            Assert.AreEqual(LuaCsGuardTripKind.Memory, ((LuaCsScriptCoroutine)co).Handle.LastTrip);
        }

        [Test]
        [Timeout(60000)]
        public void CoroutineResume_ThatOnlyChurnsGarbage_IsNotCutByTheAllocationBackstop()
        {
            // WHY the negative twin: sampling allocation inside every scheduler resume must not turn
            // transient garbage into a kill. About 96 MB of garbage passes through a 16 MB budget here;
            // every suspicion it raises is cleared by the confirming collection.
            CollectGarbage();
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state,
                "return function()\n" +
                "  local n = 0\n" +
                "  for i = 1, 96 do\n" +
                "    local garbage = string.rep('g', 262144) .. i\n" +
                "    n = n + #garbage\n" +
                "  end\n" +
                "  return n\n" +
                "end");

            IScriptCoroutine co = engine.CreateCoroutine(state, fn[0],
                new ExecutionBudget(timeoutMs: 30_000, maxSteps: 50_000_000, maxAllocatedBytes: 16 * MB));
            ScriptResumeResult result = co.Resume();

            Assert.IsTrue(result.Ok, "garbage is not the script's cost — error was: " + result.Error);
            Assert.AreEqual(LuaCsGuardTripKind.None, ((LuaCsScriptCoroutine)co).Handle.LastTrip);
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

            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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

            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
                env.RunChunk(state, "local s = 'x'\nwhile true do s = s .. s end\nreturn s", guard));

            Assert.AreEqual(1, observer.Records.Count);
            Assert.AreEqual(LuaCsGuardTripKind.Memory, observer.Records[0].TrippedBudget,
                "a live doubling string must still trip the allocation backstop");
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(ex),
                "the trip must stay classified by type so a mod cannot forge or suppress it");
        }

        [Test]
        [Timeout(60000)]
        public void MemoryTrip_PcallXpcallAndTheErrorValue_GetOneCleanLine_AndBothWalkersStillClassifyIt()
        {
            // WHY: the trip was raised over an inner LuaMemoryBudgetException, so pcall handed the script
            // "CoreAI.Sandbox.LuaCs.LuaMemoryBudgetException: ..." and xpcall's handler and a protected
            // coroutine.resume - both read the error value - got nil. The cause now travels as HostException,
            // and the mod runtime labels a handler failure through the NEUTRAL walker, so that one must still
            // find the trip by type as well as the guard's own.
            const string expected = "LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET (8388608 bytes)";
            const string bomb =
                "local function bomb() local s = 'x' for i = 1, 26 do s = s .. s end return #s end\n";
            string[] calls = { "record(pcall(bomb))", "record(xpcall(bomb, echo))" };
            foreach (string call in calls)
            {
                CollectGarbage();
                List<string> rows = new();
                LuaCsSecureSandboxEditModeTests.RunRecordingChunk(bomb + call + "\nreturn 1",
                    new LuaCsExecutionGuard(20_000, 5_000_000_000L, 8 * MB), rows);

                CollectionAssert.AreEqual(new[] { "boolean:false|string:" + expected }, rows,
                    call + " must hand the script exactly the trip's line");
                LuaCsSecureSandboxEditModeTests.AssertIsOnlyTheErrorLine(rows[0]);
            }

            CollectGarbage();
            LuaRuntimeException uncaught = LuaCsSecureSandboxEditModeTests.RunRecordingChunk(bomb + "return bomb()",
                new LuaCsExecutionGuard(20_000, 5_000_000_000L, 8 * MB), new List<string>());

            Assert.IsNotNull(uncaught, "an uncaught bomb must end the run");
            Assert.AreEqual(expected, uncaught.Message);
            Assert.AreEqual(expected, uncaught.ErrorObject.ToString(),
                "the error value, which a protected coroutine.resume hands its resumer, must be the same line");
            LuaCsHostFunctionException trip = uncaught as LuaCsHostFunctionException;
            Assert.IsNotNull(trip, "a guard trip must carry its cause for C#: " + uncaught.GetType().Name);
            Assert.IsInstanceOf<LuaMemoryBudgetException>(trip.HostException,
                "the trip's cause must stay reachable from C#");
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(uncaught),
                "the guard's classifier must still find the trip by type");
            Assert.IsTrue(ScriptExecutionErrors.IsMemoryBudgetTrip(uncaught),
                "the engine-neutral classifier the mod runtime uses must still find the trip by type");
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

            // WHY the base OperationCanceledException and not Lua-CSharp's own LuaCanceledException,
            // which this assertion originally named: the VM does raise LuaCanceledException, and it does
            // so both for a token cancelled before the run and for one cancelled mid-loop, with or
            // without the guard hook installed — measured directly against Lua.dll rather than assumed.
            // It cannot survive the trip out, though. LuaCsExecutionGuard.ExecuteAsync is an `async
            // Task`, and a cancellation escaping one completes the Task as CANCELED rather than faulted;
            // Unity's runtime does not carry the original exception through that transition, so the
            // awaiter raises a fresh TaskCanceledException (desktop .NET 8 does carry it, which is why
            // the original expectation looks right on paper). Both are OperationCanceledException, so
            // that is the contract CoreAI can actually keep and the one a host needs to tell "the run was
            // cancelled" from "the mod's script failed"; pinning the subtype pins the runtime instead.
            Assert.CatchAsync<OperationCanceledException>(async () =>
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

            Assert.ThrowsAsync<LuaCsHostFunctionException>(async () =>
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
