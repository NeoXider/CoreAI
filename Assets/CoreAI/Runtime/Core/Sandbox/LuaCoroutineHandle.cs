#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    public sealed class LuaCoroutineHandle
    {
        public const int DefaultBudgetPerResume = 10_000;

        /// <summary>
        /// Default cap on instruction steps a coroutine may consume across all resumes.
        /// Without it an infinite yield loop lives forever, burning the per-resume budget every frame.
        /// </summary>
        public const long DefaultTotalLifetimeSteps = 1_000_000;

        private readonly Script _script;
        private readonly DynValue _coroutine;
        private readonly InstructionLimitDebugger _debugger;
        private readonly int _budgetPerResume;
        private readonly long _totalLifetimeSteps;

        private bool _disposed;
        private DynValue _lastResult;
        private long _consumedSteps;

        internal LuaCoroutineHandle(
            Script script,
            DynValue coroutine,
            InstructionLimitDebugger debugger,
            int budgetPerResume = DefaultBudgetPerResume,
            long totalLifetimeSteps = DefaultTotalLifetimeSteps)
        {
            _script = script ?? throw new ArgumentNullException(nameof(script));
            _coroutine = coroutine ?? throw new ArgumentNullException(nameof(coroutine));
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
            _budgetPerResume = budgetPerResume > 0 ? budgetPerResume : DefaultBudgetPerResume;
            _totalLifetimeSteps = totalLifetimeSteps > 0 ? totalLifetimeSteps : DefaultTotalLifetimeSteps;
        }

        public bool IsAlive =>
            !_disposed &&
            _coroutine.Coroutine.State != CoroutineState.Dead;

        public CoroutineState State => _coroutine.Coroutine.State;

        public DynValue LastResult => _lastResult;

        /// <summary>Instruction steps consumed across all resumes so far.</summary>
        public long ConsumedSteps => _consumedSteps;

        /// <summary>Cap on instruction steps across the whole coroutine lifetime.</summary>
        public long TotalLifetimeSteps => _totalLifetimeSteps;

        public DynValue Resume()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LuaCoroutineHandle));
            }

            if (!IsAlive)
            {
                throw new InvalidOperationException($"Cannot resume coroutine in state {_coroutine.Coroutine.State}.");
            }

            _debugger.Reset(_budgetPerResume, 500);

            _lastResult = _coroutine.Coroutine.Resume();
            _consumedSteps += _debugger.Steps;

            // MoonSharp preemptive yield (AutoYieldCounter, e.g. set in Kill): host must
            // resume with no args until a real result — see moonsharp.org/coroutines.html.
            while (_lastResult.Type == DataType.YieldRequest && IsAlive && !_disposed)
            {
                if (_consumedSteps >= _totalLifetimeSteps)
                {
                    Kill();
                    break;
                }

                _debugger.Reset(_budgetPerResume, 500);
                _lastResult = _coroutine.Coroutine.Resume();
                _consumedSteps += _debugger.Steps;
            }

            if (_consumedSteps >= _totalLifetimeSteps)
            {
                Kill();
            }

            return _lastResult;
        }

        public void Kill()
        {
            if (!_disposed && IsAlive)
            {
                try
                {
                    // MoonSharp's Coroutine type does not expose a public ForceKill/Dispose
                    // that transitions the underlying Processor state to Dead. The only
                    // public way to influence a suspended coroutine's lifecycle is via
                    // AutoYieldCounter, which forces the VM to yield back to the caller
                    // on its next instruction instead of continuing to run. Setting it to
                    // a minimal value ensures that if anything resumes this coroutine
                    // again before _disposed is observed, it yields immediately rather
                    // than performing further work. The actual termination guarantee for
                    // this handle comes from _disposed: once set, Resume() throws
                    // ObjectDisposedException and IsAlive reports false, so the coroutine
                    // can never be resumed again through this handle and is left to be
                    // garbage collected with the rest of the script state.
                    _coroutine.Coroutine.AutoYieldCounter = 1;
                }
                catch (ScriptRuntimeException)
                {
                    // Coroutine was already in a state (e.g. Dead) where touching its
                    // internals raises a script-level error; nothing left to terminate.
                }
                catch (InvalidOperationException)
                {
                    // Coroutine type does not support this operation (e.g. CLR callback
                    // coroutines); nothing left to terminate.
                }
            }

            _disposed = true;
        }
    }
}
#endif
