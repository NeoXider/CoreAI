#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Tree.Expressions;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// MoonSharp debugger used to stop scripts that exceed instruction limits.
    /// </summary>
    internal sealed class InstructionLimitDebugger : IDebugger
    {
        private long _maxSteps;
        private int _timeoutMs;
        private readonly Stopwatch _sw = new();
        private long _steps;

        public InstructionLimitDebugger(long maxSteps, int timeoutMs)
        {
            Reset(maxSteps, timeoutMs);
        }

        /// <summary>Instruction steps consumed since the last <see cref="Reset"/>.</summary>
        public long Steps => System.Threading.Interlocked.Read(ref _steps);

        public void Reset(long maxSteps, int timeoutMs)
        {
            _maxSteps = maxSteps < 1 ? 1 : maxSteps;
            _timeoutMs = timeoutMs < 1 ? 1 : timeoutMs;
            _steps = 0;
            _sw.Restart();
        }

        public DebuggerCaps GetDebuggerCaps()
        {
            return DebuggerCaps.CanDebugSourceCode | DebuggerCaps.HasLineBasedBreakpoints;
        }

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

        public bool IsPauseRequested()
        {
            return true;
        }

        public DebuggerAction GetAction(int ip, SourceRef sourceref)
        {
            long s = System.Threading.Interlocked.Increment(ref _steps);
            if (s > _maxSteps)
            {
                throw new ScriptRuntimeException($"SecureLuaEnvironment: EXCEEDED_HARD_LIMIT_STEPS ({_maxSteps})");
            }

            if (_sw.ElapsedMilliseconds > _timeoutMs)
            {
                throw new ScriptRuntimeException($"Lua exceeded {_timeoutMs} ms.");
            }

            return new DebuggerAction { Action = DebuggerAction.ActionType.StepIn };
        }

        public bool SignalRuntimeException(ScriptRuntimeException ex)
        {
            return false;
        }

        public void SignalExecutionEnded()
        {
        }

        public List<DynamicExpression> GetWatchItems()
        {
            return new List<DynamicExpression>();
        }

        public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex)
        {
        }
    }
}
#endif
