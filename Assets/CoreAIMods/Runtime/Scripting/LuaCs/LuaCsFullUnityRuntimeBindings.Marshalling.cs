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

        /// <summary>
        /// Converts a script value for a reflected member or parameter of <paramref name="targetType"/> by
        /// the one rule set of every script surface (<see cref="LuaCsValueMarshaller"/>, C1-07): a string
        /// takes a string or a number's <c>tostring</c> text; a number takes a number or a numeric string;
        /// an integer takes the number truncated toward zero and refuses NaN and a value its type cannot
        /// hold (a <c>ulong</c> at most <see cref="long.MaxValue"/>); a boolean takes only true or false; an
        /// enum takes a member name spelled exactly or an integer. Nil is the type's default. Unity value
        /// shapes (Vector3, Color, ...) keep their table and text forms.
        /// </summary>
        internal static object ConvertArg(LuaValue value, Type targetType)
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
                // WHY only true and false: a number became a boolean by != 0 and a string threw an engine
                // cast error, while every other surface refuses both with one message (C1-07).
                return value.Type == LuaValueType.Boolean
                    ? value.Read<bool>()
                    : throw new ArgumentException($"value must be a boolean, got {value.TypeToString()}.");
            }

            if (targetType == typeof(int))
            {
                return (int)ReadInteger(value, int.MinValue, int.MaxValue);
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
                return ReadInteger(value, long.MinValue, long.MaxValue);
            }

            if (targetType == typeof(uint))
            {
                return (uint)ReadInteger(value, uint.MinValue, uint.MaxValue);
            }

            if (targetType == typeof(ulong))
            {
                return (ulong)ReadInteger(value, 0L, long.MaxValue);
            }

            if (targetType == typeof(short))
            {
                return (short)ReadInteger(value, short.MinValue, short.MaxValue);
            }

            if (targetType == typeof(ushort))
            {
                return (ushort)ReadInteger(value, ushort.MinValue, ushort.MaxValue);
            }

            if (targetType == typeof(byte))
            {
                return (byte)ReadInteger(value, byte.MinValue, byte.MaxValue);
            }

            if (targetType == typeof(sbyte))
            {
                return (sbyte)ReadInteger(value, sbyte.MinValue, sbyte.MaxValue);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.String)
            {
                return ReadEnumName(value.Read<string>(), targetType);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.Number)
            {
                return Enum.ToObject(targetType, ReadInteger(value, long.MinValue, long.MaxValue));
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
                        ReadRequiredTableByte(t, "r"),
                        ReadRequiredTableByte(t, "g"),
                        ReadRequiredTableByte(t, "b"),
                        t["a"].Type == LuaValueType.Nil ? (byte)255 : ReadRequiredTableByte(t, "a"));
                }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && value.Type == LuaValueType.Number)
            {
                return ResolveUnityObject((int)ReadInteger(value, int.MinValue, int.MaxValue), targetType);
            }

            object obj = value.Read<object>();
            return obj == null || targetType.IsInstanceOfType(obj)
                ? obj
                : Convert.ChangeType(obj, targetType, CultureInfo.InvariantCulture);
        }

        private static double ReadNumber(LuaValue value)
        {
            if (!LuaCsValueMarshaller.TryCoerceNumber(value, out double number))
            {
                throw new ArgumentException($"value must be a number, got {value.TypeToString()}.");
            }

            return number;
        }

        /// <summary>
        /// <see cref="LuaCsValueMarshaller.TryCoerceInteger"/>: truncated toward zero, and refused when NaN or
        /// outside [<paramref name="minimum"/>, <paramref name="maximum"/>].
        /// </summary>
        /// <remarks>
        /// WHY checked: an unchecked cast is undefined for such a value (x64 turned 1e300 into int.MinValue)
        /// and (uint)-1 wrapped to 4294967295, so a script's write depended on the host's CPU (C1-07).
        /// </remarks>
        private static long ReadInteger(LuaValue value, long minimum, long maximum)
        {
            if (LuaCsValueMarshaller.TryCoerceInteger(value, minimum, maximum, out long integer))
            {
                return integer;
            }

            if (!LuaCsValueMarshaller.TryCoerceNumber(value, out double number))
            {
                throw new ArgumentException($"value must be a number, got {value.TypeToString()}.");
            }

            throw new ArgumentException(
                "value must be a whole number from " + minimum.ToString(CultureInfo.InvariantCulture) + " to "
                + maximum.ToString(CultureInfo.InvariantCulture) + ", got "
                + number.ToString("R", CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>
        /// An enum member by its exact name; a different case, a numeric string or a flag list is refused,
        /// as the Rbx surface refuses them for its Enum members.
        /// </summary>
        private static object ReadEnumName(string name, Type enumType)
        {
            string[] names = Enum.GetNames(enumType);
            for (int index = 0; index < names.Length; index++)
            {
                if (string.Equals(names[index], name, StringComparison.Ordinal))
                {
                    return Enum.Parse(enumType, name, false);
                }
            }

            throw new ArgumentException($"value must name a member of {enumType.Name}, got '{name}'.");
        }

        private static string ReadStringValue(LuaValue value)
        {
            // WHY no boolean: Luau converts only numbers to strings, and every other surface refuses the
            // rest with one message (C1-07).
            if (LuaCsValueMarshaller.TryCoerceString(value, out string text))
            {
                return text;
            }

            throw new ArgumentException($"value must be a string, got {value.TypeToString()}.");
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
            if (!LuaCsValueMarshaller.TryCoerceNumber(value, out double number))
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return number;
        }

        private static byte ReadRequiredTableByte(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (!LuaCsValueMarshaller.TryCoerceInteger(value, byte.MinValue, byte.MaxValue, out long channel))
            {
                throw new ArgumentException($"'{key}' must be a whole number from 0 to 255.");
            }

            return (byte)channel;
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
