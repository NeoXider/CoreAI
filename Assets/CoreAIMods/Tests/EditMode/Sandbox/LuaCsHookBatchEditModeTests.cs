#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the hook window of the guarded Lua runtime: how many instructions run between two fires of a guard
    /// hook, what that does to step accounting, and the allocation exposure it leaves. The scheduler threads'
    /// hook (<see cref="LuaCsCoroutineHandle"/>) used to fire on every instruction and now shares the
    /// four-instruction window <see cref="LuaCsExecutionGuard"/> has always had.
    /// </summary>
    /// <remarks>
    /// The documented exposure (tools/vmbench/RESULTS.md, "Allocation exposure"), with B the allocation budget
    /// and A the bytes the VM can allocate in one millisecond: a bomb built from two-operand concatenation is
    /// cut with its largest string at no more than 5 B under the guard and 5 B + 4 A in a scheduler thread.
    /// </remarks>
    [TestFixture]
    public sealed class LuaCsHookBatchEditModeTests
    {
        private const long MB = 1024 * 1024;
        private const long BombBudgetBytes = 16 * MB;

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

        /// <summary>
        /// A lifetime cap no test reaches: a handle built with it keeps the one-instruction window, so it is the
        /// per-instruction reference every batched handle is compared with.
        /// </summary>
        private const long CappedButUnreachable = long.MaxValue - 1;

        private static void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// A state with <c>tick()</c> (counts its calls), <c>size(n)</c> (records the last number it was given),
        /// <c>slow()</c> (spins for three milliseconds) and <c>fast()</c> (returns at once).
        /// </summary>
        private sealed class ProbeState
        {
            public readonly LuaCsSecureEnvironment Environment = new();
            public readonly LuaState State;
            public long Ticks;
            public double LastSize;

            public ProbeState()
            {
                LuaCsApiRegistry registry = new();
                registry.RegisterCallback("tick", (ctx, ct) =>
                {
                    Ticks++;
                    return new ValueTask<int>(ctx.Return());
                });
                registry.RegisterCallback("size", (ctx, ct) =>
                {
                    LastSize = ctx.GetArgument<double>(0);
                    return new ValueTask<int>(ctx.Return());
                });
                registry.RegisterCallback("slow", (ctx, ct) =>
                {
                    long until = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency / 1000;
                    while (Stopwatch.GetTimestamp() < until)
                    {
                    }

                    return new ValueTask<int>(ctx.Return());
                });
                registry.RegisterCallback("fast", (ctx, ct) => new ValueTask<int>(ctx.Return()));
                State = Environment.Create(registry);
            }

            public LuaFunction Function(string body)
            {
                return Environment.RunChunk(State, "return function()\n" + body + "\nend")[0].Read<LuaFunction>();
            }
        }

        private static LuaCsCoroutineHandle NewHandle(ProbeState probe, string body, int budgetPerResume,
            long lifetimeSteps, long maxAllocatedBytes = 0)
        {
            return new LuaCsCoroutineHandle(probe.State, probe.Function(body), budgetPerResume,
                resumeTimeoutMs: 60_000, totalLifetimeSteps: lifetimeSteps, maxAllocatedBytes: maxAllocatedBytes);
        }

        [Test]
        [Timeout(60000)]
        public void HandleHookWindow_AStepTripLandsOnTheSameInstructionAsAPerInstructionHook()
        {
            // WHY every budget from 1 to 67: the window is four instructions, so the budgets cover every
            // remainder several times. A batched hook that tripped at the next multiple of four would let a
            // thread run up to three instructions past its budget and report a different step count.
            for (int budget = 1; budget <= 67; budget++)
            {
                ProbeState batchedProbe = new();
                LuaCsCoroutineHandle batched = NewHandle(batchedProbe, "while true do tick() end", budget,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                ProbeState referenceProbe = new();
                LuaCsCoroutineHandle reference = NewHandle(referenceProbe, "while true do tick() end", budget,
                    CappedButUnreachable);

                batched.Resume();
                reference.Resume();

                Assert.IsFalse(batched.LastOk, "budget " + budget + ": the runaway must be cut");
                Assert.AreEqual(LuaCsGuardTripKind.Steps, batched.LastTrip, "budget " + budget);
                StringAssert.StartsWith("sandbox: EXCEEDED_RESUME_STEP_BUDGET (" + budget + ")",
                    batched.LastErrorText);
                Assert.AreEqual(reference.LastErrorText, batched.LastErrorText,
                    "budget " + budget + ": the trip line, author line included, must not change");
                Assert.AreEqual(referenceProbe.Ticks, batchedProbe.Ticks,
                    "budget " + budget + ": the batched hook must cut the thread at the same instruction");
                Assert.AreEqual(budget + 1L, batched.ConsumedSteps,
                    "budget " + budget + ": a step trip is charged the instruction that crossed the budget");
                Assert.AreEqual(reference.ConsumedSteps, batched.ConsumedSteps, "budget " + budget);
            }
        }

        [Test]
        [Timeout(60000)]
        public void HandleHookWindow_AThreadWithoutALifetimeCapIsChargedPerWindow_AndACappedOneExactly()
        {
            // WHY: this is the accounting granularity, stated out loud. A scheduler thread (no lifetime cap) is
            // charged for every finished four-instruction window, so its step count is at most three below the
            // real count; a handle with a lifetime cap keeps the window of one, because its cap adds up the
            // steps of every resume and must not lose the tail of each.
            // WHY no "multiple of four" assertion: a window that happens to last a millisecond (a collector
            // pause, a busy machine) narrows the hook, and that resume is then counted exactly.
            bool sawAnUncountedTail = false;
            for (int run = 0; run < 18 && !(sawAnUncountedTail && run >= 6); run++)
            {
                int iterations = 1 + run % 6;
                string body = "fast() local x = 0 for i = 1, " + iterations + " do x = x + 1 end";
                ProbeState batchedProbe = new();
                LuaCsCoroutineHandle batched = NewHandle(batchedProbe, body, 10_000,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                ProbeState referenceProbe = new();
                LuaCsCoroutineHandle reference = NewHandle(referenceProbe, body, 10_000, CappedButUnreachable);

                batched.Resume();
                reference.Resume();

                Assert.IsTrue(batched.LastOk, batched.LastErrorText);
                Assert.IsTrue(reference.LastOk, reference.LastErrorText);
                long exact = reference.ConsumedSteps;
                Assert.Greater(exact, 0L, "the reference counts every instruction");
                Assert.That(exact - batched.ConsumedSteps,
                    Is.InRange(0L, LuaCsExecutionGuard.HookInstructionBatch - 1L),
                    iterations + " iterations: at most the unfinished window is uncounted");
                sawAnUncountedTail |= batched.ConsumedSteps < exact;
            }

            Assert.IsTrue(sawAnUncountedTail,
                "none of six body lengths left an uncounted tail: the hook is not batched and this fixture pins nothing");
        }

        [Test]
        [Timeout(60000)]
        public void HandleHookWindow_AWindowThatLastsASampleInterval_NarrowsTheHookToOneInstruction()
        {
            // WHY: a window that lasted a millisecond held a slow instruction, which is how a large
            // concatenation looks from the hook. From then on the thread is watched after every instruction
            // again, as it was before the hook was batched, so the rest of the resume is counted exactly. The
            // twin with a call that returns at once stays batched.
            for (int iterations = 1; iterations <= 6; iterations++)
            {
                string tail = " local x = 0 for i = 1, " + iterations + " do x = x + 1 end";
                ProbeState narrowedProbe = new();
                LuaCsCoroutineHandle narrowed = NewHandle(narrowedProbe, "slow()" + tail, 10_000,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                ProbeState referenceProbe = new();
                LuaCsCoroutineHandle reference = NewHandle(referenceProbe, "slow()" + tail, 10_000,
                    CappedButUnreachable);

                narrowed.Resume();
                reference.Resume();

                Assert.IsTrue(narrowed.LastOk, narrowed.LastErrorText);
                Assert.AreEqual(reference.ConsumedSteps, narrowed.ConsumedSteps,
                    iterations + " iterations: after a slow window every instruction is counted again");
            }
        }

        [Test]
        [Timeout(60000)]
        public void HandleHookWindow_AfterEnoughFastInstructions_TheNarrowedHookWidensAgain()
        {
            // WHY: one slow instruction (a collector pause looks the same) must not leave the rest of a long
            // resume at one instruction per fire. After a run of fast instructions the window is four again, so
            // the count is charged per window once more: never above the exact count, at most three below it,
            // and below it for at least one of six tail lengths.
            bool sawAnUncountedTail = false;
            for (int run = 0; run < 18 && !(sawAnUncountedTail && run >= 6); run++)
            {
                int iterations = 400 + run % 6;
                string body = "slow() local x = 0 for i = 1, " + iterations + " do x = x + 1 end";
                ProbeState widenedProbe = new();
                LuaCsCoroutineHandle widened = NewHandle(widenedProbe, body, 100_000,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                ProbeState referenceProbe = new();
                LuaCsCoroutineHandle reference = NewHandle(referenceProbe, body, 100_000, CappedButUnreachable);

                widened.Resume();
                reference.Resume();

                Assert.IsTrue(widened.LastOk, widened.LastErrorText);
                Assert.That(reference.ConsumedSteps - widened.ConsumedSteps,
                    Is.InRange(0L, LuaCsExecutionGuard.HookInstructionBatch - 1L), iterations + " iterations");
                sawAnUncountedTail |= widened.ConsumedSteps < reference.ConsumedSteps;
            }

            Assert.IsTrue(sawAnUncountedTail,
                "after 800 fast instructions the hook must be back to its four-instruction window");
        }

        /// <summary>A doubling bomb that reports the length of every string it builds.</summary>
        private const string LoopBomb =
            "local s = string.rep('x', 1024)\n" +
            "while true do s = s .. s size(#s) end";

        /// <summary>
        /// Two doublings between reports, the most two-operand concatenations a four-instruction window holds,
        /// starting from the largest string a library call may build.
        /// </summary>
        private const string StraightLineBomb =
            "local s = string.rep('x', 1000000)\n" +
            "s = s .. s s = s .. s size(#s)\n" +
            "s = s .. s s = s .. s size(#s)\n" +
            "s = s .. s s = s .. s size(#s)\n" +
            "s = s .. s size(#s)";

        /// <summary>About 200 MB of strings that are garbage as soon as they are built.</summary>
        private const string QuietChurn =
            "local n = 0\n" +
            "for i = 1, 200 do\n" +
            "  local garbage = string.rep('g', 500000) .. i\n" +
            "  n = n + #garbage\n" +
            "end\n" +
            "size(n)";

        /// <summary>
        /// Bytes of string the VM builds in one millisecond on this machine, measured on a 16 MB concatenation:
        /// the A of the documented exposure.
        /// </summary>
        private static long MeasureAllocatedBytesPerMillisecond()
        {
            ProbeState probe = new();
            probe.State.Environment["S"] = new string('x', 4 * 1024 * 1024);
            LuaFunction concat = probe.Function("local t = S .. S return #t");
            LuaCsExecutionGuard guard = new(60_000, 1_000_000, 0);
            long best = long.MaxValue;
            for (int trial = 0; trial < 5; trial++)
            {
                long start = Stopwatch.GetTimestamp();
                guard.Execute(probe.State, concat, CancellationToken.None);
                best = Math.Min(best, Stopwatch.GetTimestamp() - start);
            }

            double seconds = Math.Max(1L, best) / (double)Stopwatch.Frequency;
            return (long)(16d * MB / seconds / 1000d);
        }

        [TestCase("loop")]
        [TestCase("straight line")]
        [Timeout(120000)]
        public void Guard_DoublingBomb_IsCutWithinTheDocumentedExposure(string shape)
        {
            CollectGarbage();
            ProbeState probe = new();
            LuaCsExecutionGuard guard = new(60_000, 5_000_000_000L, BombBudgetBytes);

            LuaCsHostFunctionException trip = Assert.Throws<LuaCsHostFunctionException>(() =>
                probe.Environment.RunChunk(probe.State, shape == "loop" ? LoopBomb : StraightLineBomb, guard));
            CollectGarbage();

            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(trip), trip.Message);
            Assert.AreEqual("sandbox: EXCEEDED_MEMORY_BUDGET (" + BombBudgetBytes + " bytes)", trip.Message);
            long largestStringBytes = (long)probe.LastSize * sizeof(char);
            Assert.LessOrEqual(largestStringBytes, 5 * BombBudgetBytes,
                "the guard samples the heap every four instructions, so the largest string of a two-operand " +
                "doubling bomb stays within 5 budgets; it reached " + largestStringBytes + " bytes");
        }

        [TestCase("loop")]
        [TestCase("straight line")]
        [Timeout(120000)]
        public void SchedulerThread_DoublingBomb_IsCutWithinTheDocumentedExposure(string shape)
        {
            long perMillisecond = MeasureAllocatedBytesPerMillisecond();
            CollectGarbage();
            ProbeState probe = new();
            LuaCsCoroutineHandle handle = NewHandle(probe, shape == "loop" ? LoopBomb : StraightLineBomb,
                50_000_000, LuaCsCoroutineHandle.UnlimitedLifetimeSteps, BombBudgetBytes);

            handle.Resume();
            CollectGarbage();

            Assert.IsFalse(handle.LastOk, "the bomb must be cut");
            Assert.AreEqual(LuaCsGuardTripKind.Memory, handle.LastTrip, handle.LastErrorText);
            StringAssert.StartsWith("sandbox: EXCEEDED_MEMORY_BUDGET (" + BombBudgetBytes + " bytes)",
                handle.LastErrorText);
            long largestStringBytes = (long)probe.LastSize * sizeof(char);
            long bound = 5 * BombBudgetBytes + 4 * perMillisecond;
            Assert.LessOrEqual(largestStringBytes, bound,
                "a scheduler thread is sampled once per millisecond of execution and at most four instructions " +
                "later, so the largest string stays within 5 budgets plus 4 times what the VM allocates in a " +
                "millisecond (" + perMillisecond + " bytes here); it reached " + largestStringBytes + " bytes");
        }

        [Test]
        [Timeout(120000)]
        public void Guard_QuietScriptUnderTheSameBudget_IsNotCut()
        {
            // WHY the negative twin: the same budget and the same hook window must leave a script alone that
            // allocates twelve budgets of strings and keeps none of them.
            CollectGarbage();
            ProbeState probe = new();
            LuaCsExecutionGuard guard = new(60_000, 5_000_000_000L, BombBudgetBytes);

            probe.Environment.RunChunk(probe.State, QuietChurn, guard);

            Assert.Greater(probe.LastSize, 100d * 1000d * 1000d, "the script must have run to its end");
        }

        [Test]
        [Timeout(120000)]
        public void SchedulerThread_QuietScriptUnderTheSameBudget_IsNotCut()
        {
            CollectGarbage();
            ProbeState probe = new();
            LuaCsCoroutineHandle handle = NewHandle(probe, QuietChurn, 50_000_000,
                LuaCsCoroutineHandle.UnlimitedLifetimeSteps, BombBudgetBytes);

            handle.Resume();

            Assert.IsTrue(handle.LastOk, "garbage is not the script's cost: " + handle.LastErrorText);
            Assert.AreEqual(LuaCsGuardTripKind.None, handle.LastTrip);
            Assert.Greater(probe.LastSize, 100d * 1000d * 1000d, "the script must have run to its end");
        }

        [Test]
        [Timeout(60000)]
        public void SchedulerThread_BudgetTrip_StillCannotBeCaught_AndTheHandleStaysGuardedForItsNextThread()
        {
            // WHY: the batched hook changes its count from inside the hook near the step limit. The trip must
            // still end the resume from inside any protected call, and a second handle on the same state must
            // be cut again.
            ProbeState probe = new();
            string body =
                "pcall(function() while true do end end)\n" +
                "tick()\n" +
                "xpcall(function() while true do end end, function(e) tick() return e end)\n" +
                "tick()";
            LuaCsCoroutineHandle first = NewHandle(probe, body, 5_000, LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
            LuaCsCoroutineHandle second = NewHandle(probe, body, 5_000, LuaCsCoroutineHandle.UnlimitedLifetimeSteps);

            first.Resume();
            second.Resume();

            Assert.AreEqual(0L, probe.Ticks, "nothing after a trip may run, in a handler or after the protected call");
            Assert.AreEqual(LuaCsGuardTripKind.Steps, first.LastTrip);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, second.LastTrip);
            StringAssert.StartsWith("sandbox: EXCEEDED_RESUME_STEP_BUDGET (5000)", first.LastErrorText);
            StringAssert.StartsWith("sandbox: EXCEEDED_RESUME_STEP_BUDGET (5000)", second.LastErrorText);
            Assert.IsTrue(first.IsFinished);
            Assert.IsTrue(second.IsFinished);
        }

        [Test]
        [Timeout(60000)]
        public void GuardedFunctionCall_ReturnsWhatTheVmWrapperReturns_AndLeavesTheStackWhereItWas()
        {
            // WHY: the guard no longer calls a function through the VM's array-returning wrapper (which
            // completes through a Task on every call) but runs the same call on the stack itself. The results,
            // the stack depth afterwards and what an error leaves behind must be the wrapper's.
            ProbeState probe = new();
            LuaFunction echo = probe.Function("return 'a', 2, true");
            LuaFunction none = probe.Function("local x = 1");
            LuaFunction fails = probe.Function("error('boom', 0)");
            LuaFunction sum = probe.Environment.RunChunk(probe.State, "return function(a, b) return a + b, a * b end")[0]
                .Read<LuaFunction>();
            LuaCsExecutionGuard guard = new(60_000, 1_000_000, 0);
            int depth = probe.State.Stack.Count;

            LuaValue[] three = guard.Execute(probe.State, echo, CancellationToken.None);
            LuaValue[] empty = guard.Execute(probe.State, none, CancellationToken.None);
            LuaValue[] two = guard.Execute(probe.State, sum, CancellationToken.None, new LuaValue(6d), new LuaValue(7d));

            Assert.AreEqual(3, three.Length);
            Assert.AreEqual("a", three[0].Read<string>());
            Assert.AreEqual(2d, three[1].Read<double>());
            Assert.IsTrue(three[2].Read<bool>());
            Assert.AreEqual(0, empty.Length);
            Assert.AreEqual(13d, two[0].Read<double>());
            Assert.AreEqual(42d, two[1].Read<double>());
            Assert.AreEqual(depth, probe.State.Stack.Count, "a completed call must leave the stack as it found it");

            LuaRuntimeException viaGuard = Assert.Catch<LuaRuntimeException>(() =>
                guard.Execute(probe.State, fails, CancellationToken.None));
            int depthAfterGuardedError = probe.State.Stack.Count;
            probe.State.Stack.PopUntil(depth);
            LuaRuntimeException viaWrapper = Assert.Catch<LuaRuntimeException>(() =>
                probe.State.CallAsync(new LuaValue(fails), ReadOnlySpan<LuaValue>.Empty, CancellationToken.None)
                    .GetAwaiter().GetResult());
            int depthAfterWrapperError = probe.State.Stack.Count;
            probe.State.Stack.PopUntil(depth);

            Assert.AreEqual(viaWrapper.Message, viaGuard.Message);
            Assert.AreEqual(depthAfterWrapperError, depthAfterGuardedError,
                "an error must leave the stack exactly as the VM's own wrapper leaves it");
            Assert.AreEqual(13d, guard.Execute(probe.State, sum, CancellationToken.None, new LuaValue(6d),
                new LuaValue(7d))[0].Read<double>(), "the state must stay usable after an error");
        }
    }
}
#endif
