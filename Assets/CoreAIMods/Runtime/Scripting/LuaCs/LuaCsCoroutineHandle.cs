using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Wraps a single nuskey8/Lua-CSharp coroutine and advances it one step (one
    /// <c>coroutine.yield</c>) per <see cref="Resume"/> call. This is the Lua-CSharp counterpart of
    /// the MoonSharp <c>CoreAI.Sandbox.LuaCoroutineHandle</c>.
    ///
    /// Lua-CSharp models a coroutine as a dedicated <see cref="LuaState"/> thread created with
    /// <see cref="LuaState.CreateCoroutine(LuaFunction, bool)"/>. The thread is driven through the
    /// <c>ResumeAsync(LuaStack, CancellationToken)</c> overload, which treats the passed stack as the
    /// caller's stack: it consumes the whole stack as resume arguments and, on return, overwrites it
    /// with <c>[ok, values...]</c> (Lua's <c>coroutine.resume</c> convention). A well-behaved handler
    /// that reaches <c>coroutine.yield</c> completes this call SYNCHRONOUSLY, so
    /// <c>GetAwaiter().GetResult()</c> never blocks a single-threaded WASM player loop. That holds only
    /// from Lua-CSharp v0.5.6 on: earlier builds posted a suspended resume's continuation to the
    /// ambient <c>SynchronizationContext</c> — which on Unity's main thread is the very thread parked
    /// in <c>GetResult()</c> — and froze a WebGL player (upstream #327/#329). A runaway that
    /// never yields is cut by the per-resume instruction/time/allocation budget armed via
    /// <see cref="LuaState.SetHook"/> (the same mechanism as <see cref="LuaCsExecutionGuard"/>).
    ///
    /// The per-resume budget is the ONLY CPU limit a scheduler-owned thread has: like a Roblox script,
    /// a thread may run forever as long as every resume yields in time, so every scheduler site passes
    /// <see cref="UnlimitedLifetimeSteps"/>. A lifetime step cap remains only for direct users of this
    /// constructor (its default is <see cref="DefaultTotalLifetimeSteps"/>), and exhausting it fails the
    /// resume LOUDLY — <see cref="LastOk"/> false, an <c>EXCEEDED_LIFETIME_STEP_BUDGET</c> error naming
    /// the author line, <see cref="LastTrip"/> <see cref="LuaCsGuardTripKind.LifetimeSteps"/> — exactly
    /// like a per-resume trip, never as a silent kill after a successful resume.
    ///
    /// A resume that runs while another guarded run is executing - a <c>task.spawn</c> that runs the new or
    /// parked thread at once - is NESTED in the innermost such run: it is held to what is left of that run's
    /// allowance (see <see cref="LuaCsGuardedRun"/>) and continues its count of calls back into Lua (see
    /// <see cref="LuaCsSecureEnvironment.MaxCCallDepth"/>). A resume the scheduler drives from its own frame keeps
    /// the full per-resume budget and starts that count from zero.
    ///
    /// There is deliberately NO MoonSharp-style <c>AutoYieldCounter</c>/<c>YieldRequest</c> loop:
    /// Lua-CSharp has no preemptive auto-yield, so one resume already returns at exactly one yield.
    ///
    /// A budget trip ends the resume for good: no <c>pcall</c>/<c>xpcall</c> inside the coroutine can
    /// catch it, and the thread is Dead afterwards. The mechanism is shared with
    /// <see cref="LuaCsExecutionGuard"/> — see <see cref="CancelGuardedRun"/>.
    /// </summary>
    public sealed class LuaCsCoroutineHandle
    {
        /// <summary>Default instruction-step budget for a single resume.</summary>
        public const int DefaultBudgetPerResume = 10_000;

        /// <summary>Default wall-clock budget for a single resume, in milliseconds.</summary>
        public const int DefaultResumeTimeoutMs = 500;

        /// <summary>
        /// Default cap on instruction steps a DIRECTLY constructed coroutine may consume across all
        /// resumes. Scheduler-owned threads never use it (see <see cref="UnlimitedLifetimeSteps"/>);
        /// exhausting it fails the resume with <c>EXCEEDED_LIFETIME_STEP_BUDGET</c>.
        /// </summary>
        public const long DefaultTotalLifetimeSteps = 1_000_000;

        /// <summary>
        /// Lifetime step cap meaning "none": only the per-resume budget limits the thread. Every
        /// scheduler-owned thread (a mod's main chunk, <c>task.*</c> threads, pooled signal runners) and
        /// every coroutine built through <see cref="CoreAI.Scripting.LuaCs.LuaCsScriptEngine"/> uses it.
        /// </summary>
        public const long UnlimitedLifetimeSteps = long.MaxValue;

        /// <summary>
        /// Default per-resume allocation budget (bytes of LIVE heap growth one resume may add); the same
        /// value as <see cref="LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget"/>. A mod's own
        /// <c>IExecutionBudget.MaxAllocatedBytes</c> replaces it for every thread of that mod.
        /// </summary>
        public const long DefaultMaxAllocatedBytesPerResume = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;

        private static readonly LuaValue[] EmptyValues = Array.Empty<LuaValue>();

        // WHY a registry of every handle's thread: a handle is the ONLY thing that may resume its thread. The
        // body runs with the token of its first resume for life (the handle's own source), so a mod that took
        // coroutine.running() inside a task thread and resumed it with the sandbox's coroutine.resume ran that
        // body under a raw-resume hook whose trip cancelled a source the body never reads. The hook then had to
        // throw (ForeignContextTrip), which xpcall swallowed: its handler and every later frame ran unguarded
        // (audit A2-01, 60M iterations), and the thread, marked Dead with live registrations on the handle's
        // token, crashed the .NET process from a Lua-CSharp continuation when the scheduler later killed it
        // (A2-02). The sandbox refuses such a resume by looking the thread up here. The value holds no
        // reference to its key, so an entry goes away with the thread without relying on ephemeron support.
        private static readonly ConditionalWeakTable<LuaState, HandleThread> HandleThreads = new();

        private readonly LuaState _coroutine;
        private readonly LuaStack _callStack;
        private readonly CancellationTokenSource _cts;
        private readonly ResumeGuardHook _hook;
        private readonly int _budgetPerResume;
        private readonly int _resumeTimeoutMs;
        private readonly long _totalLifetimeSteps;
        private readonly long _maxAllocatedBytes;
        private readonly LuaCsCoroutineBudgetSettings _liveResumeBudget;
        private readonly bool _isProtectedMode;

        private bool _killed;
        private long _consumedSteps;
        private bool _lastOk = true;
        private LuaValue[] _lastValues = EmptyValues;
        private LuaValue _lastError = LuaValue.Nil;
        private long _observedSteps;

        /// <summary>
        /// Creates a coroutine from <paramref name="function"/> on the owning <paramref name="ownerState"/>.
        /// A <see cref="LuaClosure"/> (a loaded chunk) is a <see cref="LuaFunction"/>, so it is accepted here too.
        /// </summary>
        /// <param name="ownerState">The state whose global runtime spawns the coroutine thread.</param>
        /// <param name="function">The coroutine body (bare function or loaded closure).</param>
        /// <param name="budgetPerResume">Instruction-step budget re-armed before every resume.</param>
        /// <param name="resumeTimeoutMs">Wall-clock budget, in ms, for a single resume.</param>
        /// <param name="totalLifetimeSteps">
        /// Cap on instruction steps across the whole coroutine lifetime; <see cref="UnlimitedLifetimeSteps"/>
        /// disables it, <c>&lt;= 0</c> falls back to <see cref="DefaultTotalLifetimeSteps"/>.
        /// </param>
        /// <param name="isProtectedMode">Whether Lua errors are returned as protected resume results.</param>
        /// <param name="liveResumeBudget">
        /// Optional shared, mutable budget re-read on every <see cref="Resume"/> instead of the frozen
        /// <paramref name="budgetPerResume"/>/<paramref name="resumeTimeoutMs"/> values above. Pass this
        /// for a handle whose budget is the composition's configurable default (see
        /// <see cref="LuaCsCoroutineBudgetSettings"/>) so a later <c>ScriptContext:SetTimeout</c> call
        /// changes THIS handle's next resume too, even for a long-lived pooled handle created before the
        /// call. Null (the default) keeps a handle's budget frozen at construction, as before — the shape
        /// every explicit per-call budget (e.g. a mod's main-chunk <c>HandlerMaxSteps</c>/
        /// <c>HandlerTimeoutMs</c>) still uses.
        /// </param>
        /// <param name="maxAllocatedBytes">
        /// Live heap growth one resume may add before it is cut with <c>EXCEEDED_MEMORY_BUDGET</c>
        /// (see <see cref="LuaCsAllocationBudget"/>); <c>&lt;= 0</c> disables the check.
        /// </param>
        public LuaCsCoroutineHandle(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps,
            bool isProtectedMode = true,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null,
            long maxAllocatedBytes = DefaultMaxAllocatedBytesPerResume)
        {
            if (ownerState == null)
            {
                throw new ArgumentNullException(nameof(ownerState));
            }

            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            _budgetPerResume = budgetPerResume > 0 ? budgetPerResume : DefaultBudgetPerResume;
            _resumeTimeoutMs = resumeTimeoutMs > 0 ? resumeTimeoutMs : DefaultResumeTimeoutMs;
            _totalLifetimeSteps = totalLifetimeSteps > 0 ? totalLifetimeSteps : DefaultTotalLifetimeSteps;
            _maxAllocatedBytes = maxAllocatedBytes;
            _liveResumeBudget = liveResumeBudget;
            _isProtectedMode = isProtectedMode;

            _coroutine = ownerState.CreateCoroutine(function, isProtectedMode);
            _callStack = new LuaStack(8);
            _cts = new CancellationTokenSource();
            // WHY the handle's own source doubles as the trip source: it is already the token every resume
            // runs with, and a tripped thread is Dead and never resumed again, so cancelling it for good
            // costs nothing, where a separate linked source would allocate on every resume of every thread.
            _hook = new ResumeGuardHook(_cts);
            HandleThreads.Add(_coroutine, new HandleThread(_hook));
        }

        /// <summary>Convenience factory mirroring the constructor.</summary>
        public static LuaCsCoroutineHandle Create(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps,
            bool isProtectedMode = true,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null,
            long maxAllocatedBytes = DefaultMaxAllocatedBytesPerResume)
        {
            return new LuaCsCoroutineHandle(ownerState, function, budgetPerResume, resumeTimeoutMs,
                totalLifetimeSteps, isProtectedMode, liveResumeBudget, maxAllocatedBytes);
        }

        /// <summary>Current Lua-CSharp thread status (Suspended/Normal/Running/Dead), or Dead once killed.</summary>
        public LuaThreadStatus Status => _killed ? LuaThreadStatus.Dead : _coroutine.GetStatus();

        /// <summary>True while the coroutine has not been killed and is not dead.</summary>
        public bool IsAlive => !_killed && _coroutine.GetStatus() != LuaThreadStatus.Dead;

        /// <summary>True when a further <see cref="Resume"/> is legal (suspended and not killed).</summary>
        public bool CanResume => !_killed && _coroutine.CanResume;

        /// <summary>True once the coroutine has finished (dead) or been killed; the runner may drop it.</summary>
        public bool IsFinished => _killed || _coroutine.GetStatus() == LuaThreadStatus.Dead;

        /// <summary>
        /// Instruction steps consumed across all resumes so far, those of the runs nested in them (a raw coroutine a
        /// resume ran, a thread a <c>task.spawn</c> in it ran at once) included. The lifetime cap counts them.
        /// </summary>
        public long ConsumedSteps => _consumedSteps;

        /// <summary>
        /// The part of the steps of every resume so far that no nested run records itself: this handle's own and those
        /// of the raw coroutines its resumes ran. What the scheduler reports to observability, so that a nested
        /// handle's or guard's steps, which they report themselves, are not counted twice.
        /// </summary>
        internal long ObservedSteps => _observedSteps;

        /// <summary>
        /// Cap on instruction steps across the whole coroutine lifetime, or
        /// <see cref="UnlimitedLifetimeSteps"/> when the per-resume budget is the only limit.
        /// </summary>
        public long TotalLifetimeSteps => _totalLifetimeSteps;

        /// <summary>True when a lifetime step cap applies (see <see cref="TotalLifetimeSteps"/>).</summary>
        public bool HasLifetimeCap => _totalLifetimeSteps != UnlimitedLifetimeSteps;

        /// <summary>Live heap growth one resume may add; <c>&lt;= 0</c> when the check is disabled.</summary>
        public long MaxAllocatedBytes => _maxAllocatedBytes;

        /// <summary>
        /// True when <paramref name="state"/> is the thread of a <see cref="LuaCsCoroutineHandle"/> (a task
        /// thread, a signal runner, a mod's main chunk, any coroutine built through the script engine). Only
        /// that handle may resume it; the sandbox's <c>coroutine.resume</c> refuses it.
        /// </summary>
        internal static bool IsHandleThread(LuaState state)
        {
            return state != null && HandleThreads.TryGetValue(state, out HandleThread _);
        }

        /// <summary>
        /// The resume of the handle whose thread <paramref name="state"/> is, while that resume executes; null
        /// otherwise. See <see cref="LuaCsGuardedRun.FindExecuting"/>.
        /// </summary>
        internal static LuaCsGuardedRun ExecutingRunOn(LuaState state)
        {
            return HandleThreads.TryGetValue(state, out HandleThread thread) && thread.Run.IsExecuting
                ? thread.Run
                : null;
        }

        /// <summary>This handle's Lua thread.</summary>
        internal LuaState Thread => _coroutine;

        /// <summary>
        /// Restarts the consumed-step count. Only a pooled signal runner calls this, at the moment it is
        /// re-armed for a fresh handler: each handler is a new logical thread, so it starts counting from
        /// zero exactly as a freshly created coroutine would (and, for a handle built with a lifetime
        /// cap, starts that cap afresh too).
        /// </summary>
        internal void ResetLifetime()
        {
            _consumedSteps = 0;
        }

        /// <summary>
        /// Result flag of the most recent resume. Lua-CSharp follows <c>coroutine.resume</c> semantics:
        /// false means the last resume raised a Lua error (see <see cref="LastError"/>) and the coroutine died.
        /// </summary>
        public bool LastOk => _lastOk;

        /// <summary>Values yielded or returned by the most recent resume (excludes the leading ok flag).</summary>
        public IReadOnlyList<LuaValue> LastValues => _lastValues;

        /// <summary>Error object from the most recent resume when <see cref="LastOk"/> is false; otherwise nil.</summary>
        public LuaValue LastError => _lastError;

        /// <summary>Human-readable text of <see cref="LastError"/>, or empty when the last resume succeeded.</summary>
        public string LastErrorText => _lastOk ? string.Empty : _lastError.ToString();

        /// <summary>
        /// Which budget cut the most recent resume (per-resume steps, time or memory, or the lifetime
        /// step cap), or <see cref="LuaCsGuardTripKind.None"/> when it ended by yield, return, or a
        /// script error of its own. Recorded by the guard hook at
        /// trip time — the typed counterpart of the budget text in <see cref="LastErrorText"/>, so a
        /// consumer can classify a budget kill without matching message wording a script could forge.
        /// </summary>
        public LuaCsGuardTripKind LastTrip => _hook.Trip;

        /// <summary>
        /// Advances the coroutine to its next <c>coroutine.yield</c> (or to completion), passing
        /// <paramref name="args"/> as the values <c>coroutine.yield</c>/the initial call receives.
        /// Returns the values the coroutine yielded or returned (the leading ok flag is stripped).
        /// </summary>
        public LuaValue[] Resume(params LuaValue[] args)
        {
            // WHY the innermost run executing on this OS thread (see LuaCsGuardedRun): a task.spawn that runs this
            // thread at once runs it inside that run, whose hook cannot fire until it returns; a resume the scheduler
            // drives from its own frame finds none and keeps the full per-resume budget.
            LuaCsGuardedRun enclosing = LuaCsGuardedRun.InnermostExecuting();
            if (_killed)
            {
                throw new ObjectDisposedException(nameof(LuaCsCoroutineHandle));
            }

            if (!_coroutine.CanResume)
            {
                throw new InvalidOperationException(
                    $"Cannot resume coroutine in state {_coroutine.GetStatus()}.");
            }

            args ??= EmptyValues;

            // WHY refused before anything runs, the thread left as it was: the resume would nest one more run on
            // the native stack past the limit every other call back into Lua is held to (see
            // LuaCsSecureEnvironment.MaxCCallDepth); like Luau's resume at LUAI_MAXCCALLS it fails with the line.
            string cStackRefusal = LuaCsSecureEnvironment.ContinueCCallCount(enclosing?.Thread, _coroutine,
                "task.spawn");
            if (cStackRefusal != null)
            {
                return EndRefused(cStackRefusal);
            }

            // WHY the stack is emptied and refilled on every resume: ResumeAsync takes the WHOLE stack
            // as the values coroutine.yield (or the first call) receives, and the previous resume left
            // its [ok, values...] there. Reused as is, a parked task thread resumed by
            // task.spawn(thread, 42) read `true, <what it last yielded>` from coroutine.yield instead of
            // 42.
            _callStack.Clear();
            if (args.Length > 0)
            {
                _callStack.PushRange(args);
            }

            // WHY: Re-arm a fresh per-resume budget (instruction steps + wall clock) via SetHook, mirroring
            // LuaCsExecutionGuard. A breach cancels this handle's token, so the VM ends the resume with a
            // cancellation no pcall inside the thread can catch; the catch below turns it into the same
            // [ok=false, trip line] result a protected resume reports for an error. The hook object is
            // built once per handle and re-armed here (like the guard's pooled GuardHook): a fresh
            // LuaFunction + closure + Stopwatch per resume was measured heap churn on every signal handler
            // and every task.wait loop resume.
            //
            // WHY read _liveResumeBudget here instead of caching it once: a shared
            // LuaCsCoroutineBudgetSettings is exactly the object ScriptContext:SetTimeout mutates, and a
            // long-lived pooled handle (a signal runner serving every Heartbeat fire) must pick up that
            // change on its very next resume — not only on a freshly constructed handle.
            int budgetPerResume = _liveResumeBudget?.BudgetPerResume ?? _budgetPerResume;
            int resumeTimeoutMs = _liveResumeBudget?.ResumeTimeoutMs ?? _resumeTimeoutMs;
            long lifetimeRemaining = HasLifetimeCap
                ? Math.Max(0L, _totalLifetimeSteps - _consumedSteps)
                : UnlimitedLifetimeSteps;
            _hook.Arm(budgetPerResume, resumeTimeoutMs, lifetimeRemaining, _totalLifetimeSteps,
                _maxAllocatedBytes, enclosing, _coroutine);

            int count = 0;
            try
            {
                _coroutine.SetHook(_hook.Function, string.Empty, 1);
                // WHY: Single-step drive: a well-behaved handler reaches coroutine.yield synchronously, so
                // GetResult does not block the (single WASM) thread. A runaway is cut by the hook above.
                count = _coroutine.ResumeAsync(_callStack, _cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception) when (_hook.HasTripped)
            {
                // WHY swallowed: whatever carried the trip out (Lua-CSharp's cancellation, or an error a host
                // function built from it) is only the vehicle; the resume's outcome is the trip, reported below.
            }
            finally
            {
                _hook.End();
                try
                {
                    _coroutine.SetHook(null, string.Empty, 0);
                }
                catch
                {
                    /* ignore */
                }
            }

            _consumedSteps += _hook.Steps + _hook.NestedSteps;
            _observedSteps += _hook.Steps + _hook.NestedUnreportedSteps;
            if (_hook.HasTripped)
            {
                EndWithTrip(_hook.TripError);
                if (!_isProtectedMode)
                {
                    throw _hook.TripError;
                }

                return _lastValues;
            }

            CaptureResults(count);
            return _lastValues;
        }

        // WHY the thread is marked Dead by hand: a protected Lua-CSharp resume marks a thread Dead only for an
        // error it catches, and it lets a cancellation through (CoroutineCore.ResumeAsyncCore catches
        // `when !(ex is OperationCanceledException)`) with the thread left Running - neither resumable nor
        // finished, so a scheduler would keep it forever. A budget-cut thread is over, as it was when the trip
        // was a caught error.
        /// <summary>Records <paramref name="trip"/> as the resume's result and retires the thread.</summary>
        private void EndWithTrip(LuaCsHostFunctionException trip)
        {
            _lastOk = false;
            _lastValues = EmptyValues;
            _lastError = trip.ErrorObject;
            _coroutine.UnsafeSetStatus(LuaThreadStatus.Dead);
        }

        /// <summary>
        /// Reports a resume refused before it ran: <see cref="LastOk"/> false and <paramref name="line"/> as the
        /// error, raised when the handle is not in protected mode; the thread itself is untouched.
        /// </summary>
        private LuaValue[] EndRefused(string line)
        {
            _hook.ClearOutcome();
            _lastOk = false;
            _lastValues = EmptyValues;
            _lastError = line;
            if (!_isProtectedMode)
            {
                throw new LuaCsHostFunctionException(null, line, null);
            }

            return _lastValues;
        }

        /// <summary>Advances the coroutine one step with no resume arguments.</summary>
        public LuaValue[] ResumeStep()
        {
            return Resume(EmptyValues);
        }

        /// <summary>
        /// Stops this coroutine permanently. Cancels the per-handle <see cref="CancellationTokenSource"/>,
        /// clears any hook and best-effort marks the thread Dead. As in the MoonSharp handle, the actual
        /// termination guarantee is the internal killed flag: once set, <see cref="Resume"/> throws and
        /// <see cref="CanResume"/>/<see cref="IsAlive"/> report false, so the thread is never resumed again
        /// and is left for garbage collection. (A still-suspended Lua-CSharp thread cannot be Dispose()d —
        /// that throws because its call stack is non-empty — so we do not attempt it here.)
        /// </summary>
        public void Kill()
        {
            if (_killed)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
                /* ignore */
            }

            try
            {
                _coroutine.SetHook(null, string.Empty, 0);
            }
            catch
            {
                /* ignore */
            }

            try
            {
                if (_coroutine.GetStatus() != LuaThreadStatus.Dead)
                {
                    _coroutine.UnsafeSetStatus(LuaThreadStatus.Dead);
                }
            }
            catch
            {
                /* ignore */
            }

            _killed = true;

            try
            {
                _cts.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }

        /// <summary>
        /// Builds a budget error raised as an ordinary Lua error, carrying <paramref name="message"/> plus a
        /// best-effort " at line N" suffix as the Lua ERROR OBJECT: the per-call pattern-step refusal in
        /// <see cref="LuaCsSecureEnvironment"/>, which a library function throws directly. A count hook never
        /// throws it; it records <see cref="CreatePendingBudgetTrip"/> and cancels the run instead.
        /// </summary>
        /// <remarks>
        /// WHY the error-object constructor and not <c>LuaRuntimeException(LuaState, Exception)</c>:
        /// Lua-CSharp's protected coroutine resume reports a caught <see cref="LuaRuntimeException"/>
        /// as <c>[false, ex.ErrorObject]</c> and never reads <c>ex.Message</c>, while the
        /// <c>(LuaState, Exception)</c> overload leaves <c>ErrorObject</c> nil. A trip raised that way
        /// therefore came back as the literal text "nil": the scheduler classified the runaway as a
        /// BAD_ARGUMENT Lua bug, the fix hint said "fix the Lua error", and auto-repair was handed a
        /// diagnosis that named no bound. An unprotected resume rethrows the same exception, whose
        /// <c>Message</c> is that same text, so the text survives on both paths.
        /// <para>
        /// WHY <see cref="LuaCsHostFunctionException"/> (an error object at level 0) and not a plain
        /// level-1 error object: at level 1 pcall alone prepends a "chunk:line:" position the error value
        /// does not hold, so a pattern-step trip inside <c>string.gsub</c> read one way under pcall and
        /// another under xpcall and coroutine.resume; the trip already names its author line itself.
        /// </para>
        /// </remarks>
        internal static LuaRuntimeException CreateBudgetTrip(LuaState state, string message)
        {
            return new LuaCsHostFunctionException(state, message + DescribeCurrentLine(state), null);
        }

        /// <summary>
        /// The trip a per-resume guard hook records instead of throwing (see <see cref="CancelGuardedRun"/>):
        /// the same line as <see cref="CreateBudgetTrip"/>, the author line read now while the thread's
        /// frames are live, and no state attached because it is raised later, outside the VM.
        /// </summary>
        internal static LuaCsHostFunctionException CreatePendingBudgetTrip(LuaState state, string message)
        {
            return new LuaCsHostFunctionException(null, message + DescribeCurrentLine(state), null);
        }

        // WHY a count hook signals a trip by cancelling instead of throwing - the Lua-CSharp facts it rests on
        // (decompiled from the shipped Lua.dll):
        // - LuaVirtualMachine.ExecutePerInstructionHook sets LuaState.IsInHook = true before it calls the count
        //   hook and clears it only after the hook RETURNS; there is no finally. While the flag is set,
        //   LuaVirtualMachine.MoveNext counts every frame it enters against the static DummyHookCount, so the
        //   hook never fires again on that state, and SetHook does not clear the flag. A hook that threw
        //   therefore disarmed every guard on the state: `pcall(runaway); pcall(work)` ran `work` unguarded,
        //   and every later guarded run on the same state (the next mod event handler) had no budget at all.
        // - Right after a hook returns, the same method calls ThrowIfCancellationRequested on the running
        //   context's token, and the VM checks that token again at every JMP, FORLOOP, CALL and TAILCALL and
        //   after every C# function returns. A cancelled token ends the run within one loop iteration, however
        //   many host functions or protected calls sit in between.
        // - BasicLibrary.PCall rethrows a LuaCanceledException and turns any other OperationCanceledException
        //   into one; XPCall calls its token's ThrowIfCancellationRequested before it runs the handler;
        //   CoroutineCore.ResumeAsyncCore catches only `!(ex is OperationCanceledException)`. So a trip that
        //   travels as a cancellation crosses every protected boundary: a runaway never survives its budget.
        // - Lua-CSharp completes a failed call through LightAsyncValueTaskMethodBuilder with
        //   Task.FromException, a FAULTED task, so GetResult rethrows the cancellation as it was raised on Mono
        //   and .NET alike, and each guard boundary turns it back into the recorded trip.
        /// <summary>
        /// Signals a budget trip from inside a count hook: cancels <paramref name="runSource"/>, whose token
        /// the guarded run executes with, and reports whether the context the hook fired in reads that token.
        /// When it does (true), the hook must RETURN NORMALLY: the VM then clears its in-hook flag and raises
        /// the cancellation itself. When it does not (false), the hook fired inside host code that runs Lua on
        /// this thread with a token of its own; the hook then has to throw <see cref="ForeignContextTrip"/>.
        /// </summary>
        /// <param name="runSource">The source whose token the guarded run was started with.</param>
        /// <param name="hookToken">The token the VM passed to the hook: the running context's own.</param>
        internal static bool CancelGuardedRun(CancellationTokenSource runSource, CancellationToken hookToken)
        {
            try
            {
                runSource.Cancel();
            }
            catch (Exception)
            {
                // WHY swallowed: Cancel marks the token before it runs the registered callbacks, so a callback
                // that throws (Lua-CSharp's own coroutine registrations) cannot undo the trip; letting it escape
                // would make this hook throw, which is exactly what must not happen.
            }

            return hookToken.IsCancellationRequested;
        }

        // WHY a throw here at all: the hook fired in Lua that host code runs on this thread with a token of its
        // own, and cancelling the run's token cannot stop that call. The throw leaves the in-hook flag set:
        // LuaCsExecutionGuard clears it on its state when the run ends, and a coroutine hook's thread is dead
        // after its trip. WHY a cancellation and not the trip's Lua error: pcall rethrows every
        // OperationCanceledException. xpcall does NOT: it checks only its own token, which in a foreign context
        // is not the cancelled one, and then runs its handler with the in-hook flag still set - unguarded, as is
        // every frame after it until the run's own token is checked (audit A2-01: 60M iterations in a handler).
        // So no mod code may ever run in a foreign context, and none can: every host function that calls back
        // into mod code passes on the token it was called with, and the sandbox's coroutine.resume refuses a
        // handle's thread (see HandleThreads), the one way mod code used to run with a token (the handle's) other
        // than the one its hook cancelled (the raw resume's). What remains is host-authored Lua that runs no mod
        // code and has no xpcall (the signal runner's body factory, the HttpService bridge chunk).
        /// <summary>
        /// The exception a hook throws when <see cref="CancelGuardedRun"/> returned false; its message is the
        /// trip line.
        /// </summary>
        internal static OperationCanceledException ForeignContextTrip(LuaCsHostFunctionException trip)
        {
            return new OperationCanceledException(trip.Message);
        }

        /// <summary>
        /// Best-effort " at line N" suffix naming the author line executing when a guard hook trips,
        /// read the same way <c>LuaCsRbxValues.WithProductionContext</c> attributes an ordinary API-call
        /// error (<c>state.GetTraceback().LastLine</c>). <c>GetTraceback</c> only snapshots the thread's
        /// call-stack frames, so reading it from inside a per-instruction hook is safe. WHY still
        /// best-effort: a traceback read that throws or reports no line must never suppress the
        /// budget-exceeded error itself, so a failure here yields no suffix instead.
        /// </summary>
        private static string DescribeCurrentLine(LuaState state)
        {
            try
            {
                int line = state.GetTraceback().LastLine;
                return line > 0 ? $" at line {line}" : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void CaptureResults(int count)
        {
            if (count <= 0)
            {
                _lastOk = true;
                _lastValues = EmptyValues;
                _lastError = LuaValue.Nil;
                return;
            }

            LuaValue okValue = _callStack[0];
            _lastOk = okValue.Type == LuaValueType.Boolean && okValue.Read<bool>();

            if (!_lastOk)
            {
                _lastError = count >= 2 ? _callStack[1] : LuaValue.Nil;
                _lastValues = EmptyValues;
                return;
            }

            _lastError = LuaValue.Nil;
            int valueCount = count - 1;
            if (valueCount <= 0)
            {
                _lastValues = EmptyValues;
                return;
            }

            LuaValue[] values = new LuaValue[valueCount];
            for (int i = 0; i < valueCount; i++)
            {
                values[i] = _callStack[i + 1];
            }

            _lastValues = values;
        }

        /// <summary>What <see cref="HandleThreads"/> records about a handle's thread.</summary>
        private sealed class HandleThread
        {
            /// <summary>
            /// The handle's per-resume hook, the live allowance of its resume in progress (see
            /// <see cref="LuaCsGuardedRun"/>). It holds no reference to the thread.
            /// </summary>
            public readonly LuaCsGuardedRun Run;

            public HandleThread(LuaCsGuardedRun run)
            {
                Run = run;
            }
        }

        /// <summary>
        /// Reusable per-resume budget hook. A handle resumes one coroutine at a time (a nested resume of
        /// a non-suspended coroutine is rejected by the VM before any instruction runs), so one hook per
        /// handle is enough; its counters live in fields and are reset by <see cref="Arm"/>.
        /// </summary>
        private sealed class ResumeGuardHook : LuaCsGuardedRun
        {
            // WHY the allocation budget is sampled per MILLISECOND of execution, not every few
            // instructions like LuaCsExecutionGuard: this hook fires on every instruction of every
            // scheduler thread, the hottest path in the runtime, and a GC.GetTotalMemory(false) read was
            // measured at ~5.4 us on Unity's bundled Mono CLI against ~27 ns on CoreCLR
            // (tools/vmbench/RESULTS.md) — more than the ~0.55 us this whole hook costs per instruction
            // there. A 4-instruction batch would have tripled the cost of every handler and task.wait
            // loop; one sample per millisecond costs under 1%, plus the one baseline read per resume in
            // Arm. The bound the guard's small batch gives still holds: the growth between two samples
            // is limited by what the VM can copy in one millisecond, and an instruction that takes
            // longer than that (a large concat) is followed by a sample at once, so a doubling bomb
            // overshoots by about one doubling at most.
            private static readonly long AllocationSampleIntervalTicks =
                Math.Max(1L, Stopwatch.Frequency / 1000);

            public readonly LuaFunction Function;

            private readonly CancellationTokenSource _runSource;
            private long _steps;
            private long _stepLimit;
            private int _budget;
            private bool _lifetimeBinds;
            private long _lifetimeCap;
            private int _timeoutMs;
            private long _deadline;
            private long _nextAllocationSampleTimestamp;
            private LuaCsAllocationBudget _allocation;
            private LuaCsGuardTripKind _trip;
            private LuaCsHostFunctionException _tripError;

            /// <param name="runSource">The handle's source, whose token every resume runs with.</param>
            public ResumeGuardHook(CancellationTokenSource runSource)
            {
                _runSource = runSource;
                Function = new LuaFunction("coreai_luacs_coroutine_guard", Hook);
            }

            /// <summary>Instruction steps charged during the current resume.</summary>
            public long Steps => _steps;

            /// <summary>Which budget tripped during the current resume, or <see cref="LuaCsGuardTripKind.None"/>.</summary>
            public LuaCsGuardTripKind Trip => _trip;

            /// <summary>
            /// True once a budget of the current resume tripped; the resume can then only end with it.
            /// </summary>
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
            /// Re-arms the hook for one resume. <paramref name="lifetimeRemaining"/> is what is left of the
            /// handle's lifetime cap (<see cref="UnlimitedLifetimeSteps"/> when there is none); whichever of
            /// it and <paramref name="budget"/> is smaller is the step limit of this resume, and the trip
            /// names the one that bound. A non-null <paramref name="enclosing"/> is the executing run this
            /// resume is nested in, which may lower every limit further (see <see cref="LuaCsGuardedRun"/>).
            /// </summary>
            public void Arm(int budget, int timeoutMs, long lifetimeRemaining, long lifetimeCap,
                long maxAllocatedBytes, LuaCsGuardedRun enclosing, LuaState thread)
            {
                _steps = 0;
                _trip = LuaCsGuardTripKind.None;
                _tripError = null;
                _budget = budget;
                _lifetimeBinds = lifetimeRemaining < budget;
                _stepLimit = _lifetimeBinds ? lifetimeRemaining : budget;
                _lifetimeCap = lifetimeCap;
                _timeoutMs = timeoutMs;
                long startTimestamp = Stopwatch.GetTimestamp();
                _deadline = startTimestamp + (long)timeoutMs * Stopwatch.Frequency / 1000;
                BeginRun(enclosing, thread, ref _stepLimit, ref _deadline);
                // WHY the baseline is read here, once per resume, and not lazily at the first sample: a
                // baseline taken a millisecond in would already contain whatever one long instruction
                // allocated first — a resume that does one `s = s .. s` and yields would double its
                // string every frame, forever, without a single resume ever being charged for it.
                _allocation.ResetNested(maxAllocatedBytes, CeilingOf(enclosing));
                _nextAllocationSampleTimestamp = _allocation.IsEnabled
                    ? startTimestamp + AllocationSampleIntervalTicks
                    : long.MaxValue;
            }

            /// <summary>
            /// Ends the resume: its steps are charged to the run it was nested in, if any, as reported, since the
            /// handle keeps them in <see cref="ObservedSteps"/>.
            /// </summary>
            public void End()
            {
                EndRun(_steps, true);
            }

            /// <summary>Forgets the previous resume's steps and trip, for a resume refused before it ran.</summary>
            public void ClearOutcome()
            {
                _steps = 0;
                _trip = LuaCsGuardTripKind.None;
                _tripError = null;
            }

            /// <inheritdoc />
            protected override void ChargeNestedSteps(long steps)
            {
                _stepLimit -= steps;
            }

            /// <inheritdoc />
            protected override LuaCsHostFunctionException CreateOwnTrip(LuaCsGuardTripKind kind, LuaState where)
            {
                return CreatePendingBudgetTrip(where, OwnTripMessage(kind));
            }

            /// <inheritdoc />
            protected override void RecordNestedTrip(LuaCsGuardTripKind kind, LuaCsHostFunctionException trip,
                bool ownLimit)
            {
                _trip = ownLimit && kind == LuaCsGuardTripKind.Steps && _lifetimeBinds
                    ? LuaCsGuardTripKind.LifetimeSteps
                    : kind;
                _tripError = trip;
            }

            /// <inheritdoc />
            protected override void CancelRun()
            {
                CancelGuardedRun(_runSource, CancellationToken.None);
            }

            private string OwnTripMessage(LuaCsGuardTripKind kind)
            {
                const string prefix = LuaCsSecureEnvironment.SandboxLinePrefix;
                switch (kind)
                {
                    case LuaCsGuardTripKind.Timeout:
                        return $"{prefix}Lua coroutine resume exceeded {_timeoutMs} ms.";
                    case LuaCsGuardTripKind.Memory:
                        return $"{prefix}{LuaCsExecutionGuard.MemoryBudgetTripMarker} ({_allocation.BudgetBytes} bytes)";
                    default:
                        return _lifetimeBinds
                            ? $"{prefix}EXCEEDED_LIFETIME_STEP_BUDGET ({_lifetimeCap})"
                            : $"{prefix}EXCEEDED_RESUME_STEP_BUDGET ({_budget})";
                }
            }

            private ValueTask<int> Hook(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                // WHY first: once tripped the resume is over, so no budget is read again - a memory reading
                // could even have dropped back under the line by now.
                if (_tripError != null)
                {
                    return SignalTrip(ctx, ct);
                }

                _steps++;
                if (_steps > _stepLimit)
                {
                    return RecordTrip(ctx, ct, LuaCsGuardTripKind.Steps, false);
                }

                long now = Stopwatch.GetTimestamp();
                if (now > _deadline)
                {
                    return RecordTrip(ctx, ct, LuaCsGuardTripKind.Timeout, false);
                }

                if (now >= _nextAllocationSampleTimestamp && IsAllocationExceeded(now))
                {
                    return RecordTrip(ctx, ct, LuaCsGuardTripKind.Memory, _allocation.CeilingExceeded);
                }

                return new ValueTask<int>(ctx.Return());
            }

            /// <summary>
            /// Records the trip of <paramref name="kind"/> - the enclosing run's when it lent the exhausted limit
            /// (see <see cref="LuaCsGuardedRun"/>), else this resume's own - and ends the resume.
            /// </summary>
            private ValueTask<int> RecordTrip(LuaFunctionExecutionContext ctx, CancellationToken ct,
                LuaCsGuardTripKind kind, bool ceilingExceeded)
            {
                LuaCsHostFunctionException lenderTrip = TripOfLender(kind, ceilingExceeded, ctx.State);
                if (lenderTrip != null)
                {
                    _trip = kind;
                    _tripError = lenderTrip;
                }
                else
                {
                    _trip = kind == LuaCsGuardTripKind.Steps && _lifetimeBinds
                        ? LuaCsGuardTripKind.LifetimeSteps
                        : kind;
                    _tripError = CreateOwnTrip(kind, ctx.State);
                }

                return SignalTrip(ctx, ct);
            }

            private ValueTask<int> SignalTrip(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                if (CancelGuardedRun(_runSource, ct))
                {
                    return new ValueTask<int>(ctx.Return());
                }

                throw ForeignContextTrip(_tripError);
            }

            private bool IsAllocationExceeded(long now)
            {
                _nextAllocationSampleTimestamp = now + AllocationSampleIntervalTicks;

                // WHY the confirming collection IsExceeded may force stays on this resume's wall clock:
                // excluding it would let a script that holds live memory near the budget and churns
                // garbage buy a forced collection per quarter budget of allocation, off the clock, and
                // hold the frame far past its slice; the timeout is the one hard bound on that.
                return _allocation.IsExceeded();
            }
        }
    }
}
