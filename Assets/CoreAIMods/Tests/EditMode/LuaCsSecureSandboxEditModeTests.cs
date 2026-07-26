using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the Lua-CSharp sandbox allocation-bomb backstop (F-08): plain string
    /// concatenation and <c>table.concat</c> have no single library call site to cap the way
    /// <c>string.rep</c>/<c>string.format</c> are capped, so <see cref="LuaCsSecureEnvironment"/> and
    /// <see cref="LuaCsExecutionGuard"/> enforce a total per-execution GC allocation budget instead.
    /// </summary>
    [TestFixture]
    public sealed class LuaCsSecureSandboxEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>state.ExecuteAsync(...).GetAwaiter().GetResult()</c> inside the execution guard. On
        /// Unity's main thread a <see cref="SynchronizationContext"/> is installed, so any continuation
        /// the VM posts back to it would deadlock the blocked main thread. Detaching the context for the
        /// duration of each test lets those continuations complete on the thread pool.
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

        [Test]
        [Timeout(30000)]
        public void Coroutine_RunawayLoop_IsCutByResumeBudget()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // A runaway loop inside a mod-created coroutine runs on a CHILD LuaState the native library does not
            // guard; wrapping coroutine.resume arms a per-resume step/time budget on that child. `resume` runs
            // protected, so a cut surfaces as `ok == false` (never runs to completion). The loop is finite so a
            // REGRESSION (guard not armed) still terminates (returning ok == true, failing the assert) instead
            // of hanging the run.
            LuaValue[] result = env.RunChunk(state,
                "local co = coroutine.create(function()\n" +
                "  local n = 0\n" +
                "  for i = 1, 2000000 do n = n + 1 end\n" +
                "end)\n" +
                "local ok = coroutine.resume(co)\n" +
                "return ok");

            Assert.IsTrue(result.Length > 0, "RunChunk must return the resume result.");
            Assert.IsFalse(result[0].Read<bool>(),
                "The runaway coroutine loop must be cut by the per-resume guard (resume returns false), not complete.");
        }

        [Test]
        [Timeout(30000)]
        public void Coroutine_Wrap_IsRemoved_UnguardablePrimitiveCannotHang()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // SECURITY: coroutine.wrap drives a hidden CHILD LuaState through the library's OWN resume, bypassing
            // the guarded coroutine.resume, so a wrap body (`while true do end`) would run with no step/time/alloc
            // hook and hang the host forever. It is therefore stripped (set nil). First, assert it is gone.
            LuaValue[] isNil = env.RunChunk(state, "return coroutine.wrap == nil");
            Assert.IsTrue(isNil.Length > 0 && isNil[0].Read<bool>(),
                "coroutine.wrap must be removed from the sandbox — it cannot be guarded on this Lua-CSharp build.");

            // And reaching for it must raise a clean nil-call error PROMPTLY, not hang. If a regression re-natives
            // wrap, the unguarded child-state loop below runs forever and this [Timeout] test fails — the signal.
            Assert.Throws<LuaRuntimeException>(
                () => env.RunChunk(state, "coroutine.wrap(function() while true do end end)()"),
                "Calling the removed coroutine.wrap must raise a nil-call error, never hang.");
        }

        [Test]
        public void AllocationBomb_ConcatDoubling_ThrowsMemoryBudgetError()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // string.rep is capped at MaxStringRepLength (1MB); doubling it via plain concatenation (no library
            // call site to intercept) must be caught by the per-instruction GC allocation budget. Use an
            // explicit low budget (64MB) with generous step/time so the trip fires on the MEMORY backstop while
            // the string stays bounded (~128MB peak) — a huge default-budget bomb risks a multi-GB concat opcode
            // (uninterruptible between VM instructions) that can hang/OOM the machine.
            LuaCsExecutionGuard guard = new(8000, 10_000_000, 64 * 1024 * 1024);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local s = string.rep('x', 1000000)\n" +
                    "for i = 1, 7 do s = s .. s end\n" +
                    "return s",
                    guard));

            // The security guarantee is that the run is CUT before unbounded growth — accept the memory backstop
            // or the step/time budgets it races (GC.GetTotalMemory reflects managed growth only coarsely).
            Assert.IsTrue(
                ex.Message.Contains("EXCEEDED_MEMORY_BUDGET") || ex.Message.Contains("exceeded"),
                $"Expected the allocation bomb to be cut by a sandbox budget, got: {ex.Message}");
        }

        [Test]
        public void AllocationBomb_TableConcat_CapEnforced()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local t = {}\n" +
                    "local chunk = string.rep('x', 1000000)\n" +
                    "for i = 1, 5 do t[i] = chunk end\n" +
                    "return table.concat(t)"));

            Assert.IsTrue(ex.Message.Contains("table.concat"),
                $"Expected the table.concat cap to fire, got: {ex.Message}");
        }

        [Test]
        [Timeout(15000)]
        public void NestedGuardedCall_SameState_OuterBudgetStaysArmed()
        {
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            LuaCsExecutionGuard nestedGuard = new(2000, 10_000);
            LuaState state = null;
            LuaFunction noop = null;
            registry.Register("nested", new System.Func<double>(() =>
            {
                // Mirrors mods_call: a guarded call re-entering the guard on the SAME LuaState.
                LuaValue[] r = nestedGuard.Execute(state, noop, CancellationToken.None);
                return r.Length > 0 ? r[0].Read<double>() : 0d;
            }));
            state = env.Create(registry);
            noop = env.RunChunk(state, "return function() return 1 end")[0].Read<LuaFunction>();

            // The nested guard's cleanup must restore the outer hook instead of clearing it; otherwise
            // the over-budget loop after nested() runs unlimited and the chunk returns normally.
            LuaCsExecutionGuard outerGuard = new(2000, 5_000);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "nested()\n" +
                    "local x = 0\n" +
                    "for i = 1, 100000 do x = x + 1 end\n" +
                    "return x",
                    outerGuard));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Expected the OUTER step budget to stay armed after a nested guarded call, got: {ex.Message}");
        }

        [Test]
        public void IsMemoryBudgetTrip_ClassifiesProcessHeapTripsOnlyNotStepOrTimeOverruns()
        {
            // The runtime relies on this classification to keep a blameless mod loaded: a process-heap
            // memory trip must be recognised by its dedicated TYPE (through the wrapping exception chain)
            // while the real step and time guards must NOT be — those keep counting toward the streak.
            System.Exception memoryTrip = new System.InvalidOperationException("wrapped",
                new LuaMemoryBudgetException(
                    $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} (268435456 bytes)"));
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(memoryTrip),
                "A LuaMemoryBudgetException anywhere in the exception chain must be recognised.");

            // SECURITY: a mod's own error() text is NOT a memory trip, even if it forges the marker string —
            // classification is by type, so a mod cannot dodge the auto-unload guard by faking the message.
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.InvalidOperationException(
                        $"boom {LuaCsExecutionGuard.MemoryBudgetTripMarker} forged by a mod")),
                "A forged marker string in an ordinary exception message must NOT be classified as a memory trip.");

            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.InvalidOperationException("LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS (200000)")),
                "A step overrun is a real guard and must not be classified as a memory trip.");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.TimeoutException("Lua exceeded 500 ms.")),
                "A timeout is a real guard and must not be classified as a memory trip.");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(null),
                "A null exception is not a memory trip.");
        }

        [Test]
        public void AllocationBomb_NormalHundredKbString_StillPasses()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaValue[] result = env.RunChunk(state,
                "local s = string.rep('x', 100000)\n" +
                "s = s .. s\n" +
                "return #s");

            Assert.AreEqual(200000, (int)result[0].Read<double>(),
                "A normal, non-adversarial 100KB-class string script must not be blocked by the budget.");
        }

        [Test]
        [Timeout(15000)]
        public void StepBudget_Overrun_IsCut_AfterSamplingHookScalesSteps()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // The instruction hook now fires every HookInstructionBatch instructions (sampling) and charges
            // that batch to the step counter, so the SAME max-instruction ceiling is enforced. A loop far
            // longer than the 5,000-step budget must still be cut — if the batch scaling regressed (steps no
            // longer accumulate), the hook would under-count and the loop would run to completion, returning
            // normally and failing this Assert.Throws instead of hanging (the loop is finite).
            LuaCsExecutionGuard guard = new(60_000, 5_000, 0);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "for i = 1, 5000000 do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Expected the sampled step budget to cut the over-budget loop, got: {ex.Message}");
        }

        [Test]
        [Timeout(15000)]
        public void Timeout_Overrun_IsCut_ByWallClockRegardlessOfSampling()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // Time is wall-clock, read from Stopwatch.GetTimestamp() on each sampled hook, so sampling does
            // not weaken it. A huge step budget forces the TIME budget to be the one that trips. A regression
            // that broke the GetTimestamp/ticks-budget math would let the busy loop run unbounded and this
            // [Timeout] test would fail — the signal.
            LuaCsExecutionGuard guard = new(150, 5_000_000_000L, 0);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "while true do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("exceeded"),
                $"Expected the wall-clock timeout to cut the infinite loop, got: {ex.Message}");
        }

        [Test]
        [Timeout(15000)]
        public void NormalShortHandler_UnderGuard_CompletesAndReturns()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // A well-behaved short handler (the 20 Hz tick shape the guard is on) must pass cleanly under a
            // tight-but-sufficient budget: none of the sampled step/time/alloc limits may trip a normal call.
            LuaCsExecutionGuard guard = new(2_000, 200_000);
            LuaValue[] result = env.RunChunk(state,
                "local x = 0\n" +
                "for i = 1, 100 do x = x + i end\n" +
                "return x",
                guard);

            Assert.IsTrue(result.Length > 0, "A normal short handler must return its result under the guard.");
            Assert.AreEqual(5050, (int)result[0].Read<double>(),
                "The guarded short handler must compute the correct result, unaffected by the sampling hook.");
        }
    }
}
