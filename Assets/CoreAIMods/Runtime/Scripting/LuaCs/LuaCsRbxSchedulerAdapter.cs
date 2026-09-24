using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
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
            bool propagateOriginalException = false, bool recyclable = false,
            bool resumableByHandle = false)
        {
            OwnerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
            Callable = callable ?? throw new ArgumentNullException(nameof(callable));
            BindInitialArguments = bindInitialArguments;
            ResumeBudget = resumeBudget;
            PropagateOriginalException = propagateOriginalException;
            Recyclable = recyclable;
            ResumableByHandle = resumableByHandle;
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

        /// <summary>
        /// True when the thread is handed back to Lua as a task handle (task.spawn, task.defer,
        /// task.delay), so a native <c>coroutine.yield</c> parks it and <c>task.spawn(handle, ...)</c>
        /// can resume it later (M2-14). Any other thread (signal handlers, RemoteFunction callbacks, the
        /// legacy globals, the main chunk) has no handle to be resumed through, so a native yield ends
        /// it with a loud fault instead of leaking a record that holds a quota slot forever (M2-20).
        /// </summary>
        public bool ResumableByHandle { get; }
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
            local checkWaitForChild = task._checkWaitForChild
            local realtime = task._realtime
            local buildCharacter = task._buildCharacter
            local noteLoadCharacterDeprecation = task._noteLoadCharacterDeprecation
            -- WHY: captured before any mod code runs. Each bridge suspends its thread right after
            -- scheduling the resume; a mod that reassigned coroutine.yield could otherwise turn that
            -- suspension into a no-op and leave the scheduler waiting on a thread that is still running.
            local yield = coroutine.yield
            task._resumeValue = nil
            task._scheduleSignalWait = nil
            task._signalResumeValues = nil
            task._scheduleRemoteInvokeServer = nil
            task._scheduleRemoteInvokeClient = nil
            task._remoteFunctionResumeValues = nil
            task._warnInfiniteYield = nil
            task._checkWaitForChild = nil
            task._realtime = nil
            task._buildCharacter = nil
            task._noteLoadCharacterDeprecation = nil
            task.wait = function(duration)
                scheduleTaskWait(duration)
                yield()
                return resumeValue()
            end
            wait = function(duration)
                scheduleLegacyWait(duration)
                yield()
                return resumeValue(), realtime()
            end
            task._signalWaitBridge = function(signal)
                scheduleSignalWait(signal)
                yield()
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
                yield()
                return readRemoteFunctionResponse()
            end
            task._remoteFunctionInvokeClientBridge = function(remote, player, ...)
                scheduleRemoteInvokeClient(remote, player, ...)
                yield()
                return readRemoteFunctionResponse()
            end
            local function timedSignalWait(signal, duration)
                scheduleSignalWait(signal, duration)
                yield()
                local values = signalResumeValues()
                return values.timedOut, values.elapsed
            end
            -- WHY LoadCharacterAsync yields at all: the mirror makes it a yielding call that
            -- returns once the character is loaded, and CoreAI's signals are deferred, so a
            -- non-yielding version would return BEFORE the CharacterAdded handlers ran. One
            -- task.wait() puts the caller's resumption after the signal drain, which is exactly
            -- Roblox's observable order.
            task._loadCharacterBridge = function(player)
                local character = buildCharacter(player)
                task.wait()
                return character
            end
            task._loadCharacterDeprecatedBridge = function(player)
                noteLoadCharacterDeprecation()
                return task._loadCharacterBridge(player)
            end
            task._waitForChildBridge = function(instance, childName, timeout)
                childName, timeout = checkWaitForChild(instance, childName, timeout)
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
                    -- WHY the added child is never read: ChildAdded is deferred, so by the time this
                    -- thread resumes that child may already be destroyed (reading its Name raised
                    -- INSTANCE_DESTROYED into the waiting script) or renamed or reparented. Looking the
                    -- name up again answers what WaitForChild promises: a live child with that name.
                    local timedOut, elapsed = timedSignalWait(instance.ChildAdded, duration)
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

        /// <summary>The per-resume allocation budget a mod's state was loaded with (see <see cref="CaptureChunk"/>).</summary>
        private sealed class StateAllocationBudget
        {
            public long MaxAllocatedBytes;
        }

        // WHY: keyed by the mod's LuaState (an ephemeron table), so a runner can only ever be rented for
        // handlers captured on the state it was built on, and a torn-down mod's runners die with its
        // state instead of needing explicit teardown plumbing.
        private readonly ConditionalWeakTable<LuaState, RunnerPool> _runnerPools = new();

        // WHY keyed by the mod's LuaState like the runner pools: the mod's IExecutionBudget reaches this
        // factory once, with its main chunk, while its task.* threads and signal runners are created later
        // from bare callables captured on the same state. Without this record they fell back to a default
        // and a host's lowered HandlerMaxAllocatedBytes never reached a single handler.
        private readonly ConditionalWeakTable<LuaState, StateAllocationBudget> _allocationBudgets = new();
        private readonly IScriptEngine _scriptEngine;
        private readonly IRbxRuntimeObservabilitySink _observability;
        private readonly Func<string, Func<ScriptResumeResult>, ScriptResumeResult>
            _resumeEnvelope;
        private readonly LuaCsCoroutineBudgetSettings _coroutineResumeBudget;
        private LuaCsRbxScriptThread _currentThread;
        private LuaState _lastCaller;

        /// <summary>Signal runners built so far (diagnostic; tests prove reuse through it).</summary>
        internal long SignalRunnersCreated { get; private set; }

        /// <summary>Signal-handler spawns served by an idle runner instead of a new thread.</summary>
        internal long SignalRunnersReused { get; private set; }

        public LuaCsRbxScriptThreadFactory(IScriptEngine scriptEngine = null,
            IRbxRuntimeObservabilitySink observability = null,
            Func<string, Func<ScriptResumeResult>, ScriptResumeResult> resumeEnvelope = null,
            LuaCsCoroutineBudgetSettings coroutineResumeBudget = null)
        {
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            // WHY never null: every coroutine handle this factory builds (directly, or through its own
            // IScriptEngine below) reads this object as its configurable default — see
            // LuaCsCoroutineBudgetSettings and CoroutineResumeBudgetDefaults.
            _coroutineResumeBudget = coroutineResumeBudget ?? new LuaCsCoroutineBudgetSettings();
            _scriptEngine = scriptEngine ?? new LuaCsScriptEngine(
                observability: _observability, coroutineResumeBudget: _coroutineResumeBudget);
            _resumeEnvelope = resumeEnvelope;
        }

        /// <summary>
        /// Live per-resume coroutine budget every construction site under this factory falls back to
        /// when nothing more specific overrides it — the composition-configurable default from
        /// <see cref="LuaCsCoroutineBudgetSettings"/>, never null. Read by
        /// <see cref="LuaCsRbxScriptThread.CreateUnprotectedCoroutine"/> and by <see cref="RentSignalRunner"/>.
        /// </summary>
        internal LuaCsCoroutineBudgetSettings CoroutineResumeBudgetDefaults => _coroutineResumeBudget;

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

            // WHY the live settings object, not a frozen snapshot: this runner is pooled and reused for
            // every future fire of this mod's signal handlers, so a later ScriptContext:SetTimeout must
            // reach it too — see LuaCsCoroutineHandle's liveResumeBudget parameter.
            LuaCsRbxSignalRunner runner = new(ownerState, pool.BodyFactory, _coroutineResumeBudget,
                ResolveAllocationBudget(ownerState, null));
            SignalRunnersCreated++;
            return new LuaCsRbxScriptThread(this, _scriptEngine, launch, ownerModId, runner);
        }

        private static RunnerPool CreateRunnerPool(LuaState ownerState)
        {
            return new RunnerPool(ownerState);
        }

        private static StateAllocationBudget CreateStateAllocationBudget(LuaState ownerState)
        {
            return new StateAllocationBudget
            {
                MaxAllocatedBytes = LuaCsCoroutineHandle.DefaultMaxAllocatedBytesPerResume
            };
        }

        /// <summary>
        /// The per-resume allocation budget a thread on <paramref name="ownerState"/> runs under: the
        /// explicit <paramref name="resumeBudget"/>'s when there is one, else the budget the mod's main
        /// chunk was captured with on that state, else
        /// <see cref="LuaCsCoroutineHandle.DefaultMaxAllocatedBytesPerResume"/>. <c>&lt;= 0</c> means the
        /// check is disabled, exactly as <see cref="IExecutionBudget.MaxAllocatedBytes"/> documents.
        /// </summary>
        internal long ResolveAllocationBudget(LuaState ownerState, IExecutionBudget resumeBudget)
        {
            if (resumeBudget != null)
            {
                return resumeBudget.MaxAllocatedBytes;
            }

            return ownerState != null
                   && _allocationBudgets.TryGetValue(ownerState, out StateAllocationBudget recorded)
                ? recorded.MaxAllocatedBytes
                : LuaCsCoroutineHandle.DefaultMaxAllocatedBytesPerResume;
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
            bool recyclable = false, bool resumableByHandle = false)
        {
            // WHY: the Lua thread asking for a scheduler thread. task.spawn resumes that thread before it returns,
            // nested in this caller's run, and that resume is held to what the run has left (see TakeResumer).
            _lastCaller = ownerState;
            if (callable.Type != LuaValueType.Function)
            {
                if (LuaCsRbxLua.TryUnbox(callable, out LuaCsRbxScriptThread thread))
                {
                    return thread;
                }

                if (callable.Type == LuaValueType.Thread)
                {
                    // WHY its own message: "expects a function or thread, got thread" reads as a
                    // contradiction. The refusal is about WHICH thread: R4.10 keeps coroutine.create
                    // threads and coroutine.running() values outside the scheduler.
                    throw RbxError.BadArgument(
                        "task scheduler cannot take a coroutine.create thread or a coroutine.running() value",
                        "pass a Lua function, or a thread handle returned by task.spawn, task.defer or task.delay");
                }

                throw RbxError.BadArgument(
                    "task scheduler expects a function or thread, got "
                    + LuaCsRbxLua.Describe(callable),
                    "pass a Lua function or a thread returned by task.*");
            }

            IScriptState capturedOwnerState = _currentThread?.OwnerState
                                              ?? new LuaCsScriptState(ownerState);
            return new LuaCsRbxSchedulerCallable(capturedOwnerState, callable,
                recyclable: recyclable, resumableByHandle: resumableByHandle);
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
            if (resumeBudget != null)
            {
                _allocationBudgets.GetValue(state, CreateStateAllocationBudget).MaxAllocatedBytes =
                    resumeBudget.MaxAllocatedBytes;
            }

            return new LuaCsRbxSchedulerCallable(
                ownerState, new LuaValue(closure), false, resumeBudget, true);
        }

        internal void PrepareWaitBindings(IScriptState ownerState)
        {
            _scriptEngine.RunChunk(ownerState, WaitBridgeSource);
        }

        /// <summary>
        /// The Lua thread whose run a thread resumed now is nested in (see <see cref="LuaCsGuardedRun"/>): the one
        /// that last asked this factory for a scheduler thread - whose <c>task.spawn</c> is resuming it - when a run
        /// on it is executing, else the thread of <paramref name="previous"/>, the scheduler thread whose host
        /// callback is running. Null for a resume the scheduler drives from its own frame, which keeps its full
        /// per-resume budget and starts its count of calls back into Lua from zero. Reading it forgets the caller.
        /// </summary>
        /// <remarks>
        /// WHY both are checked for a run that is still executing: the caller is remembered until the next resume,
        /// which may come frames later (a task.defer), when that caller's run has long ended; and only a run on
        /// the stack right now can enclose this resume, since a handle's resume never waits for a frame.
        /// </remarks>
        internal LuaState TakeResumer(LuaCsRbxScriptThread previous)
        {
            LuaState caller = _lastCaller;
            _lastCaller = null;
            if (LuaCsGuardedRun.FindExecuting(caller) != null)
            {
                return caller;
            }

            LuaState previousThread = previous?.LuaThread;
            return LuaCsGuardedRun.FindExecuting(previousThread) != null ? previousThread : null;
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
    /// <para>
    /// A resume that ends suspended without a scheduler wait (a native <c>coroutine.yield</c>) parks a
    /// thread that Lua holds a task handle for, so <c>task.spawn(handle, ...)</c> can resume it (M2-14).
    /// Any other thread has no handle to be resumed through: the adapter stops it and reports the stop
    /// as its <see cref="TerminalFault"/> (M2-20).
    /// </para>
    /// </summary>
    public sealed class LuaCsRbxScriptThread : IRbxScriptThread, IRbxScriptThreadTerminalFault
    {
        private readonly LuaCsRbxScriptThreadFactory _factory;
        private readonly IScriptEngine _scriptEngine;
        private readonly LuaCsRbxSchedulerCallable _launch;
        private Func<ScriptResumeResult> _resumeCore;
        private LuaCsRbxSignalRunner _runner;
        private IScriptCoroutine _coroutine;
        private object[] _resumeArguments = Array.Empty<object>();
        private long _remoteFunctionWaitGeneration;
        private RbxError _terminalFault;
        private bool _killed;
        private bool _runnerArmed;
        private bool _runnerIterationDone;
        private bool _scheduledYieldPending;
        private bool _parkedByNativeYield;

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

        /// <inheritdoc />
        /// <remarks>
        /// Set only when this adapter stopped the thread after a resume that itself succeeded: a native
        /// <c>coroutine.yield</c> of a thread nothing can resume. A failed main-chunk load is not
        /// reported here; <see cref="LastException"/> carries it out of LoadMod instead.
        /// </remarks>
        public RbxError TerminalFault => IsDead ? _terminalFault : null;

        internal IScriptState OwnerState => _launch.OwnerState;

        /// <summary>True while this thread's handler runs on a pooled signal runner.</summary>
        internal bool IsSignalRunner => _runner != null;

        /// <summary>The Lua thread this scheduler thread currently runs on; null before its first resume.</summary>
        internal LuaState LuaThread => (_coroutine as LuaCsScriptCoroutine)?.Handle.Thread;

        /// <summary>
        /// True while this thread's own coroutine is the one executing Lua. False while it has resumed a
        /// nested <c>coroutine.create</c> coroutine, whose code then runs with this thread still current
        /// in the factory: a wait scheduled from there would suspend the wrong coroutine (M2-06).
        /// </summary>
        internal bool IsOwnCoroutineRunning =>
            _coroutine != null && _coroutine.Status == ScriptCoroutineStatus.Running;

        /// <summary>
        /// Ledger set of the bindings' tracked-thread bookkeeping that holds this thread, so the entry
        /// can be dropped in O(1) when the scheduler retires the thread (M2-07). Owned by
        /// <see cref="LuaCsRbxApiBindings"/>.
        /// </summary>
        internal HashSet<IRbxScriptThread> TrackingSet { get; set; }

        /// <summary>
        /// Connection of the signal:Wait or RemoteFunction response this thread is suspended on, so a
        /// cancelled or killed waiter disconnects it at once instead of when the signal next fires
        /// (M2-18). Owned by <see cref="LuaCsRbxApiBindings"/>.
        /// </summary>
        internal RbxScriptConnection PendingWaitConnection { get; set; }

        /// <summary>
        /// Responder of the RemoteFunction request this callback thread still has to answer; failed if
        /// the thread is stopped before it returns, so the caller is not left waiting for its timeout.
        /// Owned by <see cref="LuaCsRbxApiBindings"/>.
        /// </summary>
        internal RbxNetworkRequestResponder PendingResponder { get; set; }

        /// <summary>
        /// Records that a scheduler wait was scheduled for this thread's next suspension, so the resume
        /// that ends in that suspension is not mistaken for a native <c>coroutine.yield</c>.
        /// </summary>
        internal void NoteScheduledYield()
        {
            _scheduledYieldPending = true;
        }

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
            // WHY only after a native yield: a scheduler wait reads its resume values through the
            // bridge's own hook, so handing them to coroutine.yield as well would only cost a copy.
            bool resumeValuesToYield = _parkedByNativeYield && !isInitialResume && _runner == null;
            _parkedByNativeYield = false;
            _scheduledYieldPending = false;
            bool observe = _factory.IsObservabilityEnabled;
            long consumedStepsBefore = observe ? ReadConsumedSteps() : 0;
            LuaCsRbxScriptThread previous = _factory.Enter(this);
            LuaState resumer = _factory.TakeResumer(previous);
            LuaCsRbxSignalRunner finishedRunner = null;
            LuaCsCoroutineHandle handle = null;
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

                // WHY: a task.spawn runs this thread inside the caller's run, whose hook cannot fire until it
                // returns; the resume is held to what that run has left and continues its count of calls back
                // into Lua (see LuaCsGuardedRun, LuaCsSecureEnvironment.MaxCCallDepth).
                handle = (_coroutine as LuaCsScriptCoroutine)?.Handle;
                handle?.ResumeNextNestedIn(resumer);
                ScriptResumeResult result = resumeValuesToYield
                    ? _factory.Resume(OwnerModId, ResumeWithValuesForYield)
                    : _factory.Resume(OwnerModId, _resumeCore ??= ResumeCore);
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
                    else if (!IsDead && !_scheduledYieldPending)
                    {
                        ParkOrStopAfterNativeYield(isInitialResume);
                    }

                    return RbxScriptThreadResumeResult.Success();
                }

                _killed = true;
                _runner?.Disarm();
                LastFailure = ToRbxError(result.Error, LastResumeTrippedBudget());
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

                LastFailure = ToRbxError(ex.Message, LastResumeTrippedBudget());
                return RbxScriptThreadResumeResult.Failure(LastFailure);
            }
            finally
            {
                handle?.ResumeNextNestedIn(null);
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

        private ScriptResumeResult ResumeWithValuesForYield()
        {
            return _coroutine.Resume(_resumeArguments);
        }

        /// <summary>
        /// Handles a resume that ended suspended although no scheduler wait was scheduled: the thread's
        /// own code called <c>coroutine.yield</c>. A task thread is parked for <c>task.spawn(handle)</c>;
        /// any other thread is stopped, because nothing can ever resume it and it would otherwise hold a
        /// live-thread quota slot until its mod unloads (M2-20).
        /// </summary>
        private void ParkOrStopAfterNativeYield(bool isInitialResume)
        {
            if (_launch.ResumableByHandle)
            {
                _parkedByNativeYield = true;
                return;
            }

            string kind = _launch.PropagateOriginalException
                ? "the mod's main chunk"
                : _launch.Recyclable
                    ? "a signal handler"
                    : "a scheduler thread that has no task handle (a RemoteFunction callback or a legacy spawn/delay function)";
            RbxError stop = new(
                RbxErrorCode.ContextViolation,
                "coroutine.yield() suspended " + kind
                + " outside the task scheduler; nothing can resume it, so it was stopped",
                "pause with task.wait() or signal:Wait(), or run the work in task.spawn and resume that "
                + "thread with task.spawn(thread, ...)",
                OwnerModId);
            _runner?.Disarm();
            _coroutine?.Kill();
            _killed = true;
            if (_launch.PropagateOriginalException && isInitialResume)
            {
                LastException = stop;
                return;
            }

            _terminalFault = stop;
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

                return CreateEngineCoroutine(_launch.Callable);
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
            return CreateEngineCoroutine(boundCallable);
        }

        private IScriptCoroutine CreateEngineCoroutine(object callable)
        {
            // WHY the concrete overload when the engine is Lua-CSharp: the neutral seam can only carry an
            // allocation budget inside an IExecutionBudget, and passing one would also freeze the live
            // step/time default a task.* thread must keep reading (see LuaCsCoroutineBudgetSettings).
            if (_scriptEngine is LuaCsScriptEngine luaEngine)
            {
                return luaEngine.CreateCoroutine(_launch.OwnerState, callable, _launch.ResumeBudget,
                    _factory.ResolveAllocationBudget(
                        LuaCsScriptState.Unwrap(_launch.OwnerState), _launch.ResumeBudget));
            }

            return _scriptEngine.CreateCoroutine(_launch.OwnerState, callable, _launch.ResumeBudget);
        }

        private IScriptCoroutine CreateUnprotectedCoroutine()
        {
            LuaState ownerState = LuaCsScriptState.Unwrap(_launch.OwnerState);
            long maxAllocatedBytes = _factory.ResolveAllocationBudget(ownerState, _launch.ResumeBudget);
            LuaCsCoroutineHandle handle;
            if (_launch.ResumeBudget != null)
            {
                // WHY frozen, not live: an explicit resumeBudget is a per-call override (e.g. a mod's
                // main-chunk HandlerMaxSteps/HandlerTimeoutMs) — a different, unrelated budget from the
                // one ScriptContext:SetTimeout controls, so it must not react to that call.
                int budgetPerResume = _launch.ResumeBudget.MaxSteps > 0
                    ? (int)Math.Min(_launch.ResumeBudget.MaxSteps, int.MaxValue)
                    : LuaCsCoroutineHandle.DefaultBudgetPerResume;
                int resumeTimeoutMs = _launch.ResumeBudget.TimeoutMs > 0
                    ? _launch.ResumeBudget.TimeoutMs
                    : LuaCsCoroutineHandle.DefaultResumeTimeoutMs;
                // WHY UnlimitedLifetimeSteps: this is a mod's main chunk, which Roblox lets run for as long
                // as every resume yields in time — a lifetime cap here silently killed any mod that did
                // heavy setup before its first yield, or looped on task.wait for longer than ~16 s.
                handle = new LuaCsCoroutineHandle(
                    ownerState,
                    LuaCsScriptExecutionGuard.UnwrapCallable(_launch.Callable),
                    budgetPerResume,
                    resumeTimeoutMs,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps,
                    false,
                    maxAllocatedBytes: maxAllocatedBytes);
            }
            else
            {
                // WHY live: nothing explicit was requested, so this is the composition's configurable
                // default — see LuaCsCoroutineBudgetSettings and CoroutineResumeBudgetDefaults.
                LuaCsCoroutineBudgetSettings liveDefaults = _factory.CoroutineResumeBudgetDefaults;
                handle = new LuaCsCoroutineHandle(
                    ownerState,
                    LuaCsScriptExecutionGuard.UnwrapCallable(_launch.Callable),
                    liveDefaults.BudgetPerResume,
                    liveDefaults.ResumeTimeoutMs,
                    LuaCsCoroutineHandle.UnlimitedLifetimeSteps,
                    false,
                    liveResumeBudget: liveDefaults,
                    maxAllocatedBytes: maxAllocatedBytes);
            }

            return new LuaCsScriptCoroutine(handle);
        }

        /// <summary>
        /// True when the thread's own coroutine handle cut its most recent resume on a per-resume
        /// budget. WHY read the handle's typed trip instead of the error text alone: this is what
        /// makes a runaway classify as <see cref="RbxErrorCode.BudgetExceeded"/> regardless of the
        /// guard message's wording, and what a script's own <c>error("…EXCEEDED…")</c> cannot forge.
        /// </summary>
        private bool LastResumeTrippedBudget()
        {
            return LastResumeTrip() != LuaCsGuardTripKind.None;
        }

        private LuaCsGuardTripKind LastResumeTrip()
        {
            IScriptCoroutine coroutine = _coroutine ?? _runner?.Coroutine;
            return coroutine is LuaCsScriptCoroutine luaCoroutine
                ? luaCoroutine.Handle.LastTrip
                : LuaCsGuardTripKind.None;
        }

        private RbxError ToRbxError(string message, bool budgetTripped)
        {
            string error = string.IsNullOrWhiteSpace(message)
                ? "scheduled Lua thread failed"
                : message;
            // WHY the text match stays as a fallback behind the typed trip: a budget raised outside
            // this thread's own handle reaches here only as text — a memory trip of a guarded call
            // nested inside the handler, for one — and must still read as a budget kill, not a Lua bug.
            bool budgetExceeded = budgetTripped
                                  || error.IndexOf("EXCEEDED_RESUME_STEP_BUDGET",
                                      StringComparison.Ordinal) >= 0
                                  || error.IndexOf("EXCEEDED_MEMORY_BUDGET",
                                      StringComparison.Ordinal) >= 0
                                  || error.IndexOf("resume exceeded",
                                      StringComparison.OrdinalIgnoreCase) >= 0;
            // WHY the §5.2.7 line is kept as it is: a host error the thread did not catch (an RbxError from
            // game:GetService, Instance.new, ...) reaches here as the exact line the script's pcall would
            // have received, already carrying its code, fix and [mod: script: line:] context. Wrapping it
            // as BAD_ARGUMENT gave the fault two prefixes and two fixes and re-coded, say, UNKNOWN_SERVICE
            // as a Lua bug for ModHandlerErrored and auto-repair. Budget kills keep the classification
            // above, and any other text (error('boom'), a Lua runtime error) keeps the wrapping below.
            if (!budgetExceeded && RbxError.TryParse(error, out RbxError raised))
            {
                return raised.ModId == null ? raised.WithContext(OwnerModId, null, 0) : raised;
            }

            // WHY a separate hint for the memory trip: "reduce the work" steers a repair toward the loop,
            // while the fix for EXCEEDED_MEMORY_BUDGET is to keep less data alive inside one resume.
            bool memoryExceeded = budgetExceeded
                                  && (LastResumeTrip() == LuaCsGuardTripKind.Memory
                                      || error.IndexOf("EXCEEDED_MEMORY_BUDGET",
                                          StringComparison.Ordinal) >= 0);
            return new RbxError(
                budgetExceeded ? RbxErrorCode.BudgetExceeded : RbxErrorCode.BadArgument,
                error,
                memoryExceeded
                    ? "keep less memory alive between two yields"
                    : budgetExceeded
                        ? "reduce the work performed between yields"
                        : "fix the Lua error before scheduling the thread again",
                OwnerModId);
        }
    }
}
