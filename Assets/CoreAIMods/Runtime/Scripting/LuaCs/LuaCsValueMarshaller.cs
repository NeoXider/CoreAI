using System;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IValueMarshaller"/> and the single conversion authority for the
    /// whole stack. Consolidates the previously scattered implementations byte-for-byte:
    /// the registry's rich <c>ToLuaValue</c>/<c>CoerceArgument</c>, the runtime/logic-slots scalar
    /// <c>HostToLua</c>, the logic-slots <c>ToClr</c>, and the runtime's cross-state
    /// <c>ToPortable</c>/<c>FromPortable</c> deep copy.
    /// </summary>
    public sealed class LuaCsValueMarshaller : IValueMarshaller
    {
        /// <summary>Shared stateless instance.</summary>
        public static readonly LuaCsValueMarshaller Instance = new();

        /// <inheritdoc />
        public object ToScriptValue(object hostValue)
        {
            return Box(ToLuaValue(hostValue));
        }

        /// <inheritdoc />
        public object ToScriptArgument(object hostValue)
        {
            return Box(HostToLua(hostValue));
        }

        /// <inheritdoc />
        public object ToHostValue(object scriptValue)
        {
            if (scriptValue is LuaValue value)
            {
                return ToClr(value);
            }

            return scriptValue;
        }

        /// <inheritdoc />
        public object ToPortable(object scriptValue, int maxTableDepth)
        {
            return ToPortableCore(Unbox(scriptValue), maxTableDepth, maxTableDepth);
        }

        /// <inheritdoc />
        public object FromPortable(object portable)
        {
            return Box(FromPortableCore(portable));
        }

        /// <inheritdoc />
        public ScriptValueKind GetKind(object scriptValue)
        {
            switch (scriptValue)
            {
                case null:
                    return ScriptValueKind.Nil;
                case LuaValue value:
                    return KindOf(value.Type);
                case LuaFunction:
                    return ScriptValueKind.Function;
                case LuaTable:
                case IScriptTable:
                    return ScriptValueKind.Table;
                case bool:
                    return ScriptValueKind.Boolean;
                case double or int or long or float or decimal or short or byte:
                    return ScriptValueKind.Number;
                case string:
                    return ScriptValueKind.String;
                default:
                    return ScriptValueKind.Other;
            }
        }

        /// <inheritdoc />
        public string Describe(object scriptValue)
        {
            if (scriptValue == null)
            {
                return "nil";
            }

            return scriptValue is LuaValue value ? value.ToString() : scriptValue.ToString();
        }

        // ---- Adapter-internal typed surface -----------------------------------------------------

        internal static object Box(LuaValue value)
        {
            return value;
        }

        /// <summary>Reads a seam-level object back into a <see cref="LuaValue"/> (host scalars included).</summary>
        internal static LuaValue Unbox(object value)
        {
            if (value is LuaValue lua)
            {
                return lua;
            }

            return HostToLua(value);
        }

        /// <summary>Rich host-to-Lua conversion (the registry's historical <c>ToLuaValue</c>).</summary>
        internal static LuaValue ToLuaValue(object value)
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

            if (value is LuaCsScriptTable view)
            {
                return new LuaValue(view.Table);
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

        /// <summary>Scalar host-to-Lua conversion (the runtime/logic-slots historical <c>HostToLua</c>).</summary>
        internal static LuaValue HostToLua(object arg)
        {
            switch (arg)
            {
                case null:
                    return LuaValue.Nil;
                case LuaValue lua:
                    return lua;
                case string s:
                    return new LuaValue(s);
                case bool b:
                    return new LuaValue(b);
                case double d:
                    return new LuaValue(d);
                case int i:
                    return new LuaValue((double)i);
                case long l:
                    return new LuaValue((double)l);
                case float f:
                    return new LuaValue((double)f);
                default:
                    return LuaValue.FromObject(arg);
            }
        }

        /// <summary>Lua-to-host conversion (the logic-slots historical <c>ToClr</c>).</summary>
        internal static object ToClr(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return null;
                case LuaValueType.Boolean:
                    return value.Read<bool>();
                case LuaValueType.Number:
                    return value.Read<double>();
                case LuaValueType.String:
                    return value.Read<string>();
                default:
                    return value.Read<object>();
            }
        }

        /// <summary>Coerces one script argument to a typed delegate parameter (registry parity).</summary>
        internal static object CoerceArgument(LuaValue value, Type parameterType)
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

            if (targetType == typeof(IScriptTable))
            {
                return new LuaCsScriptTable(value.Read<LuaTable>());
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

        private static bool IsNumericType(Type type)
        {
            return type == typeof(int) || type == typeof(long) ||
                   type == typeof(double) || type == typeof(float);
        }

        private static ScriptValueKind KindOf(LuaValueType type)
        {
            return type switch
            {
                LuaValueType.Nil => ScriptValueKind.Nil,
                LuaValueType.Boolean => ScriptValueKind.Boolean,
                LuaValueType.Number => ScriptValueKind.Number,
                LuaValueType.String => ScriptValueKind.String,
                LuaValueType.Table => ScriptValueKind.Table,
                LuaValueType.Function => ScriptValueKind.Function,
                _ => ScriptValueKind.Other
            };
        }

        private static object ToPortableCore(LuaValue value, int depth, int rootDepth)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return null;
                case LuaValueType.Boolean:
                    return value.Read<bool>();
                case LuaValueType.Number:
                    return value.Read<double>();
                case LuaValueType.String:
                    return value.Read<string>();
                case LuaValueType.Table:
                    if (depth <= 0)
                    {
                        throw new ArgumentException(
                            $"cross-mod tables may nest at most {rootDepth} levels.");
                    }

                    LuaTable table = value.Read<LuaTable>();
                    List<KeyValuePair<object, object>> pairs = new();
                    foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
                    {
                        pairs.Add(new KeyValuePair<object, object>(
                            ToPortableCore(pair.Key, depth - 1, rootDepth),
                            ToPortableCore(pair.Value, depth - 1, rootDepth)));
                    }

                    return pairs;
                default:
                    throw new ArgumentException(
                        $"cross-mod values must be nil/boolean/number/string/table (got {value.Type}).");
            }
        }

        private static LuaValue FromPortableCore(object value)
        {
            switch (value)
            {
                case null:
                    return LuaValue.Nil;
                case bool b:
                    return new LuaValue(b);
                case double d:
                    return new LuaValue(d);
                case string s:
                    return new LuaValue(s);
                case List<KeyValuePair<object, object>> pairs:
                {
                    LuaTable table = new();
                    foreach (KeyValuePair<object, object> pair in pairs)
                    {
                        table[FromPortableCore(pair.Key)] = FromPortableCore(pair.Value);
                    }

                    return new LuaValue(table);
                }
                default:
                    return LuaValue.Nil;
            }
        }
    }
}
