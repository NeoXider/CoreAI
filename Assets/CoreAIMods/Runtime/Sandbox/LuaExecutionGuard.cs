using System;
using System.Diagnostics;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Runs Lua functions with timeout and instruction-step limits.
    /// <para>
    /// Scope of the guarantee: limits are enforced <em>between Lua VM instructions</em> (via
    /// <see cref="InstructionLimitDebugger"/>) plus a post-call wall-clock check. A single host
    /// callback that blocks (slow reflection in <c>unity_call</c>, a large scene scan) cannot be
    /// preempted mid-call — MoonSharp has no CLR-call interruption. Host bindings must therefore
    /// bound their own worst case (result caps, clamped scan sizes) rather than rely on this guard.
    /// </para>
    /// <para>
    /// The same instruction hook also checks total GC allocations on every instruction (see
    /// <see cref="InstructionLimitDebugger"/>) and aborts once <paramref name="maxAllocatedBytes"/> is
    /// exceeded. This is the only backstop against allocation bombs built from plain string
    /// concatenation (<c>s = s .. s</c>), which is ordinary VM opcodes with no library call site to cap.
    /// </para>
    /// </summary>
    public sealed class LuaExecutionGuard
    {
        private readonly int _timeoutMs;
        private readonly long _maxSteps;
        private readonly long _maxAllocatedBytes;

        /// <param name="timeoutMs">Maximum wall-clock time allowed for one guarded call.</param>
        /// <param name="maxSteps">Maximum MoonSharp instruction steps allowed for one guarded call.</param>
        /// <param name="maxAllocatedBytes">
        /// Maximum total GC allocation (bytes) permitted for one guarded call, checked on every VM
        /// instruction. Defaults to
        /// <see cref="InstructionLimitDebugger.DefaultMaxAllocatedBytesBudget"/> (256MB).
        /// </param>
        public LuaExecutionGuard(
            int timeoutMs = 2000,
            long maxSteps = 200_000,
            long maxAllocatedBytes = InstructionLimitDebugger.DefaultMaxAllocatedBytesBudget)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
            _maxAllocatedBytes = maxAllocatedBytes;
        }

        /// <summary>Runs a Lua function through the instruction guard.</summary>
        public DynValue Execute(Script script, DynValue function, params DynValue[] args)
        {
            if (function.Type != DataType.Function)
            {
                throw new ArgumentException("Expected Lua function.", nameof(function));
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // Attach a fresh debugger for this call so reused scripts do not carry stale counters.
                script.AttachDebugger(new InstructionLimitDebugger(_maxSteps, _timeoutMs, _maxAllocatedBytes));
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
