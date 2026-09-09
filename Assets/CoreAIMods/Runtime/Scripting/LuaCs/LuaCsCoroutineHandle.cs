using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// never yields is cut by the per-resume instruction/time budget armed via <see cref="LuaState.SetHook"/>
    /// (the same mechanism as <see cref="LuaCsExecutionGuard"/>) or by the lifetime step cap.
    ///
    /// There is deliberately NO MoonSharp-style <c>AutoYieldCounter</c>/<c>YieldRequest</c> loop:
    /// Lua-CSharp has no preemptive auto-yield, so one resume already returns at exactly one yield.
    /// </summary>
    public sealed class LuaCsCoroutineHandle
    {
        /// <summary>Default instruction-step budget for a single resume.</summary>
        public const int DefaultBudgetPerResume = 10_000;

        /// <summary>Default wall-clock budget for a single resume, in milliseconds.</summary>
        public const int DefaultResumeTimeoutMs = 500;

        /// <summary>
        /// Default cap on instruction steps a coroutine may consume across all resumes.
        /// Without it an infinite yield loop lives forever, burning the per-resume budget every frame.
        /// </summary>
        public const long DefaultTotalLifetimeSteps = 1_000_000;

        private static readonly LuaValue[] EmptyValues = Array.Empty<LuaValue>();

        private readonly LuaState _coroutine;
        private readonly LuaStack _callStack;
        private readonly CancellationTokenSource _cts;
        private readonly ResumeGuardHook _hook;
        private readonly int _budgetPerResume;
        private readonly int _resumeTimeoutMs;
        private readonly long _totalLifetimeSteps;
        private readonly LuaCsCoroutineBudgetSettings _liveResumeBudget;

        private bool _killed;
        private long _consumedSteps;
        private bool _lastOk = true;
        private LuaValue[] _lastValues = EmptyValues;
        private LuaValue _lastError = LuaValue.Nil;

        /// <summary>
        /// Creates a coroutine from <paramref name="function"/> on the owning <paramref name="ownerState"/>.
        /// A <see cref="LuaClosure"/> (a loaded chunk) is a <see cref="LuaFunction"/>, so it is accepted here too.
        /// </summary>
        /// <param name="ownerState">The state whose global runtime spawns the coroutine thread.</param>
        /// <param name="function">The coroutine body (bare function or loaded closure).</param>
        /// <param name="budgetPerResume">Instruction-step budget re-armed before every resume.</param>
        /// <param name="resumeTimeoutMs">Wall-clock budget, in ms, for a single resume.</param>
        /// <param name="totalLifetimeSteps">Cap on instruction steps across the whole coroutine lifetime.</param>
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
        public LuaCsCoroutineHandle(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps,
            bool isProtectedMode = true,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null)
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
            _liveResumeBudget = liveResumeBudget;

            _coroutine = ownerState.CreateCoroutine(function, isProtectedMode);
            _callStack = new LuaStack(8);
            _cts = new CancellationTokenSource();
            _hook = new ResumeGuardHook();
        }

        /// <summary>Convenience factory mirroring the constructor.</summary>
        public static LuaCsCoroutineHandle Create(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps,
            bool isProtectedMode = true,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null)
        {
            return new LuaCsCoroutineHandle(ownerState, function, budgetPerResume, resumeTimeoutMs,
                totalLifetimeSteps, isProtectedMode, liveResumeBudget);
        }

        /// <summary>Current Lua-CSharp thread status (Suspended/Normal/Running/Dead), or Dead once killed.</summary>
        public LuaThreadStatus Status => _killed ? LuaThreadStatus.Dead : _coroutine.GetStatus();

        /// <summary>True while the coroutine has not been killed and is not dead.</summary>
        public bool IsAlive => !_killed && _coroutine.GetStatus() != LuaThreadStatus.Dead;

        /// <summary>True when a further <see cref="Resume"/> is legal (suspended and not killed).</summary>
        public bool CanResume => !_killed && _coroutine.CanResume;

        /// <summary>True once the coroutine has finished (dead) or been killed; the runner may drop it.</summary>
        public bool IsFinished => _killed || _coroutine.GetStatus() == LuaThreadStatus.Dead;

        /// <summary>Instruction steps consumed across all resumes so far.</summary>
        public long ConsumedSteps => _consumedSteps;

        /// <summary>Cap on instruction steps across the whole coroutine lifetime.</summary>
        public long TotalLifetimeSteps => _totalLifetimeSteps;

        /// <summary>
        /// Restarts the lifetime step budget. Only a pooled signal runner calls this, at the moment it is
        /// re-armed for a fresh handler: each handler is a new logical thread, so it starts its lifetime
        /// budget from zero exactly as a freshly created coroutine would.
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
        /// Which per-resume budget cut the most recent resume, or <see cref="LuaCsGuardTripKind.None"/>
        /// when it ended by yield, return, or a script error of its own. Recorded by the guard hook at
        /// throw time — the typed counterpart of the budget text in <see cref="LastErrorText"/>, so a
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

            // WHY: Re-arm a fresh per-resume budget (instruction steps + wall clock) via SetHook, mirroring
            // LuaCsExecutionGuard. In protected mode a breach throws a LuaRuntimeException inside the VM,
            // which Lua-CSharp turns into an [ok=false, error] result and marks the thread Dead. The hook
            // object is built once per handle and re-armed here (like the guard's pooled GuardHook): a
            // fresh LuaFunction + closure + Stopwatch per resume was measured heap churn on every signal
            // handler and every task.wait loop resume.
            //
            // WHY read _liveResumeBudget here instead of caching it once: a shared
            // LuaCsCoroutineBudgetSettings is exactly the object ScriptContext:SetTimeout mutates, and a
            // long-lived pooled handle (a signal runner serving every Heartbeat fire) must pick up that
            // change on its very next resume — not only on a freshly constructed handle.
            int budgetPerResume = _liveResumeBudget?.BudgetPerResume ?? _budgetPerResume;
            int resumeTimeoutMs = _liveResumeBudget?.ResumeTimeoutMs ?? _resumeTimeoutMs;
            _hook.Arm(budgetPerResume, resumeTimeoutMs);
            _coroutine.SetHook(_hook.Function, string.Empty, 1);

            int count;
            try
            {
                // WHY: Single-step drive: a well-behaved handler reaches coroutine.yield synchronously, so
                // GetResult does not block the (single WASM) thread. A runaway is cut by the hook above.
                count = _coroutine.ResumeAsync(_callStack, _cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                try
                {
                    _coroutine.SetHook(null, string.Empty, 0);
                }
                catch
                {
                    /* ignore */
                }
            }

            _consumedSteps += _hook.Steps;
            CaptureResults(count);

            if (_consumedSteps >= _totalLifetimeSteps)
            {
                Kill();
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
        /// Builds the exception a per-resume guard hook throws to cut a runaway coroutine, carrying
        /// <paramref name="message"/> plus a best-effort " at line N" suffix as the Lua ERROR OBJECT.
        /// Shared with the raw <c>coroutine.resume</c> guard in <see cref="LuaCsSecureEnvironment"/>.
        /// </summary>
        /// <remarks>
        /// WHY the error-object constructor and not <c>LuaRuntimeException(LuaState, Exception)</c>:
        /// Lua-CSharp's protected coroutine resume reports a caught <see cref="LuaRuntimeException"/>
        /// as <c>[false, ex.ErrorObject]</c> and never reads <c>ex.Message</c>, while the
        /// <c>(LuaState, Exception)</c> overload leaves <c>ErrorObject</c> nil. A trip raised that way
        /// therefore came back as the literal text "nil": the scheduler classified the runaway as a
        /// BAD_ARGUMENT Lua bug, the fix hint said "fix the Lua error", and auto-repair was handed a
        /// diagnosis that named no bound. An unprotected resume rethrows the same exception, whose
        /// <c>Message</c> then embeds the error object, so the text survives on both paths.
        /// </remarks>
        internal static LuaRuntimeException CreateBudgetTrip(LuaState state, string message)
        {
            return new LuaRuntimeException(state, (LuaValue)(message + DescribeCurrentLine(state)));
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

        /// <summary>
        /// Reusable per-resume budget hook. A handle resumes one coroutine at a time (a nested resume of
        /// a non-suspended coroutine is rejected by the VM before any instruction runs), so one hook per
        /// handle is enough; its counters live in fields and are reset by <see cref="Arm"/>.
        /// </summary>
        private sealed class ResumeGuardHook
        {
            public readonly LuaFunction Function;

            private long _steps;
            private int _budget;
            private int _timeoutMs;
            private long _startTimestamp;
            private long _timeoutTicks;
            private LuaCsGuardTripKind _trip;

            public ResumeGuardHook()
            {
                Function = new LuaFunction("coreai_luacs_coroutine_guard", Hook);
            }

            /// <summary>Instruction steps charged during the current resume.</summary>
            public long Steps => _steps;

            /// <summary>Which budget tripped during the current resume, or <see cref="LuaCsGuardTripKind.None"/>.</summary>
            public LuaCsGuardTripKind Trip => _trip;

            public void Arm(int budget, int timeoutMs)
            {
                _steps = 0;
                _trip = LuaCsGuardTripKind.None;
                _budget = budget;
                _timeoutMs = timeoutMs;
                _startTimestamp = Stopwatch.GetTimestamp();
                _timeoutTicks = (long)timeoutMs * Stopwatch.Frequency / 1000;
            }

            private ValueTask<int> Hook(LuaFunctionExecutionContext ctx, CancellationToken ct)
            {
                _steps++;
                if (_steps > _budget)
                {
                    _trip = LuaCsGuardTripKind.Steps;
                    throw CreateBudgetTrip(ctx.State,
                        $"LuaCsCoroutineHandle: EXCEEDED_RESUME_STEP_BUDGET ({_budget})");
                }

                if (Stopwatch.GetTimestamp() - _startTimestamp > _timeoutTicks)
                {
                    _trip = LuaCsGuardTripKind.Timeout;
                    throw CreateBudgetTrip(ctx.State, $"Lua coroutine resume exceeded {_timeoutMs} ms.");
                }

                return new ValueTask<int>(ctx.Return());
            }
        }
    }
}
