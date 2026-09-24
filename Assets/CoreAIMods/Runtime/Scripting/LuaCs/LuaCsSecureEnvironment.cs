using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
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
        /// Error a Lua function raises when it tries to yield while a library function that called it back is still
        /// running: a <c>__tostring</c> run by <c>tostring</c>, <c>print</c>, <c>warn</c> or <c>string.format</c>, a
        /// <c>table.sort</c> comparator, a <c>__pairs</c>/<c>__ipairs</c> metamethod, a <c>string.gsub</c>
        /// replacement function or <c>__index</c> (audit C3-06). Same meaning as Luau's "attempt to yield across
        /// metamethod/C-call boundary". A yield across <c>pcall</c> still works, as in Luau.
        /// </summary>
        public const string YieldAcrossCallBoundaryMessage = "attempt to yield across a C-call boundary";

        /// <summary>
        /// Most LEVELS of calls from library functions back into Lua that may be open at once along one chain of
        /// nested runs on the native stack (Luau's <c>LUAI_MAXCCALLS</c>, scaled to native frame size). A call
        /// whose frames take up to about 4 KB of native stack opens <see cref="LightCallLevels"/>: a function run
        /// by <c>pcall</c> or <c>xpcall</c> (whose message handler runs inside the same call). One that takes up to
        /// about 8 KB opens <see cref="HeavyCallLevels"/>: a <c>string.gsub</c> replacement function, a
        /// <c>__tostring</c> run by <c>tostring</c>, <c>print</c>, <c>warn</c> or <c>string.format</c>, a
        /// <c>table.sort</c> comparator, a <c>__pairs</c>/<c>__ipairs</c> metamethod, a <c>string.gsub</c>
        /// <c>__index</c>, a coroutine run by <c>coroutine.resume</c>, a scheduler thread that
        /// <c>task.spawn</c> runs at once, and a guarded call that starts inside a run (re-entering the state, or a
        /// <c>mods_call</c> export on another). A thread run inside a run continues the count of the thread that run
        /// executes on. The call that would pass the limit raises
        /// <see cref="CStackOverflowMessage"/>, an ordinary error <c>pcall</c> can catch; a <c>pcall</c> past the
        /// limit returns it (<c>false</c> and the line), and an <c>xpcall</c> hands it to its handler.
        /// </summary>
        /// <remarks>
        /// WHY 128 levels of about 4 KB (audit B3-01 and the Hub-crash investigation): each of these calls is a
        /// nested VM run on the .NET stack. Measured on CoreCLR x64 at the limit, unoptimised (tier-0) code, by
        /// NativeStack_EveryChannelStaysWithinItsWeight_AtTheCap: 3,408 B a level for pcall, 3,376 for xpcall, 3,872
        /// for a gsub replacement function (5,776 while gsub was an async method chain, audit C3-02; 4,288 on Windows
        /// x64, over the 4 KB of one level, so a gsub replacement function opens two levels since 7.47.0), and per two
        /// levels 3,232 for tostring through <c>__tostring</c>, 6,432 for <c>string.format</c>, 2,736-3,904 for
        /// table.sort, 3,440 for <c>__pairs</c>, 5,168 for coroutine.resume and 7,600 for a <c>task.spawn</c> that runs
        /// its thread at once. The only other bound is <c>RuntimeHelpers.TryEnsureSufficientExecutionStack</c> inside
        /// Lua-CSharp, which Unity's Mono implements but upstream Mono and an IL2CPP build may answer with a constant
        /// <c>true</c>, so a deep enough chain would end the process instead of raising an error. Weighting each call
        /// by its frame size keeps the worst chain of any mix under 128 x 4 KB = 512 KB on CoreCLR, plus the eighth
        /// more room an xpcall message handler is given past the limit (16 levels of 3,312 B, 52 KB), while ordinary
        /// pcall nesting still goes 128 deep. Mono's JIT lays out frames about 2.8 times larger (.NET's Mono runtime:
        /// 9,696 B a pcall level, 9,472 a gsub level, 17,888 two task.spawn levels), so a chain at the limit takes
        /// up to about 1.35 MB there, where Mono's own stack check stands behind it; the weights keep every channel
        /// within its share of a pcall level on both. Before the count ran 200 per channel and restarted in every
        /// task thread: 250 nested task.spawn levels plus 200 tostring levels took 2.56 MB.
        /// <para>
        /// WHY a cap at all, rather than the .NET stack's own limit: an error raised N levels deep is rethrown once
        /// per level with a stack trace that grows with N, so unwinding costs about N squared - with no instruction
        /// running, so no budget hook can fire. A comparator that re-entered table.sort 1,000 deep took 8.1 s to
        /// fail and unbounded ran 64 s under a 10 s budget (audit A2-05); uncounted pcall recursion held one
        /// Heartbeat frame 5.7 s under a 500 ms budget (audit B3-01). Plain Lua recursion and the metamethods the
        /// VM runs inside its own loop (<c>__index</c>, <c>__newindex</c>, <c>__eq</c>, <c>__lt</c>, <c>__le</c>,
        /// arithmetic, <c>__len</c>, <c>__call</c>) use no native stack and are not counted. Still uncounted
        /// although it does nest: <c>__concat</c>, which the VM calls directly where no sandbox code runs (see the
        /// TODO on <see cref="CountCallsBackIntoLua"/>). A guarded call on any state from inside a run (a
        /// <c>mods_call</c> export, the same state or another) continues the count of the run it starts in (audit
        /// C3-03).
        /// </para>
        /// </remarks>
        public const int MaxCCallDepth = 128;

        /// <summary>
        /// Levels a call back into Lua opens when its native frames take up to about 4 KB: <c>pcall</c> and
        /// <c>xpcall</c> (see <see cref="MaxCCallDepth"/>).
        /// </summary>
        public const int LightCallLevels = 1;

        /// <summary>
        /// Levels a call back into Lua opens when its native frames take up to about 8 KB: every other counted
        /// call, a resumed coroutine and a scheduler thread run at once (see <see cref="MaxCCallDepth"/>).
        /// </summary>
        public const int HeavyCallLevels = 2;

        /// <summary>
        /// Levels a <c>string.gsub</c> replacement function opens (see <see cref="MaxCCallDepth"/>).
        /// </summary>
        /// <remarks>
        /// WHY heavy: a level measured 3,872 B on Linux CoreCLR but 4,288 B on Windows x64, and at a weight of one
        /// the 128-level chain took 531 KB there, past the 512 KB envelope the weights exist to keep.
        /// </remarks>
        private const int GSubCallbackLevels = HeavyCallLevels;

        /// <summary>Start of the error raised past <see cref="MaxCCallDepth"/>; Luau's text for the same limit.</summary>
        public const string CStackOverflowMessage = "C stack overflow";

        /// <summary>
        /// Start of every refusal and budget line the sandbox itself raises into Lua: a library result over its cap,
        /// a pattern-step trip, a raw coroutine's or a coroutine handle's budget trip. What follows it (the
        /// <c>EXCEEDED_...</c> marker, or the library function) is what code and tests classify by.
        /// </summary>
        /// <remarks>
        /// WHY a neutral word and not the class that raised the line (audit B3-06): these lines reach the script,
        /// the model and the auto-repair loop, and "LuaCsSecureEnvironment:" or "LuaCsCoroutineHandle:" named
        /// CLR types they can do nothing with.
        /// </remarks>
        internal const string SandboxLinePrefix = "sandbox: ";

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
        /// <para>
        /// The derived allowance is a ceiling, not a grant: a raw coroutine runs nested in the run that resumes
        /// it and is also held to what that run has left (see <see cref="LuaCsGuardedRun"/>), so inside a scheduler
        /// thread's resume the headroom is that resume's remaining budget, and only a coroutine resumed from a
        /// longer run (an <c>execute_lua</c> chunk, a handler's guarded call) can use all of it.
        /// </para>
        /// </remarks>
        public const int RawCoroutineResumeStepBudgetMultiplier = 50;

        /// <summary>
        /// Multiplier applied to the LIVE <see cref="LuaCsCoroutineBudgetSettings.ResumeTimeoutMs"/> to
        /// derive the wall-clock budget (ms) armed around a mod-created RAW coroutine's resume. At the
        /// documented default (<c>LuaCsCoroutineHandle.DefaultResumeTimeoutMs</c> = 500 ms) this
        /// reproduces the previous fixed 1,000 ms allowance exactly, and like the step budget it is held to the
        /// resumer's remaining time. See <see cref="RawCoroutineResumeStepBudgetMultiplier"/> for the full reasoning.
        /// </summary>
        public const int RawCoroutineResumeTimeoutMultiplier = 2;

        // WHY 4: the sampling window of the coroutine hook, matching LuaCsExecutionGuard: each fire charges
        // this many instructions to the step budget, so the same ceiling holds, and it stays tight enough
        // for the allocation backstop to catch a doubling concat bomb.
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

            LuaValue nativeYield = coro["yield"];
            if (nativeYield.Type == LuaValueType.Function)
            {
                NativeYields.Add(state, nativeYield.Read<LuaFunction>());
                coro["yield"] = new LuaFunction("coroutine.yield", GuardedYield);
            }

            LuaValue nativeResume = coro["resume"];
            if (nativeResume.Type != LuaValueType.Function)
            {
                return;
            }

            // Arm a per-resume step/time/alloc guard hook on the coroutine's child LuaState — the native
            // library never installs the execution-guard hook there. WHY liveResumeBudget is captured
            // (not read once here): it is re-read on every resume inside ResumeWithPerResumeGuard so a
            // later ScriptContext:SetTimeout reaches a raw coroutine created before the call too — see
            // RawCoroutineResumeStepBudgetMultiplier. WHY the qualified name (audit C3-07): a call from host code
            // (pcall(coroutine.resume)) names the function by it in its bad-argument line, as the native one does.
            coro["resume"] = new LuaFunction("coroutine.resume",
                (ctx, ct) => GuardedCoroutineResume(ctx, ct, nativeResume, liveResumeBudget));
        }

        // WHY (audit C3-07): Lua-CSharp's yield finds the frame that called it at the second-to-last place of the
        // thread's call stack, and a coroutine whose body IS coroutine.yield has only the one frame - as has one whose
        // body ends in `return coroutine.yield(...)`, since a tail call replaces the body's frame - so resuming either
        // handed the script "Index was outside the bounds of the array." or a nil error. Luau runs both; Lua-CSharp
        // cannot suspend a yield with no Lua function below it, so the resume fails with the call-boundary line every
        // other yield from C# code gets, naming the fix. Anything else runs the native yield in place, with this
        // call's own context, exactly as before.
        /// <summary><c>coroutine.yield</c>: the native yield, refusing one with no Lua function below it.</summary>
        private static System.Threading.Tasks.ValueTask<int> GuardedYield(LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            LuaState state = ctx.State;
            if (state.IsCoroutine && state.GetCallStackFrames().Length < 2)
            {
                throw new LuaRuntimeException(state, (LuaValue)YieldWithoutLuaCallerMessage);
            }

            return state.YieldAsync(ctx, ct);
        }

        /// <summary>What resuming a coroutine whose body is, or tail-calls, <c>coroutine.yield</c> fails with.</summary>
        internal const string YieldWithoutLuaCallerMessage = YieldAcrossCallBoundaryMessage
            + " (coroutine.yield has no Lua function below it: it is the coroutine's body, or the body ends in "
            + "'return coroutine.yield(...)'; write 'local r = coroutine.yield(...) return r')";

        // WHY captured while the environment is built and never read from it again (audit B3-07): host code that
        // suspends a thread of its own - the signal runner's loop - must park it with the real yield, and the
        // environment is the mod's to change. Signal runners are built lazily, after mod code ran, and one built
        // after `coroutine.yield = function() end` looped inside its body until every Heartbeat handler was cut
        // by its resume budget at the runner's own line. Keyed by the state's main thread, so any of its threads
        // finds it.
        private static readonly ConditionalWeakTable<LuaState, LuaFunction> NativeYields = new();

        /// <summary>
        /// The <c>coroutine.yield</c> the coroutine library had when <see cref="Create"/> built the environment of
        /// <paramref name="state"/>'s global state, whatever a script has assigned since; nil for a state
        /// <see cref="Create"/> did not build.
        /// </summary>
        internal static LuaValue NativeCoroutineYield(LuaState state)
        {
            return state != null && NativeYields.TryGetValue(state.MainThread, out LuaFunction yield)
                ? new LuaValue(yield)
                : LuaValue.Nil;
        }

        // WHY one trip source per coroutine for its whole life, and NOT linked to the resumer's token:
        // Lua-CSharp runs a coroutine body with the token of its FIRST resume for as long as it lives (the
        // body's VM contexts capture it; a later resume only wakes them), so a trip on any later resume has to
        // cancel that very token for the body to see it (LuaCsCoroutineHandle.CancelGuardedRun). A link to the
        // first resumer would outlive it: its guard's pooled source, cancelled by a trip in some later run,
        // would then kill a healthy coroutine that merely started under it. A host cancellation of the resumer
        // still ends the run: the child is held to its own per-resume budget and to what the resumer's run has
        // left, and the resumer's token is checked the moment the resume returns.
        private static readonly ConditionalWeakTable<LuaState, RawCoroutine> RawCoroutines = new();

        private static readonly ConditionalWeakTable<LuaState, RawCoroutine>.CreateValueCallback NewRawCoroutine =
            _ => new RawCoroutine();

        /// <summary>What the guarded <c>coroutine.resume</c> keeps about one mod-created coroutine.</summary>
        private sealed class RawCoroutine
        {
            /// <summary>The source a trip cancels; its token is the one the body runs with for life.</summary>
            public readonly CancellationTokenSource TripSource = new();

            /// <summary>
            /// The hook armed around every resume of this coroutine, built at its first resume and re-armed at
            /// each one after; it holds no reference to the coroutine's thread.
            /// </summary>
            public RawResumeHook Hook;
        }

        /// <summary>
        /// The raw <c>coroutine.resume</c> of the coroutine <paramref name="state"/> is, while that resume
        /// executes; null otherwise. See <see cref="LuaCsGuardedRun.FindExecuting"/>.
        /// </summary>
        internal static LuaCsGuardedRun ExecutingRawRunOn(LuaState state)
        {
            return RawCoroutines.TryGetValue(state, out RawCoroutine raw) && raw.Hook != null
                                                                          && raw.Hook.IsExecuting
                ? raw.Hook
                : null;
        }

        private static System.Threading.Tasks.ValueTask<int> GuardedCoroutineResume(
            LuaFunctionExecutionContext ctx, CancellationToken ct, LuaValue nativeResume,
            LuaCsCoroutineBudgetSettings liveResumeBudget)
        {
            // WHY read here: the native resume's own check of its first argument raises "(thread expected, got
            // nil))" (see ReadArgument).
            ReadArgument<LuaState>(ctx, 0, "thread");
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

            RawResumeHook hook = null;
            if (canResume)
            {
                // WHY the resumer's run is the innermost run executing on this OS thread (see LuaCsGuardedRun): the
                // body runs inside it, where its own hook cannot fire.
                LuaCsGuardedRun enclosing = LuaCsGuardedRun.InnermostExecuting();

                // WHY the resumed body continues the resumer's count (Luau's lua_resume does the same): each
                // nested resume is one more VM run on the .NET stack, and a chain of them unwinds like any other
                // nesting (see MaxCCallDepth). Refused like Luau's, with the coroutine left suspended. The count of
                // the thread calling resume and of the run it executes in are the same unless host code ran this
                // thread's Lua outside a run of its own; the larger one is the chain on the native stack.
                int callerDepth = Math.Max(CCallDepthOf(callerState), CCallDepthOf(enclosing?.Thread));
                if (callerDepth + HeavyCallLevels > MaxCCallDepth)
                {
                    return new[] { new LuaValue(false), (LuaValue)CStackOverflowText("coroutine.resume") };
                }

                CCallDepths.GetValue(coroutineState, NewCCallDepth).Base = callerDepth + HeavyCallLevels;

                RawCoroutine raw = RawCoroutines.GetValue(coroutineState, NewRawCoroutine);
                hook = raw.Hook ??= new RawResumeHook(raw.TripSource);
                // WHY read live here, at the moment of the ACTUAL resume, not cached from an earlier
                // Create() call: this is the fix for the coroutine.resume escape hatch — a raw coroutine
                // created before a host tightened ScriptContext:SetTimeout must still be bound by the NEW
                // value on its next resume, exactly like the C#-managed LuaCsCoroutineHandle already is.
                long stepBudget = (long)liveResumeBudget.BudgetPerResume * RawCoroutineResumeStepBudgetMultiplier;
                long timeoutMs = (long)liveResumeBudget.ResumeTimeoutMs * RawCoroutineResumeTimeoutMultiplier;
                // WHY the resumer's run: the body runs inside it, where the resumer's own hook cannot fire, so the
                // body is nested in that run and may only spend what it has left (see LuaCsGuardedRun) - a chain
                // of coroutine.create levels used to get a fresh allowance each (audit B3-02). The body's own
                // allocation budget is the resumer's too: it is part of the run that resumes it, so it gets that
                // run's budget - the mod's HandlerMaxAllocatedBytes for its handlers, task threads and main
                // chunk. A fixed 256 MB let a mod held to 16 MB keep 80 MB alive inside coroutine.create (audit
                // A2-09).
                long allocationBudget = enclosing?.AllocationBudgetBytes
                                        ?? LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;
                hook.Arm(stepBudget, timeoutMs, allocationBudget, enclosing, coroutineState);

                try
                {
                    coroutineState.SetHook(hook.Function, string.Empty, CoroutineHookInstructionBatch);
                    armed = true;
                }
                catch
                {
                    armed = false;
                }

                if (!armed)
                {
                    hook.End();
                    hook = null;
                }
            }

            // WHY the resumer's status is put back (audit B3-04): the native resume marks the thread that resumes
            // Normal while the coroutine runs and Running when it returns, whatever it was before. A resumer inside
            // a fenced library call (see CallFencedAsync) is Normal on purpose - that is the fence - and a nested
            // coroutine.resume in a __tostring or gsub callback lifted it, so a coroutine.yield or task.wait later
            // in the same callback suspended the thread under the library call and lost its error.
            bool resumerFenced = callerState.IsCoroutine && callerState.GetStatus() == LuaThreadStatus.Normal;
            try
            {
                LuaValue[] results = callerState.CallAsync(nativeResume, resumeArgs.AsSpan(),
                    armed ? hook.RunToken : ct).GetAwaiter().GetResult();
                return hook == null || !hook.HasTripped ? results : EndTrippedResume(coroutineState, hook.TripError);
            }
            catch (Exception) when (hook != null && hook.HasTripped)
            {
                return EndTrippedResume(coroutineState, hook.TripError);
            }
            finally
            {
                if (resumerFenced)
                {
                    callerState.UnsafeSetStatus(LuaThreadStatus.Normal);
                }

                if (armed)
                {
                    hook.End();
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
        /// The per-resume guard of one raw coroutine, re-armed at every resume: steps, wall clock and the
        /// allocation backstop, the same three budgets <see cref="LuaCsExecutionGuard"/> enforces. Built once per
        /// coroutine, so a resume allocates no hook.
        /// </summary>
        private sealed class RawResumeHook : LuaCsGuardedRun
        {
            /// <summary>The Lua-CSharp hook function armed on the coroutine's thread.</summary>
            public readonly LuaFunction Function;

            private readonly CancellationTokenSource _runSource;
            private long _steps;
            private long _stepBudget;
            private long _stepLimit;
            private long _timeoutMs;
            private long _deadline;
            private LuaCsAllocationBudget _allocation;
            private LuaCsHostFunctionException _tripError;

            /// <param name="runSource">The coroutine's trip source, whose token its body runs with for life.</param>
            public RawResumeHook(CancellationTokenSource runSource)
            {
                _runSource = runSource;
                Function = new LuaFunction("coreai_coroutine_guard", Hook);
            }

            /// <summary>The token the resume runs with: the coroutine's trip source's.</summary>
            public CancellationToken RunToken => _runSource.Token;

            /// <summary>True once a budget of the current resume tripped; the resume can then only end with it.</summary>
            public bool HasTripped => _tripError != null;

            /// <summary>The trip of the current resume, or null.</summary>
            public LuaCsHostFunctionException TripError => _tripError;

            /// <inheritdoc />
            internal override long RemainingSteps => _stepLimit - _steps;

            /// <inheritdoc />
            internal override long DeadlineTimestamp => _deadline;

            /// <inheritdoc />
            internal override long AllocationLineBytes => _allocation.LineBytes;

            /// <inheritdoc />
            internal override long AllocationBudgetBytes => _allocation.BudgetBytes;

            /// <inheritdoc />
            protected override LuaCsHostFunctionException RecordedTrip => _tripError;

            /// <inheritdoc />
            protected override bool AllocationLineIsLent => _allocation.LineIsCeiling;

            /// <summary>
            /// Arms a fresh allowance for one resume of <paramref name="thread"/>: <paramref name="stepBudget"/> steps,
            /// <paramref name="timeoutMs"/> of wall clock and <paramref name="allocationBudgetBytes"/> of live heap
            /// growth, each lowered to what <paramref name="enclosing"/> (the run executing on the resumer, or
            /// null) has left.
            /// </summary>
            public void Arm(long stepBudget, long timeoutMs, long allocationBudgetBytes, LuaCsGuardedRun enclosing,
                LuaState thread)
            {
                _steps = 0;
                _tripError = null;
                _stepBudget = stepBudget;
                _stepLimit = stepBudget;
                _timeoutMs = timeoutMs;
                // WHY: raw timestamp + a precomputed deadline, not a Stopwatch instance — same
                // allocation-avoidance reason as LuaCsExecutionGuard.GuardHook (see that type for detail).
                _deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                            + timeoutMs * System.Diagnostics.Stopwatch.Frequency / 1000;
                BeginRun(enclosing, thread, ref _stepLimit, ref _deadline);
                // WHY: the SAME allocation backstop the execution guard uses, on the coroutine's child
                // state - step/time caps do not catch a doubling concat bomb, which is ordinary VM opcodes
                // with no library call site to cap. Shared as one type rather than a second hand-copied
                // check, because the copy here kept its own broken rule after the guard's was fixed: see
                // LuaCsAllocationBudget for why a sampled reading may only raise a suspicion.
                _allocation.ResetNested(allocationBudgetBytes, CeilingOf(enclosing));
            }

            /// <summary>
            /// Ends the resume: its steps are charged to the run it was nested in, if any, as unreported, since
            /// nothing else records a raw coroutine's steps.
            /// </summary>
            public void End()
            {
                EndRun(_steps, false);
            }

            /// <inheritdoc />
            protected override void ChargeNestedSteps(long steps)
            {
                _stepLimit -= steps;
            }

            // WHY only the marker text and no dedicated exception type: the resumer receives the trip as the
            // error value of `ok, err = coroutine.resume(co)`, a string, so a CLR type could never be observed
            // across that boundary.
            /// <inheritdoc />
            protected override LuaCsHostFunctionException CreateOwnTrip(LuaCsGuardTripKind kind, LuaState where)
            {
                string message;
                switch (kind)
                {
                    case LuaCsGuardTripKind.Timeout:
                        message = $"{SandboxLinePrefix}Lua coroutine resume exceeded {_timeoutMs} ms.";
                        break;
                    case LuaCsGuardTripKind.Memory:
                        message = $"{SandboxLinePrefix}{LuaCsExecutionGuard.MemoryBudgetTripMarker} "
                                  + $"({_allocation.BudgetBytes} bytes)";
                        break;
                    default:
                        message = $"{SandboxLinePrefix}EXCEEDED_COROUTINE_STEP_BUDGET ({_stepBudget})";
                        break;
                }

                return LuaCsCoroutineHandle.CreatePendingBudgetTrip(where, message);
            }

            /// <inheritdoc />
            protected override void RecordNestedTrip(LuaCsGuardTripKind kind, LuaCsHostFunctionException trip,
                bool ownLimit)
            {
                _tripError = trip;
            }

            /// <inheritdoc />
            protected override void CancelRun()
            {
                LuaCsCoroutineHandle.CancelGuardedRun(_runSource, CancellationToken.None);
            }

            private System.Threading.Tasks.ValueTask<int> Hook(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                if (_tripError == null)
                {
                    _steps += CoroutineHookInstructionBatch;
                    if (_steps > _stepLimit)
                    {
                        RecordTrip(ctx, LuaCsGuardTripKind.Steps, false);
                    }
                    else if (System.Diagnostics.Stopwatch.GetTimestamp() > _deadline)
                    {
                        RecordTrip(ctx, LuaCsGuardTripKind.Timeout, false);
                    }
                    else if (_allocation.IsExceeded())
                    {
                        RecordTrip(ctx, LuaCsGuardTripKind.Memory, _allocation.CeilingExceeded);
                    }
                    else
                    {
                        return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
                    }
                }

                if (LuaCsCoroutineHandle.CancelGuardedRun(_runSource, ct))
                {
                    return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
                }

                throw LuaCsCoroutineHandle.ForeignContextTrip(_tripError);
            }

            private void RecordTrip(LuaFunctionExecutionContext ctx, LuaCsGuardTripKind kind, bool ceilingExceeded)
            {
                _tripError = TripOfLender(kind, ceilingExceeded, ctx.State) ?? CreateOwnTrip(kind, ctx.State);
            }
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
            // WHY qualified names, as the native library gives them (audit C3-07): a call from host code
            // (pcall(string.rep)) names the function by its own name in the bad-argument line.
            stringLib["rep"] = new LuaFunction("string.rep", CappedStringRep);

            // WHY: the native find/match/gmatch/gsub run a backtracking matcher inside ONE VM instruction,
            // where no guard hook can interrupt it, and gsub builds its result with no size limit. They
            // are replaced by the budgeted port below. The string metatable's __index reads this table,
            // so method calls such as s:find(...) reach the replacements too.
            stringLib["find"] = new LuaFunction("string.find", BudgetedFind);
            stringLib["match"] = new LuaFunction("string.match", BudgetedMatch);
            stringLib["gmatch"] = new LuaFunction("string.gmatch", BudgetedGMatch);
            stringLib["gsub"] = new LuaFunction("string.gsub", BudgetedGSub);

            LuaValue originalFormat = stringLib["format"];
            // WHY captured here, before any mod runs: string.format's %s converts objects through the
            // library's own tostring, and a mod may later replace the global.
            LuaValue originalToString = state.Environment["tostring"];
            if (originalFormat.Type == LuaValueType.Function)
            {
                stringLib["format"] = new LuaFunction("string.format",
                    (ctx, ct) => CappedStringFormat(ctx, ct, originalFormat, originalToString));
            }

            // WHY: table.concat(list [, sep [, i [, j]]]) allocates its whole result in one VM instruction,
            // the same allocation-bomb class as string.rep/string.format above. Replace it with a
            // version that aborts once the running result would exceed MaxTableConcatLength.
            LuaValue tableLibValue = state.Environment["table"];
            if (tableLibValue.Type == LuaValueType.Table)
            {
                LuaTable tableLib = tableLibValue.Read<LuaTable>();
                tableLib["concat"] = new LuaFunction("table.concat", CappedTableConcat);
            }
        }

        // WHY: these native functions call Lua themselves - tostring and print a __tostring, pairs and ipairs a
        // __pairs/__ipairs, table.sort its comparator or __lt - so each is a point where Lua re-enters through
        // the .NET stack and must be counted against MaxCCallDepth. Runs after StripRiskyGlobals so that
        // string.format keeps the native tostring it counts itself. A call that cannot reach Lua keeps the
        // native result without the extra frame: tostring of a number and pairs over a plain table stay as
        // cheap and allocation-free as before. A counted call runs the native function as this call itself (see
        // CallNativeInPlace), never through CallAsync, which copied the arguments and built a result array on
        // every tostring of an object and every table.sort: 2.4 MB per 20,000 calls against 1 KB natively, steady
        // garbage for WebGL's non-moving collector (audit B3-05).
        // TODO: __concat is the one metamethod Lua-CSharp runs as a nested VM call of its own (the async
        // Concat path, 2,880 B of native stack a level), so `mt.__concat = function(a, b) return a .. b end;
        // local _ = o .. 'x'` still unwinds quadratically with no hook firing: 2.6 s at a mod-imposed depth of
        // 1,000 and 24-28 s unbounded under a 10 s budget. It cannot be counted as a catchable error from here:
        // no sandbox code runs where the VM enters the metamethod, and the only code that runs inside it, a
        // count hook, must never throw (a throw leaves Lua-CSharp's in-hook flag set and disarms every guard on
        // the thread), so a hook could only END the run. The smallest in-sandbox alternative: each hook, once
        // the call-stack frame count has grown, counts the frames whose caller instruction is a CONCAT
        // (CallStackFrame.CallerInstructionIndex into the caller's LuaClosure.Proto.Code, all public) and trips
        // the run past MaxCCallDepth; it misses a metamethod that tail-calls a helper, whose frame's
        // CallerInstructionIndex points into the replaced function, and the tail-call flag is internal. The
        // complete fix is Lua.dll counting its own metamethod calls (upstream).
        private static void CountCallsBackIntoLua(LuaState state)
        {
            LuaTable environment = state.Environment;
            LuaValue[] stringMeta = CallNativeOnce(state, environment["getmetatable"], string.Empty);
            LuaTable stringMetatable = stringMeta != null && stringMeta.Length > 0
                                                       && stringMeta[0].Type == LuaValueType.Table
                ? stringMeta[0].Read<LuaTable>()
                : null;

            if (environment["tostring"].Type == LuaValueType.Function)
            {
                environment["tostring"] = new LuaFunction("tostring", (ctx, ct) =>
                {
                    if (ctx.ArgumentCount > 0 && !MayRunToString(ctx.GetArgument(0), stringMetatable))
                    {
                        return new System.Threading.Tasks.ValueTask<int>(ctx.Return(ctx.GetArgument(0).ToString()));
                    }

                    return CallNativeInPlace(ctx, ct, NativeToString, "tostring");
                });
            }

            if (environment["print"].Type == LuaValueType.Function)
            {
                environment["print"] = new LuaFunction("print",
                    (ctx, ct) => CallNativeInPlace(ctx, ct, NativePrint, "print"));
            }

            WrapIterationFactory(state, environment, "pairs", Metamethods.Pairs, LuaValue.Nil, NativePairs);
            WrapIterationFactory(state, environment, "ipairs", Metamethods.IPairs, 0d, NativeIPairs);

            // WHY pcall and xpcall too (audit B3-01): each runs the protected function as one more VM run on the
            // .NET stack, so `local function f() pcall(f) end` nested until Lua-CSharp's own stack limit, and a
            // budget trip deep inside unwound through every level with no hook able to fire - one Heartbeat
            // handler held its frame 5.7 s under a 500 ms budget, and a 200 ms guard ended after 71 s.
            if (environment["pcall"].Type == LuaValueType.Function)
            {
                environment["pcall"] = new LuaFunction("pcall", CountedPCall);
            }

            if (environment["xpcall"].Type == LuaValueType.Function)
            {
                environment["xpcall"] = new LuaFunction("xpcall", CountedXPCall);
            }

            LuaValue tableLibValue = environment["table"];
            if (tableLibValue.Type == LuaValueType.Table)
            {
                LuaTable tableLib = tableLibValue.Read<LuaTable>();
                if (tableLib["sort"].Type == LuaValueType.Function)
                {
                    // WHY named like the native function: a call from host code (pcall(table.sort, nil)) names the
                    // function by this name in its bad-argument line, and the native one is "table.sort".
                    tableLib["sort"] = new LuaFunction("table.sort",
                        (ctx, ct) => CallNativeInPlace(ctx, ct, NativeSort, "table.sort"));
                }
            }
        }

        /// <summary>
        /// <c>pcall</c> as one counted call back into Lua (see <see cref="MaxCCallDepth"/>). Below the limit the
        /// native pcall runs with this call's own context, on the caller's stack, so its results, error values and
        /// error positions are exactly the native ones and the ordinary case allocates nothing; a budget trip, which
        /// travels as a cancellation, passes through untouched. At the limit it returns <c>false</c> and the C-stack
        /// line without calling, the way Luau's pcall fails once <c>LUAI_MAXCCALLS</c> calls are open.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> CountedPCall(LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            CCallDepth depth = CCallDepths.GetValue(ctx.State, NewCCallDepth);
            if (depth.Base + depth.Local + LightCallLevels > MaxCCallDepth)
            {
                return new System.Threading.Tasks.ValueTask<int>(ctx.Return(false, CStackOverflowText("pcall")));
            }

            depth.Local += LightCallLevels;
            System.Threading.Tasks.ValueTask<int> call;
            try
            {
                call = BasicLibrary.Instance.PCall(ctx, ct);
            }
            catch
            {
                ExitCCall(depth, LightCallLevels);
                throw;
            }

            return ExitWhenComplete(call, depth, LightCallLevels, null, null);
        }

        /// <summary>
        /// <c>xpcall</c> as one counted call back into Lua, like <see cref="CountedPCall"/>: below the limit the
        /// native xpcall runs with this call's own context, and its message handler runs inside that same counted
        /// call. At the limit the handler is given the C-stack line (see <see cref="XPCallPastTheLimit"/>), and after
        /// the engine's own value-stack overflow it is given that line (see <see cref="RunHandlerAfterOverflow"/>).
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> CountedXPCall(LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            CCallDepth depth = CCallDepths.GetValue(ctx.State, NewCCallDepth);
            int open = depth.Base + depth.Local;
            if (open + LightCallLevels > MaxCCallDepth)
            {
                return XPCallPastTheLimit(ctx, ct, depth, open);
            }

            // WHY read before the call: the native xpcall moves the protected function over the handler's slot, and
            // the frame base is computed from the stack top, which an overflow leaves near the engine's limit.
            int frameBase = ctx.FrameBase;
            int frameCount = ctx.State.CallStackFrameCount;
            LuaValue handler = ctx.ArgumentCount > 1 ? ctx.GetArgument(1) : LuaValue.Nil;
            depth.Local += LightCallLevels;
            System.Threading.Tasks.ValueTask<int> call;
            try
            {
                call = BasicLibrary.Instance.XPCall(ctx, ct);
            }
            catch
            {
                ExitCCall(depth, LightCallLevels);
                throw;
            }

            if (!call.IsCompleted || !call.IsFaulted)
            {
                return ExitWhenComplete(call, depth, LightCallLevels, null, null);
            }

            Exception fault = FaultOf(call);
            if (IsEngineStackOverflow(fault) && !ct.IsCancellationRequested
                                             && handler.Type == LuaValueType.Function
                                             && ctx.State.CallStackFrameCount == frameCount)
            {
                ctx.State.Stack.PopUntil(frameBase + ctx.ArgumentCount);
                return RunHandlerAfterOverflow(ctx, handler.Read<LuaFunction>(), depth, ct);
            }

            ExitCCall(depth, LightCallLevels);
            return new System.Threading.Tasks.ValueTask<int>(
                System.Threading.Tasks.Task.FromException<int>(fault));
        }

        /// <summary>What <c>xpcall</c> returns when even its message handler has no room left to run in (Luau's text).</summary>
        private const string ErrorInErrorHandlingMessage = "error in error handling";

        /// <summary>The error value an <c>xpcall</c> handler gets for the engine's own value-stack overflow.</summary>
        private const string EngineStackOverflowMessage = "stack overflow";

        // WHY the handler still runs at the limit (Luau parity, chosen over returning the line directly): Luau
        // raises "C stack overflow" inside the protected call once LUAI_MAXCCALLS calls are open, xpcall hands that
        // error to its message handler, which gets an eighth more room to run in, and past that Luau gives up with
        // "error in error handling". The handler here is counted like any call back into Lua, so a handler that
        // calls xpcall again ends there too.
        /// <summary>
        /// <c>xpcall</c> once <see cref="MaxCCallDepth"/> calls are open: the protected function is not called,
        /// and <c>false</c> plus what the message handler returns for the C-stack line is the result.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> XPCallPastTheLimit(LuaFunctionExecutionContext ctx,
            CancellationToken ct, CCallDepth depth, int open)
        {
            // WHY read first, in this order: native xpcall's own checks of its two arguments, with the same errors.
            ctx.GetArgument(0);
            LuaFunction handler = ctx.GetArgument<LuaFunction>(1);
            if (open >= MaxCCallDepth + MaxCCallDepth / 8)
            {
                return new System.Threading.Tasks.ValueTask<int>(ctx.Return(false, ErrorInErrorHandlingMessage));
            }

            // WHY: the check native xpcall makes before it runs a handler, so no handler ever runs after a trip.
            ct.ThrowIfCancellationRequested();
            depth.Local += LightCallLevels;
            return RunHandlerPastTheLimit(ctx, handler, depth, ct, CStackOverflowText("xpcall"), false);
        }

        // WHY (audit C3-04): plain Lua recursion fills the engine's value stack in one VM run, and native xpcall then
        // runs its handler on top of that full stack, where the handler's own frame overflows it again, so the
        // overflow escaped the xpcall and ended the run while pcall of the same function returned false. The stack
        // is back at the xpcall's own frame by the time the fault reaches here (the frames above it are gone), so
        // the handler runs where it would have run for any other error, with the engine's line, like Luau's. A
        // handler that overflows the stack itself ends as Luau's does, with "error in error handling".
        /// <summary>
        /// <c>xpcall</c> after the protected function overflowed the engine's value stack: <c>false</c> plus what
        /// <paramref name="handler"/> returns for <see cref="EngineStackOverflowMessage"/>.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> RunHandlerAfterOverflow(LuaFunctionExecutionContext ctx,
            LuaFunction handler, CCallDepth depth, CancellationToken ct)
        {
            return RunHandlerPastTheLimit(ctx, handler, depth, ct, EngineStackOverflowMessage, true);
        }

        private static async System.Threading.Tasks.ValueTask<int> RunHandlerPastTheLimit(
            LuaFunctionExecutionContext ctx, LuaFunction handler, CCallDepth depth, CancellationToken ct,
            string error, bool overflowIsErrorInHandling)
        {
            int top = ctx.State.Stack.Count;
            int frameCount = ctx.State.CallStackFrameCount;
            try
            {
                ctx.State.Stack.Push(error);
                int count = await ctx.State.RunAsync(handler, 1, ctx.ReturnFrameBase + 1, ct);
                ctx.State.Stack[ctx.ReturnFrameBase] = false;
                return count + 1;
            }
            catch (Exception ex) when (overflowIsErrorInHandling && IsEngineStackOverflow(ex)
                                                                 && !ct.IsCancellationRequested
                                                                 && ctx.State.CallStackFrameCount == frameCount)
            {
                ctx.State.Stack.PopUntil(top);
                return ctx.Return(false, ErrorInErrorHandlingMessage);
            }
            finally
            {
                ExitCCall(depth, LightCallLevels);
            }
        }

        /// <summary>The exception a completed, faulted <paramref name="call"/> ended with; consumes it.</summary>
        private static Exception FaultOf(System.Threading.Tasks.ValueTask<int> call)
        {
            try
            {
                // WHY: the call has completed, so this only rethrows its exception, never waits.
                call.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return ex;
            }

            return new InvalidOperationException("a faulted call completed without an exception");
        }

        // WHY by the type's name: Lua-CSharp's overflow exception is internal, and it reaches a caller either as
        // itself (raised while a native function pushed onto the full stack) or as the inner exception of the
        // LuaRuntimeException the VM wraps it in.
        /// <summary>True when <paramref name="ex"/> is, or wraps, the engine's value-stack overflow.</summary>
        private static bool IsEngineStackOverflow(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (string.Equals(e.GetType().FullName, "Lua.LuaStackOverflowException", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Closes the counted call of <paramref name="levels"/> <paramref name="depth"/> holds, and the yield fence
        /// on <paramref name="fencedState"/> when that is not null, once <paramref name="call"/> completes: at once
        /// when it already has, which allocates nothing, else when it finishes after a yield or an awaited frame inside
        /// it, through a pooled continuation (see <see cref="CountedCallCompletion"/>). A fenced call that failed
        /// because a Lua function it ran yielded fails with the call-boundary line naming
        /// <paramref name="boundary"/> instead.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> ExitWhenComplete(
            System.Threading.Tasks.ValueTask<int> call, CCallDepth depth, int levels, LuaState fencedState,
            string boundary)
        {
            if (!call.IsCompleted)
            {
                return CountedCallCompletion.WhenDone(call, depth, levels, fencedState, boundary);
            }

            EndYieldFence(fencedState, fencedState != null);
            ExitCCall(depth, levels);
            if (fencedState == null || !call.IsFaulted)
            {
                return call;
            }

            Exception fault = FaultOf(call);
            return new System.Threading.Tasks.ValueTask<int>(System.Threading.Tasks.Task.FromException<int>(
                fault is LuaRuntimeException runtimeError && IsFencedYield(runtimeError)
                    ? YieldAcrossBoundary(fencedState, boundary)
                    : fault));
        }

        // WHY a pooled continuation and not an async method (audit C3-05): an async method that awaits a call
        // suspended by a yield boxes its state machine, about 136 bytes on every suspension of every pcall, tostring
        // or sort whose function yields - `pcall(function() ... task.wait() ... end)` in a loop is ordinary Roblox
        // code, and each suspension was steady garbage for WebGL's non-moving collector. WHY the scheduling context
        // is dropped, as Lua-CSharp's own sources drop it: a continuation posted to Unity's synchronization context
        // would complete a coroutine resume a frame later, on the very thread a resume waits on synchronously.
        /// <summary>
        /// Completes a counted call that suspended: closes its levels (and its yield fence) when the call completes,
        /// then hands the call's outcome to whoever awaits it, and goes back to the pool once that is read.
        /// </summary>
        private sealed class CountedCallCompletion : IValueTaskSource<int>
        {
            [ThreadStatic]
            private static System.Collections.Generic.Stack<CountedCallCompletion> _pool;

            private readonly Action _onCallCompleted;
            private ManualResetValueTaskSourceCore<int> _core;
            private System.Threading.Tasks.ValueTask<int> _call;
            private CCallDepth _depth;
            private int _levels;
            private LuaState _fencedState;
            private string _boundary;

            private CountedCallCompletion()
            {
                _onCallCompleted = OnCallCompleted;
            }

            /// <summary>The outcome of <paramref name="call"/> once it completes, with its levels closed then.</summary>
            public static System.Threading.Tasks.ValueTask<int> WhenDone(System.Threading.Tasks.ValueTask<int> call,
                CCallDepth depth, int levels, LuaState fencedState, string boundary)
            {
                System.Collections.Generic.Stack<CountedCallCompletion> pool = _pool;
                CountedCallCompletion completion = pool != null && pool.Count > 0
                    ? pool.Pop()
                    : new CountedCallCompletion();
                completion._call = call;
                completion._depth = depth;
                completion._levels = levels;
                completion._fencedState = fencedState;
                completion._boundary = boundary;
                short version = completion._core.Version;
                call.GetAwaiter().UnsafeOnCompleted(completion._onCallCompleted);
                return new System.Threading.Tasks.ValueTask<int>(completion, version);
            }

            private void OnCallCompleted()
            {
                System.Threading.Tasks.ValueTask<int> call = _call;
                LuaState fencedState = _fencedState;
                _call = default;
                EndYieldFence(fencedState, fencedState != null);
                ExitCCall(_depth, _levels);
                int result;
                try
                {
                    result = call.GetAwaiter().GetResult();
                }
                catch (LuaRuntimeException ex) when (fencedState != null && IsFencedYield(ex))
                {
                    _core.SetException(YieldAcrossBoundary(fencedState, _boundary));
                    return;
                }
                catch (Exception ex)
                {
                    _core.SetException(ex);
                    return;
                }

                _core.SetResult(result);
            }

            /// <inheritdoc />
            public int GetResult(short token)
            {
                try
                {
                    return _core.GetResult(token);
                }
                finally
                {
                    _core.Reset();
                    _depth = null;
                    _fencedState = null;
                    _boundary = null;
                    (_pool ??= new System.Collections.Generic.Stack<CountedCallCompletion>()).Push(this);
                }
            }

            /// <inheritdoc />
            public ValueTaskSourceStatus GetStatus(short token)
            {
                return _core.GetStatus(token);
            }

            /// <inheritdoc />
            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token,
                    flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
            }
        }

        /// <summary>A native library function, called with the context of the call it runs as.</summary>
        private delegate System.Threading.Tasks.ValueTask<int> NativeLibraryFunction(LuaFunctionExecutionContext ctx,
            CancellationToken ct);

        // WHY the native functions are held as delegates of the library singletons rather than read back from the
        // environment: calling one directly with the caller's own context is what keeps a counted call as cheap as
        // the native one (see CallNativeInPlace), and a LuaFunction's own delegate is not reachable from outside
        // Lua.dll. They are the very functions OpenBasicLibrary and OpenTableLibrary install.
        private static readonly NativeLibraryFunction NativeToString = BasicLibrary.Instance.ToString;

        private static readonly NativeLibraryFunction NativePrint = BasicLibrary.Instance.Print;

        private static readonly NativeLibraryFunction NativePairs = BasicLibrary.Instance.Pairs;

        private static readonly NativeLibraryFunction NativeIPairs = BasicLibrary.Instance.IPairs;

        private static readonly NativeLibraryFunction NativeSort = TableLibrary.Instance.Sort;

        // WHY the yield fence here too (audit C3-06, Luau parity): Luau refuses a yield through every C function that
        // calls back into Lua - tostring, print, table.sort, pairs, ipairs, warn - exactly as through string.format
        // and string.gsub, where the sandbox already refused it; a script that yielded through tostring here failed
        // on Roblox. A call that comes back unfinished for any other reason (the execute_lua path awaiting a frame)
        // is awaited with the fence up, as in CallFencedAsync.
        /// <summary>
        /// Runs the native library function <paramref name="native"/> as this call itself, as one counted call back
        /// into Lua of <see cref="HeavyCallLevels"/> (see <see cref="MaxCCallDepth"/>) under the yield fence (see
        /// <see cref="CallFencedAsync"/>): with this call's own context, on the caller's stack, the way
        /// <see cref="CountedPCall"/> runs native pcall. Its results, error values and the function name in its errors
        /// are the native ones, and nothing is copied or allocated for the call. Past the limit it raises the C-stack
        /// line instead; a Lua function it runs that yields fails with <see cref="YieldAcrossCallBoundaryMessage"/>.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> CallNativeInPlace(LuaFunctionExecutionContext ctx,
            CancellationToken ct, NativeLibraryFunction native, string boundary)
        {
            LuaState state = ctx.State;
            CCallDepth depth = EnterCCall(state, boundary, HeavyCallLevels);
            bool fenced = BeginYieldFence(state);
            System.Threading.Tasks.ValueTask<int> call;
            try
            {
                call = native(ctx, ct);
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                EndYieldFence(state, true);
                ExitCCall(depth, HeavyCallLevels);
                throw YieldAcrossBoundary(state, boundary);
            }
            catch
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, HeavyCallLevels);
                throw;
            }

            return ExitWhenComplete(call, depth, HeavyCallLevels, fenced ? state : null, boundary);
        }

        /// <summary>
        /// Replaces <c>pairs</c> or <c>ipairs</c>: a table without the <paramref name="metamethod"/> gets the
        /// native iterator triple directly, anything else goes to the native function as a counted call.
        /// </summary>
        private static void WrapIterationFactory(LuaState state, LuaTable environment, string name,
            string metamethod, LuaValue control, NativeLibraryFunction nativeFunction)
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

                return CallNativeInPlace(ctx, ct, nativeFunction, name);
            });
        }

        // WHY a function, a thread and a light userdata never do: their metatables are per type and only
        // debug.setmetatable, which the sandbox removes, could set one - exactly as for nil, booleans and numbers.
        // WHY a userdata whose __tostring is a host function is still counted, although that function is C#: a
        // script can replace it (getmetatable(part).__tostring = tostring), and a C# function such as the sandbox's
        // own tostring, print or pairs runs Lua again - uncounted, `tostring(part)` would recurse until the native
        // stack gave out.
        /// <summary>
        /// False when native <c>tostring</c> of <paramref name="value"/> runs no Lua: a value of a type no
        /// sandboxed script can give a metatable, or a string, table or userdata whose metatable has no
        /// <c>__tostring</c>.
        /// </summary>
        private static bool MayRunToString(LuaValue value, LuaTable stringMetatable)
        {
            LuaTable metatable;
            switch (value.Type)
            {
                case LuaValueType.String:
                    if (stringMetatable == null)
                    {
                        return true;
                    }

                    metatable = stringMetatable;
                    break;
                case LuaValueType.Table:
                    metatable = value.Read<LuaTable>().Metatable;
                    break;
                case LuaValueType.UserData:
                    metatable = value.TryRead(out ILuaUserData userData) ? userData.Metatable : null;
                    break;
                default:
                    return false;
            }

            return metatable != null && metatable.TryGetValue(Metamethods.ToString, out LuaValue _);
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
        /// <see cref="MaxCCallDepth"/>) under the yield fence (see <see cref="CallFencedAsync"/>): once the limit is
        /// reached it raises the C-stack error instead of calling, and a Lua function it runs that yields fails with
        /// <see cref="YieldAcrossCallBoundaryMessage"/>, as Luau's warn does (audit C3-06). A call that comes back
        /// unfinished (a frame yield of the async guard) stays counted and fenced on this thread until it completes.
        /// Its one caller is <c>warn</c>, which converts its arguments with the script's <c>tostring</c>; the
        /// sandbox's own library functions count their calls in place (see <see cref="CallNativeInPlace"/>). A host
        /// function that runs a script's function recursively must call through here too; the host callbacks that
        /// run one as the body of a thread use plain <c>CallAsync</c> (see <see cref="MaxCCallDepth"/>).
        /// </summary>
        /// <param name="boundary">The library or host function named in the error, e.g. <c>table.sort</c>.</param>
        /// <param name="levels">
        /// The levels the call opens (see <see cref="MaxCCallDepth"/>); <see cref="HeavyCallLevels"/> unless its
        /// native frames were measured smaller.
        /// </param>
        internal static System.Threading.Tasks.ValueTask<LuaValue[]> CallCountedAsync(LuaState state,
            LuaValue function, ReadOnlySpan<LuaValue> arguments, CancellationToken ct, string boundary,
            int levels = HeavyCallLevels)
        {
            CCallDepth depth = EnterCCall(state, boundary, levels);
            bool fenced = BeginYieldFence(state);
            System.Threading.Tasks.ValueTask<LuaValue[]> call;
            try
            {
                call = state.CallAsync(function, arguments, ct);
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                EndYieldFence(state, true);
                ExitCCall(depth, levels);
                throw YieldAcrossBoundary(state, boundary);
            }
            catch
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, levels);
                throw;
            }

            if (!call.IsCompleted)
            {
                return AwaitCounted(call, state, depth, levels, fenced, boundary);
            }

            try
            {
                // WHY: the task is already complete, so this unwraps its result or exception, never waits.
                return new System.Threading.Tasks.ValueTask<LuaValue[]>(call.GetAwaiter().GetResult());
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, levels);
            }
        }

        private static async System.Threading.Tasks.ValueTask<LuaValue[]> AwaitCounted(
            System.Threading.Tasks.ValueTask<LuaValue[]> call, LuaState state, CCallDepth depth, int levels,
            bool fenced, string boundary)
        {
            try
            {
                return await call;
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, levels);
            }
        }

        // WHY not ctx.GetArgument<T>: Lua-CSharp's typed read ends its reason with a closing parenthesis of its own
        // before BadArgument wraps the reason in a pair, so every sandbox function handed the script "bad argument
        // #1 to 'concat' (table expected, got nil))" (audit B3-06). LuaRuntimeException.BadArgument with the bare
        // reason keeps everything else of the native line: the name the caller used and the "calling 'x' on bad
        // self" form of a method call.
        /// <summary>
        /// Argument <paramref name="index"/> read as <typeparamref name="T"/> with exactly the conversions
        /// <c>ctx.GetArgument&lt;T&gt;</c> makes; a missing or unconvertible argument raises Lua's own line, "bad
        /// argument #n to 'name' (<paramref name="expected"/> expected, got <c>type</c>)", with "got no value" for a
        /// missing one.
        /// </summary>
        private static T ReadArgument<T>(LuaFunctionExecutionContext ctx, int index, string expected)
        {
            string reason;
            if (ctx.ArgumentCount > index)
            {
                LuaValue value = ctx.GetArgument(index);
                if (value.TryRead(out T result))
                {
                    return result;
                }

                if ((typeof(T) == typeof(int) || typeof(T) == typeof(long)) && value.Type == LuaValueType.Number)
                {
                    LuaRuntimeException.BadArgumentNumberIsNotInteger(ctx.State, index + 1);
                }

                reason = expected + " expected, got " + value.TypeToString();
            }
            else
            {
                reason = expected + " expected, got no value";
            }

            LuaRuntimeException.BadArgument(ctx.State, index + 1, reason);
            // WHY: BadArgument always throws; this line only satisfies the compiler.
            throw new LuaRuntimeException(ctx.State, (LuaValue)("bad argument #" + (index + 1) + " (" + reason + ")"));
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
            string s = ReadArgument<string>(ctx, 0, "string");
            double countRaw = ReadArgument<double>(ctx, 1, "number");
            string sep = ctx.ArgumentCount >= 3 ? ReadArgument<string>(ctx, 2, "string") : "";

            if (double.IsNaN(countRaw) || countRaw < 1)
            {
                return new System.Threading.Tasks.ValueTask<int>(ctx.Return(""));
            }

            long count = countRaw > MaxStringRepLength ? MaxStringRepLength + 1L : (long)countRaw;
            long total = s.Length * count + sep.Length * (count - 1);
            if (total > MaxStringRepLength)
            {
                throw LibraryRefusal(ctx.State,
                    $"{SandboxLinePrefix}string.rep result would exceed {MaxStringRepLength} chars.");
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
            LuaTable table = ReadArgument<LuaTable>(ctx, 0, "table");
            string sep = ctx.HasArgument(1) ? ReadArgument<string>(ctx, 1, "string") : "";
            long start = ctx.HasArgument(2) ? (long)ReadArgument<double>(ctx, 2, "number") : 1;
            long end = ctx.HasArgument(3) ? (long)ReadArgument<double>(ctx, 3, "number") : table.ArrayLength;

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
                    // WHY TypeToString and not the value's Type: that is the CLR enum, "(Table)" (audit B3-06).
                    throw LibraryRefusal(ctx.State,
                        $"invalid value ({v.TypeToString()}) at index {i} in table for 'concat'");
                }

                if (i != end)
                {
                    sb.Append(sep);
                }

                if (sb.Length > MaxTableConcatLength)
                {
                    throw LibraryRefusal(ctx.State,
                        $"{SandboxLinePrefix}table.concat result would exceed {MaxTableConcatLength} chars.");
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
            return FormatAsync(ctx, ct, ctx.Arguments.ToArray(), originalFormat, originalToString);
        }

        /// <summary>
        /// The body of <see cref="CappedStringFormat"/>, over <paramref name="args"/>, a copy of the call's
        /// arguments; it completes synchronously unless a <c>__tostring</c> it runs awaits a frame.
        /// </summary>
        private static async System.Threading.Tasks.ValueTask<int> FormatAsync(LuaFunctionExecutionContext ctx,
            CancellationToken ct, LuaValue[] args, LuaValue originalFormat, LuaValue originalToString)
        {
            if (args.Length >= 1 && args[0].Type == LuaValueType.String)
            {
                string format = args[0].Read<string>();
                EnsureFormatWidthWithinCap(ctx.State, format);
                await PrepareFormatArgumentsAsync(ctx.State, format, args, originalToString, ct);
            }

            LuaValue[] results = await CallFencedAsync(ctx.State, originalFormat, args, ct, "string.format",
                HeavyCallLevels);
            if (results.Length > 0 && results[0].Type == LuaValueType.String
                                   && results[0].Read<string>().Length > MaxStringFormatResultLength)
            {
                throw ResultTooLong(ctx.State, "string.format", MaxStringFormatResultLength);
            }

            return ctx.Return(results);
        }

        /// <summary>
        /// Walks <paramref name="format"/> the way the native <c>string.format</c> parses it, converts
        /// every <c>%s</c> argument that is not already a string, number, boolean or nil to its string
        /// (in <paramref name="args"/>), and throws once an upper bound of the result exceeds
        /// <see cref="MaxStringFormatResultLength"/>. It stops at the first specifier the native
        /// implementation rejects, because the native call then fails at that point too.
        /// </summary>
        private static async System.Threading.Tasks.ValueTask PrepareFormatArgumentsAsync(LuaState state,
            string format, LuaValue[] args, LuaValue toStringFunction, CancellationToken ct)
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
                            LuaValue[] converted = await CallFencedAsync(state, toStringFunction, new[] { arg }, ct,
                                "string.format", HeavyCallLevels);
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
            return LibraryRefusal(state, $"{SandboxLinePrefix}{function} result would exceed {cap} chars.");
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
        /// Calls <paramref name="function"/> from a sandbox library function, as one counted call of
        /// <paramref name="levels"/> (see <see cref="MaxCCallDepth"/>), under the yield fence: the running
        /// coroutine is marked non-running for the duration, so Lua-CSharp's <c>coroutine.yield</c> fails BEFORE it
        /// signals the resumer and the call raises <see cref="YieldAcrossCallBoundaryMessage"/> instead. A call
        /// that comes back unfinished is awaited, never waited on; it completes synchronously otherwise.
        /// </summary>
        /// <remarks>
        /// WHY the fence: a yield that got through used to hand the resumer a "suspended" result while this frame
        /// kept running the thread, after which the caller saw "Operation is not valid due to the current state
        /// of the object." and every later yield of that thread failed. Luau refuses the same yield ("attempt to
        /// yield across metamethod/C-call boundary").
        /// <para>
        /// WHY an unfinished call is awaited and not refused (audit B3-03): behind the fence it cannot be a
        /// coroutine yield - the fence refuses a fenced thread's yield before it signals, and the main thread
        /// cannot yield at all - so it is the execution guard awaiting a frame (the execute_lua path does every
        /// 6 ms) or an awaited host operation. Refused, a replacement function or <c>__tostring</c> that merely
        /// ran past one slice failed with the yield line, and the VM continuation it left behind resumed on the
        /// next frame over a call stack that had moved on and faulted the whole run. Awaited, the fence stays up
        /// for the whole call, frame yields included, so no real yield crosses it. The alternative, no frame
        /// yields while a fenced call is open, would have held the player's frame for as long as a callback ran:
        /// up to the chunk's whole 10 s budget.
        /// </para>
        /// </remarks>
        private static async System.Threading.Tasks.ValueTask<LuaValue[]> CallFencedAsync(LuaState state,
            LuaValue function, LuaValue[] args, CancellationToken ct, string boundary, int levels)
        {
            CCallDepth depth = EnterCCall(state, boundary, levels);
            bool fenced = false;
            try
            {
                fenced = BeginYieldFence(state);
                return await state.CallAsync(function, args.AsSpan(), ct);
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, levels);
            }
        }

        /// <summary>
        /// Reads <paramref name="table"/>[<paramref name="key"/>] honouring <c>__index</c>, like
        /// <c>lua_gettable</c> in the reference <c>gsub</c>, under the same yield fence and counting as
        /// <see cref="CallFencedAsync"/>; a read that comes back unfinished is awaited.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<LuaValue> GetTableFencedAsync(LuaState state,
            LuaTable table, LuaValue key, CancellationToken ct, string boundary)
        {
            return table.Metatable == null
                ? new System.Threading.Tasks.ValueTask<LuaValue>(table[key])
                : GetTableThroughMetatableAsync(state, table, key, ct, boundary);
        }

        private static async System.Threading.Tasks.ValueTask<LuaValue> GetTableThroughMetatableAsync(
            LuaState state, LuaTable table, LuaValue key, CancellationToken ct, string boundary)
        {
            CCallDepth depth = EnterCCall(state, boundary, HeavyCallLevels);
            bool fenced = false;
            try
            {
                fenced = BeginYieldFence(state);
                return await state.GetTableAsync(table, key, ct);
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, boundary);
            }
            finally
            {
                EndYieldFence(state, fenced);
                ExitCCall(depth, HeavyCallLevels);
            }
        }

        // WHY per thread (LuaState) and not per .NET thread: a coroutine suspended inside a library call keeps
        // that call open, and a shared counter would charge it to every other thread - on Unity's one main
        // thread, to every other mod, which a mod could then make fail on purpose. A thread that runs inside a
        // run instead starts from the count of the thread that run executes on (Base) - a raw coroutine always, a
        // coroutine handle's thread when a task.spawn runs it at once, the state a guarded call re-enters or a
        // mods_call export runs on (audit C3-03) - so a chain of nested runs is counted as a whole, whichever
        // channels and states it mixes, and a suspended thread's calls count again only when it runs.
        private static readonly ConditionalWeakTable<LuaState, CCallDepth> CCallDepths = new();

        private static readonly ConditionalWeakTable<LuaState, CCallDepth>.CreateValueCallback NewCCallDepth =
            _ => new CCallDepth();

        /// <summary>
        /// Levels of calls back into Lua open on one thread (see <see cref="MaxCCallDepth"/>): its resumer's when
        /// it was resumed from inside a run, and its own.
        /// </summary>
        private sealed class CCallDepth
        {
            /// <summary>The count of the thread that resumed this one plus the resume's own levels.</summary>
            public int Base;

            /// <summary>Levels this thread's own library functions have open.</summary>
            public int Local;
        }

        /// <summary>
        /// How many levels of calls back into Lua are open on <paramref name="state"/>, its resumers' included; 0 for
        /// null.
        /// </summary>
        private static int CCallDepthOf(LuaState state)
        {
            return state != null && CCallDepths.TryGetValue(state, out CCallDepth depth) ? depth.Base + depth.Local : 0;
        }

        /// <summary>
        /// Opens one call of <paramref name="levels"/> from a library function back into Lua on
        /// <paramref name="state"/>, or raises <see cref="CStackOverflowMessage"/> when it would pass
        /// <see cref="MaxCCallDepth"/>. Every successful call is paired with <see cref="ExitCCall"/>.
        /// </summary>
        private static CCallDepth EnterCCall(LuaState state, string boundary, int levels)
        {
            CCallDepth depth = CCallDepths.GetValue(state, NewCCallDepth);
            if (depth.Base + depth.Local + levels > MaxCCallDepth)
            {
                throw LibraryRefusal(state, CStackOverflowText(boundary));
            }

            depth.Local += levels;
            return depth;
        }

        private static void ExitCCall(CCallDepth depth, int levels)
        {
            depth.Local -= levels;
        }

        /// <summary>
        /// Sets where the count of <paramref name="thread"/>, a coroutine handle's thread about to be resumed,
        /// starts: after the count of <paramref name="resumer"/> plus <see cref="HeavyCallLevels"/> when it is resumed
        /// inside a run executing on <paramref name="resumer"/> (a <c>task.spawn</c> that runs it at once), from
        /// zero when <paramref name="resumer"/> is null (the scheduler's own frame). Returns the C-stack line,
        /// and leaves the thread untouched, when that resume would pass <see cref="MaxCCallDepth"/>; else null.
        /// </summary>
        internal static string ContinueCCallCount(LuaState resumer, LuaState thread, string boundary)
        {
            int baseDepth = resumer == null ? 0 : CCallDepthOf(resumer) + HeavyCallLevels;
            if (baseDepth > MaxCCallDepth)
            {
                return CStackOverflowText(boundary);
            }

            if (baseDepth > 0 || CCallDepths.TryGetValue(thread, out CCallDepth _))
            {
                CCallDepths.GetValue(thread, NewCCallDepth).Base = baseDepth;
            }

            return null;
        }

        /// <summary>
        /// Opens <see cref="HeavyCallLevels"/> for a guarded call on <paramref name="state"/> that starts inside a run
        /// executing on <paramref name="enclosingThread"/> (a host function running Lua of the same state again, or a
        /// <c>mods_call</c> export on another state): the call's Lua continues the higher of the two threads' counts.
        /// Returns the C-stack line, changing nothing, when that would pass <see cref="MaxCCallDepth"/>; else null,
        /// and <paramref name="restoredBase"/> is what <see cref="CloseNestedRun"/> puts back when the call ends.
        /// </summary>
        /// <remarks>
        /// WHY the higher count: the enclosing run's thread holds the chain below the call on the native stack, and
        /// <paramref name="state"/>'s own count holds whatever is open on it lower down (the same state re-entered).
        /// WHY Base is raised rather than Local: the call's library functions open and close their own levels on
        /// Local, which is back where it was when the call ends, so putting Base back ends the call's share exactly.
        /// </remarks>
        internal static string OpenNestedRun(LuaState enclosingThread, LuaState state, string boundary,
            out int restoredBase)
        {
            CCallDepth depth = CCallDepths.GetValue(state, NewCCallDepth);
            restoredBase = depth.Base;
            int from = Math.Max(depth.Base + depth.Local, CCallDepthOf(enclosingThread));
            if (from + HeavyCallLevels > MaxCCallDepth)
            {
                return CStackOverflowText(boundary);
            }

            depth.Base = from + HeavyCallLevels - depth.Local;
            return null;
        }

        /// <summary>Closes what <see cref="OpenNestedRun"/> opened on <paramref name="state"/>.</summary>
        internal static void CloseNestedRun(LuaState state, int restoredBase)
        {
            if (CCallDepths.TryGetValue(state, out CCallDepth depth))
            {
                depth.Base = restoredBase;
            }
        }

        /// <summary>The line a call past <see cref="MaxCCallDepth"/> fails with, naming <paramref name="boundary"/>.</summary>
        internal static string CStackOverflowText(string boundary)
        {
            return $"{CStackOverflowMessage} ({boundary}: more than {MaxCCallDepth} levels of nested calls from "
                   + "library functions back into Lua)";
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
                        $"{SandboxLinePrefix}string.format width/precision exceeds {MaxStringFormatLength} chars.");
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
            string subject = ReadArgument<string>(ctx, 0, "string");
            string pattern = ReadArgument<string>(ctx, 1, "string");
            int init = RelativePosition(ctx.HasArgument(2) ? ReadArgument<int>(ctx, 2, "number") : 1, subject.Length);
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
            string subject = ReadArgument<string>(ctx, 0, "string");
            string pattern = ReadArgument<string>(ctx, 1, "string");
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
        // __index run under the yield fence of CallFencedAsync (Luau refuses those yields too). Unlike the
        // native build this also tries the position after the last char (("abc"):gsub("x*", "-") is
        // "-a-b-c-", 4), accepts a number replacement, and rejects '%' followed by a non-digit.
        private static System.Threading.Tasks.ValueTask<int> BudgetedGSub(
            LuaFunctionExecutionContext ctx,
            CancellationToken ct)
        {
            LuaState state = ctx.State;
            string subject = ReadArgument<string>(ctx, 0, "string");
            string pattern = ReadArgument<string>(ctx, 1, "string");
            LuaValue replacement = ctx.GetArgument(2);
            double maxArgument = ctx.HasArgument(3) ? ReadArgument<double>(ctx, 3, "number") : subject.Length + 1;
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
            GSubRun run = new(new LuaPatternMatcher(state, "string.gsub", subject, pattern, MaxPatternMatchSteps),
                new StringBuilder(Math.Min(subject.Length, MaxStringGsubLength) + 16), subject, replacement,
                replacementText, maxArgument > int.MaxValue ? int.MaxValue : (long)maxArgument,
                pattern.Length > 0 && pattern[0] == '^');
            return GSubLoop(ctx, ct, ref run);
        }

        /// <summary>The state of one <c>string.gsub</c> call's matching loop.</summary>
        private struct GSubRun
        {
            public readonly LuaPatternMatcher Matcher;
            public readonly StringBuilder Result;
            public readonly string Subject;
            public readonly LuaValue Replacement;
            public readonly string ReplacementText;
            public readonly long MaxReplacements;
            public readonly bool Anchor;

            /// <summary>The subject index the next match attempt starts at.</summary>
            public int Source;

            /// <summary>Replacements made so far.</summary>
            public long Count;

            public GSubRun(LuaPatternMatcher matcher, StringBuilder result, string subject, LuaValue replacement,
                string replacementText, long maxReplacements, bool anchor)
            {
                Matcher = matcher;
                Result = result;
                Subject = subject;
                Replacement = replacement;
                ReplacementText = replacementText;
                MaxReplacements = maxReplacements;
                Anchor = anchor;
                Source = 0;
                Count = 0;
            }
        }

        // WHY synchronous, and async only from a replacement call that comes back unfinished (audit C3-02): the
        // replacement function of one gsub runs on top of this loop on the native stack, so a gsub that recurses
        // through its replacement function nests every frame of the loop once per level. As an async method chain
        // (the loop, the replacement read and the fenced call) it took 5.8 KB of native stack a level, 42% over
        // what its C-call weight allows; the only call that can come back unfinished is one that awaited a frame
        // (see CallFencedAsync), after which the loop goes on in GSubAfterAsync. The loop state is one struct passed
        // by reference and the replacement call is made in the loop itself, without an exception handler (what can
        // fail before the call is handled in PushReplacementCall, and the call reports failure as a faulted task),
        // so a level takes no more native stack than a pcall level.
        /// <summary>
        /// The matching loop of <see cref="BudgetedGSub"/> from where <paramref name="run"/> stands; it completes
        /// synchronously unless a replacement function or <c>__index</c> it runs awaits a frame. A replacement
        /// function is called as one counted call of <see cref="GSubCallbackLevels"/> under the yield fence (see
        /// <see cref="CallFencedAsync"/>), with its arguments and results on the Lua stack, so nothing is allocated.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> GSubLoop(LuaFunctionExecutionContext ctx,
            CancellationToken ct, ref GSubRun run)
        {
            LuaState state = ctx.State;
            LuaPatternMatcher matcher = run.Matcher;
            int patternStart = run.Anchor ? 1 : 0;
            while (run.Count < run.MaxReplacements)
            {
                matcher.Reset();
                int end = matcher.Match(run.Source, patternStart);
                if (end >= 0)
                {
                    run.Count++;
                    if (run.ReplacementText != null)
                    {
                        AppendReplacementText(state, run.Result, matcher, run.Subject, run.Source, end,
                            run.ReplacementText);
                    }
                    else if (run.Replacement.Type == LuaValueType.Function)
                    {
                        CCallDepth depth = EnterCCall(state, "string.gsub", GSubCallbackLevels);
                        int top = state.Stack.Count;
                        PushReplacementCall(state, depth, top, matcher, run.Source, end, run.Replacement);
                        bool fenced = BeginYieldFence(state);
                        System.Threading.Tasks.ValueTask<int> call = state.CallAsync(top, top, ct);
                        if (!call.IsCompleted)
                        {
                            return GSubAfterAsync(ctx, ct, AwaitReplacementAsync(state, call, top, depth, fenced),
                                run, end);
                        }

                        if (call.IsFaulted)
                        {
                            return ReplacementFailed(state, call, top, depth, fenced);
                        }

                        LuaValue value = state.Stack.Count > top ? state.Stack[top] : LuaValue.Nil;
                        EndReplacementCall(state, top, depth, fenced);
                        AppendReplacementValue(state, run.Result, run.Subject, run.Source, end, value);
                    }
                    else
                    {
                        System.Threading.Tasks.ValueTask<LuaValue> value = GetTableFencedAsync(state,
                            run.Replacement.Read<LuaTable>(), matcher.GetCapture(0, run.Source, end), ct,
                            "string.gsub");
                        if (!value.IsCompleted)
                        {
                            return GSubAfterAsync(ctx, ct, value, run, end);
                        }

                        AppendReplacementValue(state, run.Result, run.Subject, run.Source, end,
                            value.GetAwaiter().GetResult());
                    }
                }

                if (!NextGSubSource(state, ref run, end))
                {
                    break;
                }
            }

            AppendCapped(state, run.Result, run.Subject, run.Source, run.Subject.Length - run.Source);
            return new System.Threading.Tasks.ValueTask<int>(ctx.Return(run.Result.ToString(), (double)run.Count));
        }

        /// <summary>
        /// Moves <paramref name="run"/> past the match attempt that ended at <paramref name="end"/> (-1 for no
        /// match), copying an unmatched character; false when the loop is over.
        /// </summary>
        private static bool NextGSubSource(LuaState state, ref GSubRun run, int end)
        {
            if (end >= 0 && end > run.Source)
            {
                run.Source = end;
            }
            else if (run.Source < run.Subject.Length)
            {
                AppendCapped(state, run.Result, run.Subject, run.Source, 1);
                run.Source++;
            }
            else
            {
                return false;
            }

            return !run.Anchor;
        }

        /// <summary>
        /// The rest of <see cref="GSubLoop"/> once <paramref name="pending"/>, the replacement for the match that
        /// starts at <c>run.Source</c> and ends at <paramref name="end"/>, completes.
        /// </summary>
        private static async System.Threading.Tasks.ValueTask<int> GSubAfterAsync(LuaFunctionExecutionContext ctx,
            CancellationToken ct, System.Threading.Tasks.ValueTask<LuaValue> pending, GSubRun run, int end)
        {
            LuaValue value = await pending;
            AppendReplacementValue(ctx.State, run.Result, run.Subject, run.Source, end, value);
            if (!NextGSubSource(ctx.State, ref run, end))
            {
                AppendCapped(ctx.State, run.Result, run.Subject, run.Source, run.Subject.Length - run.Source);
                return ctx.Return(run.Result.ToString(), (double)run.Count);
            }

            return await GSubLoop(ctx, ct, ref run);
        }

        /// <summary>
        /// Pushes the replacement <paramref name="function"/> and the captures of the match [<paramref name="start"/>,
        /// <paramref name="end"/>) onto the Lua stack above <paramref name="top"/>; on failure (an invalid capture)
        /// drops them and closes the call's levels.
        /// </summary>
        private static void PushReplacementCall(LuaState state, CCallDepth depth, int top, LuaPatternMatcher matcher,
            int start, int end, LuaValue function)
        {
            try
            {
                LuaStack stack = state.Stack;
                stack.Push(function);
                int captureCount = matcher.Level == 0 ? 1 : matcher.Level;
                for (int i = 0; i < captureCount; i++)
                {
                    stack.Push(matcher.GetCapture(i, start, end));
                }
            }
            catch
            {
                EndReplacementCall(state, top, depth, false);
                throw;
            }
        }

        /// <summary>
        /// A replacement call that completed faulted, ended: its error, or the call-boundary line for a yield the
        /// fence refused.
        /// </summary>
        private static System.Threading.Tasks.ValueTask<int> ReplacementFailed(LuaState state,
            System.Threading.Tasks.ValueTask<int> call, int top, CCallDepth depth, bool fenced)
        {
            Exception fault = FaultOf(call);
            EndReplacementCall(state, top, depth, fenced);
            return new System.Threading.Tasks.ValueTask<int>(System.Threading.Tasks.Task.FromException<int>(
                fenced && fault is LuaRuntimeException runtimeError && IsFencedYield(runtimeError)
                    ? YieldAcrossBoundary(state, "string.gsub")
                    : fault));
        }

        private static async System.Threading.Tasks.ValueTask<LuaValue> AwaitReplacementAsync(LuaState state,
            System.Threading.Tasks.ValueTask<int> call, int top, CCallDepth depth, bool fenced)
        {
            try
            {
                await call;
                return state.Stack.Count > top ? state.Stack[top] : LuaValue.Nil;
            }
            catch (LuaRuntimeException ex) when (fenced && IsFencedYield(ex))
            {
                throw YieldAcrossBoundary(state, "string.gsub");
            }
            finally
            {
                EndReplacementCall(state, top, depth, fenced);
            }
        }

        /// <summary>Drops a replacement call's values from the Lua stack, ends its fence and closes its levels.</summary>
        private static void EndReplacementCall(LuaState state, int top, CCallDepth depth, bool fenced)
        {
            state.Stack.PopUntil(top);
            EndYieldFence(state, fenced);
            ExitCCall(depth, GSubCallbackLevels);
        }

        /// <summary>
        /// Appends <paramref name="value"/>, what a replacement function or table gave for the match
        /// [<paramref name="start"/>, <paramref name="end"/>): the match itself for false or nil.
        /// </summary>
        private static void AppendReplacementValue(LuaState state, StringBuilder result, string subject, int start,
            int end, LuaValue value)
        {
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
                    $"{SandboxLinePrefix}{PatternStepBudgetTripMarker} ({budget}) in {function}: the resume "
                    + "exceeded the step budget of one pattern-matching call; simplify the pattern or match a "
                    + "shorter string");
            }
        }
    }
}
