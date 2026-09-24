using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
                return ReadStringArgument(value);
            }

            if (targetType == typeof(bool))
            {
                return value.Read<bool>();
            }

            if (targetType == typeof(double))
            {
                return ReadNumberArgument(value);
            }

            if (targetType == typeof(float))
            {
                return (float)ReadNumberArgument(value);
            }

            if (targetType == typeof(int))
            {
                return (int)ReadIntegerArgument(value, int.MinValue, int.MaxValue);
            }

            if (targetType == typeof(long))
            {
                return ReadIntegerArgument(value, long.MinValue, long.MaxValue);
            }

            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, (int)ReadIntegerArgument(value, int.MinValue, int.MaxValue));
            }

            object obj = value.Read<object>();
            return obj == null || targetType.IsInstanceOfType(obj) ? obj : Convert.ChangeType(obj, targetType);
        }

        /// <summary>
        /// Reads a script argument given for a string parameter by <see cref="TryCoerceString"/>; any other
        /// kind throws the engine's conversion failure, which the caller words as Lua's "bad argument".
        /// </summary>
        internal static string ReadStringArgument(LuaValue value)
        {
            return TryCoerceString(value, out string text) ? text : value.Read<string>();
        }

        // ---- Argument coercion: the one rule set every script surface reads parameters by ----------

        /// <summary>
        /// Luau's string-parameter rule (<c>luaL_checklstring</c> over <c>lua_tolstring</c>): a string as it
        /// is and a number as the text <c>tostring</c> gives it. Every other kind, a boolean included, is
        /// false: Luau converts only numbers, and the caller refuses the rest with its own error text.
        /// </summary>
        /// <remarks>
        /// WHY a number is accepted: Luau and Roblox convert it (<c>store_set(7, 8)</c> stores "7",
        /// <c>player:Kick(42)</c> kicks with "42", <c>part.Name = 5</c> names the part "5"), so a script
        /// that relies on it must behave the same here (B2-12, RBX-COERCE). WHY
        /// <see cref="LuaValue.ToString()"/>: it is the VM's own formatting behind <c>tostring</c>, so the
        /// text is exactly what <c>tostring(n)</c> gives the script for the same number.
        /// </remarks>
        internal static bool TryCoerceString(LuaValue value, out string text)
        {
            switch (value.Type)
            {
                case LuaValueType.String:
                    text = value.Read<string>();
                    return true;
                case LuaValueType.Number:
                    text = value.ToString();
                    return true;
                default:
                    text = null;
                    return false;
            }
        }

        /// <summary>
        /// Luau's number-parameter rule (<c>luaL_checknumber</c> over <c>lua_tonumberx</c>): a number as it
        /// is and a string <c>tonumber</c> would accept (<see cref="TryParseNumber"/>). Every other kind is
        /// false. NaN and infinities pass through; a member that refuses them checks the number itself.
        /// </summary>
        internal static bool TryCoerceNumber(LuaValue value, out double number)
        {
            if (value.Type == LuaValueType.Number)
            {
                number = value.Read<double>();
                return true;
            }

            if (value.Type == LuaValueType.String)
            {
                return TryParseNumber(value.Read<string>(), out number);
            }

            number = 0d;
            return false;
        }

        /// <summary>
        /// Luau's integer-parameter rule (<c>luaL_checkinteger</c>): <see cref="TryCoerceNumber"/>, then the
        /// fraction truncated toward zero, as Luau's <c>(int)</c> cast does. NaN and a value outside
        /// [<paramref name="minimum"/>, <paramref name="maximum"/>] are false: the cast is undefined in C
        /// for them, so they are refused instead of wrapping to an arbitrary integer.
        /// </summary>
        internal static bool TryCoerceInteger(LuaValue value, long minimum, long maximum, out long integer)
        {
            integer = 0L;
            if (!TryCoerceNumber(value, out double number) || double.IsNaN(number))
            {
                return false;
            }

            double truncated = Math.Truncate(number);
            // WHY 2^63 as a literal bound: (double)long.MaxValue rounds up to 2^63, which no long holds.
            if (truncated < -9223372036854775808d || truncated >= 9223372036854775808d)
            {
                return false;
            }

            long whole = (long)truncated;
            if (whole < minimum || whole > maximum)
            {
                return false;
            }

            integer = whole;
            return true;
        }

        /// <summary>
        /// Parses a string the way Luau's <c>tonumber</c> does (<c>luaO_str2d</c>: C <c>strtod</c>, then a
        /// base-16 retry): optional surrounding C whitespace and sign; a decimal with optional point and
        /// exponent (<c>"5"</c>, <c>".5"</c>, <c>"1e3"</c>); a <c>0x</c> hexadecimal with optional fraction
        /// and binary exponent (<c>" 0x10 "</c>, <c>"0x1p4"</c>); <c>inf</c>, <c>infinity</c> or <c>nan</c>
        /// in any case. An out-of-range decimal becomes an infinity, as <c>strtod</c> answers it. Only the
        /// text before an embedded NUL is read, as C reads the string (<c>"5\0x"</c> is 5).
        /// </summary>
        internal static bool TryParseNumber(string text, out double number)
        {
            number = 0d;
            if (text == null)
            {
                return false;
            }

            int start = 0;
            // WHY the NUL ends the text: luaO_str2d hands the string to strtod as a C string, so Luau's
            // tonumber("5\0x") is 5; refusing it converted the same string differently from Roblox (C1-09).
            int nul = text.IndexOf('\0');
            int end = nul >= 0 ? nul : text.Length;
            while (start < end && IsCSpace(text[start]))
            {
                start++;
            }

            while (end > start && IsCSpace(text[end - 1]))
            {
                end--;
            }

            if (start == end)
            {
                return false;
            }

            bool negative = text[start] == '-';
            if (negative || text[start] == '+')
            {
                start++;
            }

            double magnitude;
            if (end - start >= 2 && text[start] == '0' && (text[start + 1] == 'x' || text[start + 1] == 'X'))
            {
                if (!TryParseHexadecimal(text, start + 2, end, out magnitude))
                {
                    return false;
                }
            }
            else if (!TryParseNamedNumber(text, start, end, out magnitude)
                     && !TryParseDecimal(text, start, end, out magnitude))
            {
                return false;
            }

            number = negative ? -magnitude : magnitude;
            return true;
        }

        private static double ReadNumberArgument(LuaValue value)
        {
            if (TryCoerceNumber(value, out double number))
            {
                return number;
            }

            throw new ArgumentException("number expected");
        }

        private static long ReadIntegerArgument(LuaValue value, long minimum, long maximum)
        {
            if (TryCoerceInteger(value, minimum, maximum, out long integer))
            {
                return integer;
            }

            throw new ArgumentException("number has no integer representation");
        }

        /// <summary>C's <c>isspace</c> in the "C" locale, which <c>strtod</c> and Luau skip.</summary>
        private static bool IsCSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
        }

        private static bool TryParseNamedNumber(string text, int start, int end, out double magnitude)
        {
            magnitude = 0d;
            int length = end - start;
            if (length == 3 && string.Compare(text, start, "inf", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
                || length == 8
                && string.Compare(text, start, "infinity", 0, 8, StringComparison.OrdinalIgnoreCase) == 0)
            {
                magnitude = double.PositiveInfinity;
                return true;
            }

            if (length < 3 || string.Compare(text, start, "nan", 0, 3, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            // WHY "nan(chars)" too: strtod takes an optional parenthesised payload of letters, digits
            // and underscores after "nan", and Luau's tonumber accepts whatever strtod consumed.
            if (length > 3)
            {
                if (text[start + 3] != '(' || text[end - 1] != ')')
                {
                    return false;
                }

                for (int index = start + 4; index < end - 1; index++)
                {
                    char c = text[index];
                    if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_'))
                    {
                        return false;
                    }
                }
            }

            magnitude = double.NaN;
            return true;
        }

        private static bool TryParseDecimal(string text, int start, int end, out double magnitude)
        {
            magnitude = 0d;
            int index = start;
            int digits = 0;
            while (index < end && text[index] >= '0' && text[index] <= '9')
            {
                index++;
                digits++;
            }

            if (index < end && text[index] == '.')
            {
                index++;
                while (index < end && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                return false;
            }

            if (index < end && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < end && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                int exponentDigits = 0;
                while (index < end && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                    exponentDigits++;
                }

                if (exponentDigits == 0)
                {
                    return false;
                }
            }

            if (index != end)
            {
                return false;
            }

            if (double.TryParse(text.Substring(start, end - start),
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out magnitude))
            {
                return true;
            }

            // WHY: the grammar above already matched, so the only failure left is an exponent too large
            // for a double, which Mono's parser refuses and strtod answers with HUGE_VAL.
            magnitude = double.PositiveInfinity;
            return true;
        }

        private static bool TryParseHexadecimal(string text, int start, int end, out double magnitude)
        {
            magnitude = 0d;
            ulong mantissa = 0UL;
            int binaryExponent = 0;
            bool anyDigit = false;
            bool droppedNonZero = false;
            int index = start;
            bool inFraction = false;
            while (index < end)
            {
                char c = text[index];
                if (c == '.' && !inFraction)
                {
                    inFraction = true;
                    index++;
                    continue;
                }

                int digit = HexDigit(c);
                if (digit < 0)
                {
                    break;
                }

                anyDigit = true;
                // WHY 2^56: sixteen times it plus a digit stays below 2^63, so the long-to-double conversion
                // below is exact hardware rounding; later digits only scale the value or set the sticky bit.
                if (mantissa < 1UL << 56)
                {
                    mantissa = mantissa * 16UL + (ulong)digit;
                    if (inFraction)
                    {
                        binaryExponent -= 4;
                    }
                }
                else
                {
                    droppedNonZero |= digit != 0;
                    if (!inFraction)
                    {
                        binaryExponent += 4;
                    }
                }

                index++;
            }

            if (!anyDigit)
            {
                return false;
            }

            if (index < end && (text[index] == 'p' || text[index] == 'P'))
            {
                index++;
                bool negativeExponent = index < end && text[index] == '-';
                if (index < end && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                int exponent = 0;
                int exponentDigits = 0;
                while (index < end && text[index] >= '0' && text[index] <= '9')
                {
                    exponent = Math.Min(exponent * 10 + (text[index] - '0'), 100000);
                    index++;
                    exponentDigits++;
                }

                if (exponentDigits == 0)
                {
                    return false;
                }

                binaryExponent += negativeExponent ? -exponent : exponent;
            }

            if (index != end)
            {
                return false;
            }

            if (droppedNonZero)
            {
                mantissa |= 1UL;
            }

            magnitude = ScaleByPowerOfTwo((long)mantissa, binaryExponent);
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        private static double ScaleByPowerOfTwo(long mantissa, int exponent)
        {
            double value = mantissa;
            while (exponent > 1000)
            {
                value *= Math.Pow(2d, 1000);
                exponent -= 1000;
            }

            while (exponent < -1000)
            {
                value *= Math.Pow(2d, -1000);
                exponent += 1000;
            }

            return value * Math.Pow(2d, exponent);
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
