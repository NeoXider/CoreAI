#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using System.Reflection;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Registry of host callbacks exposed to secured Lua scripts. One registry serves one
    /// <see cref="Script"/>: <see cref="ApplyToGlobals"/> binds callbacks to the globals table's
    /// owning script, so applying the same registry to a second script is not supported.
    /// </summary>
    public sealed class LuaApiRegistry
    {
        private readonly Dictionary<string, Delegate> _apis = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Func<ScriptExecutionContext, CallbackArguments, DynValue>> _callbacks =
            new(StringComparer.Ordinal);

        /// <summary>Registers a new value or callback with the target runtime registry.</summary>
        public void Register(string name, Delegate callback)
        {
            _apis[name] = callback;
            _callbacks.Remove(name);
        }

        /// <summary>Registers a Lua-facing callback when the API needs custom argument handling.</summary>
        public void RegisterCallback(
            string name,
            Func<ScriptExecutionContext, CallbackArguments, DynValue> callback)
        {
            _callbacks[name] = callback;
            _apis.Remove(name);
        }

        /// <summary>Attempts to resolve a registered Lua API callback by name.</summary>
        public bool TryGet(string name, out Delegate callback)
        {
            return _apis.TryGetValue(name, out callback);
        }

        /// <summary>True when <paramref name="name"/> is registered (tests / introspection).</summary>
        public bool Contains(string name)
        {
            return _apis.ContainsKey(name) || _callbacks.ContainsKey(name);
        }

        /// <summary>
        /// Exposes registered callbacks on the script global table. MoonSharp converts CLR
        /// delegates to Lua functions with typed marshalling — no manual DynamicInvoke layer.
        /// </summary>
        public void ApplyToGlobals(Table globals)
        {
            foreach (KeyValuePair<string, Delegate> kv in _apis)
            {
                string name = kv.Key;
                Delegate callback = kv.Value;
                // Built eagerly from the owning script: registries are one-per-script, and a lazy
                // ctx.GetScript() capture would only look safer while inviting cross-script reuse.
                // ParameterInfo[] is reflected once here instead of on every call.
                CallbackFunction moonSharpCallback = CallbackFunction.FromDelegate(globals.OwnerScript, callback);
                ParameterInfo[] parameters = callback.Method.GetParameters();

                globals[name] = DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        return moonSharpCallback.Invoke(ctx,
                            CoerceArgsForDelegate(parameters, args.GetArray()), args.IsMethodCall);
                    }
                    catch (InterpreterException)
                    {
                        throw;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        throw ToScriptRuntimeException(name, ex.InnerException);
                    }
                    catch (Exception ex)
                    {
                        throw ToScriptRuntimeException(name, ex);
                    }
                }, name);
            }

            foreach (KeyValuePair<string, Func<ScriptExecutionContext, CallbackArguments, DynValue>> kv in _callbacks)
            {
                string name = kv.Key;
                Func<ScriptExecutionContext, CallbackArguments, DynValue> callback = kv.Value;

                globals[name] = DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        return callback(ctx, args);
                    }
                    catch (InterpreterException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw ToScriptRuntimeException(name, ex);
                    }
                }, name);
            }
        }

        /// <summary>
        /// Model-friendly argument coercion for typed host delegates: LLM-written Lua constantly
        /// passes a whole entry from list-style results (a table like <c>{id=123, name=...}</c>)
        /// where an API expects the numeric id - MoonSharp's marshaller then fails with
        /// "cannot convert a table to a clr type System.Int32". When the delegate parameter is
        /// numeric and the argument is a table carrying a numeric <c>id</c> field, substitute that
        /// id so the intuitive code works instead of erroring.
        /// </summary>
        private static DynValue[] CoerceArgsForDelegate(ParameterInfo[] parameters, DynValue[] args)
        {
            for (int i = 0; i < args.Length && i < parameters.Length; i++)
            {
                if (args[i].Type != DataType.Table)
                {
                    continue;
                }

                Type parameterType = parameters[i].ParameterType;
                if (parameterType != typeof(int) && parameterType != typeof(long) &&
                    parameterType != typeof(double) && parameterType != typeof(float))
                {
                    continue;
                }

                DynValue id = args[i].Table.Get("id");
                if (id.Type == DataType.Number)
                {
                    args[i] = id;
                }
            }

            return args;
        }

        private static ScriptRuntimeException ToScriptRuntimeException(string name, Exception ex)
        {
            string message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = ex.GetType().Name;
            }

            return new ScriptRuntimeException($"{name}: {message}");
        }
    }
}
#endif