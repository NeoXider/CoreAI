using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CoreAI.Scripting.LuaCs;
using Lua;
using UnityEngine;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// VM-specific half of <see cref="LuaCsFullUnityRuntimeBindings"/>: converts raw script values from
    /// the seam to CLR reflection arguments (incl. Unity math/color table shapes) and CLR results back
    /// to script values. A second engine reimplements exactly this partial.
    /// </summary>
    public sealed partial class LuaCsFullUnityRuntimeBindings
    {
        /// <summary>Converts a CLR value to a raw script value with the Full-tier rendering rules.</summary>
        private static object ClrToScriptValue(object value)
        {
            return LuaCsValueMarshaller.Box(ToLuaValue(value));
        }

        /// <summary>Converts a raw script value to a CLR argument of the given target type.</summary>
        private static object ConvertScriptArg(object raw, Type targetType)
        {
            return ConvertArg(AsLuaValue(raw), targetType);
        }

        /// <summary>Converts a raw script value to the CLR type of the given field/property.</summary>
        private static object ConvertScriptMember(object raw, MemberInfo member)
        {
            return FromLuaValue(AsLuaValue(raw), member);
        }

        private static LuaValue AsLuaValue(object raw)
        {
            return LuaCsValueMarshaller.Unbox(raw);
        }

        private static LuaValue ToLuaValue(object value)
        {
            if (value == null)
            {
                return LuaValue.Nil;
            }

            switch (value)
            {
                case LuaValue lua:
                    return lua;
                case LuaTable table:
                    return new LuaValue(table);
                case bool b:
                    return new LuaValue(b);
                case string s:
                    return new LuaValue(s);
                case int i:
                    return new LuaValue((double)i);
                case long l:
                    return new LuaValue((double)l);
                case float f:
                    return new LuaValue((double)f);
                case double d:
                    return new LuaValue(d);
                case IDictionary<string, object> dict:
                {
                    LuaTable table = new();
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        table[kv.Key] = ToLuaValue(kv.Value);
                    }

                    return new LuaValue(table);
                }
                case IEnumerable<string> strings:
                {
                    LuaTable table = new();
                    int index = 1;
                    foreach (string item in strings)
                    {
                        table[new LuaValue((double)index++)] = new LuaValue(item);
                    }

                    return new LuaValue(table);
                }
                case IEnumerable<object> list:
                {
                    LuaTable table = new();
                    int index = 1;
                    foreach (object item in list)
                    {
                        table[new LuaValue((double)index++)] = ToLuaValue(item);
                    }

                    return new LuaValue(table);
                }
                default:
                    return new LuaValue(value.ToString());
            }
        }

        private static object FromLuaValue(LuaValue value, MemberInfo member)
        {
            Type targetType = member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => typeof(object)
            };
            return ConvertArg(value, targetType);
        }

        private static object ConvertArg(LuaValue value, Type targetType)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return ReadStringValue(value);
            }

            if (targetType == typeof(bool))
            {
                return value.Type == LuaValueType.Boolean ? value.Read<bool>() : value.Read<double>() != 0d;
            }

            if (targetType == typeof(int))
            {
                return (int)ReadNumber(value);
            }

            if (targetType == typeof(float))
            {
                return (float)ReadNumber(value);
            }

            if (targetType == typeof(double))
            {
                return ReadNumber(value);
            }

            if (targetType == typeof(long))
            {
                return (long)ReadNumber(value);
            }

            if (targetType == typeof(uint))
            {
                return (uint)ReadNumber(value);
            }

            if (targetType == typeof(ulong))
            {
                return (ulong)ReadNumber(value);
            }

            if (targetType == typeof(short))
            {
                return (short)ReadNumber(value);
            }

            if (targetType == typeof(ushort))
            {
                return (ushort)ReadNumber(value);
            }

            if (targetType == typeof(byte))
            {
                return (byte)ReadNumber(value);
            }

            if (targetType == typeof(sbyte))
            {
                return (sbyte)ReadNumber(value);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.String)
            {
                return Enum.Parse(targetType, value.Read<string>(), true);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.Number)
            {
                return Enum.ToObject(targetType, (long)ReadNumber(value));
            }

            if (targetType == typeof(Vector3) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector3(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"));
            }

            if (targetType == typeof(Vector2) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector2(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"));
            }

            if (targetType == typeof(Vector4) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector4(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"),
                    (float)ReadRequiredTableNumber(t, "w"));
            }

            if (targetType == typeof(Color))
            {
                if (value.Type == LuaValueType.String)
                {
                    string text = value.Read<string>();
                    if (ColorUtility.TryParseHtmlString(text, out Color color))
                    {
                        return color;
                    }

                    throw new InvalidOperationException($"Could not parse Color from '{text}'.");
                }

                if (value.Type == LuaValueType.Table)
                {
                    LuaTable t = value.Read<LuaTable>();
                    return new Color(
                        (float)ReadRequiredTableNumber(t, "r"),
                        (float)ReadRequiredTableNumber(t, "g"),
                        (float)ReadRequiredTableNumber(t, "b"),
                        ReadOptionalTableNumber(t, "a", 1f));
                }
            }

            if (targetType == typeof(Quaternion) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                LuaValue w = t["w"];
                if (w.Type != LuaValueType.Nil)
                {
                    return new Quaternion(
                        (float)ReadRequiredTableNumber(t, "x"),
                        (float)ReadRequiredTableNumber(t, "y"),
                        (float)ReadRequiredTableNumber(t, "z"),
                        (float)ReadNumber(w));
                }

                return Quaternion.Euler(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"));
            }

            if (targetType == typeof(Rect) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Rect(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "width"),
                    (float)ReadRequiredTableNumber(t, "height"));
            }

            if (targetType == typeof(Bounds) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                Vector3 center = ReadVector3(ReadRequiredTable(t, "center"));
                Vector3 size = ReadVector3(ReadRequiredTable(t, "size"));
                return new Bounds(center, size);
            }

            if (targetType == typeof(Color32))
            {
                if (value.Type == LuaValueType.String &&
                    ColorUtility.TryParseHtmlString(value.Read<string>(), out Color parsed))
                {
                    return (Color32)parsed;
                }

                if (value.Type == LuaValueType.Table)
                {
                    LuaTable t = value.Read<LuaTable>();
                    return new Color32(
                        (byte)ReadRequiredTableNumber(t, "r"),
                        (byte)ReadRequiredTableNumber(t, "g"),
                        (byte)ReadRequiredTableNumber(t, "b"),
                        (byte)ReadOptionalTableNumber(t, "a", 255f));
                }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && value.Type == LuaValueType.Number)
            {
                return ResolveUnityObject((int)ReadNumber(value), targetType);
            }

            object obj = value.Read<object>();
            return obj == null || targetType.IsInstanceOfType(obj)
                ? obj
                : Convert.ChangeType(obj, targetType, CultureInfo.InvariantCulture);
        }

        private static double ReadNumber(LuaValue value)
        {
            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"value must be a number, got {value.Type}.");
            }

            return value.Read<double>();
        }

        private static string ReadStringValue(LuaValue value)
        {
            return value.Type switch
            {
                LuaValueType.String => value.Read<string>(),
                LuaValueType.Number => value.Read<double>().ToString(CultureInfo.InvariantCulture),
                LuaValueType.Boolean => value.Read<bool>() ? "true" : "false",
                LuaValueType.Nil => "",
                _ => value.ToString()
            };
        }

        private static LuaTable ReadRequiredTable(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.Table)
            {
                throw new ArgumentException($"'{key}' must be a table.");
            }

            return value.Read<LuaTable>();
        }

        private static double ReadRequiredTableNumber(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return value.Read<double>();
        }

        private static float ReadOptionalTableNumber(LuaTable table, string key, float defaultValue)
        {
            LuaValue value = table[key];
            return value.Type == LuaValueType.Nil ? defaultValue : (float)ReadNumber(value);
        }

        private static Vector3 ReadVector3(LuaTable t)
        {
            if (t == null)
            {
                return Vector3.zero;
            }

            return new Vector3(
                (float)ReadRequiredTableNumber(t, "x"),
                (float)ReadRequiredTableNumber(t, "y"),
                (float)ReadRequiredTableNumber(t, "z"));
        }
    }
}
