using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lua;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Registry of host callbacks exposed to secured Lua-CSharp scripts.
    /// </summary>
    public sealed class LuaCsApiRegistry
    {
        private readonly Dictionary<string, Delegate> _apis = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>>>
            _callbacks =
                new(StringComparer.Ordinal);

        private readonly Dictionary<string, LuaFunction> _luaFunctions = new(StringComparer.Ordinal);

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
            return _apis.ContainsKey(name) || _callbacks.ContainsKey(name) || _luaFunctions.ContainsKey(name);
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
                    return new ValueTask<int>(ctx.Return(ToLuaValue(result)));
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
                args[i] = CoerceArgument(value, parameterType);
            }

            return args;
        }

        private static object CoerceArgument(LuaValue value, Type parameterType)
        {
            Type targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (value.Type == LuaValueType.Table && IsNumericType(targetType))
            {
                LuaTable table = value.Read<LuaTable>();
                LuaValue id = table["id"];
                if (id.Type == LuaValueType.Number)
                {
                    value = id;
                }
            }

            if (value.Type == LuaValueType.Nil)
            {
                return parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null
                    ? Activator.CreateInstance(parameterType)
                    : null;
            }

            if (targetType == typeof(LuaValue))
            {
                return value;
            }

            if (targetType == typeof(LuaTable))
            {
                return value.Read<LuaTable>();
            }

            if (targetType == typeof(string))
            {
                return value.Read<string>();
            }

            if (targetType == typeof(bool))
            {
                return value.Read<bool>();
            }

            if (targetType == typeof(double))
            {
                return value.Read<double>();
            }

            if (targetType == typeof(float))
            {
                return (float)value.Read<double>();
            }

            if (targetType == typeof(int))
            {
                return Convert.ToInt32(value.Read<double>());
            }

            if (targetType == typeof(long))
            {
                return Convert.ToInt64(value.Read<double>());
            }

            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, Convert.ToInt32(value.Read<double>()));
            }

            object obj = value.Read<object>();
            return obj == null || targetType.IsInstanceOfType(obj) ? obj : Convert.ChangeType(obj, targetType);
        }

        private static LuaValue ToLuaValue(object value)
        {
            if (value == null)
            {
                return LuaValue.Nil;
            }

            if (value is LuaValue luaValue)
            {
                return luaValue;
            }

            if (value is LuaFunction function)
            {
                return new LuaValue(function);
            }

            if (value is LuaTable table)
            {
                return new LuaValue(table);
            }

            if (value is bool b)
            {
                return new LuaValue(b);
            }

            if (value is string s)
            {
                return new LuaValue(s);
            }

            if (value is int or long or float or double or decimal or short or byte)
            {
                return new LuaValue(Convert.ToDouble(value));
            }

            if (value is IDictionary dictionary)
            {
                LuaTable result = new();
                foreach (DictionaryEntry entry in dictionary)
                {
                    result[ToLuaValue(entry.Key)] = ToLuaValue(entry.Value);
                }

                return new LuaValue(result);
            }

            if (value is IEnumerable enumerable)
            {
                LuaTable result = new();
                int index = 1;
                foreach (object item in enumerable)
                {
                    result[new LuaValue((double)index++)] = ToLuaValue(item);
                }

                return new LuaValue(result);
            }

            return LuaValue.FromObject(value);
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(int) || type == typeof(long) ||
                   type == typeof(double) || type == typeof(float);
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
