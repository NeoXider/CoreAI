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
        private readonly long _maxSteps;

        /// <param name="timeoutMs">Мягкий лимит длительности вызова Lua (после исполнения).</param>
        /// <param name="maxSteps">Best-effort лимит «шагов» Lua (через debugger callbacks).</param>
        public LuaExecutionGuard(int timeoutMs = 2000, long maxSteps = 200_000)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
        }

        /// <summary>Вызвать Lua-функцию с проверкой таймаута по wall-clock.</summary>
        public DynValue Execute(Script script, DynValue function, params DynValue[] args)
        {
            if (function.Type != DataType.Function)
            {
                throw new ArgumentException("Expected Lua function.", nameof(function));
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // MoonSharp: без debugger бесконечный цикл может зависнуть навсегда.
                // Подключаем минимальный debugger, который ограничивает шаги и wall-clock (best-effort).
                script.AttachDebugger(new InstructionLimitDebugger(_maxSteps, _timeoutMs));
                DynValue result = script.Call(function, args);
                if (sw.ElapsedMilliseconds > _timeoutMs)
                {
                    throw new TimeoutException($"Lua exceeded {_timeoutMs} ms (elapsed {sw.ElapsedMilliseconds} ms).");
                }

                return result;
            }
            catch (InterpreterException)
            {
                throw;
            }
            finally
            {
                try
                {
                    script.DetachDebugger();
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}