using System;
using System.Collections.Generic;
using System.Diagnostics;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Tree.Expressions;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// MoonSharp debugger used to stop scripts that exceed instruction limits, wall-clock time, or a
    /// total GC allocation budget. The allocation check runs on every VM instruction, same as the
    /// wall-clock check above it: <see cref="GC.GetTotalMemory(bool)"/> is a cheap heap-size
    /// counter read, not a GC pass, so it is cheap enough to check unconditionally. A coarser sampling
    /// interval was considered, but concatenation-doubling (<c>s = s .. s</c>) grows exponentially — a
    /// handful of loop iterations can jump from megabytes to gigabytes, so any interval wide enough to
    /// matter for performance is also wide enough to miss the attack (or let the runtime hit a real
    /// out-of-memory condition) before the next sample point.
    /// </summary>
    internal sealed class InstructionLimitDebugger : IDebugger
    {
        /// <summary>Default per-execution GC allocation budget enforced between VM instructions.</summary>
        public const long DefaultMaxAllocatedBytesBudget = 64 * 1024 * 1024;

        private long _maxSteps;
        private int _timeoutMs;
        private long _maxAllocatedBytes;
        private readonly Stopwatch _sw = new();
        private long _steps;
        private long _allocBaseline;

        public InstructionLimitDebugger(long maxSteps, int timeoutMs, long maxAllocatedBytes = DefaultMaxAllocatedBytesBudget)
        {
            _maxAllocatedBytes = maxAllocatedBytes;
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
            // GC.GetTotalMemory(false), NOT GC.GetAllocatedBytesForCurrentThread: Unity's Mono does not
            // implement the thread-local counter (it returns 0 unconditionally — verified empirically),
            // so a budget based on it can never fire. Heap total is process-wide and noisy, but an
            // allocation bomb overwhelms that noise within a few doublings.
            _allocBaseline = GC.GetTotalMemory(false);
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

            // Allocation-bomb backstop: string.rep/string.format are capped at the library-function
            // level, but plain concatenation (s = s .. s) or a loop of table.insert calls has no single
            // interceptable call site — it is ordinary VM opcodes. Checking total thread allocations
            // between instructions is the only place this VM exposes to catch that pattern.
            if (_maxAllocatedBytes > 0)
            {
                long allocated = GC.GetTotalMemory(false) - _allocBaseline;
                if (allocated > _maxAllocatedBytes)
                {
                    throw new ScriptRuntimeException(
                        $"SecureLuaEnvironment: EXCEEDED_MEMORY_BUDGET ({_maxAllocatedBytes} bytes)");
                }
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
