using System;
using System.Diagnostics;
using System.Threading;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
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
                    if (allocated > maxAllocatedBytes)
                    {
                        throw new LuaRuntimeException(ctx.State,
                            new InvalidOperationException(
                                $"LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET ({maxAllocatedBytes} bytes)"));
                    }
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            });

            state.SetHook(hook, string.Empty, 1);
            try
            {
                return body(cancellationToken);
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                try
                {
                    state.SetHook(null, string.Empty, 0);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}
