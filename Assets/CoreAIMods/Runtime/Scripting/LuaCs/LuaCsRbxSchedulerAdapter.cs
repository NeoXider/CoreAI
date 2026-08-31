using System;
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
            bool propagateOriginalException = false)
        {
            OwnerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
            Callable = callable ?? throw new ArgumentNullException(nameof(callable));
            BindInitialArguments = bindInitialArguments;
            ResumeBudget = resumeBudget;
            PropagateOriginalException = propagateOriginalException;
        }

        public IScriptState OwnerState { get; }

        public object Callable { get; }

        public bool BindInitialArguments { get; }

        public IExecutionBudget ResumeBudget { get; }

        public bool PropagateOriginalException { get; }
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
            local realtime = task._realtime
            task._resumeValue = nil
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
            end";

        private readonly IScriptEngine _scriptEngine;
        private readonly IRbxRuntimeObservabilitySink _observability;
        private LuaCsRbxScriptThread _currentThread;

        public LuaCsRbxScriptThreadFactory(IScriptEngine scriptEngine = null,
            IRbxRuntimeObservabilitySink observability = null)
        {
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            _scriptEngine = scriptEngine ?? new LuaCsScriptEngine(observability: _observability);
        }

        /// <summary>The scheduler-owned thread currently executing a Lua host callback.</summary>
        public IRbxScriptThread CurrentThread => _currentThread;

        internal bool IsObservabilityEnabled => _observability != null;

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

            return new LuaCsRbxScriptThread(
                this, _scriptEngine, launch, ownerModId);
        }

        internal object CaptureCallable(LuaState ownerState, LuaValue callable)
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
            return new LuaCsRbxSchedulerCallable(capturedOwnerState, callable);
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

    /// <summary>Lua-CSharp scheduler thread adapter over one <see cref="IScriptCoroutine"/>.</summary>
    public sealed class LuaCsRbxScriptThread : IRbxScriptThread
    {
        private readonly LuaCsRbxScriptThreadFactory _factory;
        private readonly IScriptEngine _scriptEngine;
        private readonly LuaCsRbxSchedulerCallable _launch;
        private IScriptCoroutine _coroutine;
        private object[] _resumeArguments = Array.Empty<object>();
        private bool _killed;

        internal LuaCsRbxScriptThread(LuaCsRbxScriptThreadFactory factory,
            IScriptEngine scriptEngine, LuaCsRbxSchedulerCallable launch, string ownerModId)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
            _launch = launch ?? throw new ArgumentNullException(nameof(launch));
            OwnerModId = string.IsNullOrWhiteSpace(ownerModId)
                ? throw new ArgumentException("Owner mod id is required.", nameof(ownerModId))
                : ownerModId;
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
        public bool IsDead => _killed || _coroutine != null
            && (_coroutine.IsFinished || _coroutine.Status == ScriptCoroutineStatus.Dead);

        internal RbxError LastFailure { get; private set; }

        internal Exception LastException { get; private set; }

        internal IScriptState OwnerState => _launch.OwnerState;

        /// <inheritdoc />
        public RbxScriptThreadResumeResult Resume(params object[] args)
        {
            if (IsDead || _coroutine != null && !_coroutine.CanResume)
            {
                return RbxScriptThreadResumeResult.Failure(RbxError.BadArgument(
                    "cannot resume a dead Lua scheduler thread owned by mod " + OwnerModId,
                    "retain and resume only a live suspended task thread"));
            }

            bool isInitialResume = _coroutine == null;
            bool observe = _factory.IsObservabilityEnabled;
            long consumedStepsBefore = observe ? ReadConsumedSteps() : 0;
            LuaCsRbxScriptThread previous = _factory.Enter(this);
            _resumeArguments = args == null || args.Length == 0
                ? Array.Empty<object>()
                : (object[])args.Clone();
            try
            {
                if (_coroutine == null)
                {
                    _coroutine = CreateCoroutine(_resumeArguments);
                }

                ScriptResumeResult result = _coroutine.Resume();
                if (result.Ok)
                {
                    return RbxScriptThreadResumeResult.Success();
                }

                _killed = true;
                LastFailure = ToRbxError(result.Error);
                return RbxScriptThreadResumeResult.Failure(LastFailure);
            }
            catch (Exception ex)
            {
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
                    _factory.RecordThreadResume(ReadConsumedSteps() - consumedStepsBefore);
                }
            }
        }

        private long ReadConsumedSteps()
        {
            return _coroutine is LuaCsScriptCoroutine luaCoroutine
                ? luaCoroutine.ConsumedSteps
                : 0;
        }

        internal object ReadCurrentResumeArgument(int index)
        {
            return index >= 0 && index < _resumeArguments.Length
                ? _resumeArguments[index]
                : null;
        }

        /// <inheritdoc />
        public void Kill()
        {
            if (_killed)
            {
                return;
            }

            _killed = true;
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
