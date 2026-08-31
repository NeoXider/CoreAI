using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Registry of host callbacks exposed to secured Lua-CSharp scripts. Implements the engine-neutral
    /// <see cref="IScriptFunctionRegistry"/> seam; the Lua-typed <see cref="RegisterCallback(string, LuaFunction)"/>
    /// overloads remain as the engine-specific escape hatch for adapter-layer and legacy callers.
    /// Value conversion is delegated to <see cref="LuaCsValueMarshaller"/> (single authority).
    /// </summary>
    public sealed class LuaCsApiRegistry : IScriptFunctionRegistry
    {
        private readonly Dictionary<string, Delegate> _apis = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>>>
            _callbacks =
                new(StringComparer.Ordinal);

        private readonly Dictionary<string, LuaFunction> _luaFunctions = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Func<LuaValue>> _valueFactories = new(StringComparer.Ordinal);

        private readonly List<Action<LuaState>> _environmentDecorators = new();

        /// <summary>Registers a typed host delegate with the target runtime registry.</summary>
        public void Register(string name, Delegate callback)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("API name is required.", nameof(name));
            }

            _apis[name] = callback ?? throw new ArgumentNullException(nameof(callback));
            _callbacks.Remove(name);
            _luaFunctions.Remove(name);
            _valueFactories.Remove(name);
        }

        /// <summary>
        /// Registers a non-function global (table/userdata) built lazily per state. Engine-specific
        /// escape hatch like <see cref="RegisterCallback(string, LuaFunction)"/>: the Roblox API
        /// surface installs value globals (<c>Vector3</c>, <c>Enum</c>, <c>game</c>, ...) through it.
        /// WHY: the factory runs once per <see cref="ApplyTo"/> so mutable tables are never shared
        /// between mod states.
        /// </summary>
        public void RegisterValue(string name, Func<LuaValue> valueFactory)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("API name is required.", nameof(name));
            }

            _valueFactories[name] = valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));
            _apis.Remove(name);
            _callbacks.Remove(name);
            _luaFunctions.Remove(name);
        }

        /// <summary>
        /// Registers an engine-specific per-state decorator that runs after every ordinary global
        /// has been materialized. Adapter surfaces use this to extend state-local metatables without
        /// mutating VM-global behavior or bypassing production registry composition.
        /// </summary>
        public void RegisterEnvironmentDecorator(Action<LuaState> decorator)
        {
            _environmentDecorators.Add(
                decorator ?? throw new ArgumentNullException(nameof(decorator)));
        }

        /// <summary>Registers an engine-neutral var-args callback (raw arguments, multiple returns).</summary>
        public void RegisterVarArgs(string name, Func<ScriptCallContext, ScriptCallResult> callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            RegisterCallback(name, (ctx, ct) =>
            {
                ScriptCallResult result = callback(new LuaCsScriptCallContext(ctx));
                IReadOnlyList<object> values = result.Values;
                if (values.Count == 0)
                {
                    return new ValueTask<int>(ctx.Return());
                }

                LuaValue[] luaValues = new LuaValue[values.Count];
                for (int i = 0; i < values.Count; i++)
                {
                    luaValues[i] = LuaCsValueMarshaller.ToLuaValue(values[i]);
                }

                return new ValueTask<int>(ctx.Return(luaValues));
            });
        }

        /// <summary>Registers a Lua-CSharp callback when the API needs custom argument handling.</summary>
        public void RegisterCallback(
            string name,
            Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> callback)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("API name is required.", nameof(name));
            }

            _callbacks[name] = callback ?? throw new ArgumentNullException(nameof(callback));
            _apis.Remove(name);
            _luaFunctions.Remove(name);
            _valueFactories.Remove(name);
        }

        /// <summary>Registers a prebuilt Lua-CSharp callback.</summary>
        public void RegisterCallback(string name, LuaFunction callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("API name is required.", nameof(name));
            }

            _luaFunctions[name] = callback;
            _apis.Remove(name);
            _callbacks.Remove(name);
            _valueFactories.Remove(name);
        }

        /// <summary>Attempts to resolve a registered typed host delegate by name.</summary>
        public bool TryGet(string name, out Delegate callback)
        {
            return _apis.TryGetValue(name, out callback);
        }

        /// <summary>Attempts to resolve a registered Lua-CSharp callback by name.</summary>
        public bool TryGetCallback(
            string name,
            out Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> callback)
        {
            return _callbacks.TryGetValue(name, out callback);
        }

        /// <summary>True when <paramref name="name"/> is registered (tests / introspection).</summary>
        public bool Contains(string name)
        {
            return _apis.ContainsKey(name) || _callbacks.ContainsKey(name) || _luaFunctions.ContainsKey(name)
                   || _valueFactories.ContainsKey(name);
        }

        /// <summary>Exposes registered callbacks on a seam-created state's global environment.</summary>
        public void ApplyTo(IScriptState state)
        {
            ApplyToEnvironment(LuaCsScriptState.Unwrap(state));
        }

        /// <summary>Exposes registered callbacks on the Lua-CSharp global environment.</summary>
        public void ApplyToEnvironment(LuaState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            foreach (KeyValuePair<string, Delegate> kv in _apis)
            {
                state.Environment[kv.Key] = CreateFunction(kv.Key, kv.Value);
            }

            foreach (KeyValuePair<string, Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>>> kv in
                     _callbacks)
            {
                string name = kv.Key;
                Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> callback = kv.Value;
                state.Environment[name] = new LuaFunction(name, async (ctx, ct) =>
                {
                    try
                    {
                        return await callback(ctx, ct);
                    }
                    catch (LuaRuntimeException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw ToLuaRuntimeException(ctx.State, name, ex);
                    }
                });
            }

            foreach (KeyValuePair<string, LuaFunction> kv in _luaFunctions)
            {
                state.Environment[kv.Key] = kv.Value;
            }

            foreach (KeyValuePair<string, Func<LuaValue>> kv in _valueFactories)
            {
                state.Environment[kv.Key] = kv.Value();
            }

            for (int index = 0; index < _environmentDecorators.Count; index++)
            {
                _environmentDecorators[index](state);
            }
        }

        private static LuaFunction CreateFunction(string name, Delegate callback)
        {
            ParameterInfo[] parameters = callback.Method.GetParameters();
            return new LuaFunction(name, (ctx, ct) =>
            {
                try
                {
                    object[] args = CoerceArgsForDelegate(ctx, parameters);
                    object result = callback.DynamicInvoke(args);
                    return new ValueTask<int>(ctx.Return(LuaCsValueMarshaller.ToLuaValue(result)));
                }
                catch (LuaRuntimeException)
                {
                    throw;
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ToLuaRuntimeException(ctx.State, name, ex.InnerException);
                }
                catch (Exception ex)
                {
                    throw ToLuaRuntimeException(ctx.State, name, ex);
                }
            });
        }

        private static object[] CoerceArgsForDelegate(
            LuaFunctionExecutionContext ctx,
            ParameterInfo[] parameters)
        {
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                LuaValue value = ctx.HasArgument(i) ? ctx.GetArgument(i) : LuaValue.Nil;
                Type parameterType = parameters[i].ParameterType;
                args[i] = LuaCsValueMarshaller.CoerceArgument(value, parameterType);
            }

            return args;
        }

        private static LuaRuntimeException ToLuaRuntimeException(LuaState state, string name, Exception ex)
        {
            string message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = ex.GetType().Name;
            }

            return new LuaRuntimeException(state, new InvalidOperationException($"{name}: {message}", ex));
        }
    }
}
