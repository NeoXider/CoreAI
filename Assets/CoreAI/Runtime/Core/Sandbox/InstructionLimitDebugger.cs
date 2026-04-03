using System;
using System.Diagnostics;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Минимальный debugger для MoonSharp, который лимитирует количество шагов (best-effort) и wall-clock.
    /// Нужен, чтобы <c>while true do end</c> не мог зависнуть навсегда.
    /// </summary>
    internal sealed class InstructionLimitDebugger : IDebugger
    {
        private readonly long _maxSteps;
        private readonly int _timeoutMs;
        private readonly Stopwatch _sw = new();
        private long _steps;

        public InstructionLimitDebugger(long maxSteps, int timeoutMs)
        {
            _maxSteps = maxSteps < 1 ? 1 : maxSteps;
            _timeoutMs = timeoutMs < 1 ? 1 : timeoutMs;
            _sw.Start();
        }

        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;

        public void SetDebugService(DebugService debugService)
        {
        }

        public void SetSourceCode(SourceCode sourceCode)
        {
        }

        public void SetByteCode(string[] bytecode)
        {
        }

        public void RefreshBreakpoints(IEnumerable<SourceRef> refs)
        {
        }

        public bool IsPauseRequested() => false;

        public DebuggerAction GetAction(int ip, SourceRef sourceref)
        {
            var s = System.Threading.Interlocked.Increment(ref _steps);
            if (s > _maxSteps)
                throw new ScriptRuntimeException($"Lua exceeded max steps: {_maxSteps}");
            if (_sw.ElapsedMilliseconds > _timeoutMs)
                throw new ScriptRuntimeException($"Lua exceeded {_timeoutMs} ms.");
            return new DebuggerAction { Action = DebuggerAction.ActionType.Run };
        }

        public void SignalRuntimeException(ScriptRuntimeException ex)
        {
        }

        public void SignalExecutionEnded()
        {
        }

        public WatchItem[] GetWatchItems() => Array.Empty<WatchItem>();

        public void Update(WatchType watchType)
        {
        }
    }
}

