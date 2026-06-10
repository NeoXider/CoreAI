#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    public sealed class LuaCoroutineHandle
    {
        public const int DefaultBudgetPerResume = 10_000;

        private readonly Script _script;
        private readonly DynValue _coroutine;
        private readonly InstructionLimitDebugger _debugger;
        private readonly int _budgetPerResume;

        private bool _disposed;
        private DynValue _lastResult;

        internal LuaCoroutineHandle(
            Script script,
            DynValue coroutine,
            InstructionLimitDebugger debugger,
            int budgetPerResume = DefaultBudgetPerResume)
        {
            _script = script ?? throw new ArgumentNullException(nameof(script));
            _coroutine = coroutine ?? throw new ArgumentNullException(nameof(coroutine));
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
            _budgetPerResume = budgetPerResume > 0 ? budgetPerResume : DefaultBudgetPerResume;
        }

        public bool IsAlive =>
            !_disposed &&
            _coroutine.Coroutine.State != CoroutineState.Dead;

        public CoroutineState State => _coroutine.Coroutine.State;

        public DynValue LastResult => _lastResult;

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
