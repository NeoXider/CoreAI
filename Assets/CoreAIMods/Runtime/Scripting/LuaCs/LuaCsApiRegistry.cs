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
                    // WHY: DynamicInvoke wraps whatever the delegate threw, so a Lua error from a nested
                    // guarded call reaches here instead of the clause above; ToLuaRuntimeException rethrows it.
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

        /// <summary>
        /// Converts a registered host function's failure into the VM error Lua code sees as
        /// "<c>name: message</c>". A Lua error that crossed the function (a nested guarded call
        /// unwrapped from <see cref="TargetInvocationException"/>) is returned unchanged, as the
        /// callback path already rethrows it, so its own error value and budget-trip cause survive.
        /// </summary>
        private static LuaRuntimeException ToLuaRuntimeException(LuaState state, string name, Exception ex)
        {
            if (ex is LuaRuntimeException lua)
            {
                return lua;
            }

            string message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = ex.GetType().Name;
            }

            return new LuaCsHostFunctionException(state, $"{name}: {message}", ex);
        }
    }

    /// <summary>
    /// The Lua error raised by a host (C#) function: a failing API call, a sandbox library refusal
    /// (a <c>string.rep</c>/<c>table.concat</c>/<c>string.format</c> cap) or a guard hook cutting the
    /// script on a budget. Lua code receives exactly <see cref="Message"/> as a string error value,
    /// through <c>pcall</c>, <c>xpcall</c> and a protected <c>coroutine.resume</c> alike: the host's own
    /// one-line text (on the Roblox surface the §5.2.7 <c>[mod:id script:path line:n] CODE: message |
    /// fix: ...</c> line), never a CLR type name, a managed stack trace or a source path. C# code reads
    /// the original exception from <see cref="HostException"/>, which engine-neutral walkers reach through
    /// <see cref="IScriptHostFailure"/>.
    /// </summary>
    public sealed class LuaCsHostFunctionException : LuaRuntimeException, IScriptHostFailure
    {
        private readonly string _message;

        // WHY the error-object base constructor and not LuaRuntimeException(LuaState, Exception): whenever
        // InnerException is set, Lua-CSharp's pcall hands the script InnerException.ToString() - the host
        // exception's type names, its managed stack trace and absolute source paths, about 1,600 chars per
        // refusal, enough for four refusals to overflow execute_lua's result cap - while xpcall and a
        // protected coroutine.resume read ErrorObject, which that constructor leaves nil. The error-object
        // constructor gives all three the same text, but it cannot also set InnerException (not virtual,
        // no setter), so the cause travels as HostException. Level 0 keeps pcall's text free of a
        // "chunk:line:" position, as Lua reports an error raised by a C function.
        /// <summary>
        /// Creates the error for <paramref name="hostException"/>, shown to Lua as <paramref name="message"/>.
        /// </summary>
        /// <param name="state">The state whose host function failed; may be null.</param>
        /// <param name="message">The exact error text Lua code receives.</param>
        /// <param name="hostException">The exception the host function threw.</param>
        public LuaCsHostFunctionException(LuaState state, string message, Exception hostException)
            : base(state, (LuaValue)(message ?? string.Empty), 0)
        {
            _message = message ?? string.Empty;
            HostException = hostException;
        }

        /// <summary>
        /// The exception the host function threw (for example an <c>RbxError</c> carrying its code and
        /// mod context, or a guard's <see cref="LuaMemoryBudgetException"/>); null for a refusal that has
        /// no separate cause. Not exposed as <see cref="Exception.InnerException"/>: see the constructor's WHY.
        /// </summary>
        public Exception HostException { get; }

        /// <summary>
        /// The Lua-visible error text, identical to the error value, without the "Lua-CSharp: " and
        /// position decoration the base type adds to an error object's message.
        /// </summary>
        public override string Message => _message;

        /// <summary>
        /// Next link of a cause chain: <see cref="HostException"/> for this type, otherwise
        /// <see cref="Exception.InnerException"/>. Walkers that classify a failure by the TYPE of a
        /// wrapped cause step with this so a host function's error does not end the chain. The same
        /// rule as the engine-neutral <see cref="ScriptExecutionErrors.NextCause"/>, which it delegates to.
        /// </summary>
        public static Exception NextCause(Exception exception)
        {
            return ScriptExecutionErrors.NextCause(exception);
        }
    }
}
