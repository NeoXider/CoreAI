using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
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
    public sealed class LuaMemoryBudgetException : Exception, CoreAI.Scripting.IScriptMemoryBudgetTrip
    {
        /// <param name="message">The message value.</param>
        /// <param name="inner">The underlying VM exception, if any.</param>
        public LuaMemoryBudgetException(string message, Exception inner = null) : base(message, inner)
        {
        }
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
    /// directly in <see cref="LuaCsSecureEnvironment"/>.
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

        // WHY: Guarded execution can re-enter on the SAME LuaState (mods_call: a self-call, or A calls B
        // which calls back into A): a nested finally that simply cleared the hook would disarm the
        // limits the still-running outer call depends on, letting the rest of the outer chunk run
        // unlimited (sandbox escape). Track the installed guard states per state — shared across guard
        // instances via this static table — so a nested guard restores the previous state's hook on exit
        // and the hook is only fully uninstalled when the outermost guarded call unwinds. The stack holds
        // the per-call GuardHook (each with its OWN counters), so restoring the outer hook restores its
        // live step/time/alloc budget, never the inner call's exhausted one.
        private static readonly ConditionalWeakTable<LuaState, Stack<GuardHook>> InstalledHooks = new();

        // WHY: Sampling window — the count-hook fires once per this many VM instructions. Kept small on
        // purpose: it must stay tight enough that an exponential concat bomb cannot overshoot the
        // allocation budget by more than ~one doubling between samples (see the type doc). Each hook fire
        // charges this many instructions to the step budget, so the SAME max-instruction limit holds.
        private const int HookInstructionBatch = 4;

        // WHY: Zero steady-state allocation. ExecuteGuarded ran hundreds of times per second would
        // otherwise allocate a fresh LuaFunction + capture closure + Stopwatch on EVERY call, churning
        // the single-threaded WebGL Boehm GC. Instead each call rents a reusable GuardHook (its
        // LuaFunction/delegate built once) from this thread-local pool and returns it on unwind, so
        // after warm-up the guard allocates nothing per call. Thread-local because rent/return run only
        // on the synchronous calling thread (body() blocks via GetResult), while the hook itself closes
        // directly over its GuardHook and so is thread-agnostic when the async VM migrates pool threads.
        [ThreadStatic] private static Stack<GuardHook> _hookPool;

        private readonly int _timeoutMs;
        private readonly long _maxSteps;
        private readonly long _maxAllocatedBytes;

        /// <param name="timeoutMs">Maximum wall-clock time allowed for one guarded call.</param>
        /// <param name="maxSteps">Maximum Lua-CSharp instruction steps allowed for one guarded call.</param>
        /// <param name="maxAllocatedBytes">
        /// Maximum total GC allocation (bytes) permitted for one guarded call, checked on every
        /// instruction. Defaults to <see cref="DefaultMaxAllocatedBytesBudget"/> (256MB).
        /// <c>&lt;= 0</c> disables the check.
        /// </param>
        // WHY: Roblox parity — a Luau script is only terminated after ~10 s of continuous execution, so the
        // guard's defaults match that (wall-clock is the real limiter; maxSteps is a high secondary net).
        public LuaCsExecutionGuard(
            int timeoutMs = 10_000,
            long maxSteps = 50_000_000,
            long maxAllocatedBytes = DefaultMaxAllocatedBytesBudget)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
            _maxAllocatedBytes = maxAllocatedBytes;
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

            GuardHook hook = BeginGuard(state, out Stack<GuardHook> installed);
            try
            {
                return state.ExecuteAsync(closure, cancellationToken).GetAwaiter().GetResult();
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                EndGuard(state, installed, hook);
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
            GuardHook hook = BeginGuard(state, out Stack<GuardHook> installed);
            try
            {
                return state.CallAsync(new LuaValue(function), args.AsSpan(), cancellationToken)
                    .GetAwaiter().GetResult();
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                EndGuard(state, installed, hook);
            }
        }

        /// <summary>Runs a loaded Lua-CSharp chunk and reads the first returned value as <typeparamref name="T"/>.</summary>
        public T Execute<T>(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            LuaValue[] results = Execute(state, closure, cancellationToken);
            return results.Length == 0 ? default : results[0].Read<T>();
        }

        // WHY: The guard scaffolding is split into Begin/End (not a Func<> body wrapper) so the two call
        // shapes — Execute(closure) and Execute(function, args) — inline their VM call directly. A delegate
        // body would capture state/closure/function/args into a fresh display-class + delegate on EVERY
        // guarded call (timer/event/mods_call at 20 Hz across mods), re-introducing exactly the per-call
        // heap churn the pooled GuardHook removed one frame lower. Begin rents + arms the hook; End restores
        // the enclosing hook (or clears) and returns the hook to the pool.
        private GuardHook BeginGuard(LuaState state, out Stack<GuardHook> installed)
        {
            GuardHook hook = RentHook();
            hook.Reset(_maxSteps, _timeoutMs, _maxAllocatedBytes);

            installed = InstalledHooks.GetOrCreateValue(state);
            installed.Push(hook);
            state.SetHook(hook.Function, string.Empty, HookInstructionBatch);
            return hook;
        }

        private static void EndGuard(LuaState state, Stack<GuardHook> installed, GuardHook hook)
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
            private long _maxAllocatedBytes;
            private long _allocBaseline;

            public GuardHook()
            {
                Function = new LuaFunction("coreai_instruction_guard", Hook);
            }

            /// <summary>Re-arms a fresh per-call budget onto this reusable hook.</summary>
            public void Reset(long maxSteps, int timeoutMs, long maxAllocatedBytes)
            {
                _steps = 0;
                _maxSteps = maxSteps < 1 ? 1 : maxSteps;
                _timeoutMs = timeoutMs < 1 ? 1 : timeoutMs;

                // WHY: Timeout via raw Stopwatch.GetTimestamp() (a long) + a precomputed ticks budget,
                // NOT a Stopwatch instance — the reference-type Stopwatch was a per-call heap allocation
                // on this hot path. Comparing two longs on each hook is allocation-free. The division is
                // done once here, not per hook.
                _startTimestamp = Stopwatch.GetTimestamp();
                _timeoutTicks = (long)_timeoutMs * Stopwatch.Frequency / 1000;

                _maxAllocatedBytes = maxAllocatedBytes;

                // WHY: Allocation accounting uses GC.GetTotalMemory(false) (managed-heap size, no
                // collection): Unity's Mono does NOT implement GC.GetAllocatedBytesForCurrentThread — it
                // returns 0 unconditionally (verified empirically), so a thread-local counter can never
                // fire here. Heap total is process-wide and therefore noisy (other systems allocate
                // concurrently) and can shrink when a collection runs mid-execution, but an allocation
                // bomb overwhelms both effects within a few doublings, which is exactly the pattern this
                // backstop exists for. It is also thread-agnostic, so the async VM migrating between pool
                // threads is harmless.
                _allocBaseline = maxAllocatedBytes > 0 ? GC.GetTotalMemory(false) : 0;
            }

            private System.Threading.Tasks.ValueTask<int> Hook(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                // WHY: The hook fires once per HookInstructionBatch instructions, so charge that many
                // steps per fire — the SAME max-instruction ceiling is enforced, just checked in batches.
                _steps += HookInstructionBatch;
                if (_steps > _maxSteps)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException(
                            $"LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS ({_maxSteps})"));
                }

                if (Stopwatch.GetTimestamp() - _startTimestamp > _timeoutTicks)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new TimeoutException($"Lua exceeded {_timeoutMs} ms."));
                }

                // WHY: Allocation-bomb backstop: string.rep/string.format/table.concat are capped at their
                // library call sites, but plain concatenation (s = s .. s) has no such call site to
                // intercept — it is ordinary VM opcodes. Checking total allocations between instruction
                // batches is the only place this hook can catch that pattern; the batch is kept small so
                // the exponential growth cannot overshoot the budget by more than ~one doubling.
                if (_maxAllocatedBytes > 0)
                {
                    // WHY: Trip on the CHEAP process-heap reading, with NO forced-GC "confirmation". A forced
                    // GC.GetTotalMemory(true) measured against a garbage-inclusive baseline UNDER-counts (the
                    // baseline's own collectible garbage is freed by the forced GC), so the trip fired late and
                    // a doubling bomb reached true OutOfMemory before it was cut. The cheap reading grows
                    // monotonically with a retained buffer, so it trips promptly. NOTE this is a PER-CALL,
                    // first-growth backstop, not a cross-call cumulative limiter: GC.GetTotalMemory reports the
                    // COMMITTED heap high-water mark, so the FIRST oversized call grows the heap and trips, but
                    // later calls reuse that committed space and their per-call delta no longer crosses the
                    // budget (empirically a mod bombing every call trips ~once, even with a forced GC between
                    // calls). Downstream (LuaCsModRuntime) charges the trip to the ordinary consecutive-error
                    // streak, which a success resets — so the lone trip is forgiven, and a mod that keeps
                    // allocating within the committed envelope is bounded by the step/time budgets instead.
                    // Classify by TYPE (LuaMemoryBudgetException as the CLR cause) — unforgeable and pcall-safe —
                    // for the trip's log label, while the outer type stays LuaRuntimeException for the contract.
                    long allocated = GC.GetTotalMemory(false) - _allocBaseline;
                    if (allocated > _maxAllocatedBytes)
                    {
                        throw new LuaRuntimeException(ctx.State,
                            new LuaMemoryBudgetException(
                                $"LuaCsSecureEnvironment: {MemoryBudgetTripMarker} ({_maxAllocatedBytes} bytes)"));
                    }
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            }
        }
    }
}
