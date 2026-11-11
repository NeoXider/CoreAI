#if COREAI_LUA
using System;
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using CoreAI.Tests.EditMode.Performance;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Allocation budgets of the guarded paths every Lua handler crosses: a guarded call, a scheduler thread's
    /// resume, the engine-neutral guard seam, an idle scheduler frame and the legacy event and timer dispatch.
    /// Each budget has a negative twin (the same body plus one small allocation per operation must break it), so
    /// a budget can never pass because the counter saw nothing.
    /// </summary>
    /// <remarks>
    /// A budget above zero is the garbage that is left after this fixture's paths were cleaned and names where
    /// it comes from; lower it when that source is fixed. On a runtime whose allocation counter answers zero
    /// (Unity's Mono) every test here reports Ignored, as <see cref="AllocationMeter"/> documents.
    /// </remarks>
    [TestFixture]
    public sealed class LuaCsGuardedPathAllocationBudgetEditModeTests
    {
        private const int Ops = 4_000;

        /// <summary>
        /// What one idle scheduler frame allocates: nothing, since the PreRender pump hands
        /// <c>RbxRunService.FireRenderPhase</c> the delta unboxed and it is boxed only for a listener.
        /// </summary>
        private const long IdleFrameBudgetBytes = 0;

        /// <summary>
        /// What one legacy event or timer dispatch still allocates: the transaction frame pushed around
        /// every guarded handler call; the registry's mutation envelope scope is pooled.
        /// </summary>
        private const long LegacyDispatchBudgetBytes = 64;

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

        private static Action<int> GuardedCallOfAnEmptyFunction()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaFunction function = environment.RunChunk(state, "return function() end")[0].Read<LuaFunction>();
            LuaCsExecutionGuard guard = new(60_000, 1_000_000);
            return count =>
            {
                for (int index = 0; index < count; index++)
                {
                    guard.Execute(state, function, CancellationToken.None);
                }
            };
        }

        [Test]
        public void GuardedCall_OfAnEmptyFunction_AllocatesNothing()
        {
            AllocationMeter.AssertWithinBudget("LuaCsExecutionGuard.Execute(empty function)",
                GuardedCallOfAnEmptyFunction(), Ops, 0);
        }

        [Test]
        public void GuardedCall_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("LuaCsExecutionGuard.Execute(empty function)",
                GuardedCallOfAnEmptyFunction(), Ops, 0);
        }

        private static Action<int> SchedulerThreadResume()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaFunction body = environment.RunChunk(state,
                "return function() while true do coroutine.yield() end end")[0].Read<LuaFunction>();
            LuaCsCoroutineHandle handle = new(state, body,
                totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
            return count =>
            {
                for (int index = 0; index < count; index++)
                {
                    handle.Resume();
                }
            };
        }

        [Test]
        public void SchedulerThreadResume_ThatYieldsNoValue_AllocatesNothing()
        {
            AllocationMeter.AssertWithinBudget("LuaCsCoroutineHandle.Resume, immediate yield",
                SchedulerThreadResume(), Ops, 0);
        }

        [Test]
        public void SchedulerThreadResume_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("LuaCsCoroutineHandle.Resume, immediate yield",
                SchedulerThreadResume(), Ops, 0);
        }

        private static Action<int> SeamInvokeWithTwoArguments()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object handler = engine.RunChunk(state, "local n = 0 return function(name, payload) n = n + 1 end")[0];
            IScriptExecutionGuard guard = engine.CreateGuard(new ExecutionBudget(60_000, 1_000_000));
            object[] arguments = { "event", "payload" };
            return count =>
            {
                for (int index = 0; index < count; index++)
                {
                    guard.Invoke(state, handler, CancellationToken.None, arguments);
                }
            };
        }

        [Test]
        public void SeamInvoke_OfAHandlerThatReturnsNothing_AllocatesNothing()
        {
            // WHY a handler with no result: the seam returns script values as object[], so a result costs its
            // array and one box per value by contract; the arguments and the call itself are what this pins.
            AllocationMeter.AssertWithinBudget("IScriptExecutionGuard.Invoke(handler, 2 arguments), no result",
                SeamInvokeWithTwoArguments(), Ops, 0);
        }

        [Test]
        public void SeamInvoke_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("IScriptExecutionGuard.Invoke(handler, 2 arguments), no result",
                SeamInvokeWithTwoArguments(), Ops, 0);
        }

        private static Action<int> IdleSchedulerFrame()
        {
            CoreAI.Ai.LuaCs.LuaCsRbxApiBindings bindings = new();
            LuaCsSecureSandboxEditModeTests.NestedRunModStore store = new();
            LuaCsSecureSandboxEditModeTests.NewNestedRunModStack(bindings, store);
            return count =>
            {
                for (int index = 0; index < count; index++)
                {
                    bindings.Scheduler.Advance(1d / 60d);
                }
            };
        }

        [Test]
        public void IdleSchedulerFrame_AllocatesNothing()
        {
            AllocationMeter.AssertWithinBudget("ModScheduler.Advance, mod stack composed, no mod loaded",
                IdleSchedulerFrame(), Ops, IdleFrameBudgetBytes);
        }

        [Test]
        public void IdleSchedulerFrame_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("ModScheduler.Advance, mod stack composed, no mod loaded",
                IdleSchedulerFrame(), Ops, IdleFrameBudgetBytes);
        }

        private static Action<int> LegacyEventDispatch()
        {
            CoreAI.Ai.LuaCs.LuaCsRbxApiBindings bindings = new();
            LuaCsSecureSandboxEditModeTests.NestedRunModStore store = new();
            CoreAI.Ai.LuaCs.LuaCsModStack stack = LuaCsSecureSandboxEditModeTests.NewNestedRunModStack(bindings, store);
            stack.Runtime.LoadMod("budget-events",
                "local n = 0\n" +
                "hooks_on('budget.ping', function(name, payload) n = n + 1 end)\n" +
                "hooks_on('budget.ping', function(name, payload) n = n + 2 end)");
            return count =>
            {
                for (int index = 0; index < count; index++)
                {
                    stack.Runtime.EmitEvent("budget.ping", "p");
                    stack.Runtime.Tick(0d);
                }
            };
        }

        [Test]
        public void LegacyEventDispatch_IntoTwoHandlers_AllocatesOnlyTheTransactionFrames()
        {
            // WHY two handlers under twice the per-dispatch budget: the snapshot of the handler list, the
            // argument array, the envelope closure and its label used to be allocated per event or per handler.
            AllocationMeter.AssertWithinBudget("events_emit into two hooks_on handlers, one Tick",
                LegacyEventDispatch(), Ops, 2 * LegacyDispatchBudgetBytes);
        }

        [Test]
        public void LegacyEventDispatch_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("events_emit into two hooks_on handlers, one Tick",
                LegacyEventDispatch(), Ops, 2 * LegacyDispatchBudgetBytes);
        }
    }
}
#endif
