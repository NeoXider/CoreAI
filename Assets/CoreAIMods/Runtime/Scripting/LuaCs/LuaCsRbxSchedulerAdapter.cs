using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>Lua-CSharp launch data kept opaque to the engine-free scheduler.</summary>
    internal sealed class LuaCsRbxSchedulerCallable
    {
        public LuaCsRbxSchedulerCallable(IScriptState ownerState, object callable,
            bool bindInitialArguments = true, IExecutionBudget resumeBudget = null,
            bool propagateOriginalException = false, bool recyclable = false)
        {
            OwnerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
            Callable = callable ?? throw new ArgumentNullException(nameof(callable));
            BindInitialArguments = bindInitialArguments;
            ResumeBudget = resumeBudget;
            PropagateOriginalException = propagateOriginalException;
            Recyclable = recyclable;
        }

        public IScriptState OwnerState { get; }

        public object Callable { get; }

        public bool BindInitialArguments { get; }

        public IExecutionBudget ResumeBudget { get; }

        public bool PropagateOriginalException { get; }

        /// <summary>
        /// True for signal-handler callables: their thread is never handed to Lua as a task thread, so
        /// a handler that returns without yielding may run on a pooled <see cref="LuaCsRbxSignalRunner"/>.
        /// </summary>
        public bool Recyclable { get; }
    }

    /// <summary>
    /// Lua-CSharp implementation of the scheduler thread factory over the engine-neutral
    /// <see cref="IScriptEngine.CreateCoroutine"/> seam.
    /// </summary>
    public sealed class LuaCsRbxScriptThreadFactory : IRbxScriptThreadFactory
    {
        private const string WaitBridgeSource = @"
            local scheduleTaskWait = task.wait
            local scheduleLegacyWait = wait
            local resumeValue = task._resumeValue
            local scheduleSignalWait = task._scheduleSignalWait
            local signalResumeValues = task._signalResumeValues
            local scheduleRemoteInvokeServer = task._scheduleRemoteInvokeServer
            local scheduleRemoteInvokeClient = task._scheduleRemoteInvokeClient
            local remoteFunctionResumeValues = task._remoteFunctionResumeValues
            local warnInfiniteYield = task._warnInfiniteYield
            local realtime = task._realtime
            task._resumeValue = nil
            task._scheduleSignalWait = nil
            task._signalResumeValues = nil
            task._scheduleRemoteInvokeServer = nil
            task._scheduleRemoteInvokeClient = nil
            task._remoteFunctionResumeValues = nil
            task._warnInfiniteYield = nil
            task._realtime = nil
            task.wait = function(duration)
                scheduleTaskWait(duration)
                coroutine.yield()
                return resumeValue()
            end
            wait = function(duration)
                scheduleLegacyWait(duration)
                coroutine.yield()
                return resumeValue(), realtime()
            end
            task._signalWaitBridge = function(signal)
                scheduleSignalWait(signal)
                coroutine.yield()
                local values = signalResumeValues()
                return table.unpack(values, 1, values.n)
            end
            local function readRemoteFunctionResponse()
                local values = remoteFunctionResumeValues()
                if not values.ok then
                    error(values.error, 2)
                end
                return table.unpack(values, 1, values.n)
            end
            task._remoteFunctionInvokeServerBridge = function(remote, ...)
                scheduleRemoteInvokeServer(remote, ...)
                coroutine.yield()
                return readRemoteFunctionResponse()
            end
            task._remoteFunctionInvokeClientBridge = function(remote, player, ...)
                scheduleRemoteInvokeClient(remote, player, ...)
                coroutine.yield()
                return readRemoteFunctionResponse()
            end
            local function timedSignalWait(signal, duration)
                scheduleSignalWait(signal, duration)
                coroutine.yield()
                local values = signalResumeValues()
                return values.timedOut, values.elapsed, table.unpack(values, 1, values.n)
            end
            task._waitForChildBridge = function(instance, childName, timeout)
                local child = instance:FindFirstChild(childName)
                if child ~= nil then
                    return child
                end
                if timeout ~= nil and timeout <= 0 then
                    return nil
                end

                local remaining = timeout
                local warningRemaining = nil
                if timeout == nil then
                    warningRemaining = 5
                end

                while true do
                    local duration = remaining
                    if duration == nil then
                        duration = warningRemaining
                    end
                    local timedOut, elapsed, added = timedSignalWait(
                        instance.ChildAdded, duration)
                    if added ~= nil and added.Name == childName then
                        return added
                    end

                    child = instance:FindFirstChild(childName)
                    if child ~= nil then
                        return child
                    end

                    if timedOut then
                        if remaining ~= nil then
                            return nil
                        end
                        warnInfiniteYield(instance, childName)
                        warningRemaining = nil
                    elseif remaining ~= nil then
                        remaining = remaining - elapsed
                        if remaining <= 0 then
                            return nil
                        end
                    elseif warningRemaining ~= nil then
                        warningRemaining = warningRemaining - elapsed
                        if warningRemaining <= 0 then
                            warnInfiniteYield(instance, childName)
                            warningRemaining = nil
                        end
                    end
                end
            end";

        /// <summary>
        /// Idle runners kept per mod state. Handlers in one drain run one after another, so one parked
        /// runner serves a mod's whole Heartbeat; the rest absorb handlers that were parked in a yield
        /// while later fires arrived. Anything beyond that is released to the GC exactly as every
        /// handler thread was before pooling.
        /// </summary>
        internal const int MaxIdleRunnersPerState = 8;

        private sealed class RunnerPool
        {
            public RunnerPool(LuaState ownerState)
            {
                BodyFactory = LuaCsRbxSignalRunner.LoadBodyFactory(ownerState);
            }

            /// <summary>Runner body factory compiled once per state.</summary>
            public LuaValue BodyFactory { get; }

            /// <summary>The single mod this state's runners may serve; fixed by the first rent.</summary>
            public string OwnerModId { get; set; }

            public Stack<LuaCsRbxSignalRunner> Idle { get; } = new();
        }

        // WHY: keyed by the mod's LuaState (an ephemeron table), so a runner can only ever be rented for
        // handlers captured on the state it was built on, and a torn-down mod's runners die with its
        // state instead of needing explicit teardown plumbing.
        private readonly ConditionalWeakTable<LuaState, RunnerPool> _runnerPools = new();
        private readonly IScriptEngine _scriptEngine;
        private readonly IRbxRuntimeObservabilitySink _observability;
        private readonly Func<string, Func<ScriptResumeResult>, ScriptResumeResult>
            _resumeEnvelope;
        private LuaCsRbxScriptThread _currentThread;

        /// <summary>Signal runners built so far (diagnostic; tests prove reuse through it).</summary>
        internal long SignalRunnersCreated { get; private set; }

        /// <summary>Signal-handler spawns served by an idle runner instead of a new thread.</summary>
        internal long SignalRunnersReused { get; private set; }

        public LuaCsRbxScriptThreadFactory(IScriptEngine scriptEngine = null,
            IRbxRuntimeObservabilitySink observability = null,
            Func<string, Func<ScriptResumeResult>, ScriptResumeResult> resumeEnvelope = null)
        {
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            _scriptEngine = scriptEngine ?? new LuaCsScriptEngine(observability: _observability);
            _resumeEnvelope = resumeEnvelope;
        }

        /// <summary>The scheduler-owned thread currently executing a Lua host callback.</summary>
        public IRbxScriptThread CurrentThread => _currentThread;

        internal bool IsObservabilityEnabled => _observability != null;

        internal ScriptResumeResult Resume(string ownerModId,
            Func<ScriptResumeResult> resume)
        {
            return _resumeEnvelope == null
                ? resume()
                : _resumeEnvelope(ownerModId, resume);
        }

        /// <inheritdoc />
        public IRbxScriptThread Create(string ownerModId, object callable)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw RbxError.BadArgument(
                    "Lua scheduler thread owner mod id cannot be empty",
                    "schedule task work from a persistent mod context");
            }

            if (callable is LuaCsRbxScriptThread existingThread)
            {
                if (!string.Equals(existingThread.OwnerModId, ownerModId,
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "task thread belongs to mod " + existingThread.OwnerModId
                        + ", not " + ownerModId,
                        "resume or cancel the thread only from its owning mod");
                }

                return existingThread;
            }

            if (!(callable is LuaCsRbxSchedulerCallable launch))
            {
                throw RbxError.BadArgument(
                    "Lua scheduler callable is not a captured function",
                    "pass a Lua function to task.spawn, task.defer, or task.delay");
            }

            if (launch.Recyclable && launch.BindInitialArguments
                && launch.ResumeBudget == null && !launch.PropagateOriginalException)
            {
                return RentSignalRunner(ownerModId, launch);
            }

            return new LuaCsRbxScriptThread(
                this, _scriptEngine, launch, ownerModId);
        }

        private IRbxScriptThread RentSignalRunner(string ownerModId,
            LuaCsRbxSchedulerCallable launch)
        {
            LuaState ownerState = LuaCsScriptState.Unwrap(launch.OwnerState);
            RunnerPool pool = _runnerPools.GetValue(ownerState, CreateRunnerPool);
            if (pool.OwnerModId == null)
            {
                pool.OwnerModId = ownerModId;
            }
            else if (!string.Equals(pool.OwnerModId, ownerModId, StringComparison.Ordinal))
            {
                // WHY: a state serves one mod. Should composition ever run a second mod id on the
                // same state, that mod gets dedicated threads rather than another mod's runners.
                return new LuaCsRbxScriptThread(this, _scriptEngine, launch, ownerModId);
            }

            // WHY: a fresh wrapper per fire, only the runner is reused. Every C#-side identity (the
            // scheduler's record key, the mod's tracked-thread sets, RemoteFunction waits) belongs to
            // the wrapper, so nothing outside the pool can ever alias a runner's next tenant.
            while (pool.Idle.Count > 0)
            {
                LuaCsRbxSignalRunner idle = pool.Idle.Pop();
                if (idle.CanRun && ReferenceEquals(idle.OwnerState, ownerState))
                {
                    idle.ResetLifetime();
                    SignalRunnersReused++;
                    return new LuaCsRbxScriptThread(this, _scriptEngine, launch, ownerModId, idle);
                }
            }

            LuaCsRbxSignalRunner runner = new(ownerState, pool.BodyFactory);
            SignalRunnersCreated++;
            return new LuaCsRbxScriptThread(this, _scriptEngine, launch, ownerModId, runner);
        }

        private static RunnerPool CreateRunnerPool(LuaState ownerState)
        {
            return new RunnerPool(ownerState);
        }

        /// <summary>
        /// Returns a runner whose handler has returned (yielded along the way or not) to the idle pool
        /// of its own state and mod. A runner whose coroutine died or was killed is never offered here.
        /// </summary>
        internal void Recycle(LuaCsRbxSignalRunner runner, string ownerModId)
        {
            if (runner == null || !runner.CanRun)
            {
                return;
            }

            if (!_runnerPools.TryGetValue(runner.OwnerState, out RunnerPool pool)
                || !string.Equals(pool.OwnerModId, ownerModId, StringComparison.Ordinal)
                || pool.Idle.Count >= MaxIdleRunnersPerState)
            {
                return;
            }

            pool.Idle.Push(runner);
        }

        internal object CaptureCallable(LuaState ownerState, LuaValue callable,
            bool recyclable = false)
        {
            if (callable.Type != LuaValueType.Function)
            {
                if (LuaCsRbxLua.TryUnbox(callable, out LuaCsRbxScriptThread thread))
                {
                    return thread;
                }

                throw RbxError.BadArgument(
                    "task scheduler expects a function or thread, got "
                    + LuaCsRbxLua.Describe(callable),
                    "pass a Lua function or a thread returned by task.*");
            }

            IScriptState capturedOwnerState = _currentThread?.OwnerState
                                              ?? new LuaCsScriptState(ownerState);
            return new LuaCsRbxSchedulerCallable(capturedOwnerState, callable,
                recyclable: recyclable);
        }

        internal object CaptureChunk(IScriptState ownerState, string source,
            IExecutionBudget resumeBudget)
        {
            if (ownerState == null)
            {
                throw new ArgumentNullException(nameof(ownerState));
            }

            LuaState state = LuaCsScriptState.Unwrap(ownerState);
            LuaClosure closure = state.Load(source ?? string.Empty, "sandbox_chunk");
            return new LuaCsRbxSchedulerCallable(
                ownerState, new LuaValue(closure), false, resumeBudget, true);
        }

        internal void PrepareWaitBindings(IScriptState ownerState)
        {
            _scriptEngine.RunChunk(ownerState, WaitBridgeSource);
        }

        internal LuaCsRbxScriptThread Enter(LuaCsRbxScriptThread thread)
        {
            LuaCsRbxScriptThread previous = _currentThread;
            _currentThread = thread;
            return previous;
        }

        internal void Exit(LuaCsRbxScriptThread thread, LuaCsRbxScriptThread previous)
        {
            if (ReferenceEquals(_currentThread, thread))
            {
                _currentThread = previous;
            }
        }

        internal LuaState ResolveOwnerState(LuaState fallbackState)
        {
            return _currentThread == null
                ? fallbackState
                : LuaCsScriptState.Unwrap(_currentThread.OwnerState);
        }

        internal void RecordThreadResume(long guardedInstructionSteps)
        {
            if (_observability == null)
            {
                return;
            }

            try
            {
                _observability.RecordThreadResumes(1);
            }
            catch
            {
            }

            if (guardedInstructionSteps <= 0)
            {
                return;
            }

            try
            {
                _observability.RecordGuardedInstructionSteps(guardedInstructionSteps);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Lua-CSharp scheduler thread adapter over one <see cref="IScriptCoroutine"/>. In runner mode the
    /// coroutine is a pooled <see cref="LuaCsRbxSignalRunner"/>: once the armed handler has returned
    /// the thread detaches from the runner, reports itself dead to the scheduler, and hands the runner
    /// back to the factory pool for the next fire. The wrapper itself is never reused.
    /// </summary>
    public sealed class LuaCsRbxScriptThread : IRbxScriptThread
    {
        private readonly LuaCsRbxScriptThreadFactory _factory;
        private readonly IScriptEngine _scriptEngine;
        private readonly LuaCsRbxSchedulerCallable _launch;
        private Func<ScriptResumeResult> _resumeCore;
        private LuaCsRbxSignalRunner _runner;
        private IScriptCoroutine _coroutine;
        private object[] _resumeArguments = Array.Empty<object>();
        private long _remoteFunctionWaitGeneration;
        private bool _killed;
        private bool _runnerArmed;
        private bool _runnerIterationDone;

        internal LuaCsRbxScriptThread(LuaCsRbxScriptThreadFactory factory,
            IScriptEngine scriptEngine, LuaCsRbxSchedulerCallable launch, string ownerModId)
            : this(factory, scriptEngine, launch, ownerModId, null)
        {
        }

        internal LuaCsRbxScriptThread(LuaCsRbxScriptThreadFactory factory,
            IScriptEngine scriptEngine, LuaCsRbxSchedulerCallable launch, string ownerModId,
            LuaCsRbxSignalRunner runner)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
            _launch = launch ?? throw new ArgumentNullException(nameof(launch));
            OwnerModId = string.IsNullOrWhiteSpace(ownerModId)
                ? throw new ArgumentException("Owner mod id is required.", nameof(ownerModId))
                : ownerModId;
            _runner = runner;
            _coroutine = runner?.Coroutine;
            // WHY: the envelope call sits on the hottest path in the scheduler: a runner thread borrows
            // the runner's stable delegate, an ordinary thread builds its own once, never per resume.
            _resumeCore = runner?.ResumeDelegate;
        }

        /// <summary>The persistent mod that owns this scheduled thread.</summary>
        public string OwnerModId { get; }

        /// <inheritdoc />
        public RbxScriptThreadStatus Status
        {
            get
            {
                if (IsDead)
                {
                    return RbxScriptThreadStatus.Dead;
                }

                return _coroutine == null
                    || _coroutine.Status == ScriptCoroutineStatus.Suspended
                    ? RbxScriptThreadStatus.Suspended
                    : RbxScriptThreadStatus.Running;
            }
        }

        /// <inheritdoc />
        public bool IsDead => _killed || _runnerIterationDone || _coroutine != null
            && (_coroutine.IsFinished || _coroutine.Status == ScriptCoroutineStatus.Dead);

        internal RbxError LastFailure { get; private set; }

        internal Exception LastException { get; private set; }

        internal IScriptState OwnerState => _launch.OwnerState;

        /// <summary>True while this thread's handler runs on a pooled signal runner.</summary>
        internal bool IsSignalRunner => _runner != null;

        /// <inheritdoc />
        public RbxScriptThreadResumeResult Resume(params object[] args)
        {
            if (IsDead || _coroutine != null && !_coroutine.CanResume)
            {
                return RbxScriptThreadResumeResult.Failure(RbxError.BadArgument(
                    "cannot resume a dead Lua scheduler thread owned by mod " + OwnerModId,
                    "retain and resume only a live suspended task thread"));
            }

            bool isInitialResume = _runner != null ? !_runnerArmed : _coroutine == null;
            bool observe = _factory.IsObservabilityEnabled;
            long consumedStepsBefore = observe ? ReadConsumedSteps() : 0;
            LuaCsRbxScriptThread previous = _factory.Enter(this);
            LuaCsRbxSignalRunner finishedRunner = null;
            _resumeArguments = args == null || args.Length == 0
                ? Array.Empty<object>()
                : (object[])args.Clone();
            try
            {
                if (_runner != null)
                {
                    if (!_runnerArmed)
                    {
                        _runner.Arm(LuaCsValueMarshaller.Unbox(_launch.Callable), _resumeArguments);
                        _runnerArmed = true;
                    }
                }
                else if (_coroutine == null)
                {
                    _coroutine = CreateCoroutine(_resumeArguments);
                }

                ScriptResumeResult result = _factory.Resume(
                    OwnerModId, _resumeCore ??= ResumeCore);
                if (result.Ok)
                {
                    if (_runner != null && _runner.IterationCompleted)
                    {
                        // WHY: the handler returned, so to the scheduler this thread is dead from here
                        // on. Detach first: a dead wrapper must never reach the runner again (Kill or
                        // status queries), because the runner's next tenant is another wrapper.
                        finishedRunner = _runner;
                        _runner = null;
                        _coroutine = null;
                        _runnerIterationDone = true;
                    }

                    return RbxScriptThreadResumeResult.Success();
                }

                _killed = true;
                _runner?.Disarm();
                LastFailure = ToRbxError(result.Error);
                return RbxScriptThreadResumeResult.Failure(LastFailure);
            }
            catch (Exception ex)
            {
                _runner?.Disarm();
                _coroutine?.Kill();
                _killed = true;
                if (_launch.PropagateOriginalException && isInitialResume)
                {
                    LastException = ex;
                    return RbxScriptThreadResumeResult.Success();
                }

                LastFailure = ToRbxError(ex.Message);
                return RbxScriptThreadResumeResult.Failure(LastFailure);
            }
            finally
            {
                _resumeArguments = Array.Empty<object>();
                _factory.Exit(this, previous);
                if (observe)
                {
                    _factory.RecordThreadResume(ReadConsumedSteps(finishedRunner) - consumedStepsBefore);
                }

                if (finishedRunner != null)
                {
                    _factory.Recycle(finishedRunner, OwnerModId);
                }
            }
        }

        private ScriptResumeResult ResumeCore()
        {
            return _coroutine.Resume();
        }

        private long ReadConsumedSteps(LuaCsRbxSignalRunner finishedRunner = null)
        {
            IScriptCoroutine coroutine = _coroutine ?? finishedRunner?.Coroutine;
            return coroutine is LuaCsScriptCoroutine luaCoroutine
                ? luaCoroutine.ConsumedSteps
                : 0;
        }

        internal object ReadCurrentResumeArgument(int index)
        {
            return index >= 0 && index < _resumeArguments.Length
                ? _resumeArguments[index]
                : null;
        }

        internal long AdvanceRemoteFunctionWaitGeneration()
        {
            _remoteFunctionWaitGeneration = checked(_remoteFunctionWaitGeneration + 1L);
            return _remoteFunctionWaitGeneration;
        }

        /// <inheritdoc />
        public void Kill()
        {
            if (_killed)
            {
                return;
            }

            _killed = true;
            _runner?.Disarm();
            _coroutine?.Kill();
        }

        private IScriptCoroutine CreateCoroutine(object[] initialArguments)
        {
            if (!_launch.BindInitialArguments)
            {
                if (_launch.PropagateOriginalException)
                {
                    return CreateUnprotectedCoroutine();
                }

                return _scriptEngine.CreateCoroutine(
                    _launch.OwnerState, _launch.Callable, _launch.ResumeBudget);
            }

            LuaValue callable = LuaCsValueMarshaller.Unbox(_launch.Callable);
            LuaValue[] luaArguments = new LuaValue[initialArguments.Length];
            for (int index = 0; index < initialArguments.Length; index++)
            {
                luaArguments[index] = LuaCsValueMarshaller.Unbox(initialArguments[index]);
            }

            LuaFunction boundCallable = new("task.scheduled", async (ctx, ct) =>
            {
                LuaValue[] results = await ctx.State.CallAsync(
                    callable, luaArguments.AsSpan(), ct);
                return ctx.Return(results);
            });
            return _scriptEngine.CreateCoroutine(
                _launch.OwnerState, boundCallable, _launch.ResumeBudget);
        }

        private IScriptCoroutine CreateUnprotectedCoroutine()
        {
            LuaState ownerState = LuaCsScriptState.Unwrap(_launch.OwnerState);
            int budgetPerResume = _launch.ResumeBudget != null
                                  && _launch.ResumeBudget.MaxSteps > 0
                ? (int)Math.Min(_launch.ResumeBudget.MaxSteps, int.MaxValue)
                : LuaCsCoroutineHandle.DefaultBudgetPerResume;
            int resumeTimeoutMs = _launch.ResumeBudget != null
                                  && _launch.ResumeBudget.TimeoutMs > 0
                ? _launch.ResumeBudget.TimeoutMs
                : LuaCsCoroutineHandle.DefaultResumeTimeoutMs;
            LuaCsCoroutineHandle handle = new(
                ownerState,
                LuaCsScriptExecutionGuard.UnwrapCallable(_launch.Callable),
                budgetPerResume,
                resumeTimeoutMs,
                LuaCsCoroutineHandle.DefaultTotalLifetimeSteps,
                false);
            return new LuaCsScriptCoroutine(handle);
        }

        private RbxError ToRbxError(string message)
        {
            string error = string.IsNullOrWhiteSpace(message)
                ? "scheduled Lua thread failed"
                : message;
            bool budgetExceeded = error.IndexOf("EXCEEDED_RESUME_STEP_BUDGET",
                                      StringComparison.Ordinal) >= 0
                                  || error.IndexOf("EXCEEDED_MEMORY_BUDGET",
                                      StringComparison.Ordinal) >= 0
                                  || error.IndexOf("resume exceeded",
                                      StringComparison.OrdinalIgnoreCase) >= 0;
            return new RbxError(
                budgetExceeded ? RbxErrorCode.BudgetExceeded : RbxErrorCode.BadArgument,
                error,
                budgetExceeded
                    ? "reduce the work performed between yields"
                    : "fix the Lua error before scheduling the thread again",
                OwnerModId);
        }
    }
}
