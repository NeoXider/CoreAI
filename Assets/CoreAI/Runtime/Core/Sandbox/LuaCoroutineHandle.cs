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
                }
                catch
                {
                }
            }

            _disposed = true;
        }
    }
}