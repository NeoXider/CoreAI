using System;
using System.Threading;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;
using Lua.Runtime;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IScriptExecutionGuard"/> wrapping the concrete
    /// <see cref="LuaCsExecutionGuard"/> (per-instruction step/time/allocation hook).
    /// </summary>
    public sealed class LuaCsScriptExecutionGuard : IScriptExecutionGuard
    {
        private readonly LuaCsExecutionGuard _inner;

        public LuaCsScriptExecutionGuard(LuaCsExecutionGuard inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public LuaCsScriptExecutionGuard(IExecutionBudget budget,
            IRbxRuntimeObservabilitySink observability = null,
            ILuaCsGuardObserver guardObserver = null)
            : this(new LuaCsExecutionGuard(
                budget?.TimeoutMs ?? ExecutionBudget.DefaultTimeoutMs,
                budget?.MaxSteps ?? ExecutionBudget.DefaultMaxSteps,
                budget?.MaxAllocatedBytes ?? ExecutionBudget.DefaultMaxAllocatedBytes,
                observability,
                guardObserver))
        {
        }

        /// <summary>The wrapped concrete guard (adapter-internal, used by the engine's chunk runner).</summary>
        internal LuaCsExecutionGuard Inner => _inner;

        /// <inheritdoc />
        public object[] Invoke(IScriptState state, object callable, params object[] args)
        {
            return Invoke(state, callable, CancellationToken.None, args);
        }

        /// <inheritdoc />
        public object[] Invoke(IScriptState state, object callable, CancellationToken cancellationToken, object[] args)
        {
            LuaState lua = LuaCsScriptState.Unwrap(state);
            LuaFunction function = UnwrapCallable(callable);

            args ??= Array.Empty<object>();
            LuaValue[] luaArgs = args.Length == 0 ? Array.Empty<LuaValue>() : new LuaValue[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                luaArgs[i] = LuaCsValueMarshaller.Unbox(args[i]);
            }

            LuaValue[] results = _inner.Execute(lua, function, cancellationToken, luaArgs);
            if (results.Length == 0)
            {
                return Array.Empty<object>();
            }

            object[] boxed = new object[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                boxed[i] = LuaCsValueMarshaller.Box(results[i]);
            }

            return boxed;
        }

        /// <summary>Reads a seam-level callable back to a <see cref="LuaFunction"/>.</summary>
        internal static LuaFunction UnwrapCallable(object callable)
        {
            switch (callable)
            {
                case LuaFunction function:
                    return function;
                case LuaValue value when value.Type == LuaValueType.Function:
                    return value.Read<LuaFunction>();
                default:
                    throw new ScriptRuntimeException(
                        $"callable of type '{callable?.GetType().Name ?? "null"}' is not a Lua-CSharp function.");
            }
        }
    }
}
