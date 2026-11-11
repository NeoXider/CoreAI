#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;
using Lua;
using Lua.Runtime;
using Lua.Standard;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// Pins what CoreAI's guard and coroutine budgets rely on from the Lua-CSharp VM (0.5.8): a count hook fires again
    /// after one was aborted (it threw, was cancelled, or its frame could not be set up), a thread created under a host
    /// hook inherits that hook until its own replaces it, and the instruction counts every budget is sized in. Each VM
    /// fact (<c>VM_</c>) stands next to the CoreAI behaviour built on it, so a VM update that moves either fails here
    /// by name. Background: <c>dev-docs/DEPENDENCY_VERSIONS_2026-11-11.md</c>, section "Lua-CSharp".
    /// <para>
    /// The <c>VM_CountHook*</c> tests guard the removal of the guard's in-hook-flag workaround (<c>ClearInHookFlag</c>,
    /// gone with 0.5.8): the guard relies on the VM restoring that flag after an aborted hook, so a VM build that loses
    /// the <c>finally</c> fails them, and would otherwise leave every state unguarded after its first foreign-context
    /// trip.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class LuaCsVmContractEditModeTests
    {
        /// <summary>Steps the guard charges for <see cref="ReferenceLoop"/>: 501 hook windows of four instructions.</summary>
        private const long ReferenceLoopSteps = 2004;

        private const string ReferenceLoop = "local x = 0 for i = 1, 1000 do x = x + 1 end return x";

        private const string ShortLoop = "local n = 0 for i = 1, 100 do n = n + 1 end return n";

        /// <summary>
        /// Recursion whose deepest Lua frame is the tenth: it fits a call-depth limit of ten, and a count hook called
        /// from that frame is the eleventh.
        /// </summary>
        private const string RecursionToTheTenthFrame =
            "local function f(d) if d <= 1 then local x = 1 x = x + 1 return x end return f(d - 1) + 0 end return f(10)";

        private SynchronizationContext _savedContext;

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

        /// <summary>A count hook that records how often it fired on each thread.</summary>
        private sealed class CountingHook
        {
            public readonly LuaFunction Function;
            private readonly Dictionary<LuaState, int> _firesByThread = new();

            public CountingHook()
            {
                Function = new LuaFunction("vm_contract_counting_hook", (ctx, ct) =>
                {
                    Fires++;
                    _firesByThread.TryGetValue(ctx.State, out int fires);
                    _firesByThread[ctx.State] = fires + 1;
                    return new ValueTask<int>(ctx.Return());
                });
            }

            public int Fires { get; private set; }

            public int FiresOn(LuaState thread)
            {
                return _firesByThread.TryGetValue(thread, out int fires) ? fires : 0;
            }
        }

        private static LuaState NativeState()
        {
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenCoroutineLibrary();
            return state;
        }

        private static LuaValue[] RunNative(LuaState state, string source)
        {
            return state.DoStringAsync(source, "vm_contract").AsTask().GetAwaiter().GetResult();
        }

        private static LuaFunction AbortingHook(string abort)
        {
            return new LuaFunction("vm_contract_aborting_hook", (ctx, ct) =>
            {
                switch (abort)
                {
                    case "throws":
                        throw new InvalidOperationException("hook aborted");
                    case "throws-cancellation":
                        throw new OperationCanceledException("hook aborted");
                    case "returns-cancelled":
                        return new ValueTask<int>(Task.FromCanceled<int>(new CancellationToken(true)));
                    default:
                        return new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("hook aborted")));
                }
            });
        }

        [TestCase("throws")]
        [TestCase("throws-cancellation")]
        [TestCase("returns-cancelled")]
        [TestCase("returns-faulted")]
        public void VM_CountHookAbortedOnce_LaterHooksStillFireOnThatState(string abort)
        {
            // WHY (Lua-CSharp #353): up to 0.5.6 the VM set its in-hook flag before calling a count hook and cleared it
            // only when the hook returned, so one aborted hook stopped every later count hook on that state: a trip
            // that ended by throwing disarmed every later guard on it. The flag is now restored in a finally.
            LuaState state = NativeState();
            state.SetHook(AbortingHook(abort), string.Empty, 1);
            Assert.Catch<Exception>(() => RunNative(state, ShortLoop), "the aborted hook ends the run it fired in");

            CountingHook later = new();
            state.SetHook(later.Function, string.Empty, 1);
            Assert.AreEqual(100d, RunNative(state, ShortLoop)[0].Read<double>());
            Assert.Greater(later.Fires, 100, "a count hook installed after an aborted one must fire");
        }

        [Test]
        public void VM_CountHookRemoved_DoesNotFire_NegativeTwin()
        {
            LuaState state = NativeState();
            CountingHook hook = new();
            state.SetHook(hook.Function, string.Empty, 1);
            RunNative(state, ShortLoop);
            int fired = hook.Fires;
            state.SetHook(null, string.Empty, 0);
            RunNative(state, ShortLoop);

            Assert.Greater(fired, 100, "the counting hook counts the instructions it is installed for");
            Assert.AreEqual(fired, hook.Fires, "and nothing once it is removed, so the count above is not vacuous");
        }

        [Test]
        public void VM_CountHookWhoseFrameCannotBePushed_LaterHooksStillFireOnThatState()
        {
            // WHY (Lua-CSharp #354): a count hook called from the deepest frame a call-depth limit allows fails while
            // its own frame is set up, before it runs; the in-hook flag is restored on that path too.
            LuaState state = NativeState();
            state.MaxCallDepth = 10;
            CountingHook hook = new();
            state.SetHook(hook.Function, string.Empty, 1);
            LuaRuntimeException overflow = Assert.Catch<LuaRuntimeException>(
                () => RunNative(state, RecursionToTheTenthFrame), "the hook's frame is the one past the limit");
            StringAssert.Contains("stack overflow", overflow.Message);

            int firesBefore = hook.Fires;
            Assert.AreEqual(100d, RunNative(state, ShortLoop)[0].Read<double>());
            Assert.Greater(hook.Fires - firesBefore, 100, "the same hook must keep firing after its frame failed");
        }

        [Test]
        public void VM_TheSameRecursionWithoutAPerInstructionHook_FitsTheLimit_NegativeTwin()
        {
            LuaState state = NativeState();
            state.MaxCallDepth = 10;
            Assert.AreEqual(2d, RunNative(state, RecursionToTheTenthFrame)[0].Read<double>(),
                "only the hook's own frame passes the limit, so the test above fails the hook's frame setup");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [Timeout(60000)]
        public void GuardedRun_AfterTheEnginesCallDepthOverflow_StaysGuarded(int padding)
        {
            // WHY: the overflow below lands on a Lua call or on the guard hook's own frame, depending on where the
            // four-instruction hook window falls (the paddings move it); either way the run, and the next run on the
            // state, must still be cut by the guard.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            state.MaxCallDepth = 64;
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(60_000, 20_000, 0, guardObserver: observer);
            string pad = string.Concat(System.Linq.Enumerable.Repeat("local _ = 1 ", padding));

            LuaCsHostFunctionException sameRun = Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state,
                "local function f() " + pad + "return 1 + f() end\n" +
                "local ok, e = pcall(f)\n" +
                "assert(not ok and tostring(e):find('stack overflow'))\n" +
                "local n = 0 for i = 1, 50000000 do n = n + 1 end return n", guard));
            StringAssert.Contains("EXCEEDED_HARD_LIMIT_STEPS", sameRun.Message);
            Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state,
                "local n = 0 for i = 1, 50000000 do n = n + 1 end return n", guard), "and so must the next run");
            Assert.AreEqual(LuaCsGuardTripKind.Steps, observer.Records[0].TrippedBudget);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, observer.Records[1].TrippedBudget);
        }

        [Test]
        public void VM_ThreadCreatedUnderAHostHook_RunsIt_UntilItsOwnHookReplacesIt()
        {
            // WHY (Lua-CSharp #355): CreateCoroutine, CreateThread, coroutine.create and coroutine.wrap copy the
            // creator's host hook with a fresh counter. CoreAI arms its own hook on a thread before every resume, so
            // an inherited hook never runs one of its instructions; the guarded-resume tests below pin that.
            LuaState state = NativeState();
            LuaFunction body = RunNative(state, "return function() " + ShortLoop + " end")[0].Read<LuaFunction>();
            CountingHook creator = new();
            state.SetHook(creator.Function, string.Empty, 4);
            LuaState inheriting = state.CreateCoroutine(body, true);
            LuaState replacing = state.CreateCoroutine(body, true);
            state.SetHook(null, string.Empty, 0);
            LuaState createdAfter = state.CreateCoroutine(body, true);
            CountingHook own = new();
            replacing.SetHook(own.Function, string.Empty, 4);

            foreach (LuaState thread in new[] { inheriting, replacing, createdAfter })
            {
                thread.ResumeAsync(new LuaStack(4), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }

            Assert.Greater(creator.FiresOn(inheriting), 10, "a thread created under a host hook runs it");
            Assert.AreEqual(0, creator.FiresOn(replacing), "the thread's own SetHook replaces the inherited hook");
            Assert.Greater(own.FiresOn(replacing), 10);
            Assert.AreEqual(0, creator.FiresOn(createdAfter), "a thread created after the hook was removed has none");
        }

        [Test]
        public void RawCoroutine_CreatedAndResumedInOneGuardedCall_IsChargedOnce()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(60_000, 50_000_000, 0, guardObserver: observer);

            env.RunChunk(state, ReferenceLoop, guard);
            env.RunChunk(state, "local co = coroutine.create(function() " + ReferenceLoop + " end)\n" +
                                "return coroutine.resume(co)", guard);

            Assert.AreEqual(ReferenceLoopSteps, observer.Records[0].Steps);
            Assert.That(observer.Records[1].Steps, Is.InRange(ReferenceLoopSteps, ReferenceLoopSteps + 16),
                "the coroutine's instructions are charged to the call once, not by the inherited hook as well");
        }

        [Test]
        public void RawCoroutine_CreatedInOneGuardedCall_IsChargedToTheNestedCallThatResumesIt()
        {
            // WHY: the coroutine inherits the hook installed when it was created - the pooled hook of the first call,
            // which the second call rents again. Run with that hook, its instructions would go to the second call and
            // the nested call that resumes it would see none of them; the guarded coroutine.resume arms the
            // coroutine's own hook, nested in the resumer's run, before the first instruction.
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            RecordingObserver nestedObserver = new();
            LuaCsExecutionGuard nestedGuard = new(60_000, 50_000_000, 0, guardObserver: nestedObserver);
            LuaState state = null;
            registry.RegisterCallback("resume_nested", (ctx, ct) =>
            {
                LuaValue[] results = nestedGuard.Execute(state, ctx.GetArgument<LuaFunction>(0), ct);
                return new ValueTask<int>(ctx.Return(results));
            });
            state = env.Create(registry);
            RecordingObserver outerObserver = new();
            LuaCsExecutionGuard outerGuard = new(60_000, 50_000_000, 0, guardObserver: outerObserver);

            env.RunChunk(state, "co = coroutine.create(function() " + ReferenceLoop + " end)", outerGuard);
            LuaValue[] resumed = env.RunChunk(state,
                "return resume_nested(function() return coroutine.resume(co) end)", outerGuard);

            Assert.IsTrue(resumed[0].Read<bool>());
            Assert.AreEqual(1000d, resumed[1].Read<double>());
            Assert.AreEqual(1, nestedObserver.Records.Count);
            Assert.GreaterOrEqual(nestedObserver.Records[0].Steps, ReferenceLoopSteps,
                "the nested call that resumed the coroutine is charged its instructions");
            Assert.That(outerObserver.Records[1].Steps, Is.InRange(ReferenceLoopSteps, ReferenceLoopSteps + 64),
                "and the call around it receives them once, through the nested call");
        }

        [TestCase(1300, false)]
        [TestCase(2400, true)]
        [Timeout(60000)]
        public void RawCoroutine_CreatedInASignalHandler_IsHeldToTheHandlersBudgetAcrossFires(int iterations,
            bool overBudget)
        {
            // WHY the numbers: a resume of the coroutine costs about two steps an iteration, and the handler may spend
            // 4,000 a fire. 1,300 iterations fit once and would not fit twice, so the pair fails if the coroutine,
            // created under the handler's inherited hook, were charged by that hook as well; 2,400 do not fit once,
            // so the twin fails if the coroutine ran outside the handler's budget at all.
            LuaCsRbxApiBindings bindings = new(coroutineResumeBudget: new LuaCsCoroutineBudgetSettings(4000, 60_000));
            LuaCsSecureSandboxEditModeTests.NestedRunModStore store = new();
            LuaCsModStack stack = LuaCsSecureSandboxEditModeTests.NewNestedRunModStack(bindings, store);
            stack.Runtime.LoadMod("m",
                "local co\n" +
                "local fires = 0\n" +
                "game:GetService('RunService').Heartbeat:Connect(function()\n" +
                "  fires = fires + 1\n" +
                "  if fires == 1 then\n" +
                "    co = coroutine.create(function()\n" +
                "      for round = 1, 2 do local x = 0 for i = 1, " + iterations + " do x = x + 1 end coroutine.yield(round) end\n" +
                "      return 'done'\n" +
                "    end)\n" +
                "  end\n" +
                "  if fires <= 2 then\n" +
                "    local ok, v = coroutine.resume(co)\n" +
                "    store_set('fire' .. fires, tostring(ok) .. ':' .. tostring(v))\n" +
                "  end\n" +
                "end)");

            bindings.Scheduler.Advance(1d / 60d);
            bindings.Scheduler.Advance(1d / 60d);

            string errors = LuaCsSecureSandboxEditModeTests.HandlerErrorsOf(stack);
            if (overBudget)
            {
                StringAssert.Contains("EXCEEDED_RESUME_STEP_BUDGET (4000)", errors,
                    "the coroutine runs inside the handler's resume and is held to what it has left");
                Assert.AreEqual("", store.Get("m", "fire1"), "the trip ends the handler, uncaught");
                return;
            }

            Assert.AreEqual("", errors);
            Assert.AreEqual("true:1", store.Get("m", "fire1"));
            Assert.AreEqual("true:2", store.Get("m", "fire2"), "a later fire resumes the same coroutine within budget");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoroutineWrap_BodyCost_GoesToTheRunThatCallsIt_OnlyThroughTheSandboxsWrap(bool sandbox)
        {
            // WHY (LuaCsSecureEnvironment.HardenCoroutineLibrary): native wrap resumes its thread inside the library,
            // where CoreAI arms no hook. Since Lua-CSharp 0.5.8 that body is not hook-free: it runs the hook that was
            // installed when wrap was called, the pooled hook of the run that created it. Here the creating call has
            // ended and the next call holds that pooled hook again, so the body is charged to that call, and the call
            // nested in it - the one that runs the body - sees none of it, nor its budget. The sandbox's wrap resumes
            // through the guarded coroutine.resume, which charges the run that calls it.
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            RecordingObserver nestedObserver = new();
            LuaCsExecutionGuard nestedGuard = new(60_000, 50_000_000, 0, guardObserver: nestedObserver);
            LuaState state = null;
            registry.RegisterCallback("call_nested", (ctx, ct) =>
            {
                LuaValue[] results = nestedGuard.Execute(state, ctx.GetArgument<LuaFunction>(0), ct);
                return new ValueTask<int>(ctx.Return(results));
            });
            if (sandbox)
            {
                state = env.Create(registry);
            }
            else
            {
                state = NativeState();
                registry.ApplyToEnvironment(state);
            }

            RecordingObserver outerObserver = new();
            LuaCsExecutionGuard outerGuard = new(60_000, 50_000_000, 0, guardObserver: outerObserver);
            outerGuard.Execute(state, state.Load("w = coroutine.wrap(function() " + ReferenceLoop + " end)", "create"));
            LuaValue[] result = outerGuard.Execute(state, state.Load("return call_nested(function() return w() end)", "call"));

            Assert.AreEqual(1000d, result[0].Read<double>());
            if (sandbox)
            {
                Assert.GreaterOrEqual(nestedObserver.Records[0].Steps, ReferenceLoopSteps,
                    "the sandbox's wrap charges the run that calls the function");
            }
            else
            {
                Assert.Less(nestedObserver.Records[0].Steps, ReferenceLoopSteps,
                    "native wrap: the run that calls the function never sees the body's instructions");
                Assert.GreaterOrEqual(outerObserver.Records[1].Steps, ReferenceLoopSteps,
                    "they go to the run holding the hook the body inherited");
            }
        }

        [Test]
        public void StepCounts_OfTheReferenceLoop_AreTheBatchedCountsOnEveryGuardedPath()
        {
            // WHY exact: every budget CoreAI ships is a step count (a mod's HandlerMaxSteps, the per-resume 10,000, the
            // raw-coroutine multiplier), and the observability sink reports these counts as the "Lua steps" of a run.
            // A VM update that compiles the same source to more instructions, or fires hooks at other points, changes
            // what those budgets allow; it has to show up here first.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            RecordingObserver observer = new();
            LuaCsExecutionGuard guard = new(60_000, 50_000_000, 0, guardObserver: observer);

            env.RunChunk(state, ReferenceLoop, guard);
            env.RunChunk(state, "gco = coroutine.create(function() " + ReferenceLoop + " end)", guard);
            env.RunChunk(state, "return coroutine.resume(gco)", guard);
            LuaFunction body = env.RunChunk(state,
                "return function() local x = 0 for i = 1, 1000 do x = x + 1 end coroutine.yield(x) " +
                "for i = 1, 500 do x = x + 1 end return x end")[0].Read<LuaFunction>();
            LuaCsCoroutineHandle scheduled = new(state, body, 1_000_000, 60_000,
                LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
            LuaCsCoroutineHandle capped = new(state, body, 1_000_000, 60_000, 1_000_000);
            scheduled.Resume();
            long scheduledFirst = scheduled.ConsumedSteps;
            scheduled.Resume();
            capped.Resume();
            long cappedFirst = capped.ConsumedSteps;
            capped.Resume();

            Assert.AreEqual(ReferenceLoopSteps, observer.Records[0].Steps, "guarded call");
            Assert.AreEqual(4, observer.Records[1].Steps, "creating a coroutine runs none of its instructions");
            Assert.AreEqual(ReferenceLoopSteps, observer.Records[2].Steps, "raw coroutine resumed by a later call");
            Assert.AreEqual(2008, scheduledFirst, "scheduler-thread handle, first resume");
            Assert.AreEqual(3012, scheduled.ConsumedSteps, "scheduler-thread handle, both resumes");
            Assert.AreEqual(2009, cappedFirst, "handle with a lifetime cap counts every instruction, first resume");
            Assert.AreEqual(3014, capped.ConsumedSteps, "handle with a lifetime cap, both resumes");
        }

        [Test]
        public void Xpcall_HandlerOfTheEnginesCallDepthOverflow_GetsTheStackOverflowLine()
        {
            // WHY (Lua-CSharp 0.5.8): native xpcall now catches the overflow of LuaState.MaxCallDepth, and it hands its
            // handler the error object, which that overflow does not carry; Luau gives the handler the line. A small
            // limit keeps this on any thread; XpcallAroundRecursionToTheEnginesStackOverflow_* runs the default one.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            state.MaxCallDepth = 200;

            LuaValue[] described = env.RunChunk(state,
                "local function f() return 1 + f() end\n" +
                "return xpcall(f, function(m) return 'H:' .. type(m) .. ':' .. tostring(m) end)");
            LuaValue[] concatenated = env.RunChunk(state,
                "local function f() return 1 + f() end\n" +
                "return xpcall(f, function(e) return 'failed: ' .. e end)");

            Assert.IsFalse(described[0].Read<bool>());
            Assert.AreEqual("H:string:stack overflow", described[1].Read<string>());
            Assert.AreEqual("failed: stack overflow", concatenated[1].Read<string>(),
                "the common handler shape must not fail on the overflow's error value");
        }

        [TestCase(0, "string")]
        [TestCase(7, "string")]
        [TestCase(8, "nil")]
        public void Xpcall_HandlerOfTheOverflow_GetsTheLineOnlyBelowTheShallowLevels(int pcallLevels, string seen)
        {
            // WHY the twin at 8: the line costs a call into the VM per xpcall, so only xpcalls with fewer than 8 calls
            // back into Lua open below them pay it (LuaCsSecureEnvironment.CountedXPCall); deeper ones keep native
            // xpcall's nil. A change of that bound must be deliberate.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            state.MaxCallDepth = 200;

            LuaValue[] result = env.RunChunk(state,
                "local function f() return 1 + f() end\n" +
                "local seen = 'not called'\n" +
                "local function h(m) seen = type(m) return m end\n" +
                "local function nest(n) if n == 0 then xpcall(f, h) return end pcall(nest, n - 1) end\n" +
                "nest(" + pcallLevels + ")\n" +
                "return seen");

            Assert.AreEqual(seen, result[0].Read<string>());
        }

        [Test]
        public void Xpcall_PassesArgumentsResultsAndOtherErrorValuesThroughUnchanged_NegativeTwin()
        {
            List<string> rows = new();
            LuaRuntimeException ended = LuaCsSecureSandboxEditModeTests.RunRecordingChunk(
                "local function h(e) return 'H:' .. type(e) .. ':' .. tostring(e) end\n" +
                "record(xpcall(function(a, b, c) return c, b, a end, h, 1, 2, 3))\n" +
                "record(xpcall(function(...) return select('#', ...) end, h, nil, nil))\n" +
                "record(xpcall(function() error(nil) end, h))\n" +
                "record(xpcall(function() error('msg') end, h))\n" +
                "record(xpcall(function() error({}) end, function(e) return type(e) end))\n" +
                "record(xpcall(function() local t = nil return t.x end, h))\n" +
                "record(xpcall(function() return xpcall(function() error('inner', 0) end, h) end, h))\n" +
                "record(pcall(xpcall, nil, h))",
                new LuaCsExecutionGuard(60_000, 5_000_000, 0), rows);

            Assert.IsNull(ended, ended?.Message);
            CollectionAssert.AreEqual(new[]
            {
                "boolean:true|number:3|number:2|number:1",
                "boolean:true|number:2",
                "boolean:false|string:H:nil:nil",
                "boolean:false|string:H:string:msg",
                "boolean:false|string:table",
                "boolean:false|string:H:string:attempt to index a nil value (local 't')",
                "boolean:true|boolean:false|string:H:string:inner",
                "boolean:true|boolean:false|string:H:string:attempt to call a nil value"
            }, rows);
        }
    }
}
#endif
