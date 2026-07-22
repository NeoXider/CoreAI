using System;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IScriptState"/>: an opaque wrapper around one sandboxed
    /// <see cref="LuaState"/>. Only the adapter layer unwraps it.
    /// </summary>
    public sealed class LuaCsScriptState : IScriptState
    {
        internal LuaCsScriptState(LuaState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>The wrapped VM state (adapter-internal).</summary>
        internal LuaState State { get; }

        /// <summary>
        /// Unwraps a seam state back to its <see cref="LuaState"/>, rejecting states from other engines.
        /// </summary>
        internal static LuaState Unwrap(IScriptState state)
        {
            if (state is LuaCsScriptState luaState)
            {
                return luaState.State;
            }

            throw new ScriptRuntimeException(
                $"IScriptState of type '{state?.GetType().Name ?? "null"}' was not created by the Lua-CSharp engine.");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // WHY: A suspended Lua-CSharp state throws on Dispose (non-empty call stack) and mod states
            // were never explicitly disposed before this seam; lifetime stays GC-managed for parity.
        }
    }
}
