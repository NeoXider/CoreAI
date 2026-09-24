using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Scripting;
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
        /// <summary>
        /// Maximum instruction budget for one-shot Lua script execution. Sized for Roblox parity: paired with
        /// the guard's ~10 s wall-clock timeout (which is the real limiter), a build/setup script gets the same
        /// headroom a Luau script does before "exhausted allowed execution time".
        /// </summary>
        public const int OneShotHardLimitSteps = 50_000_000;

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
        /// Maximum length of the string that <c>string.gsub</c> may build, for the same reason and with
        /// the same value as <see cref="MaxStringRepLength"/>: one call can replace every character of a
        /// long subject with a long replacement. The limit is checked before each append, so the result
        /// never grows past it.
        /// </summary>
        public const int MaxStringGsubLength = MaxStringRepLength;

        /// <summary>
        /// Maximum length of the string that <c>string.format</c> may build. An upper bound of the result
        /// is computed from the format string and the arguments before anything is formatted, so a call
        /// such as forty <c>%s</c> of a 1M-char string is refused without being built.
        /// </summary>
        public const int MaxStringFormatResultLength = MaxStringRepLength;

        /// <summary>
        /// Most pattern-matching steps one call of <c>string.find</c>, <c>string.match</c>,
        /// <c>string.gsub</c> or one <c>string.gmatch</c> iterator step may spend. A step is one entry
        /// into the matcher's recursive <c>match</c> (the point where Luau calls its interrupt), one
        /// character a repetition or <c>%b</c> scans, or one character a back-reference or a plain
        /// search compares.
        /// </summary>
        /// <remarks>
        /// WHY a per-call cap rather than the resume's own counter: the VM's guard hook only runs
        /// BETWEEN instructions, so a single library call used to run unbounded inside one instruction
        /// (<c>('a'):rep(120):find('.-.-.-.-b')</c> ran 3.3 s under a 500 ms budget and grows about
        /// n^4.6). The per-resume counters live in the hooks that the scheduler and the execution guard
        /// own, and a library function cannot reach them, so each call gets its own deterministic
        /// allowance instead and a loop of calls is still cut by the hook between them. Sized so that
        /// linear work over a <see cref="MaxStringRepLength"/>-char subject (a <c>gsub('%s+', ' ')</c>
        /// needs 1-2 steps per char) fits with room to spare, while a catastrophic pattern is refused after
        /// a fraction of a second (about 25 million steps per second on a desktop CoreCLR) instead of
        /// running for minutes.
        /// </remarks>
        public const int MaxPatternMatchSteps = 5_000_000;

        /// <summary>
        /// Marker in the error a pattern-matching call raises when it exceeds
        /// <see cref="MaxPatternMatchSteps"/>. The message also says "resume exceeded", which is the
        /// text the scheduler classifies as <c>BUDGET_EXCEEDED</c> for budget errors raised outside the
        /// thread's own hook.
        /// </summary>
        public const string PatternStepBudgetTripMarker = "EXCEEDED_PATTERN_STEP_BUDGET";

        /// <summary>
        /// Error a Lua function raises when it tries to yield while a sandbox library function (the
        /// <c>__tostring</c> call of <c>string.format</c>, a <c>string.gsub</c> replacement function or
        /// <c>__index</c>) is running it. Same meaning as Luau's "attempt to yield across
        /// metamethod/C-call boundary".
        /// </summary>
        public const string YieldAcrossCallBoundaryMessage = "attempt to yield across a C-call boundary";

        /// <summary>
        /// Most calls from library functions back into Lua that may be open at once on one thread (Luau's
        /// <c>LUAI_MAXCCALLS</c>): a <c>table.sort</c> comparator, a <c>__tostring</c> run by <c>tostring</c>,
        /// <c>print</c> or <c>string.format</c>, a <c>string.gsub</c> replacement function or <c>__index</c>,
        /// a <c>__pairs</c>/<c>__ipairs</c> metamethod, or a coroutine run by <c>coroutine.resume</c> (which
        /// continues the count of the thread that resumes it). The next such call raises
        /// <see cref="CStackOverflowMessage"/>, an ordinary error <c>pcall</c> can catch.
        /// </summary>
        /// <remarks>
        /// WHY a cap far below what the .NET stack allows: every one of these calls is a nested VM run on the
        /// .NET stack, and an error raised N levels deep is rethrown once per level with a stack trace that
        /// grows with N, so unwinding costs about N squared - with no instruction running, so no budget hook
        /// can fire. A comparator that re-entered table.sort 1,000 deep took 8.1 s to fail, and the same
        /// recursion unbounded ran 64 s under a 10 s budget (audit A2-05). Plain Lua recursion and the
        /// metamethods the VM runs inside its own loop (<c>__index</c>, <c>__newindex</c>, <c>__eq</c>,
        /// <c>__lt</c>, <c>__le</c>, arithmetic, <c>__len</c>, <c>__call</c>) do not nest .NET calls and are not
        /// counted. <c>__concat</c> does nest them, but the VM calls it directly, where no sandbox code runs, so
        /// it is not counted either (see the TODO on <see cref="CountCallsBackIntoLua"/>).
        /// </remarks>
        public const int MaxCCallDepth = 200;

        /// <summary>Start of the error raised past <see cref="MaxCCallDepth"/>; Luau's text for the same limit.</summary>
        public const string CStackOverflowMessage = "C stack overflow";

        /// <summary>
        /// What the sandbox's <c>coroutine.resume</c> returns (after <c>false</c>) for a thread only the
        /// scheduler may resume: a task thread, a signal handler's thread or a mod's main chunk, as reached
        /// through <c>coroutine.running()</c>. The thread is not touched.
        /// </summary>
        public const string SchedulerThreadResumeRefusal =
            "cannot resume a task or signal-handler thread with coroutine.resume; resume a parked task thread "
            + "with task.spawn(thread, ...), passing the handle task.spawn, task.defer or task.delay returned";

        /// <summary>
        /// Default per-execution GC allocation budget (bytes). Unlike the caps above, this is enforced
        /// by sampling <see cref="System.GC.GetTotalMemory(bool)"/> between VM instructions (Mono does not implement the thread-local allocation counter)
        /// (see <see cref="LuaCsExecutionGuard"/>) rather than at a specific library call, because plain
        /// string concatenation (<c>s = s .. s</c>) has no library call site to intercept. It is the
        /// last line of defense against allocation bombs built purely from concatenation opcodes.
        /// </summary>
        public const long MaxAllocatedBytesBudget = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;

        /// <summary>
        /// Creates a secured Lua-CSharp state and registers the allowed Lua APIs.
        /// </summary>
        /// <param name="registry">Optional API surface applied to the new state's environment.</param>
        /// <param name="liveResumeBudget">
        /// Optional shared, mutable per-resume budget (see <see cref="LuaCsCoroutineBudgetSettings"/>)
        /// that the guard armed around a mod-created RAW <c>coroutine.resume</c> (see
        /// <see cref="HardenCoroutineLibrary"/>) re-reads on every resume, deriving its step/time caps
        /// from it — see <see cref="RawCoroutineResumeStepBudgetMultiplier"/>/
        /// <see cref="RawCoroutineResumeTimeoutMultiplier"/> for why it derives rather than copies. Null
        /// (the default) builds a settings object holding CoreAI's documented defaults, reproducing the
        /// previous fixed 500,000-step / 1,000 ms allowance and never changing afterwards.
        /// </param>
        public LuaState Create(LuaCsApiRegistry registry = null,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null)
        {
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            state.OpenMathLibrary();
            state.OpenCoroutineLibrary();
            state.OpenBitwiseLibrary();

            StripRiskyGlobals(state);
            HardenCoroutineLibrary(state, liveResumeBudget ?? new LuaCsCoroutineBudgetSettings());
            CountCallsBackIntoLua(state);
            registry?.ApplyToEnvironment(state);
            return state;
        }

        /// <summary>
        /// Multiplier applied to the LIVE <see cref="LuaCsCoroutineBudgetSettings.BudgetPerResume"/> to
        /// derive the instruction-step budget armed around a mod-created RAW coroutine's resume (native
        /// <c>coroutine.create</c>/<c>coroutine.resume</c>, not the C#-managed
        /// <c>CoreAI.Ai.LuaCs.LuaCsCoroutineHandle</c> a scheduler thread or signal handler uses). At
        /// CoreAI's documented default (<c>LuaCsCoroutineHandle.DefaultBudgetPerResume</c> = 10,000) this
        /// reproduces the previous fixed 500,000-step allowance exactly.
        /// </summary>
        /// <remarks>
        /// WHY a multiplier and not equality with the managed per-resume budget: a raw coroutine is
        /// typically a mod's own explicit state machine doing heavier one-shot work across FEWER resumes
        /// than the frame-paced scheduler handles, so it keeps deliberately more headroom than the
        /// per-resume default — collapsing it to exact equality would silently shrink every raw
        /// coroutine's allowance 50x under default settings, a behavior change this fix does not need to
        /// make. WHY tied to the live settings at all (this IS the fix): the raw-coroutine guard used to
        /// arm fixed, untracked constants, so a mod could escape a budget the host had just tightened via
        /// <c>ScriptContext:SetTimeout</c> by moving its runaway loop into a child coroutine — the
        /// parent's hook cannot intervene until the nested resume returns control. Deriving from the live
        /// settings keeps the extra headroom PROPORTIONATE instead of independent, so a host's tightened
        /// budget meaningfully constrains raw coroutines too, scaling down right along with it.
        /// </remarks>
        public const int RawCoroutineResumeStepBudgetMultiplier = 50;

        /// <summary>
        /// Multiplier applied to the LIVE <see cref="LuaCsCoroutineBudgetSettings.ResumeTimeoutMs"/> to
        /// derive the wall-clock budget (ms) armed around a mod-created RAW coroutine's resume. At the
        /// documented default (<c>LuaCsCoroutineHandle.DefaultResumeTimeoutMs</c> = 500 ms) this
        /// reproduces the previous fixed 1,000 ms allowance exactly. See
        /// <see cref="RawCoroutineResumeStepBudgetMultiplier"/> for the full reasoning.
        /// </summary>
        public const int RawCoroutineResumeTimeoutMultiplier = 2;

        // Sampling window for the coroutine hook, matching LuaCsExecutionGuard: each fire charges this
        // many instructions to the step budget, so the same ceiling holds, and it stays tight enough for
        // the allocation backstop below to catch a doubling concat bomb.
        // TODO: pool the hook the way LuaCsExecutionGuard.RentHook does — it still allocates a
        // LuaFunction plus its capture per resume.
        private const int CoroutineHookInstructionBatch = 4;

        // WHY: A coroutine body runs on a CHILD LuaState that does NOT inherit LuaCsExecutionGuard's hook,
        // so an unbounded loop inside a resumed coroutine would escape every budget and hang the game.
        // Wrapping `coroutine.resume` arms a per-resume step+time+alloc guard hook on the child state and
        // clears it afterwards, so the coroutine body is cut like any other guarded execution.
        //
        // coroutine.wrap is REMOVED (set nil), not left native: its resumer drives a hidden child thread
        // through the library's OWN internal resume, bypassing the wrapped coroutine.resume below, so a
        // wrap-created body would run with NO guard hook — an unbounded-loop / allocation-bomb host-hang
        // vector. It cannot be safely re-armed on this Lua-CSharp build: a C# reimplementation's returned
        // function did not round-trip as callable, and a Lua redefinition needs a sync-over-async
        // Load/Execute that DEADLOCKS on a SynchronizationContext-bearing thread (editor domain reload).
        // Removing it is fail-safe: mods use the guarded create + resume pair instead.
        private static void HardenCoroutineLibrary(LuaState state, LuaCsCoroutineBudgetSettings liveResumeBudget)
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
            // library never installs the execution-guard hook there. WHY liveResumeBudget is captured
            // (not read once here): it is re-read on every resume inside ResumeWithPerResumeGuard so a
            // later ScriptContext:SetTimeout reaches a raw coroutine created before the call too — see
            // RawCoroutineResumeStepBudgetMultiplier.
            coro["resume"] = new LuaFunction("resume",
                (ctx, ct) => GuardedCoroutineResume(ctx, ct, nativeResume, liveResumeBudget));
        }

        // WHY one trip source per coroutine for its whole life, and NOT linked to the resumer's token:
        // Lua-CSharp runs a coroutine body with the token of its FIRST resume for as long as it lives (the
        // body's VM contexts capture it; a later resume only wakes them), so a trip on any later resume has to
        // cancel that very token for the body to see it (LuaCsCoroutineHandle.CancelGuardedRun). A link to the
        // first resumer would outlive it: its guard's pooled source, cancelled by a trip in some later run,
        // would then kill a healthy coroutine that merely started under it. A host cancellation of the resumer
        // still ends the run: the child is held to its own per-resume budget, and the resumer's token is
        // checked the moment the resume returns.
        private static readonly ConditionalWeakTable<LuaState, RawCoroutine> RawCoroutines = new();

        private static readonly ConditionalWeakTable<LuaState, RawCoroutine>.CreateValueCallback NewRawCoroutine =
            _ => new RawCoroutine();

        /// <summary>What the guarded <c>coroutine.resume</c> keeps about one mod-created coroutine.</summary>
        private sealed class RawCoroutine
        {
            /// <summary>The source a trip cancels; its token is the one the body runs with for life.</summary>
            public readonly CancellationTokenSource TripSource = new();

            /// <summary>
            /// The allocation budget of its latest resume, which a coroutine it resumes in turn inherits.
            /// </summary>
            public long AllocationBudgetBytes = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;
        }

        private static System.Threading.Tasks.ValueTask<int> GuardedCoroutineResume(
            LuaFunctionExecutionContext ctx, CancellationToken ct, LuaValue nativeResume,
            LuaCsCoroutineBudgetSettings liveResumeBudget)
        {
            LuaValue[] result = ResumeWithPerResumeGuard(
                ctx.State, nativeResume, ctx.Arguments.ToArray(), ct, liveResumeBudget);
            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(result));
        }

        private static LuaValue[] ResumeWithPerResumeGuard(
            LuaState callerState, LuaValue nativeResume, LuaValue[] resumeArgs, CancellationToken ct,
            LuaCsCoroutineBudgetSettings liveResumeBudget)
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

            // WHY refused before anything touches the thread: a handle's thread runs with the handle's token for
            // life, so a hook armed here could only trip it from a foreign context, which xpcall swallows (see
            // LuaCsCoroutineHandle.HandleThreads). Returned like the native refusal of a non-suspended coroutine.
            if (LuaCsCoroutineHandle.IsHandleThread(coroutineState))
            {
                return new[] { new LuaValue(false), (LuaValue)SchedulerThreadResumeRefusal };
            }

            // WHY: Arm/clear the hook ONLY on a genuinely SUSPENDED coroutine. A re-entrant resume of a
            // state already executing higher in the call chain (self-resume, running ancestor, main thread)
            // is non-suspended and rejected by native resume without running an instruction — arming there
            // would strip whichever outer guard hook is already installed, reopening the DoS.
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

            CancellationTokenSource tripSource = null;
            LuaCsHostFunctionException tripError = null;
            if (canResume)
            {
                // WHY the resumed body continues the resumer's count (Luau's lua_resume does the same): each
                // nested resume is one more VM run on the .NET stack, and a chain of them unwinds like any other
                // nesting (see MaxCCallDepth). Refused like Luau's, with the coroutine left suspended.
                int callerDepth = CCallDepthOf(callerState);
                if (callerDepth >= MaxCCallDepth)
                {
                    return new[] { new LuaValue(false), (LuaValue)CStackOverflowText("coroutine.resume") };
                }

                CCallDepths.GetValue(coroutineState, NewCCallDepth).Base = callerDepth + 1;

                RawCoroutine raw = RawCoroutines.GetValue(coroutineState, NewRawCoroutine);
                long steps = 0;
                // WHY read live here, at the moment of the ACTUAL resume, not cached from an earlier
                // Create() call: this is the fix for the coroutine.resume escape hatch — a raw coroutine
                // created before a host tightened ScriptContext:SetTimeout must still be bound by the NEW
                // value on its next resume, exactly like the C#-managed LuaCsCoroutineHandle already is.
                long stepBudget = (long)liveResumeBudget.BudgetPerResume * RawCoroutineResumeStepBudgetMultiplier;
                long timeoutMs = (long)liveResumeBudget.ResumeTimeoutMs * RawCoroutineResumeTimeoutMultiplier;
                // WHY: raw timestamp + a precomputed ticks budget, not a Stopwatch instance — same
                // allocation-avoidance reason as LuaCsExecutionGuard.GuardHook (see that type for detail).
                long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                long timeoutTicks = timeoutMs * System.Diagnostics.Stopwatch.Frequency / 1000;
                // WHY: the SAME allocation backstop the execution guard uses, on the coroutine's child
                // state - step/time caps do not catch a doubling concat bomb, which is ordinary VM opcodes
                // with no library call site to cap. Shared as one type rather than a second hand-copied
                // check, because the copy here kept its own broken rule after the guard's was fixed: see
                // LuaCsAllocationBudget for why a sampled reading may only raise a suspicion.
                // WHY the resumer's budget and not a fixed one: the body is part of the run that resumes it, so
                // it gets that run's budget - the mod's HandlerMaxAllocatedBytes for its handlers, task threads
                // and main chunk. A fixed 256 MB let a mod held to 16 MB keep 80 MB alive inside
                // coroutine.create (audit A2-09).
                raw.AllocationBudgetBytes = ResumerAllocationBudget(callerState);
                LuaCsAllocationBudget allocation = default;
                allocation.Reset(raw.AllocationBudgetBytes);

                tripSource = raw.TripSource;
                CancellationTokenSource runSource = tripSource;
                LuaFunction hook = new("coreai_coroutine_guard", (hctx, hct) =>
                {
                    if (tripError == null)
                    {
                        steps += CoroutineHookInstructionBatch;
                        if (steps > stepBudget)
                        {
                            tripError = LuaCsCoroutineHandle.CreatePendingBudgetTrip(hctx.State,
                                $"LuaCsSecureEnvironment: EXCEEDED_COROUTINE_STEP_BUDGET ({stepBudget})");
                        }
                        else if (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp > timeoutTicks)
                        {
                            tripError = LuaCsCoroutineHandle.CreatePendingBudgetTrip(hctx.State,
                                $"Lua coroutine resume exceeded {timeoutMs} ms.");
                        }
                        else if (allocation.IsExceeded())
                        {
                            // WHY only the marker text and no dedicated exception type: the resumer receives
                            // the trip as the error value of `ok, err = coroutine.resume(co)`, a string, so a
                            // CLR type could never be observed across that boundary.
                            tripError = LuaCsCoroutineHandle.CreatePendingBudgetTrip(hctx.State,
                                $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} "
                                + $"({allocation.BudgetBytes} bytes)");
                        }
                        else
                        {
                            return new System.Threading.Tasks.ValueTask<int>(hctx.Return());
                        }
                    }

                    if (LuaCsCoroutineHandle.CancelGuardedRun(runSource, hct))
                    {
                        return new System.Threading.Tasks.ValueTask<int>(hctx.Return());
                    }

                    throw LuaCsCoroutineHandle.ForeignContextTrip(tripError);
                });

                try
                {
                    coroutineState.SetHook(hook, string.Empty, CoroutineHookInstructionBatch);
                    armed = true;
                }
                catch
                {
                    armed = false;
                }
            }

            try
            {
                LuaValue[] results = callerState.CallAsync(nativeResume, resumeArgs.AsSpan(),
                    armed ? tripSource.Token : ct).GetAwaiter().GetResult();
                return tripError == null ? results : EndTrippedResume(coroutineState, tripError);
            }
            catch (Exception) when (tripError != null)
            {
                return EndTrippedResume(coroutineState, tripError);
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

        /// <summary>
        /// The allocation budget of the run executing on <paramref name="resumer"/>: the innermost guarded run
        /// on that state, else the handle whose thread it is, else the budget the raw coroutine it is was last
        /// resumed with, else <see cref="LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget"/>.
        /// </summary>
        private static long ResumerAllocationBudget(LuaState resumer)
        {
            if (LuaCsExecutionGuard.TryGetRunAllocationBudget(resumer, out long bytes)
                || LuaCsCoroutineHandle.TryGetHandleAllocationBudget(resumer, out bytes))
            {
                return bytes;
            }

            return RawCoroutines.TryGetValue(resumer, out RawCoroutine raw)
                ? raw.AllocationBudgetBytes
                : LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;
        }

        // WHY the thread is marked Dead here: Lua-CSharp's protected resume lets a cancellation through with the
        // thread left Running (CoroutineCore.ResumeAsyncCore catches `when !(ex is OperationCanceledException)`),
        // which no later resume could use and nothing would ever finish. A budget-cut coroutine is over, and its
        // resumer gets the [false, trip line] a protected resume returns for any error the coroutine raised.
        // Only a coroutine.create thread ever gets here: a handle's thread is refused before it is touched.
        /// <summary>The <c>coroutine.resume</c> results of a resume whose per-resume budget tripped.</summary>
        private static LuaValue[] EndTrippedResume(LuaState coroutineState, LuaCsHostFunctionException trip)
        {
            coroutineState.UnsafeSetStatus(LuaThreadStatus.Dead);
            return new[] { new LuaValue(false), trip.ErrorObject };
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

        /// <summary>
        /// Loads and runs Lua code inside a secured state without holding the host frame: the guard hook
        /// awaits <paramref name="frameYielder"/> every few milliseconds, so a chunk that runs for
        /// seconds still lets the player draw. A null yielder behaves exactly like
        /// <see cref="RunChunk"/>, only asynchronously.
        /// </summary>
        public Task<LuaValue[]> RunChunkAsync(
            LuaState state,
            string luaCode,
            LuaCsExecutionGuard guard = null,
            IScriptFrameYielder frameYielder = null,
            CancellationToken cancellationToken = default)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            // WHY: compilation stays synchronous — Load has no yield points and a chunk's source is
            // bounded by the tool's own input cap, so it cannot be the thing that holds the frame.
            LuaClosure closure = state.Load(luaCode, "sandbox_chunk");
            guard ??= new LuaCsExecutionGuard(maxSteps: OneShotHardLimitSteps);
            return guard.ExecuteAsync(state, closure, frameYielder, cancellationToken);
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

            // WHY: the native find/match/gmatch/gsub run a backtracking matcher inside ONE VM instruction,
            // where no guard hook can interrupt it, and gsub builds its result with no size limit. They
            // are replaced by the budgeted port below. The string metatable's __index reads this table,
            // so method calls such as s:find(...) reach the replacements too.
            stringLib["find"] = new LuaFunction("find", BudgetedFind);
            stringLib["match"] = new LuaFunction("match", BudgetedMatch);
            stringLib["gmatch"] = new LuaFunction("gmatch", BudgetedGMatch);
            stringLib["gsub"] = new LuaFunction("gsub", BudgetedGSub);

            LuaValue originalFormat = stringLib["format"];
            // WHY captured here, before any mod runs: string.format's %s converts objects through the
            // library's own tostring, and a mod may later replace the global.
            LuaValue originalToString = state.Environment["tostring"];
            if (originalFormat.Type == LuaValueType.Function)
            {
                stringLib["format"] = new LuaFunction("format",
                    (ctx, ct) => CappedStringFormat(ctx, ct, originalFormat, originalToString));
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

        // WHY: these native functions call Lua themselves - tostring and print a __tostring, pairs and ipairs a
        // __pairs/__ipairs, table.sort its comparator or __lt - so each is a point where Lua re-enters through
        // the .NET stack and must be counted against MaxCCallDepth. Runs after StripRiskyGlobals so that
        // string.format keeps the native tostring it counts itself. A call that cannot reach Lua keeps the
        // native result without the extra frame: tostring of a number and pairs over a plain table stay as
        // cheap and allocation-free as before.
        // TODO: __concat is the one metamethod Lua-CSharp runs as a nested VM call of its own (the async
        // Concat path), so `mt.__concat = function(a, b) return a .. b end; local _ = o .. 'x'` still unwinds
        // quadratically with no hook firing: 2.6 s at a mod-imposed depth of 1,000 and 24-28 s unbounded under a
        // 10 s budget. Counting it needs Lua.dll to expose the call, or a guard hook that scans the Lua frames
        // for CONCAT re-entries once the frame count grows.
        private static void CountCallsBackIntoLua(LuaState state)
        {
            LuaTable environment = state.Environment;
            LuaValue[] stringMeta = CallNativeOnce(state, environment["getmetatable"], string.Empty);
            LuaTable stringMetatable = stringMeta != null && stringMeta.Length > 0
                                                       && stringMeta[0].Type == LuaValueType.Table
                ? stringMeta[0].Read<LuaTable>()
                : null;

            LuaValue nativeToString = environment["tostring"];
            if (nativeToString.Type == LuaValueType.Function)
            {
                environment["tostring"] = new LuaFunction("tostring", (ctx, ct) =>
                {
                    if (ctx.ArgumentCount > 0 && !MayRunToString(ctx.GetArgument(0), stringMetatable))
                    {
                        return new System.Threading.Tasks.ValueTask<int>(ctx.Return(ctx.GetArgument(0).ToString()));
                    }

                    return CallNativeCounted(ctx, ct, nativeToString, "tostring");
                });
            }

            LuaValue nativePrint = environment["print"];
            if (nativePrint.Type == LuaValueType.Function)
            {
                environment["print"] = new LuaFunction("print",
                    (ctx, ct) => CallNativeCounted(ctx, ct, nativePrint, "print"));
            }

            WrapIterationFactory(state, environment, "pairs", Metamethods.Pairs, LuaValue.Nil);
            WrapIterationFactory(state, environment, "ipairs", Metamethods.IPairs, 0d);

            LuaValue tableLibValue = environment["table"];
            if (tableLibValue.Type == LuaValueType.Table)
            {
                LuaTable tableLib = tableLibValue.Read<LuaTable>();
                LuaValue nativeSort = tableLib["sort"];
                if (nativeSort.Type == LuaValueType.Function)
                {
                    tableLib["sort"] = new LuaFunction("sort",
                        (ctx, ct) => CallNativeCounted(ctx, ct, nativeSort, "table.sort"));
                }
            }
        }

        /// <summary>
        /// Replaces <c>pairs</c> or <c>ipairs</c>: a table without the <paramref name="metamethod"/> gets the
        /// native iterator triple directly, anything else goes to the native function as a counted call.
        /// </summary>
        private static void WrapIterationFactory(LuaState state, LuaTable environment, string name,
            string metamethod, LuaValue control)
        {
            LuaValue native = environment[name];
            if (native.Type != LuaValueType.Function)
            {
                return;
            }

            // WHY read from the native function once: its iterator is a private singleton of the basic library,
            // and handing out the very same function keeps pairs(t) identical to what it returned before.
            LuaValue[] triple = CallNativeOnce(state, native, new LuaTable());
            LuaValue iterator = triple != null && triple.Length > 0 && triple[0].Type == LuaValueType.Function
                ? triple[0]
                : LuaValue.Nil;
            environment[name] = new LuaFunction(name, (ctx, ct) =>
            {
                if (iterator.Type == LuaValueType.Function && ctx.ArgumentCount > 0
                                                          && ctx.GetArgument(0).Type == LuaValueType.Table)
                {
                    LuaTable table = ctx.GetArgument(0).Read<LuaTable>();
                    if (table.Metatable == null || !table.Metatable.TryGetValue(metamethod, out LuaValue _))
                    {
                        return new System.Threading.Tasks.ValueTask<int>(ctx.Return(iterator, table, control));
                    }
                }

                return CallNativeCounted(ctx, ct, native, name);
            });
        }

        /// <summary>
        /// False when native <c>tostring</c> of <paramref name="value"/> runs no Lua: a value of a type no
        /// sandboxed script can give a metatable, or a string or table whose metatable has no <c>__tostring</c>.
        /// </summary>
        private static bool MayRunToString(LuaValue value, LuaTable stringMetatable)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                case LuaValueType.Boolean:
                case LuaValueType.Number:
                    return false;
                case LuaValueType.String:
                    return stringMetatable == null || stringMetatable.TryGetValue(Metamethods.ToString, out LuaValue _);
                case LuaValueType.Table:
                    LuaTable metatable = value.Read<LuaTable>().Metatable;
                    return metatable != null && metatable.TryGetValue(Metamethods.ToString, out LuaValue _);
                default:
                    return true;
            }
        }

        /// <summary>
        /// Runs a native library function once while the environment is built (it runs no Lua then) and
        /// returns its results, or null when it did not complete synchronously or failed.
        /// </summary>
        private static LuaValue[] CallNativeOnce(LuaState state, LuaValue function, LuaValue argument)
        {
            if (function.Type != LuaValueType.Function)
            {
                return null;
            }

            try
            {
                System.Threading.Tasks.ValueTask<LuaValue[]> call =
                    state.CallAsync(function, new[] { argument }.AsSpan(), CancellationToken.None);
                // WHY only read when complete: nothing is ever waited on (WebGL); a native library function
                // given a plain value completes synchronously.
                return call.IsCompletedSuccessfully ? call.GetAwaiter().GetResult() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Calls <paramref name="function"/> as one counted call back into Lua on <paramref name="state"/> (see
        /// <see cref="MaxCCallDepth"/>): once the limit is reached it raises the C-stack error instead of calling.
        /// A call that suspends (a yield inside it, a frame yield of the async guard) stays counted on this
        /// thread until it completes. A host function that runs a script's Lua function - a callback, a
        /// <c>__tostring</c> - calls through here, so a script cannot nest it without bound.
        /// </summary>
        /// <param name="boundary">The library or host function named in the error, e.g. <c>table.sort</c>.</param>
        internal static System.Threading.Tasks.ValueTask<LuaValue[]> CallCountedAsync(LuaState state,
            LuaValue function, ReadOnlySpan<LuaValue> arguments, CancellationToken ct, string boundary)
        {
            CCallDepth depth = EnterCCall(state, boundary);
            System.Threading.Tasks.ValueTask<LuaValue[]> call;
            try
            {
                call = state.CallAsync(function, arguments, ct);
            }
            catch
            {
                ExitCCall(depth);
                throw;
            }

            if (!call.IsCompleted)
            {
                return AwaitCounted(call, depth);
            }

            try
            {
                // WHY: the task is already complete, so this unwraps its result or exception, never waits.
                return new System.Threading.Tasks.ValueTask<LuaValue[]>(call.GetAwaiter().GetResult());
            }
            finally
            {
                ExitCCall(depth);
            }
        }

        private static async System.Threading.Tasks.ValueTask<LuaValue[]> AwaitCounted(
            System.Threading.Tasks.ValueTask<LuaValue[]> call, CCallDepth depth)
        {
            try
            {
                return await call;
            }
            finally
            {
                ExitCCall(depth);
            }
        }

        /// <summary>
        /// Calls the native <paramref name="function"/> with this call's own arguments through
        /// <see cref="CallCountedAsync"/> and returns its results as this call's.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> CallNativeCounted(LuaFunctionExecutionContext ctx,
            CancellationToken ct, LuaValue function, string boundary)
        {
            System.Threading.Tasks.ValueTask<LuaValue[]> call =
                CallCountedAsync(ctx.State, function, ctx.Arguments, ct, boundary);
            return call.IsCompleted
                ? new System.Threading.Tasks.ValueTask<int>(ctx.Return(call.GetAwaiter().GetResult()))
                : ReturnWhenDone(ctx, call);
        }

        private static async System.Threading.Tasks.ValueTask<int> ReturnWhenDone(LuaFunctionExecutionContext ctx,
            System.Threading.Tasks.ValueTask<LuaValue[]> call)
        {
            return ctx.Return(await call);
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
                throw LibraryRefusal(ctx.State,
                    $"LuaCsSecureEnvironment: string.rep result would exceed {MaxStringRepLength} chars.");
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
                    throw LibraryRefusal(ctx.State, $"invalid value ({v.Type}) at index {i} in table for 'concat'");
                }

                if (i != end)
                {
                    sb.Append(sep);
                }

                if (sb.Length > MaxTableConcatLength)
                {
                    throw LibraryRefusal(ctx.State,
                        $"LuaCsSecureEnvironment: table.concat result would exceed {MaxTableConcatLength} chars.");
                }
            }

            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(sb.ToString()));
        }

        // WHY: string.format replacement. The native implementation still does every conversion, so the
        // output is exactly what it was; this wrapper only (1) turns every %s object into its string through
        // the library's own tostring first, so the native call runs no Lua code at all and a yield inside a
        // __tostring meets the C-call boundary here instead of corrupting the thread, and (2) refuses a call
        // whose result could exceed MaxStringFormatResultLength before anything is built.
        private static System.Threading.Tasks.ValueTask<int> CappedStringFormat(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct,
            LuaValue originalFormat,
            LuaValue originalToString)
        {
            LuaValue[] args = ctx.Arguments.ToArray();
            if (args.Length >= 1 && args[0].Type == LuaValueType.String)
            {
                string format = args[0].Read<string>();
                EnsureFormatWidthWithinCap(ctx.State, format);
                PrepareFormatArguments(ctx.State, format, args, originalToString, ct);
            }

            LuaValue[] results = CallWithoutYield(ctx.State, originalFormat, args, ct, "string.format");
            if (results.Length > 0 && results[0].Type == LuaValueType.String
                                   && results[0].Read<string>().Length > MaxStringFormatResultLength)
            {
                throw ResultTooLong(ctx.State, "string.format", MaxStringFormatResultLength);
            }

            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(results));
        }

        /// <summary>
        /// Walks <paramref name="format"/> the way the native <c>string.format</c> parses it, converts
        /// every <c>%s</c> argument that is not already a string, number, boolean or nil to its string
        /// (in <paramref name="args"/>), and throws once an upper bound of the result exceeds
        /// <see cref="MaxStringFormatResultLength"/>. It stops at the first specifier the native
        /// implementation rejects, because the native call then fails at that point too.
        /// </summary>
        private static void PrepareFormatArguments(LuaState state, string format, LuaValue[] args,
            LuaValue toStringFunction, CancellationToken ct)
        {
            long bound = 0;
            int argIndex = 1;
            int i = 0;
            while (i < format.Length)
            {
                char c = format[i++];
                if (c != '%')
                {
                    bound++;
                    continue;
                }

                if (i >= format.Length)
                {
                    break;
                }

                if (format[i] == '%')
                {
                    bound++;
                    i++;
                    continue;
                }

                while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0)
                {
                    i++;
                }

                if (!TryReadFormatNumber(format, ref i, false, out int width))
                {
                    break;
                }

                int precision = -1;
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    if (!TryReadFormatNumber(format, ref i, true, out precision))
                    {
                        break;
                    }
                }

                if (i >= format.Length || argIndex >= args.Length)
                {
                    break;
                }

                char specifier = format[i++];
                LuaValue arg = args[argIndex];
                long item;
                switch (specifier)
                {
                    case 's':
                        if (arg.Type != LuaValueType.String && arg.Type != LuaValueType.Number
                                                            && arg.Type != LuaValueType.Boolean
                                                            && arg.Type != LuaValueType.Nil
                                                            && toStringFunction.Type == LuaValueType.Function)
                        {
                            LuaValue[] converted = CallWithoutYield(state, toStringFunction, new[] { arg }, ct,
                                "string.format");
                            if (converted.Length == 0 || converted[0].Type != LuaValueType.String)
                            {
                                throw new LuaRuntimeException(state, (LuaValue)"'__tostring' must return a string");
                            }

                            arg = converted[0];
                            args[argIndex] = arg;
                        }

                        if (arg.Type == LuaValueType.String)
                        {
                            int length = arg.Read<string>().Length;
                            item = precision > 0 && precision <= length ? precision : length;
                        }
                        else
                        {
                            item = FormatNumberItemBound;
                        }

                        break;
                    case 'q':
                        if (arg.Type == LuaValueType.String)
                        {
                            // WHY 3x: the native escaper writes at most three chars per input char ("\13").
                            item = 3L * arg.Read<string>().Length + 2;
                        }
                        else if (arg.Type == LuaValueType.Number || arg.Type == LuaValueType.Boolean
                                                                 || arg.Type == LuaValueType.Nil)
                        {
                            item = FormatNumberItemBound;
                        }
                        else
                        {
                            // WHY: Lua and Luau accept only literal-capable values for %q. The native build
                            // printed tostring(value) unquoted, which ran Lua code (a __tostring) inside the
                            // native call where a yield or an unbounded result cannot be caught.
                            throw new LuaRuntimeException(state, (LuaValue)
                                $"bad argument #{argIndex + 1} to 'format' (string expected, got {arg.TypeToString()})");
                        }

                        break;
                    case 'f':
                    case 'e':
                    case 'g':
                    case 'G':
                        item = FloatItemBound(specifier, precision, arg);
                        break;
                    case 'd':
                    case 'i':
                    case 'u':
                        item = Math.Max(precision, 20) + 2;
                        break;
                    case 'x':
                    case 'X':
                        item = 20;
                        break;
                    case 'c':
                        item = 1;
                        break;
                    default:
                        item = -1;
                        break;
                }

                if (item < 0)
                {
                    break;
                }

                argIndex++;
                // WHY +2: room for the '+' and ' ' flags the native implementation prepends.
                bound += Math.Max(width, item + 2);
                if (bound > MaxStringFormatResultLength)
                {
                    throw ResultTooLong(state, "string.format", MaxStringFormatResultLength);
                }
            }

            if (bound > MaxStringFormatResultLength)
            {
                throw ResultTooLong(state, "string.format", MaxStringFormatResultLength);
            }
        }

        /// <summary>Upper bound of one %s/%q item that prints a number, boolean or nil.</summary>
        private const int FormatNumberItemBound = 64;

        /// <summary>
        /// Reads a width or precision exactly like the native parser: at most two digits, where a third
        /// digit (and, for a precision, no digit at all) is an error the native call raises itself.
        /// </summary>
        private static bool TryReadFormatNumber(string format, ref int i, bool required, out int value)
        {
            value = 0;
            int start = i;
            while (i < format.Length && char.IsDigit(format[i]) && i - start < 3)
            {
                i++;
            }

            int digits = i - start;
            if (digits == 0)
            {
                return !required;
            }

            return digits <= 2 && int.TryParse(format.Substring(start, digits), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Upper bound of one %f/%e/%g/%G item as the native implementation prints it (.NET "F", "E",
        /// "G" formats with a precision of at most 99, or the shortest round-trip form without one).
        /// </summary>
        private static long FloatItemBound(char specifier, int precision, LuaValue arg)
        {
            const int shortestForm = 40;
            int digits = Math.Max(precision, 0);
            if (arg.Type != LuaValueType.Number)
            {
                return 330 + digits + 16;
            }

            if (precision < 0)
            {
                return shortestForm;
            }

            if (specifier != 'f')
            {
                return digits + 16;
            }

            double magnitude = Math.Abs(arg.Read<double>());
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude))
            {
                return shortestForm;
            }

            int integerDigits = magnitude < 10 ? 1 : (int)Math.Floor(Math.Log10(magnitude)) + 2;
            return integerDigits + digits + 2;
        }

        private static LuaRuntimeException ResultTooLong(LuaState state, string function, int cap)
        {
            return LibraryRefusal(state, $"LuaCsSecureEnvironment: {function} result would exceed {cap} chars.");
        }

        // WHY LuaCsHostFunctionException and not LuaRuntimeException(LuaState, Exception) or a plain error
        // object: the inner-exception form gave pcall "System.InvalidOperationException: ..." and gave xpcall
        // and a protected coroutine.resume nil, while a level-1 error object lets pcall alone prepend a
        // "chunk:line:" position (string.gsub's refusal read one way under pcall and another under xpcall).
        // Level 0 with the text as the error value gives pcall, xpcall, resume and C# the same one line.
        /// <summary>
        /// The error a sandbox library function raises when it refuses a call (a result or field over its
        /// cap, a value it cannot concatenate); <paramref name="message"/> is exactly what Lua receives.
        /// </summary>
        private static LuaRuntimeException LibraryRefusal(LuaState state, string message)
        {
            return new LuaCsHostFunctionException(state, message, null);
        }

        /// <summary>
        /// Calls <paramref name="function"/> from a sandbox library function and returns its results
        /// without ever blocking. A yield inside the call is refused with
        /// <see cref="YieldAcrossCallBoundaryMessage"/>: the running thread is marked non-running for
        /// the duration, so Lua-CSharp's <c>coroutine.yield</c> fails BEFORE it signals the resumer.
        /// </summary>
        /// <remarks>
        /// WHY the fence and not only a completion check: a yield that got through used to hand the
        /// resumer a "suspended" result while this synchronous frame kept running the thread, after which
        /// the caller saw "Operation is not valid due to the current state of the object." and every later
        /// yield of that thread failed. Luau refuses the same yield ("attempt to yield across
        /// metamethod/C-call boundary"). A call that still comes back unfinished (an awaited host
        /// operation) is reported with the same error rather than waited for: WebGL has no thread to wait on.
        /// </remarks>
        private static LuaValue[] CallWithoutYield(LuaState state, LuaValue function, LuaValue[] args,
            CancellationToken ct, string boundary)
        {
            CCallDepth depth = EnterCCall(state, boundary);
            bool fenced = false;
            try
            {
                fenced = BeginYieldFence(state);
                System.Threading.Tasks.ValueTask<LuaValue[]> call = state.CallAsync(function, args.AsSpan(), ct);
                if (!call.IsCompleted)
                {
                    throw YieldAcrossBoundary(state, boundary);
                }

                // WHY: the task is already complete, so this unwraps its result or exception, never waits.
                return call.GetAwaiter().GetResult();
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth);
            }
        }

        /// <summary>
        /// Reads <paramref name="table"/>[<paramref name="key"/>] honouring <c>__index</c>, like
        /// <c>lua_gettable</c> in the reference <c>gsub</c>, under the same yield fence as
        /// <see cref="CallWithoutYield"/>.
        /// </summary>
        private static LuaValue GetTableWithoutYield(LuaState state, LuaTable table, LuaValue key,
            CancellationToken ct, string boundary)
        {
            if (table.Metatable == null)
            {
                return table[key];
            }

            CCallDepth depth = EnterCCall(state, boundary);
            bool fenced = false;
            try
            {
                fenced = BeginYieldFence(state);
                System.Threading.Tasks.ValueTask<LuaValue> read = state.GetTableAsync(table, key, ct);
                if (!read.IsCompleted)
                {
                    throw YieldAcrossBoundary(state, boundary);
                }

                // WHY: the task is already complete, so this unwraps its result or exception, never waits.
                return read.GetAwaiter().GetResult();
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth);
            }
        }

        // WHY per thread (LuaState) and not per .NET thread: a coroutine suspended inside a library call keeps
        // that call open, and a shared counter would charge it to every other thread - on Unity's one main
        // thread, to every other mod, which a mod could then make fail on purpose. A coroutine instead starts
        // from the count of the thread that resumes it (Base), so a chain of resumes is counted as a whole.
        // TODO: a handle's thread (task thread, signal runner, main chunk) starts from zero even when host code
        // resumes it from inside a library call; the scheduler's thread quota and signal-generation cap bound
        // that nesting today.
        private static readonly ConditionalWeakTable<LuaState, CCallDepth> CCallDepths = new();

        private static readonly ConditionalWeakTable<LuaState, CCallDepth>.CreateValueCallback NewCCallDepth =
            _ => new CCallDepth();

        /// <summary>Calls back into Lua that library functions have open on one thread (see <see cref="MaxCCallDepth"/>).</summary>
        private sealed class CCallDepth
        {
            /// <summary>The count of the thread that last resumed this one (plus one), for a raw coroutine.</summary>
            public int Base;

            /// <summary>Calls this thread's own library functions have open.</summary>
            public int Local;
        }

        /// <summary>How many calls back into Lua are open on <paramref name="state"/>, its resumers' included.</summary>
        private static int CCallDepthOf(LuaState state)
        {
            return CCallDepths.TryGetValue(state, out CCallDepth depth) ? depth.Base + depth.Local : 0;
        }

        /// <summary>
        /// Opens one call from a library function back into Lua on <paramref name="state"/>, or raises
        /// <see cref="CStackOverflowMessage"/> when <see cref="MaxCCallDepth"/> are already open. Every
        /// successful call is paired with <see cref="ExitCCall"/>.
        /// </summary>
        private static CCallDepth EnterCCall(LuaState state, string boundary)
        {
            CCallDepth depth = CCallDepths.GetValue(state, NewCCallDepth);
            if (depth.Base + depth.Local >= MaxCCallDepth)
            {
                throw LibraryRefusal(state, CStackOverflowText(boundary));
            }

            depth.Local++;
            return depth;
        }

        private static void ExitCCall(CCallDepth depth)
        {
            depth.Local--;
        }

        private static string CStackOverflowText(string boundary)
        {
            return $"{CStackOverflowMessage} ({boundary}: more than {MaxCCallDepth} nested calls from library "
                   + "functions back into Lua)";
        }

        private static bool BeginYieldFence(LuaState state)
        {
            if (!state.IsCoroutine || state.GetStatus() != LuaThreadStatus.Running)
            {
                return false;
            }

            state.UnsafeSetStatus(LuaThreadStatus.Normal);
            return true;
        }

        private static void EndYieldFence(LuaState state, bool fenced)
        {
            if (fenced)
            {
                state.UnsafeSetStatus(LuaThreadStatus.Running);
            }
        }

        private const string NonRunningYieldMessage = "cannot yield from a non-running coroutine";

        private static bool IsFencedYield(LuaRuntimeException ex)
        {
            LuaValue errorObject = ex.ErrorObject;
            if (errorObject.Type == LuaValueType.String
                && errorObject.Read<string>().IndexOf(NonRunningYieldMessage, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            string message = ex.Message;
            return message != null && message.IndexOf(NonRunningYieldMessage, StringComparison.Ordinal) >= 0;
        }

        private static LuaRuntimeException YieldAcrossBoundary(LuaState state, string boundary)
        {
            return new LuaRuntimeException(state,
                (LuaValue)$"{YieldAcrossCallBoundaryMessage} ({boundary} called a Lua function that yielded)");
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
                    throw LibraryRefusal(state,
                        $"LuaCsSecureEnvironment: string.format width/precision exceeds {MaxStringFormatLength} chars.");
                }

                i++;
            }

            return hasDigits ? i : start;
        }

        private static readonly char[] PatternSpecials = { '^', '$', '*', '+', '?', '.', '(', '[', '%', '-' };

        private static System.Threading.Tasks.ValueTask<int> BudgetedFind(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            return new System.Threading.Tasks.ValueTask<int>(FindOrMatch(ctx, true));
        }

        private static System.Threading.Tasks.ValueTask<int> BudgetedMatch(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            return new System.Threading.Tasks.ValueTask<int>(FindOrMatch(ctx, false));
        }

        // WHY: port of Luau's str_find_aux. A start past the end returns nil (Lua 5.2+/Luau), a pattern
        // without specials is a plain search, and every start position is tried INCLUDING the one after the
        // last char, which the native build skipped (so ("abc"):find("$") returned nil instead of 4, 3).
        private static int FindOrMatch(LuaFunctionExecutionContext ctx, bool find)
        {
            string function = find ? "string.find" : "string.match";
            string subject = ctx.GetArgument<string>(0);
            string pattern = ctx.GetArgument<string>(1);
            int init = RelativePosition(ctx.HasArgument(2) ? ctx.GetArgument<int>(2) : 1, subject.Length);
            if (init < 1)
            {
                init = 1;
            }
            else if (init > subject.Length + 1)
            {
                return ctx.Return(LuaValue.Nil);
            }

            if (find && ((ctx.ArgumentCount > 3 && ctx.GetArgument(3).ToBoolean())
                         || pattern.IndexOfAny(PatternSpecials) < 0))
            {
                int found = LuaPatternMatcher.PlainFind(ctx.State, function, subject, pattern, init - 1,
                    MaxPatternMatchSteps);
                return found < 0
                    ? ctx.Return(LuaValue.Nil)
                    : ctx.Return((double)(found + 1), (double)(found + pattern.Length));
            }

            LuaPatternMatcher matcher = new(ctx.State, function, subject, pattern, MaxPatternMatchSteps);
            bool anchor = pattern.Length > 0 && pattern[0] == '^';
            int patternStart = anchor ? 1 : 0;
            int start = init - 1;
            do
            {
                matcher.Reset();
                int end = matcher.Match(start, patternStart);
                if (end >= 0)
                {
                    return ReturnCaptures(ctx, matcher, start, end, find);
                }
            }
            while (start++ < subject.Length && !anchor);

            return ctx.Return(LuaValue.Nil);
        }

        /// <summary>
        /// Pushes a successful match the way Luau does: <c>string.find</c> returns the start and end
        /// followed by the captures (none when the pattern has none); <c>string.match</c> and the
        /// <c>string.gmatch</c> iterator return the captures, or the whole match when there are none.
        /// </summary>
        private static int ReturnCaptures(LuaFunctionExecutionContext ctx, LuaPatternMatcher matcher, int start,
            int end, bool find)
        {
            int captureCount = matcher.Level == 0 && !find ? 1 : matcher.Level;
            int offset = find ? 2 : 0;
            Span<LuaValue> values = ctx.GetReturnBuffer(offset + captureCount);
            if (find)
            {
                values[0] = start + 1;
                values[1] = end;
            }

            for (int i = 0; i < captureCount; i++)
            {
                values[offset + i] = matcher.GetCapture(i, start, end);
            }

            return offset + captureCount;
        }

        private static int RelativePosition(int position, int length)
        {
            if (position < 0)
            {
                position += length + 1;
            }

            return position >= 0 ? position : 0;
        }

        private static System.Threading.Tasks.ValueTask<int> BudgetedGMatch(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            string subject = ctx.GetArgument<string>(0);
            string pattern = ctx.GetArgument<string>(1);
            GMatchIterator iterator = new(subject, pattern);
            return new System.Threading.Tasks.ValueTask<int>(
                ctx.Return(new LuaFunction("gmatch_iterator", iterator.Next)));
        }

        /// <summary>
        /// State of one <c>string.gmatch</c> iterator (Luau's <c>gmatch_aux</c>): a '^' is an ordinary
        /// character here, as in Lua 5.1 and Luau, and every call gets its own step budget.
        /// </summary>
        private sealed class GMatchIterator
        {
            private readonly string _subject;
            private readonly string _pattern;
            private LuaPatternMatcher _matcher;
            private int _start;

            public GMatchIterator(string subject, string pattern)
            {
                _subject = subject;
                _pattern = pattern;
            }

            public System.Threading.Tasks.ValueTask<int> Next(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                _matcher ??= new LuaPatternMatcher(ctx.State, "string.gmatch", _subject, _pattern,
                    MaxPatternMatchSteps);
                _matcher.BeginCall(ctx.State);
                for (int start = _start; start <= _subject.Length; start++)
                {
                    _matcher.Reset();
                    int end = _matcher.Match(start, 0);
                    if (end >= 0)
                    {
                        _start = end == start ? end + 1 : end;
                        return new System.Threading.Tasks.ValueTask<int>(
                            ReturnCaptures(ctx, _matcher, start, end, false));
                    }
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            }
        }

        // WHY: port of Luau's str_gsub/add_value/add_s. The result is capped at MaxStringGsubLength before
        // every append, the whole call shares one pattern step budget, and replacement functions and
        // __index run under the yield fence of CallWithoutYield (Luau refuses those yields too). Unlike the
        // native build this also tries the position after the last char (("abc"):gsub("x*", "-") is
        // "-a-b-c-", 4), accepts a number replacement, and rejects '%' followed by a non-digit.
        private static System.Threading.Tasks.ValueTask<int> BudgetedGSub(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            LuaState state = ctx.State;
            string subject = ctx.GetArgument<string>(0);
            string pattern = ctx.GetArgument<string>(1);
            LuaValue replacement = ctx.GetArgument(2);
            double maxArgument = ctx.HasArgument(3) ? ctx.GetArgument<double>(3) : subject.Length + 1;
            LuaRuntimeException.ThrowBadArgumentIfNumberIsNotInteger(state, 4, maxArgument);

            LuaValueType kind = replacement.Type;
            if (kind != LuaValueType.String && kind != LuaValueType.Number && kind != LuaValueType.Function
                && kind != LuaValueType.Table)
            {
                throw new LuaRuntimeException(state,
                    (LuaValue)"bad argument #3 to 'gsub' (string/function/table expected)");
            }

            string replacementText = kind == LuaValueType.String
                ? replacement.Read<string>()
                : kind == LuaValueType.Number
                    ? replacement.ToString()
                    : null;
            long maxReplacements = maxArgument > int.MaxValue ? int.MaxValue : (long)maxArgument;

            LuaPatternMatcher matcher = new(state, "string.gsub", subject, pattern, MaxPatternMatchSteps);
            bool anchor = pattern.Length > 0 && pattern[0] == '^';
            int patternStart = anchor ? 1 : 0;
            StringBuilder result = new(Math.Min(subject.Length, MaxStringGsubLength) + 16);
            int source = 0;
            long count = 0;
            while (count < maxReplacements)
            {
                matcher.Reset();
                int end = matcher.Match(source, patternStart);
                if (end >= 0)
                {
                    count++;
                    AppendReplacement(state, result, matcher, subject, source, end, replacement, replacementText,
                        ct);
                }

                if (end >= 0 && end > source)
                {
                    source = end;
                }
                else if (source < subject.Length)
                {
                    AppendCapped(state, result, subject, source, 1);
                    source++;
                }
                else
                {
                    break;
                }

                if (anchor)
                {
                    break;
                }
            }

            AppendCapped(state, result, subject, source, subject.Length - source);
            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(result.ToString(), (double)count));
        }

        private static void AppendReplacement(LuaState state, StringBuilder result, LuaPatternMatcher matcher,
            string subject, int start, int end, LuaValue replacement, string replacementText, CancellationToken ct)
        {
            if (replacementText != null)
            {
                AppendReplacementText(state, result, matcher, subject, start, end, replacementText);
                return;
            }

            LuaValue value;
            if (replacement.Type == LuaValueType.Function)
            {
                int captureCount = matcher.Level == 0 ? 1 : matcher.Level;
                LuaValue[] captures = new LuaValue[captureCount];
                for (int i = 0; i < captureCount; i++)
                {
                    captures[i] = matcher.GetCapture(i, start, end);
                }

                LuaValue[] returned = CallWithoutYield(state, replacement, captures, ct, "string.gsub");
                value = returned.Length > 0 ? returned[0] : LuaValue.Nil;
            }
            else
            {
                value = GetTableWithoutYield(state, replacement.Read<LuaTable>(), matcher.GetCapture(0, start, end),
                    ct, "string.gsub");
            }

            if (!value.ToBoolean())
            {
                AppendCapped(state, result, subject, start, end - start);
            }
            else if (value.Type == LuaValueType.String || value.Type == LuaValueType.Number)
            {
                string text = value.Type == LuaValueType.String ? value.Read<string>() : value.ToString();
                AppendCapped(state, result, text, 0, text.Length);
            }
            else
            {
                throw new LuaRuntimeException(state,
                    (LuaValue)$"invalid replacement value (a {value.TypeToString()})");
            }
        }

        private static void AppendReplacementText(LuaState state, StringBuilder result, LuaPatternMatcher matcher,
            string subject, int start, int end, string text)
        {
            if (text.IndexOf('%') < 0)
            {
                AppendCapped(state, result, text, 0, text.Length);
                return;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '%')
                {
                    AppendCapped(state, result, text, i, 1);
                    continue;
                }

                i++;
                char next = i < text.Length ? text[i] : '\0';
                if (next < '0' || next > '9')
                {
                    if (next != '%')
                    {
                        throw new LuaRuntimeException(state,
                            (LuaValue)"invalid use of '%' in replacement string");
                    }

                    AppendCapped(state, result, text, i, 1);
                }
                else if (next == '0')
                {
                    AppendCapped(state, result, subject, start, end - start);
                }
                else
                {
                    LuaValue capture = matcher.GetCapture(next - '1', start, end);
                    string captureText = capture.Type == LuaValueType.String
                        ? capture.Read<string>()
                        : capture.ToString();
                    AppendCapped(state, result, captureText, 0, captureText.Length);
                }
            }
        }

        private static void AppendCapped(LuaState state, StringBuilder result, string text, int start, int length)
        {
            if (length <= 0)
            {
                return;
            }

            if ((long)result.Length + length > MaxStringGsubLength)
            {
                throw ResultTooLong(state, "string.gsub", MaxStringGsubLength);
            }

            result.Append(text, start, length);
        }

        /// <summary>
        /// Budgeted port of the Lua 5.1/Luau pattern matcher (<c>MatchState</c> and <c>match</c> in
        /// Luau's lstrlib.cpp): character classes, sets, anchors, the four quantifiers, captures and
        /// position captures, back-references, <c>%b</c> and <c>%f</c>, with the reference error texts
        /// and Luau's 200-level recursion limit ("pattern too complex"). Every entry into
        /// <see cref="Match"/> and every character a loop scans is charged against a step budget, and
        /// exceeding it raises <see cref="PatternStepBudgetTripMarker"/>, so no call can backtrack for
        /// longer than the budget allows.
        /// </summary>
        /// <remarks>
        /// WHY character classes are ASCII-exact and .NET-classified above 127: Lua-CSharp strings are
        /// UTF-16, so a non-ASCII character is one char here and two or more bytes in Luau, where the C
        /// locale classifies none of them. Keeping the classification the native Lua-CSharp matcher used
        /// (<c>char.IsLetter</c> and friends) leaves every existing non-ASCII result unchanged, and for
        /// ASCII it is exactly the C locale Lua and Luau use.
        /// </remarks>
        internal sealed class LuaPatternMatcher
        {
            /// <summary>Most captures one pattern may open (<c>LUA_MAXCAPTURES</c>).</summary>
            internal const int MaxCaptures = 32;

            /// <summary>Deepest recursion of <see cref="Match"/> (Luau's <c>LUAI_MAXCCALLS</c>).</summary>
            internal const int MaxMatchDepth = 200;

            private const int CapUnfinished = -1;
            private const int CapPosition = -2;
            private const char Escape = '%';

            private readonly string _function;
            private readonly string _source;
            private readonly string _pattern;
            private readonly long _stepBudget;
            private readonly int[] _captureInit = new int[MaxCaptures];
            private readonly int[] _captureLength = new int[MaxCaptures];
            private LuaState _state;
            private long _steps;
            private int _matchDepth = MaxMatchDepth;
            private int _level;

            /// <param name="state">Thread errors are raised on.</param>
            /// <param name="function">Library function named in errors, e.g. <c>string.find</c>.</param>
            /// <param name="source">Subject string.</param>
            /// <param name="pattern">Pattern, including a leading '^' the caller skips itself.</param>
            /// <param name="stepBudget">Steps allowed before the budget error.</param>
            internal LuaPatternMatcher(LuaState state, string function, string source, string pattern,
                long stepBudget)
            {
                _state = state;
                _function = function;
                _source = source;
                _pattern = pattern;
                _stepBudget = stepBudget;
            }

            /// <summary>Steps charged since construction or the last <see cref="BeginCall"/>.</summary>
            internal long Steps => _steps;

            /// <summary>Number of captures the last successful <see cref="Match"/> recorded.</summary>
            internal int Level => _level;

            /// <summary>Starts a new library call: a fresh step budget on <paramref name="state"/>.</summary>
            internal void BeginCall(LuaState state)
            {
                _state = state;
                _steps = 0;
                Reset();
            }

            /// <summary>Clears the captures before a match attempt at a new start position.</summary>
            internal void Reset()
            {
                _level = 0;
                _matchDepth = MaxMatchDepth;
            }

            /// <summary>
            /// Searches <paramref name="pattern"/> literally in <paramref name="source"/> from
            /// <paramref name="init"/> (0-based) and returns the 0-based index or -1. Each candidate costs
            /// one step per compared character, so a quadratic plain search is bounded as well.
            /// </summary>
            internal static int PlainFind(LuaState state, string function, string source, string pattern, int init,
                long stepBudget)
            {
                int length = pattern.Length;
                if (length == 0)
                {
                    return init;
                }

                int last = source.Length - length;
                char first = pattern[0];
                long steps = 0;
                int i = init;
                while (i <= last)
                {
                    int candidate = source.IndexOf(first, i, last - i + 1);
                    if (candidate < 0)
                    {
                        return -1;
                    }

                    int matched = 1;
                    while (matched < length && source[candidate + matched] == pattern[matched])
                    {
                        matched++;
                    }

                    steps += matched;
                    if (steps > stepBudget)
                    {
                        throw BudgetTrip(state, function, stepBudget);
                    }

                    if (matched == length)
                    {
                        return candidate;
                    }

                    i = candidate + 1;
                }

                return -1;
            }

            /// <summary>
            /// Value of capture <paramref name="index"/> of the match
            /// [<paramref name="start"/>, <paramref name="end"/>): the substring, the 1-based position of a
            /// position capture, or the whole match when the pattern has no captures and index is 0.
            /// </summary>
            internal LuaValue GetCapture(int index, int start, int end)
            {
                if (index >= _level)
                {
                    if (index == 0)
                    {
                        return _source.Substring(start, end - start);
                    }

                    throw Error("invalid capture index");
                }

                int length = _captureLength[index];
                if (length == CapUnfinished)
                {
                    throw Error("unfinished capture");
                }

                if (length == CapPosition)
                {
                    return _captureInit[index] + 1;
                }

                return _source.Substring(_captureInit[index], length);
            }

            /// <summary>
            /// Matches the pattern from index <paramref name="p"/> against the subject from index
            /// <paramref name="s"/> and returns the index after the match, or -1.
            /// </summary>
            internal int Match(int s, int p)
            {
                if (_matchDepth-- == 0)
                {
                    throw Error("pattern too complex");
                }

                Charge(1);
                int patternEnd = _pattern.Length;
                while (p != patternEnd)
                {
                    char item = _pattern[p];
                    if (item == '(')
                    {
                        s = p + 1 < patternEnd && _pattern[p + 1] == ')'
                            ? StartCapture(s, p + 2, CapPosition)
                            : StartCapture(s, p + 1, CapUnfinished);
                        break;
                    }

                    if (item == ')')
                    {
                        s = EndCapture(s, p + 1);
                        break;
                    }

                    if (item == '$' && p + 1 == patternEnd)
                    {
                        s = s == _source.Length ? s : -1;
                        break;
                    }

                    if (item == Escape && p + 1 < patternEnd)
                    {
                        char next = _pattern[p + 1];
                        if (next == 'b')
                        {
                            s = MatchBalance(s, p + 2);
                            if (s >= 0)
                            {
                                p += 4;
                                continue;
                            }

                            break;
                        }

                        if (next == 'f')
                        {
                            p += 2;
                            if (p >= patternEnd || _pattern[p] != '[')
                            {
                                throw Error("missing '[' after '%f' in pattern");
                            }

                            int setEnd = ClassEnd(p);
                            char previous = s == 0 ? '\0' : _source[s - 1];
                            char current = s < _source.Length ? _source[s] : '\0';
                            if (!MatchBracketClass(previous, p, setEnd - 1) && MatchBracketClass(current, p, setEnd - 1))
                            {
                                p = setEnd;
                                continue;
                            }

                            s = -1;
                            break;
                        }

                        if (next >= '0' && next <= '9')
                        {
                            s = MatchCapture(s, next);
                            if (s >= 0)
                            {
                                p += 2;
                                continue;
                            }

                            break;
                        }
                    }

                    int ep = ClassEnd(p);
                    char suffix = ep < patternEnd ? _pattern[ep] : '\0';
                    if (!SingleMatch(s, p, ep))
                    {
                        if (suffix == '*' || suffix == '?' || suffix == '-')
                        {
                            p = ep + 1;
                            continue;
                        }

                        s = -1;
                        break;
                    }

                    if (suffix == '?')
                    {
                        int optional = Match(s + 1, ep + 1);
                        if (optional >= 0)
                        {
                            s = optional;
                            break;
                        }

                        p = ep + 1;
                        continue;
                    }

                    if (suffix == '+')
                    {
                        s = MaxExpand(s + 1, p, ep);
                        break;
                    }

                    if (suffix == '*')
                    {
                        s = MaxExpand(s, p, ep);
                        break;
                    }

                    if (suffix == '-')
                    {
                        s = MinExpand(s, p, ep);
                        break;
                    }

                    s++;
                    p = ep;
                }

                _matchDepth++;
                return s;
            }

            private int MaxExpand(int s, int p, int ep)
            {
                int count = 0;
                while (SingleMatch(s + count, p, ep))
                {
                    count++;
                    Charge(1);
                }

                while (count >= 0)
                {
                    int result = Match(s + count, ep + 1);
                    if (result >= 0)
                    {
                        return result;
                    }

                    count--;
                }

                return -1;
            }

            private int MinExpand(int s, int p, int ep)
            {
                while (true)
                {
                    int result = Match(s, ep + 1);
                    if (result >= 0)
                    {
                        return result;
                    }

                    if (!SingleMatch(s, p, ep))
                    {
                        return -1;
                    }

                    s++;
                }
            }

            private int StartCapture(int s, int p, int what)
            {
                if (_level >= MaxCaptures)
                {
                    throw Error("too many captures");
                }

                _captureInit[_level] = s;
                _captureLength[_level] = what;
                _level++;
                int result = Match(s, p);
                if (result < 0)
                {
                    _level--;
                }

                return result;
            }

            private int EndCapture(int s, int p)
            {
                int open = CaptureToClose();
                _captureLength[open] = s - _captureInit[open];
                int result = Match(s, p);
                if (result < 0)
                {
                    _captureLength[open] = CapUnfinished;
                }

                return result;
            }

            private int CaptureToClose()
            {
                for (int level = _level - 1; level >= 0; level--)
                {
                    if (_captureLength[level] == CapUnfinished)
                    {
                        return level;
                    }
                }

                throw Error("invalid pattern capture");
            }

            private int MatchCapture(int s, char digit)
            {
                int index = digit - '1';
                if (index < 0 || index >= _level || _captureLength[index] == CapUnfinished)
                {
                    throw Error($"invalid capture index %{index + 1}");
                }

                int length = _captureLength[index];
                if (length < 0)
                {
                    return -1;
                }

                Charge(Math.Max(length, 1));
                if (_source.Length - s >= length
                    && string.CompareOrdinal(_source, _captureInit[index], _source, s, length) == 0)
                {
                    return s + length;
                }

                return -1;
            }

            private int MatchBalance(int s, int p)
            {
                if (p >= _pattern.Length - 1)
                {
                    throw Error("malformed pattern (missing arguments to '%b')");
                }

                if (s >= _source.Length || _source[s] != _pattern[p])
                {
                    return -1;
                }

                char open = _pattern[p];
                char close = _pattern[p + 1];
                int depth = 1;
                while (++s < _source.Length)
                {
                    Charge(1);
                    char c = _source[s];
                    if (c == close)
                    {
                        if (--depth == 0)
                        {
                            return s + 1;
                        }
                    }
                    else if (c == open)
                    {
                        depth++;
                    }
                }

                return -1;
            }

            private int ClassEnd(int p)
            {
                int patternEnd = _pattern.Length;
                char item = _pattern[p++];
                if (item == Escape)
                {
                    if (p >= patternEnd)
                    {
                        throw Error("malformed pattern (ends with '%')");
                    }

                    return p + 1;
                }

                if (item == '[')
                {
                    if (p < patternEnd && _pattern[p] == '^')
                    {
                        p++;
                    }

                    do
                    {
                        if (p >= patternEnd)
                        {
                            throw Error("malformed pattern (missing ']')");
                        }

                        if (_pattern[p++] == Escape && p < patternEnd)
                        {
                            p++;
                        }
                    }
                    while (p >= patternEnd || _pattern[p] != ']');

                    return p + 1;
                }

                return p;
            }

            private bool SingleMatch(int s, int p, int ep)
            {
                if (s >= _source.Length)
                {
                    return false;
                }

                char c = _source[s];
                switch (_pattern[p])
                {
                    case '.':
                        return true;
                    case Escape:
                        return MatchClass(c, _pattern[p + 1]);
                    case '[':
                        return MatchBracketClass(c, p, ep - 1);
                    default:
                        return _pattern[p] == c;
                }
            }

            private bool MatchBracketClass(char c, int p, int ec)
            {
                bool matches = true;
                if (_pattern[p + 1] == '^')
                {
                    matches = false;
                    p++;
                }

                while (++p < ec)
                {
                    char item = _pattern[p];
                    if (item == Escape)
                    {
                        p++;
                        if (MatchClass(c, _pattern[p]))
                        {
                            return matches;
                        }
                    }
                    else if (_pattern[p + 1] == '-' && p + 2 < ec)
                    {
                        p += 2;
                        if (_pattern[p - 2] <= c && c <= _pattern[p])
                        {
                            return matches;
                        }
                    }
                    else if (item == c)
                    {
                        return matches;
                    }
                }

                return !matches;
            }

            private static bool MatchClass(char c, char cl)
            {
                bool result;
                char lower = cl >= 'A' && cl <= 'Z' ? (char)(cl + ('a' - 'A')) : cl;
                switch (lower)
                {
                    case 'a':
                        result = IsAlpha(c);
                        break;
                    case 'c':
                        result = c < 128 ? c < 32 || c == 127 : char.IsControl(c);
                        break;
                    case 'd':
                        result = IsDigit(c);
                        break;
                    case 'g':
                        result = IsGraph(c);
                        break;
                    case 'l':
                        result = c < 128 ? c >= 'a' && c <= 'z' : char.IsLower(c);
                        break;
                    case 'p':
                        result = c < 128 && IsGraph(c) && !IsAlpha(c) && !IsDigit(c);
                        break;
                    case 's':
                        result = c < 128 ? c == ' ' || (c >= '\t' && c <= '\r') : char.IsWhiteSpace(c);
                        break;
                    case 'u':
                        result = c < 128 ? c >= 'A' && c <= 'Z' : char.IsUpper(c);
                        break;
                    case 'w':
                        result = IsAlpha(c) || IsDigit(c);
                        break;
                    case 'x':
                        result = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                        break;
                    case 'z':
                        result = c == '\0';
                        break;
                    default:
                        return cl == c;
                }

                return cl >= 'a' && cl <= 'z' ? result : !result;
            }

            private static bool IsAlpha(char c)
            {
                return c < 128 ? (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') : char.IsLetter(c);
            }

            private static bool IsDigit(char c)
            {
                return c < 128 ? c >= '0' && c <= '9' : char.IsDigit(c);
            }

            private static bool IsGraph(char c)
            {
                return c < 128 ? c > 32 && c < 127 : !char.IsControl(c) && !char.IsWhiteSpace(c);
            }

            private void Charge(int steps)
            {
                _steps += steps;
                if (_steps > _stepBudget)
                {
                    throw BudgetTrip(_state, _function, _stepBudget);
                }
            }

            private LuaRuntimeException Error(string message)
            {
                return new LuaRuntimeException(_state, (LuaValue)message);
            }

            /// <summary>
            /// The budget error: the <see cref="PatternStepBudgetTripMarker"/> shape of the other guard
            /// trips, naming the library function and the author line, and worded so the scheduler
            /// classifies it as <c>BUDGET_EXCEEDED</c>.
            /// </summary>
            internal static LuaRuntimeException BudgetTrip(LuaState state, string function, long budget)
            {
                return LuaCsCoroutineHandle.CreateBudgetTrip(state,
                    $"LuaCsSecureEnvironment: {PatternStepBudgetTripMarker} ({budget}) in {function}: the resume "
                    + "exceeded the step budget of one pattern-matching call; simplify the pattern or match a "
                    + "shorter string");
            }
        }
    }
}
