using System;
using System.Diagnostics;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Обёртка исполнения с таймаутом (best-effort) и перехватом исключений.
    /// </summary>
    public sealed class LuaExecutionGuard
    {
        private readonly int _timeoutMs;

        public LuaExecutionGuard(int timeoutMs = 2000)
        {
            _timeoutMs = timeoutMs;
        }

        public DynValue Execute(Script script, DynValue function, params DynValue[] args)
        {
            if (function.Type != DataType.Function)
                throw new ArgumentException("Expected Lua function.", nameof(function));

            var sw = Stopwatch.StartNew();
            try
            {
                var result = script.Call(function, args);
                if (sw.ElapsedMilliseconds > _timeoutMs)
                    throw new TimeoutException($"Lua exceeded {_timeoutMs} ms (elapsed {sw.ElapsedMilliseconds} ms).");
                return result;
            }
            catch (InterpreterException)
            {
                throw;
            }
        }
    }
}
