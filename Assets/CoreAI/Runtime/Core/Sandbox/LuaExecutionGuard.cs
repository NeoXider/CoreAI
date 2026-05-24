using System;
using System.Diagnostics;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Provides lua execution guard functionality.
    /// </summary>
    public sealed class LuaExecutionGuard
    {
        private readonly int _timeoutMs;
        private readonly long _maxSteps;

        /// <param name="timeoutMs">The timeout ms value.</param>
        /// <param name="maxSteps">The max steps value.</param>
        public LuaExecutionGuard(int timeoutMs = 2000, long maxSteps = 200_000)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
        }

        /// <summary>Executes a Lua function through the instruction guard.</summary>
        public DynValue Execute(Script script, DynValue function, params DynValue[] args)
        {
            if (function.Type != DataType.Function)
            {
                throw new ArgumentException("Expected Lua function.", nameof(function));
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                /* Implementation note in English. */
                /* Implementation note in English. */
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
