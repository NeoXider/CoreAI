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
    /// <c>GetAwaiter().GetResult()</c> never blocks a single-threaded WASM player loop. A runaway that
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
        private readonly int _budgetPerResume;
        private readonly int _resumeTimeoutMs;
        private readonly long _totalLifetimeSteps;

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
        public LuaCsCoroutineHandle(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps)
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

            // WHY: isProtectedMode: true means a hook/runtime error is converted to an [ok=false, error]
            // result and the thread transitions to Dead + disposes itself SYNCHRONOUSLY, instead of
            // throwing a C# exception that would leave the thread half-executed. That is exactly the
            // self-cleaning, no-hang behaviour we want for a host-driven top-level coroutine.
            _coroutine = ownerState.CreateCoroutine(function, true);
            _callStack = new LuaStack(8);
            _cts = new CancellationTokenSource();
        }

        /// <summary>Convenience factory mirroring the constructor.</summary>
        public static LuaCsCoroutineHandle Create(
            LuaState ownerState,
            LuaFunction function,
            int budgetPerResume = DefaultBudgetPerResume,
            int resumeTimeoutMs = DefaultResumeTimeoutMs,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps)
        {
            return new LuaCsCoroutineHandle(ownerState, function, budgetPerResume, resumeTimeoutMs, totalLifetimeSteps);
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
            // which Lua-CSharp turns into an [ok=false, error] result and marks the thread Dead.
            long steps = 0;
            Stopwatch sw = Stopwatch.StartNew();
            int budget = _budgetPerResume;
            int timeout = _resumeTimeoutMs;

            LuaFunction hook = new("coreai_luacs_coroutine_guard", (ctx, ct) =>
            {
                steps++;
                if (steps > budget)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException(
                            $"LuaCsCoroutineHandle: EXCEEDED_RESUME_STEP_BUDGET ({budget})"));
                }

                if (sw.ElapsedMilliseconds > timeout)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new TimeoutException($"Lua coroutine resume exceeded {timeout} ms."));
                }

                return new ValueTask<int>(ctx.Return());
            });

            _coroutine.SetHook(hook, string.Empty, 1);

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

            _consumedSteps += steps;
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
    }
}
