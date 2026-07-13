using System;
using System.Text;
using System.Threading;
using Lua;
using Lua.Runtime;
using Lua.Standard;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Creates Lua-CSharp runtimes with a restricted global surface and execution guards.
    /// </summary>
    public sealed class LuaCsSecureEnvironment
    {
        /// <summary>Maximum instruction budget for one-shot Lua script execution.</summary>
        public const int OneShotHardLimitSteps = 500_000;

        /// <summary>Maximum length of a string that <c>string.rep</c> may build.</summary>
        public const int MaxStringRepLength = 1_000_000;

        /// <summary>Maximum width/precision a single <c>string.format</c> conversion specifier may request.</summary>
        public const int MaxStringFormatLength = MaxStringRepLength;

        /// <summary>
        /// Maximum length of the string that <c>table.concat</c> may build, for the same reason and
        /// with the same value as <see cref="MaxStringRepLength"/>: a single VM instruction can join an
        /// entire table's elements into one huge string.
        /// </summary>
        public const int MaxTableConcatLength = MaxStringRepLength;

        /// <summary>
        /// Default per-execution GC allocation budget (bytes). Unlike the caps above, this is enforced
        /// by sampling <see cref="System.GC.GetTotalMemory(bool)"/> between VM instructions (Mono does not implement the thread-local allocation counter)
        /// (see <see cref="LuaCsExecutionGuard"/>) rather than at a specific library call, because plain
        /// string concatenation (<c>s = s .. s</c>) has no library call site to intercept. It is the
        /// last line of defense against allocation bombs built purely from concatenation opcodes.
        /// </summary>
        public const long MaxAllocatedBytesBudget = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;

        /// <summary>Creates a secured Lua-CSharp state and registers the allowed Lua APIs.</summary>
        public LuaState Create(LuaCsApiRegistry registry = null)
        {
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            state.OpenMathLibrary();
            state.OpenCoroutineLibrary();
            state.OpenBitwiseLibrary();

            StripRiskyGlobals(state);
            HardenCoroutineLibrary(state);
            registry?.ApplyToEnvironment(state);
            return state;
        }

        /// <summary>Per-resume instruction-step budget for a mod-created raw Lua coroutine.</summary>
        public const long CoroutineResumeStepBudget = 500_000;

        /// <summary>Per-resume wall-clock budget (ms) for a mod-created raw Lua coroutine.</summary>
        public const int CoroutineResumeTimeoutMs = 1000;

        // WHY: The native coroutine library runs a coroutine body on a CHILD LuaState that does NOT inherit
        // LuaCsExecutionGuard's step/time/alloc hook, so an unbounded loop inside a resumed coroutine
        // (e.g. `local co=coroutine.create(function() while true do end end); coroutine.resume(co)`) would
        // escape every budget and hang the game. Wrapping `coroutine.resume` arms a per-resume step+time+alloc
        // guard hook on the coroutine's own child state (mirroring LuaCsCoroutineHandle.Resume) and clears it
        // afterwards, so the coroutine body is cut like any other guarded execution.
        //
        // coroutine.wrap is REMOVED (set nil), not left native: its returned resumer drives a hidden child
        // thread through the library's OWN internal resume, bypassing the wrapped coroutine.resume below, so a
        // wrap-created body would run on a child state with NO guard hook — an unbounded-loop / allocation-bomb
        // host-hang vector (`coroutine.wrap(function() while true do end end)()` hangs the game thread forever,
        // no cut, no unload). It cannot be safely re-armed on this Lua-CSharp build: a C# reimplementation's
        // returned Lua function did not round-trip as callable, and a Lua redefinition needs a sync-over-async
        // Load/Execute in Create() that DEADLOCKS on a SynchronizationContext-bearing thread (the editor main
        // thread during domain reload). Removing it is fail-safe: mods use the guarded create + resume pair, and
        // calling the absent wrap raises a clean "attempt to call a nil value" error instead of hanging.
        private static void HardenCoroutineLibrary(LuaState state)
        {
            LuaValue coroValue = state.Environment["coroutine"];
            if (coroValue.Type != LuaValueType.Table)
            {
                return;
            }

            LuaTable coro = coroValue.Read<LuaTable>();

            // WHY: strip the unguardable wrap primitive (see the note above) before any mod can reach it.
            coro["wrap"] = LuaValue.Nil;

            LuaValue nativeResume = coro["resume"];
            if (nativeResume.Type != LuaValueType.Function)
            {
                return;
            }

            // Arm a per-resume step/time/alloc guard hook on the coroutine's child LuaState — the native
            // library never installs the execution-guard hook there.
            coro["resume"] = new LuaFunction("resume",
                (ctx, ct) => GuardedCoroutineResume(ctx, ct, nativeResume));
        }

        private static System.Threading.Tasks.ValueTask<int> GuardedCoroutineResume(
            LuaFunctionExecutionContext ctx, CancellationToken ct, LuaValue nativeResume)
        {
            LuaValue[] result = ResumeWithPerResumeGuard(ctx.State, nativeResume, ctx.Arguments.ToArray(), ct);
            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(result));
        }

        private static LuaValue[] ResumeWithPerResumeGuard(
            LuaState callerState, LuaValue nativeResume, LuaValue[] resumeArgs, CancellationToken ct)
        {
            LuaState coroutineState = null;
            try
            {
                if (resumeArgs.Length > 0)
                {
                    coroutineState = resumeArgs[0].Read<LuaState>();
                }
            }
            catch
            {
                coroutineState = null;
            }

            // WHY: Arm/clear the hook ONLY on a genuinely SUSPENDED coroutine — the only state native resume
            // will actually run. Any re-entrant resume of a state already executing higher in the call chain
            // (a self-resume, a running ancestor coroutine, or the main thread) is NON-suspended: native resume
            // rejects it with [false,...] without running an instruction, so arming would be pointless and the
            // finally's SetHook(null) would strip the guard hook the OUTER guarded call (or LuaCsExecutionGuard
            // on the main state) already installed there, re-opening the unbounded-loop/allocation DoS. The
            // CanResume gate skips arming in exactly those cases, so a running state's guard is never clobbered.
            bool armed = false;
            bool canResume = false;
            try
            {
                canResume = coroutineState != null && coroutineState.CanResume;
            }
            catch
            {
                canResume = false;
            }

            if (canResume)
            {
                long steps = 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                // WHY: Mirror LuaCsExecutionGuard's allocation backstop on the coroutine's child state. Step and
                // time caps do NOT catch a doubling concat bomb (`s = s .. s`): it runs only a handful of VM
                // opcodes yet allocates to OOM, and plain concatenation has no library call site to cap. Trip on
                // the cheap monotonic process-heap reading (no forced-GC confirmation — that under-counts vs a
                // garbage-inclusive baseline and lets the bomb reach OutOfMemory before the trip fires).
                long maxAllocatedBytes = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;
                long allocBaseline = maxAllocatedBytes > 0 ? System.GC.GetTotalMemory(false) : 0;

                LuaFunction hook = new("coreai_coroutine_guard", (hctx, hct) =>
                {
                    steps++;
                    if (steps > CoroutineResumeStepBudget)
                    {
                        throw new LuaRuntimeException(hctx.State,
                            new InvalidOperationException(
                                $"LuaCsSecureEnvironment: EXCEEDED_COROUTINE_STEP_BUDGET ({CoroutineResumeStepBudget})"));
                    }

                    if (sw.ElapsedMilliseconds > CoroutineResumeTimeoutMs)
                    {
                        throw new LuaRuntimeException(hctx.State,
                            new TimeoutException($"Lua coroutine resume exceeded {CoroutineResumeTimeoutMs} ms."));
                    }

                    if (maxAllocatedBytes > 0)
                    {
                        long allocated = System.GC.GetTotalMemory(false) - allocBaseline;
                        if (allocated > maxAllocatedBytes)
                        {
                            throw new LuaRuntimeException(hctx.State,
                                new LuaMemoryBudgetException(
                                    $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} ({maxAllocatedBytes} bytes)"));
                        }
                    }

                    return new System.Threading.Tasks.ValueTask<int>(hctx.Return());
                });

                try
                {
                    coroutineState.SetHook(hook, string.Empty, 1);
                    armed = true;
                }
                catch
                {
                    armed = false;
                }
            }

            try
            {
                return callerState.CallAsync(nativeResume, resumeArgs.AsSpan(), ct).GetAwaiter().GetResult();
            }
            finally
            {
                if (armed)
                {
                    try
                    {
                        coroutineState.SetHook(null, string.Empty, 0);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
        }

        /// <summary>Loads and runs Lua code inside a secured state with the optional execution guard.</summary>
        public LuaValue[] RunChunk(
            LuaState state,
            string luaCode,
            LuaCsExecutionGuard guard = null,
            CancellationToken cancellationToken = default)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            LuaClosure closure = state.Load(luaCode, "sandbox_chunk");
            guard ??= new LuaCsExecutionGuard(maxSteps: OneShotHardLimitSteps);
            return guard.Execute(state, closure, cancellationToken);
        }

        private static void StripRiskyGlobals(LuaState state)
        {
            RemoveGlobal(state, "load");
            RemoveGlobal(state, "loadstring");
            RemoveGlobal(state, "loadfile");
            RemoveGlobal(state, "dofile");
            RemoveGlobal(state, "require");
            RemoveGlobal(state, "io");
            RemoveGlobal(state, "os");
            RemoveGlobal(state, "debug");
            RemoveGlobal(state, "package");
            RemoveGlobal(state, "collectgarbage");

            LuaValue stringLibValue = state.Environment["string"];
            if (stringLibValue.Type != LuaValueType.Table)
            {
                return;
            }

            LuaTable stringLib = stringLibValue.Read<LuaTable>();
            stringLib["dump"] = LuaValue.Nil;
            stringLib["rep"] = new LuaFunction("rep", CappedStringRep);

            LuaValue originalFormat = stringLib["format"];
            if (originalFormat.Type == LuaValueType.Function)
            {
                stringLib["format"] = new LuaFunction("format",
                    (ctx, ct) => CappedStringFormat(ctx, ct, originalFormat));
            }

            // WHY: table.concat(list [, sep [, i [, j]]]) allocates its whole result in one VM instruction,
            // the same allocation-bomb class as string.rep/string.format above. Replace it with a
            // version that aborts once the running result would exceed MaxTableConcatLength.
            LuaValue tableLibValue = state.Environment["table"];
            if (tableLibValue.Type == LuaValueType.Table)
            {
                LuaTable tableLib = tableLibValue.Read<LuaTable>();
                tableLib["concat"] = new LuaFunction("concat", CappedTableConcat);
            }
        }

        private static void RemoveGlobal(LuaState state, string name)
        {
            try
            {
                state.Environment[name] = LuaValue.Nil;
            }
            catch
            {
            }
        }

        private static System.Threading.Tasks.ValueTask<int> CappedStringRep(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            string s = ctx.GetArgument<string>(0);
            double countRaw = ctx.GetArgument<double>(1);
            string sep = ctx.ArgumentCount >= 3 ? ctx.GetArgument<string>(2) : "";

            if (double.IsNaN(countRaw) || countRaw < 1)
            {
                return new System.Threading.Tasks.ValueTask<int>(ctx.Return(""));
            }

            long count = countRaw > MaxStringRepLength ? MaxStringRepLength + 1L : (long)countRaw;
            long total = s.Length * count + sep.Length * (count - 1);
            if (total > MaxStringRepLength)
            {
                throw new LuaRuntimeException(ctx.State,
                    new InvalidOperationException(
                        $"LuaCsSecureEnvironment: string.rep result would exceed {MaxStringRepLength} chars."));
            }

            StringBuilder sb = new((int)total);
            for (long i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }

                sb.Append(s);
            }

            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(sb.ToString()));
        }

        // WHY: table.concat replacement: mirrors Lua-CSharp's own Lua.Standard.TableLibrary.Concat algorithm
        // (same start/end/sep defaults and the same "invalid value" error), but aborts as soon as the
        // in-progress result exceeds MaxTableConcatLength instead of finishing the (potentially huge)
        // build first.
        private static System.Threading.Tasks.ValueTask<int> CappedTableConcat(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            LuaTable table = ctx.GetArgument<LuaTable>(0);
            string sep = ctx.HasArgument(1) ? ctx.GetArgument<string>(1) : "";
            long start = ctx.HasArgument(2) ? (long)ctx.GetArgument<double>(2) : 1;
            long end = ctx.HasArgument(3) ? (long)ctx.GetArgument<double>(3) : table.ArrayLength;

            StringBuilder sb = new(512);
            for (long i = start; i <= end; i++)
            {
                LuaValue v = table[i];
                if (v.Type == LuaValueType.String)
                {
                    sb.Append(v.Read<string>());
                }
                else if (v.Type == LuaValueType.Number)
                {
                    sb.Append(v.Read<double>().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException($"invalid value ({v.Type}) at index {i} in table for 'concat'"));
                }

                if (i != end)
                {
                    sb.Append(sep);
                }

                if (sb.Length > MaxTableConcatLength)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException(
                            $"LuaCsSecureEnvironment: table.concat result would exceed {MaxTableConcatLength} chars."));
                }
            }

            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(sb.ToString()));
        }

        private static System.Threading.Tasks.ValueTask<int> CappedStringFormat(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct,
            LuaValue originalFormat)
        {
            if (ctx.ArgumentCount >= 1)
            {
                LuaValue format = ctx.GetArgument(0);
                if (format.Type == LuaValueType.String)
                {
                    EnsureFormatWidthWithinCap(ctx.State, format.Read<string>());
                }
            }

            LuaValue[] args = ctx.Arguments.ToArray();
            LuaValue[] results = ctx.State.CallAsync(originalFormat, args.AsSpan(), ct).GetAwaiter().GetResult();
            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(results));
        }

        private static void EnsureFormatWidthWithinCap(LuaState state, string format)
        {
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%')
                {
                    continue;
                }

                i++;
                if (i >= format.Length)
                {
                    break;
                }

                if (format[i] == '%')
                {
                    continue;
                }

                while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0)
                {
                    i++;
                }

                i = CheckNumericField(state, format, i);
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    i = CheckNumericField(state, format, i);
                }
            }
        }

        private static int CheckNumericField(LuaState state, string format, int start)
        {
            int i = start;
            long value = 0;
            bool hasDigits = false;
            while (i < format.Length && format[i] >= '0' && format[i] <= '9')
            {
                hasDigits = true;
                value = value * 10 + (format[i] - '0');
                if (value > MaxStringFormatLength)
                {
                    throw new LuaRuntimeException(state,
                        new InvalidOperationException(
                            $"LuaCsSecureEnvironment: string.format width/precision exceeds {MaxStringFormatLength} chars."));
                }

                i++;
            }

            return hasDigits ? i : start;
        }
    }
}
