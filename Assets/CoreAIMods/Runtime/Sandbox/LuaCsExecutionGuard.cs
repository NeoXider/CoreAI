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
    /// <c>error("…EXCEEDED_MEMORY_BUDGET…")</c> text cannot masquerade as a memory-budget trip and evade
    /// the consecutive-error auto-unload guard. Only this guard can construct it.
    /// </summary>
    public sealed class LuaMemoryBudgetException : Exception
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
    /// The allocation budget is checked on every instruction inside the same hook used for the step
    /// count and timeout вЂ” <see cref="GC.GetTotalMemory(bool)"/> is a cheap heap-size counter
    /// read, not a GC pass, so it costs about as much as the timeout's <c>Stopwatch</c> read already
    /// performed on the same hot path. A coarser sampling interval was considered and rejected:
    /// concatenation-doubling (<c>s = s .. s</c>) grows exponentially, so a handful of loop iterations
    /// can jump from megabytes to gigabytes, and any interval wide enough to matter for performance is
    /// also wide enough to miss the attack before the next sample point. This is the only defense
    /// against allocation bombs built from plain string concatenation: that is ordinary VM opcodes with
    /// no library call site to cap, unlike <c>string.rep</c>/<c>string.format</c>/<c>table.concat</c>,
    /// which are capped directly in <see cref="LuaCsSecureEnvironment"/>.
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
        /// text, so a mod cannot forge the classification via its own error message and thereby dodge the
        /// consecutive-error auto-unload guard. Such trips still cut the offending run, but a caller may
        /// choose not to charge a single trip to the general error streak, since the process-wide heap can be
        /// pushed over budget by allocations the mod never made. The step and time budgets are real per-call
        /// guards and are NOT reported here.
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

        private static bool ExceptionChainContains(Exception ex, Exception target)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (ReferenceEquals(e, target))
                {
                    return true;
                }
            }

            return false;
        }

        // WHY: Guarded execution can re-enter on the SAME LuaState (mods_call: a self-call, or A calls B
        // which calls back into A): a nested finally that simply cleared the hook would disarm the
        // limits the still-running outer call depends on, letting the rest of the outer chunk run
        // unlimited (sandbox escape). Track the installed hooks per state — shared across guard
        // instances via this static table — so a nested guard restores the previous hook on exit and
        // the hook is only fully uninstalled when the outermost guarded call unwinds.
        private static readonly ConditionalWeakTable<LuaState, Stack<LuaFunction>> InstalledHooks = new();

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
        public LuaCsExecutionGuard(
            int timeoutMs = 2000,
            long maxSteps = 200_000,
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

            return ExecuteGuarded(state, ct => state.ExecuteAsync(closure, ct).GetAwaiter().GetResult(),
                cancellationToken);
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
            return ExecuteGuarded(state,
                ct => state.CallAsync(new LuaValue(function), args.AsSpan(), ct).GetAwaiter().GetResult(),
                cancellationToken);
        }

        /// <summary>Runs a loaded Lua-CSharp chunk and reads the first returned value as <typeparamref name="T"/>.</summary>
        public T Execute<T>(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            LuaValue[] results = Execute(state, closure, cancellationToken);
            return results.Length == 0 ? default : results[0].Read<T>();
        }

        private LuaValue[] ExecuteGuarded(
            LuaState state,
            Func<CancellationToken, LuaValue[]> body,
            CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            long steps = 0;
            long maxSteps = _maxSteps < 1 ? 1 : _maxSteps;
            int timeoutMs = _timeoutMs < 1 ? 1 : _timeoutMs;
            long maxAllocatedBytes = _maxAllocatedBytes;

            // WHY: Allocation accounting uses GC.GetTotalMemory(false) (managed-heap size, no collection):
            // Unity's Mono does NOT implement GC.GetAllocatedBytesForCurrentThread вЂ” it returns 0
            // unconditionally (verified empirically), so a thread-local counter can never fire here.
            // Heap total is process-wide and therefore noisy (other systems allocate concurrently) and
            // can shrink when a collection runs mid-execution, but an allocation bomb overwhelms both
            // effects within a few doublings, which is exactly the pattern this backstop exists for.
            // It is also thread-agnostic, so the async VM migrating between pool threads is harmless.
            long allocBaseline = maxAllocatedBytes > 0 ? GC.GetTotalMemory(false) : 0;

            // WHY: Captured locals, per THIS ExecuteGuarded invocation (re-entrancy-safe — a nested guarded
            // call gets its own closure). `memoryTrip` holds the EXACT exception this run's hook raised for a
            // budget trip; the catch converts to the dedicated type only when that exact instance surfaces
            // (matched by reference), so a trip swallowed by a mod's pcall cannot launder a later error.
            // `allocAtLastForcedCheck` debounces the expensive forced-GC confirmation so a persistently-high
            // process heap cannot force a full GC on every instruction.
            LuaRuntimeException memoryTrip = null;
            long allocAtLastForcedCheck = long.MinValue;
            long forcedCheckStep = maxAllocatedBytes > 0 ? Math.Max(1, maxAllocatedBytes / 8) : long.MaxValue;

            LuaFunction hook = new("coreai_instruction_guard", (ctx, ct) =>
            {
                steps++;
                if (steps > maxSteps)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException(
                            $"LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS ({maxSteps})"));
                }

                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new TimeoutException($"Lua exceeded {timeoutMs} ms."));
                }

                // WHY: Allocation-bomb backstop: string.rep/string.format/table.concat are capped at their
                // library call sites, but plain concatenation (s = s .. s) has no such call site to
                // intercept вЂ” it is ordinary VM opcodes. Checking total thread allocations between
                // instructions is the only place this hook can catch that pattern.
                if (maxAllocatedBytes > 0)
                {
                    long allocated = GC.GetTotalMemory(false) - allocBaseline;
                    if (allocated > maxAllocatedBytes &&
                        allocated >= allocAtLastForcedCheck + forcedCheckStep)
                    {
                        // WHY: GC.GetTotalMemory(false) is process-wide and counts collectible garbage —
                        // both this run's transient allocations and other systems' — so a raw trip has
                        // false positives that would needlessly cut a blameless run. Confirm with a forced
                        // collection: only LIVE managed memory that survives a full GC counts. A real
                        // allocation bomb (s = s .. s / table.concat) RETAINS its doubled buffer, so it
                        // still trips; unrelated transient growth is reclaimed and does not.
                        // The forced collection is DEBOUNCED (only re-run once the cheap reading climbs a
                        // further step) so a process heap that legitimately sits above budget for a while
                        // cannot induce a full GC on every instruction; a doubling bomb climbs past the step
                        // within one iteration and still trips promptly.
                        // WHY: Cap the watermark at the budget (not the inflated cheap reading). The cheap
                        // reading counts dead/collectible garbage, so setting the watermark to it would let a
                        // transient garbage spike ratchet the next-confirm threshold arbitrarily high and let a
                        // mod retain LIVE memory above budget without re-confirmation (fail-open). Capping keeps
                        // the confirmation window to at most budget + one step regardless of transient noise.
                        allocAtLastForcedCheck = maxAllocatedBytes;
                        long live = GC.GetTotalMemory(true) - allocBaseline;
                        if (live > maxAllocatedBytes)
                        {
                            // WHY: Record the exact trip exception so the catch can match it by reference and
                            // convert to the dedicated LuaMemoryBudgetException type. A trip swallowed by a
                            // mod's pcall/coroutine is a different instance from any later error(), so it can
                            // never launder a subsequent unrelated failure into a "memory trip".
                            memoryTrip = new LuaRuntimeException(ctx.State,
                                new LuaMemoryBudgetException(
                                    $"LuaCsSecureEnvironment: {MemoryBudgetTripMarker} ({maxAllocatedBytes} bytes)"));
                            throw memoryTrip;
                        }
                    }
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            });

            Stack<LuaFunction> installed = InstalledHooks.GetOrCreateValue(state);
            installed.Push(hook);
            try
            {
                state.SetHook(hook, string.Empty, 1);
                return body(cancellationToken);
            }
            catch (LuaRuntimeException ex) when (memoryTrip != null && ExceptionChainContains(ex, memoryTrip))
            {
                // WHY: This run's own budget trip surfaced unhandled (matched by reference through any VM
                // re-wrap). Re-raise it as the dedicated, unforgeable type so callers classify it reliably by
                // TYPE, with the marker in the message for observability. Only the exact trip instance reaches
                // here, so a mod cannot forge it and a pcall-swallowed trip cannot launder a later error.
                throw new LuaMemoryBudgetException(
                    $"LuaCsSecureEnvironment: {MemoryBudgetTripMarker} ({maxAllocatedBytes} bytes)", ex);
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                installed.Pop();
                try
                {
                    if (installed.Count > 0)
                    {
                        // WHY: An enclosing guarded call is still running on this state: re-arm ITS hook
                        // instead of clearing, so the outer step/time/alloc limits stay live.
                        state.SetHook(installed.Peek(), string.Empty, 1);
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
            }
        }
    }
}
