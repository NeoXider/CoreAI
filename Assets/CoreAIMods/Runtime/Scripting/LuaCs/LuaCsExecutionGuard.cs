using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Scripting;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Raised (as the CLR cause) by <see cref="LuaCsExecutionGuard"/> when a guarded run exceeds the
    /// process-heap allocation budget. A dedicated type — never a message substring — so a mod's own
    /// <c>error("…EXCEEDED_MEMORY_BUDGET…")</c> text cannot masquerade as a memory-budget trip in logs or
    /// telemetry. Only this guard can construct it.
    /// </summary>
    public sealed class LuaMemoryBudgetException : Exception, Scripting.IScriptMemoryBudgetTrip
    {
        /// <param name="message">The message value.</param>
        /// <param name="inner">The underlying VM exception, if any.</param>
        public LuaMemoryBudgetException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Classifies how one guarded execution ended, so a measurement host can tell a normal finish
    /// from a guard trip without parsing exception messages.
    /// </summary>
    public enum LuaCsGuardTripKind
    {
        /// <summary>The execution finished (or threw) without any guard budget tripping.</summary>
        None,

        /// <summary>The instruction-step budget tripped.</summary>
        Steps,

        /// <summary>The wall-clock timeout tripped.</summary>
        Timeout,

        /// <summary>The process-heap allocation budget tripped.</summary>
        Memory,
    }

    /// <summary>
    /// The cost of one completed guarded execution, delivered to
    /// <see cref="ILuaCsGuardObserver"/> exactly once per <see cref="LuaCsExecutionGuard"/> call.
    /// </summary>
    public readonly struct LuaCsGuardExecutionRecord
    {
        /// <summary>Guarded instructions charged to the step budget by this execution.</summary>
        public long Steps { get; }

        /// <summary>Wall-clock <see cref="Stopwatch"/> ticks spent inside this execution.</summary>
        public long ElapsedTicks { get; }

        /// <summary>False when the execution ended by throwing (guard trip or script error alike).</summary>
        public bool Completed { get; }

        /// <summary>Which guard budget tripped, or <see cref="LuaCsGuardTripKind.None"/>.</summary>
        public LuaCsGuardTripKind TrippedBudget { get; }

        /// <param name="steps">Guarded instructions charged to the step budget.</param>
        /// <param name="elapsedTicks">Wall-clock <see cref="Stopwatch"/> ticks for the execution.</param>
        /// <param name="completed">False when the execution ended by throwing.</param>
        /// <param name="trippedBudget">Which guard budget tripped, if any.</param>
        public LuaCsGuardExecutionRecord(long steps, long elapsedTicks, bool completed, LuaCsGuardTripKind trippedBudget)
        {
            Steps = steps;
            ElapsedTicks = elapsedTicks;
            Completed = completed;
            TrippedBudget = trippedBudget;
        }
    }

    /// <summary>
    /// Receives the cost of one completed guarded execution. The observation port for frame-gate
    /// measurement: a host supplies an instance through production composition (guard constructor
    /// or property) instead of reading the pooled hook's counters by reflection.
    /// </summary>
    public interface ILuaCsGuardObserver
    {
        /// <param name="record">The cost of the execution that just completed.</param>
        void OnGuardedExecutionCompleted(in LuaCsGuardExecutionRecord record);
    }

    /// <summary>
    /// Runs Lua-CSharp chunks/functions with timeout, instruction-step, and total-allocation limits.
    /// <para>
    /// All three limits are enforced from a single count-hook installed via <see cref="LuaState.SetHook"/>.
    /// The hook fires every <see cref="HookInstructionBatch"/> instructions (not every instruction): a
    /// count hook pays a wall-clock read and a <see cref="GC.GetTotalMemory(bool)"/> heap read on each
    /// fire, and at 20 Hz timers across several mods that is hundreds of guarded calls per second on a
    /// single-threaded WebGL Boehm GC, so sampling divides that fixed per-instruction cost by the batch.
    /// </para>
    /// <para>
    /// The batch is deliberately SMALL. Step and time are linear budgets, so a wider window would only
    /// delay their trip by at most one batch — negligible against 200k steps / 2 s. The allocation
    /// budget is different: concatenation-doubling (<c>s = s .. s</c>) grows exponentially, so each
    /// batch of unchecked instructions multiplies the heap. A wide window (e.g. 128–256) both misses a
    /// short bomb entirely — a doubling loop is only a few instructions per iteration, so a 128-wide
    /// window can sample zero times before the loop finishes — and, on an unbounded loop, lets the heap
    /// overshoot the budget by ~2^(window/iterationSize) before the first sample, i.e. straight to
    /// out-of-memory. A batch of <see cref="HookInstructionBatch"/> keeps at most ~one doubling between
    /// samples, so the peak heap stays within a small constant factor of the budget, matching the
    /// per-instruction guarantee this backstop replaced. This is the only defense against allocation
    /// bombs built from plain string concatenation: that is ordinary VM opcodes with no library call
    /// site to cap, unlike <c>string.rep</c>/<c>string.format</c>/<c>table.concat</c>, which are capped
    /// directly in <see cref="LuaCsSecureEnvironment"/>. The allocation rule itself — a sampled suspicion
    /// that only becomes a trip once a forced collection confirms the growth is LIVE — lives in
    /// <see cref="LuaCsAllocationBudget"/>, shared with the per-resume coroutine hook.
    /// </para>
    /// <para>
    /// <see cref="ExecuteAsync"/> additionally lets the hook release the host frame every
    /// <see cref="FrameYieldSliceMs"/> through an <see cref="IScriptFrameYielder"/>, so a chunk that
    /// legitimately runs for seconds does not freeze a single-threaded player. The synchronous
    /// <c>Execute</c> overloads never arm a yielder: their caller is blocked in
    /// <c>GetAwaiter().GetResult()</c>, so awaiting a frame from inside the hook could not complete.
    /// </para>
    /// </summary>
    public sealed class LuaCsExecutionGuard
    {
        /// <summary>Default per-execution GC allocation budget enforced between VM instructions.</summary>
        public const long DefaultMaxAllocatedBytesBudget = 256 * 1024 * 1024;

        /// <summary>
        /// Human-readable substring stamped into a memory-budget trip's message. NOTE: never classify a
        /// trip by matching this literal — a mod can put it into its own <c>error("…")</c> text. Use
        /// <see cref="IsMemoryBudgetTrip"/>, which tests the dedicated <see cref="LuaMemoryBudgetException"/>
        /// TYPE that only this guard can throw.
        /// </summary>
        public const string MemoryBudgetTripMarker = "EXCEEDED_MEMORY_BUDGET";

        /// <summary>
        /// True when <paramref name="ex"/> (or any exception it wraps) is a process-heap memory-budget trip
        /// raised by this guard. Detection is by TYPE (<see cref="LuaMemoryBudgetException"/>), NOT by message
        /// text, so a mod cannot forge the classification via its own error message. Used only for the trip's
        /// log label: the runtime charges a memory trip to the same consecutive-error streak as any other
        /// failure, so classification does not change whether a mod is unloaded (a forged marker and a real trip
        /// both charge alike). The step and time budgets are real per-call guards and are NOT reported here.
        /// </summary>
        public static bool IsMemoryBudgetTrip(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is LuaMemoryBudgetException)
                {
                    return true;
                }
            }

            return false;
        }

        // WHY: Guarded execution can re-enter on the SAME LuaState (mods_call self-call, or A calls B
        // which calls back into A). A nested finally that just cleared the hook would disarm the
        // still-running outer call's limits (sandbox escape), so per-state guard stacks let a nested
        // guard restore the outer call's own hook/budget on exit instead.
        private static readonly ConditionalWeakTable<LuaState, Stack<GuardHook>> InstalledHooks = new();

        // WHY: Sampling window — the count-hook fires once per this many VM instructions. Kept small on
        // purpose: it must stay tight enough that an exponential concat bomb cannot overshoot the
        // allocation budget by more than ~one doubling between samples (see the type doc). Each hook fire
        // charges this many instructions to the step budget, so the SAME max-instruction limit holds.
        private const int HookInstructionBatch = 4;

        /// <summary>
        /// Wall-clock slice a chunk may hold the host frame for before the hook yields it (async path
        /// only). Sized as a fraction of a 60 Hz frame: short enough that a runaway never stutters the
        /// host, long enough that an ordinary chunk — which finishes in far less than this — never pays
        /// for a single yield.
        /// </summary>
        public const int FrameYieldSliceMs = 6;

        // WHY: Pooled to keep steady-state allocation at zero — hundreds of guarded calls per second would
        // otherwise build a fresh LuaFunction/closure/Stopwatch each time, churning the single-threaded
        // WebGL Boehm GC. Thread-local because rent/return run on the synchronous calling thread, while
        // the hook itself is thread-agnostic when the async VM migrates pool threads.
        [ThreadStatic]
        private static Stack<GuardHook> _hookPool;

        private readonly int _timeoutMs;
        private readonly long _maxSteps;
        private readonly long _maxAllocatedBytes;
        private readonly IRbxRuntimeObservabilitySink _observability;
        private ILuaCsGuardObserver _guardObserver;

        /// <summary>
        /// Per-execution observer receiving one <see cref="LuaCsGuardExecutionRecord"/> per call.
        /// Null by default (no cost); set before use — a host supplies it through production
        /// composition rather than reading pooled hook state by reflection.
        /// </summary>
        public ILuaCsGuardObserver GuardObserver
        {
            get => _guardObserver;
            set => _guardObserver = value;
        }

        /// <param name="timeoutMs">Maximum wall-clock time allowed for one guarded call.</param>
        /// <param name="maxSteps">Maximum Lua-CSharp instruction steps allowed for one guarded call.</param>
        /// <param name="maxAllocatedBytes">
        /// Maximum total GC allocation (bytes) permitted for one guarded call, checked on every
        /// instruction. Defaults to <see cref="DefaultMaxAllocatedBytesBudget"/> (256MB).
        /// <c>&lt;= 0</c> disables the check.
        /// </param>
        /// <param name="guardObserver">
        /// Optional per-execution observer. Null by default: with no observer the behaviour is identical
        /// and the only added cost is one null check per execution.
        /// </param>
        // WHY: Roblox parity — a Luau script is only terminated after ~10 s of continuous execution, so the
        // guard's defaults match that (wall-clock is the real limiter; maxSteps is a high secondary net).
        public LuaCsExecutionGuard(
            int timeoutMs = 10_000,
            long maxSteps = 50_000_000,
            long maxAllocatedBytes = DefaultMaxAllocatedBytesBudget,
            IRbxRuntimeObservabilitySink observability = null,
            ILuaCsGuardObserver guardObserver = null)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
            _maxAllocatedBytes = maxAllocatedBytes;
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            _guardObserver = guardObserver;
        }

        /// <summary>Runs a loaded Lua-CSharp chunk synchronously under the guard.</summary>
        public LuaValue[] Execute(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (closure == null)
            {
                throw new ArgumentNullException(nameof(closure));
            }

            // WHY: null yielder, always. A synchronous caller is blocked in GetAwaiter().GetResult()
            // below, so a hook that awaited a frame yield would wait for a loop iteration that cannot
            // run until this very call returns — a guaranteed deadlock on the single WebGL thread. The
            // yielder is a parameter of BeginGuard rather than a field precisely so that no synchronous
            // entry point can arm one.
            GuardHook hook = BeginGuard(state, null, out Stack<GuardHook> installed);
            bool completed = false;
            try
            {
                LuaValue[] results = state.ExecuteAsync(closure, cancellationToken).GetAwaiter().GetResult();
                completed = true;
                return results;
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                EndGuard(state, installed, hook, completed);
            }
        }

        /// <summary>
        /// Runs a loaded Lua-CSharp chunk asynchronously under the guard, releasing the host frame every
        /// <see cref="FrameYieldSliceMs"/> when <paramref name="frameYielder"/> is supplied.
        /// <para>
        /// This is the one-shot chunk entry (<c>execute_lua</c>). The synchronous <see cref="Execute(LuaState,
        /// LuaClosure, CancellationToken)"/> stays for the short mod-event/handler call sites, which run
        /// at 20 Hz inside the host loop and have nothing to yield to.
        /// </para>
        /// </summary>
        /// <param name="state">The sandboxed state to run on.</param>
        /// <param name="closure">The loaded chunk.</param>
        /// <param name="frameYielder">Host frame port; null runs without yielding, exactly as before.</param>
        /// <param name="cancellationToken">Cancels the run; the guard hook is restored either way.</param>
        public async Task<LuaValue[]> ExecuteAsync(
            LuaState state,
            LuaClosure closure,
            IScriptFrameYielder frameYielder = null,
            CancellationToken cancellationToken = default)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (closure == null)
            {
                throw new ArgumentNullException(nameof(closure));
            }

            GuardHook hook = BeginGuard(state, frameYielder, out Stack<GuardHook> installed);
            bool completed = false;
            try
            {
                LuaValue[] results = await state.ExecuteAsync(closure, cancellationToken);
                completed = true;
                return results;
            }
            finally
            {
                // WHY: the same EndGuard as the synchronous path, so a cancelled or timed-out async
                // chunk re-arms an enclosing guarded call's hook instead of leaving the state unguarded.
                EndGuard(state, installed, hook, completed);
            }
        }

        /// <summary>Calls a Lua-CSharp function synchronously under the guard.</summary>
        public LuaValue[] Execute(
            LuaState state,
            LuaFunction function,
            CancellationToken cancellationToken = default,
            params LuaValue[] args)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            args ??= Array.Empty<LuaValue>();
            // WHY: null yielder — see the note on the synchronous chunk overload above.
            GuardHook hook = BeginGuard(state, null, out Stack<GuardHook> installed);
            bool completed = false;
            try
            {
                LuaValue[] results = state.CallAsync(new LuaValue(function), args.AsSpan(), cancellationToken)
                    .GetAwaiter().GetResult();
                completed = true;
                return results;
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                EndGuard(state, installed, hook, completed);
            }
        }

        /// <summary>Runs a loaded Lua-CSharp chunk and reads the first returned value as <typeparamref name="T"/>.</summary>
        public T Execute<T>(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            LuaValue[] results = Execute(state, closure, cancellationToken);
            return results.Length == 0 ? default : results[0].Read<T>();
        }

        // WHY: Split into Begin/End rather than a Func<> body wrapper — a delegate body would capture
        // state/closure/function/args into a fresh display-class on EVERY guarded call (20 Hz timers/
        // events across mods), reintroducing the per-call heap churn the pooled GuardHook removes.
        private GuardHook BeginGuard(LuaState state, IScriptFrameYielder frameYielder,
            out Stack<GuardHook> installed)
        {
            GuardHook hook = RentHook();
            hook.Reset(_maxSteps, _timeoutMs, _maxAllocatedBytes, frameYielder);

            installed = InstalledHooks.GetOrCreateValue(state);
            installed.Push(hook);
            state.SetHook(hook.Function, string.Empty, HookInstructionBatch);
            return hook;
        }

        private void EndGuard(LuaState state, Stack<GuardHook> installed, GuardHook hook, bool completed)
        {
            installed.Pop();
            try
            {
                if (installed.Count > 0)
                {
                    // WHY: An enclosing guarded call is still running on this state: re-arm ITS hook
                    // instead of clearing, so the outer step/time/alloc limits stay live.
                    state.SetHook(installed.Peek().Function, string.Empty, HookInstructionBatch);
                }
                else
                {
                    state.SetHook(null, string.Empty, 0);
                }
            }
            catch
            {
                /* ignore */
            }

            if (_observability != null && hook.Steps > 0)
            {
                try
                {
                    _observability.RecordGuardedInstructionSteps(hook.Steps);
                }
                catch
                {
                }
            }

            // WHY: Read Steps/ElapsedTicks/Trip BEFORE ReturnHook — the pooled hook is zeroed by
            // Reset on its next rent, so reading after the return would report zero.
            ILuaCsGuardObserver observer = _guardObserver;
            if (observer != null)
            {
                LuaCsGuardExecutionRecord record = new(
                    hook.Steps,
                    hook.ElapsedTicks,
                    completed,
                    completed ? LuaCsGuardTripKind.None : hook.Trip);
                try
                {
                    observer.OnGuardedExecutionCompleted(in record);
                }
                catch
                {
                    // WHY: swallowed deliberately and without logging. This runs in the finally of the
                    // hottest path in the project, once per guarded execution; a measurement sink that
                    // throws must never turn into a mod failure, and logging here would let a broken
                    // observer flood the log at execution frequency. An observer that needs to report
                    // its own faults owns that channel itself.
                }
            }

            // WHY: the pool is process-lived, so a returned hook must not keep the caller's frame port
            // (and whatever it closes over) reachable until the hook happens to be rented again.
            hook.ClearFrameYielder();
            ReturnHook(hook);
        }

        private static GuardHook RentHook()
        {
            Stack<GuardHook> pool = _hookPool;
            return pool != null && pool.Count > 0 ? pool.Pop() : new GuardHook();
        }

        private static void ReturnHook(GuardHook hook)
        {
            (_hookPool ??= new Stack<GuardHook>()).Push(hook);
        }

        /// <summary>
        /// A poolable, reusable instruction hook: its <see cref="LuaFunction"/> (and the delegate it
        /// wraps) is built ONCE and re-armed by <see cref="Reset"/> at the top of every guarded call, so
        /// steady-state execution allocates nothing. The mutable budget state lives in fields rather than
        /// a per-call capture closure, and each in-flight (re-entrant) call rents a distinct instance, so
        /// a nested call never clobbers the outer call's counters.
        /// </summary>
        private sealed class GuardHook
        {
            /// <summary>The reusable Lua-CSharp hook function; its identity is stable across calls.</summary>
            public readonly LuaFunction Function;

            private long _steps;
            private long _maxSteps;
            private long _startTimestamp;
            private long _timeoutTicks;
            private int _timeoutMs;
            private LuaCsAllocationBudget _allocation;
            private IScriptFrameYielder _frameYielder;
            private long _frameYieldSliceTicks;
            private long _lastYieldTimestamp;
            private long _yieldedTicks;
            private LuaCsGuardTripKind _trip;

            /// <summary>Instruction steps accumulated by the current guarded execution.</summary>
            public long Steps => _steps;

            /// <summary>Wall-clock <see cref="Stopwatch"/> ticks elapsed since <see cref="Reset"/>.</summary>
            public long ElapsedTicks => Stopwatch.GetTimestamp() - _startTimestamp;

            /// <summary>
            /// Which guard budget tripped during the current execution, or
            /// <see cref="LuaCsGuardTripKind.None"/>. Recorded by the hook itself at throw time so the
            /// reporter never classifies a mod's own <c>error()</c> text as a budget trip.
            /// </summary>
            public LuaCsGuardTripKind Trip => _trip;

            public GuardHook()
            {
                Function = new LuaFunction("coreai_instruction_guard", Hook);
            }

            /// <summary>Re-arms a fresh per-call budget onto this reusable hook.</summary>
            public void Reset(long maxSteps, int timeoutMs, long maxAllocatedBytes,
                IScriptFrameYielder frameYielder)
            {
                _steps = 0;
                _trip = LuaCsGuardTripKind.None;
                _maxSteps = maxSteps < 1 ? 1 : maxSteps;
                _timeoutMs = timeoutMs < 1 ? 1 : timeoutMs;

                // WHY: Timeout via raw Stopwatch.GetTimestamp() (a long) + a precomputed ticks budget,
                // NOT a Stopwatch instance — the reference-type Stopwatch was a per-call heap allocation
                // on this hot path. Comparing two longs on each hook is allocation-free. The division is
                // done once here, not per hook.
                _startTimestamp = Stopwatch.GetTimestamp();
                _timeoutTicks = (long)_timeoutMs * Stopwatch.Frequency / 1000;

                _allocation.Reset(maxAllocatedBytes);

                _frameYielder = frameYielder;
                _frameYieldSliceTicks = (long)FrameYieldSliceMs * Stopwatch.Frequency / 1000;
                _lastYieldTimestamp = _startTimestamp;
                _yieldedTicks = 0;
            }

            /// <summary>Drops the frame port before this hook goes back to the pool.</summary>
            public void ClearFrameYielder()
            {
                _frameYielder = null;
            }

            private System.Threading.Tasks.ValueTask<int> Hook(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                // WHY: The hook fires once per HookInstructionBatch instructions, so charge that many
                // steps per fire — the SAME max-instruction ceiling is enforced, just checked in batches.
                _steps += HookInstructionBatch;
                if (_steps > _maxSteps)
                {
                    _trip = LuaCsGuardTripKind.Steps;
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException(
                            $"LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS ({_maxSteps})"));
                }

                // WHY: The clock is read on EVERY fire, deliberately — sampling every Nth fire saved only
                // ~6% (measured) and defeats the timeout in its key case: the count hook does not fire
                // during a host call, so a handler of mostly expensive bindings (Instance.new, property
                // writes) can blow a per-frame budget while hitting the sampling threshold zero times.
                //
                // WHY: _yieldedTicks is subtracted so the budget measures EXECUTED time, matching what it
                // promises ("~10 s of continuous execution"). Without it, a legitimate long chunk on the
                // yielding path would spend most of its wall clock waiting for frames and be cut for work
                // it never did.
                long now = Stopwatch.GetTimestamp();
                if (now - _startTimestamp - _yieldedTicks > _timeoutTicks)
                {
                    _trip = LuaCsGuardTripKind.Timeout;
                    throw new LuaRuntimeException(ctx.State,
                        new TimeoutException($"Lua exceeded {_timeoutMs} ms."));
                }

                // WHY: Backstop for plain concatenation (s = s .. s), which unlike string.rep/format/
                // table.concat has no library call site to cap — it is ordinary VM opcodes. Checking
                // allocations between instruction batches is the only place this hook can catch that.
                // The sampled/confirmed rule and why the trip may NOT be decided by the cheap sampled
                // reading alone live in LuaCsAllocationBudget. Classified by TYPE
                // (LuaMemoryBudgetException), not message text, so a mod cannot forge the trip.
                if (_allocation.IsExceeded())
                {
                    _trip = LuaCsGuardTripKind.Memory;
                    throw new LuaRuntimeException(ctx.State,
                        new LuaMemoryBudgetException(
                            $"LuaCsSecureEnvironment: {MemoryBudgetTripMarker} ({_allocation.BudgetBytes} bytes)"));
                }

                if (_frameYielder != null && now - _lastYieldTimestamp > _frameYieldSliceTicks)
                {
                    return YieldFrameAsync(ctx, ct);
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            }

            // WHY: kept out of Hook so the fast path stays a plain (non-async) method returning a
            // completed ValueTask. An async Hook would build a state machine on EVERY fire — hundreds of
            // guarded calls per second across mods — while this one is entered only on the ~6 ms slice.
            private async System.Threading.Tasks.ValueTask<int> YieldFrameAsync(
                LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                long yieldStart = Stopwatch.GetTimestamp();
                await _frameYielder.YieldFrameAsync(ct);
                long resumed = Stopwatch.GetTimestamp();
                _yieldedTicks += resumed - yieldStart;
                _lastYieldTimestamp = resumed;
                return ctx.Return();
            }
        }
    }
}
