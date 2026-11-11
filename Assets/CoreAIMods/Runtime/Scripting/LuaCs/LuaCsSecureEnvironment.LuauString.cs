using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Standard;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The Luau extensions of the <c>string</c> library: <c>string.split</c>, the <c>%*</c> conversion of
    /// <c>string.format</c>, and Luau's own numeric conversions (<c>%d %i %u %c %o %x %X %e %E %f %F %g %G</c>),
    /// printed from the exact binary value with C's rounding so every runtime gives the same text.
    /// <c>string.pack</c>, <c>string.packsize</c> and <c>string.unpack</c> are loud stubs.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>
        /// Steps charged per piece <c>string.split</c> produces: one turn of the loop it replaces, a plain
        /// <c>string.find</c>, a <c>string.sub</c> and an append.
        /// </summary>
        internal const int SplitStepsPerPiece = 20;

        private static void InstallLuauStringExtensions(LuaTable environment)
        {
            if (!environment["string"].TryRead(out LuaTable stringLib))
            {
                return;
            }

            stringLib["split"] = new LuaFunction("string.split", LuauSplit);

            // WHY the native functions are rebuilt here rather than read back: string.format's capping body
            // (FormatAsync) wants the library's own format and tostring, and the environment's tostring is the
            // counted wrapper by now. These are the same delegates OpenStringLibrary and OpenBasicLibrary install.
            LuaValue nativeFormat = new LuaFunction("string.format", StringLibrary.Instance.Format);
            LuaValue nativeToString = new LuaFunction("tostring", BasicLibrary.Instance.ToString);
            stringLib["format"] = new LuaFunction("string.format",
                (ctx, ct) => LuauFormat(ctx, ct, nativeFormat, nativeToString));

            const string packWorkaround =
                "build binary data with string.char and read it with string.byte";
            stringLib["pack"] = StubFunction("string.pack", "MVP14", packWorkaround);
            stringLib["packsize"] = StubFunction("string.packsize", "MVP14", packWorkaround);
            stringLib["unpack"] = StubFunction("string.unpack", "MVP14", packWorkaround);
        }

        private static LuaFunction StubFunction(string name, string rung, string workaround)
        {
            return LuaCsRbxLua.Fn(name, _ => throw RbxKnownUnimplementedErrors.ForMember(name,
                RbxKnownUnimplementedMemberStatus.Planned, rung, workaround));
        }

        /// <summary>
        /// Luau <c>string.split(s, separator)</c>: the pieces between occurrences of the plain separator (default
        /// <c>","</c>), the last piece included even when empty; an empty separator splits into single characters.
        /// </summary>
        private static ValueTask<int> LuauSplit(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            string separator = ReadOptionalLuauString(ctx, 1, ",");
            LuaTable pieces = new();
            int count = 0;
            if (separator.Length == 0)
            {
                if (text.Length > MaxTableBuildLength)
                {
                    throw SplitTooLong(ctx.State, text.Length);
                }

                for (int i = 0; i < text.Length; i++)
                {
                    pieces[++count] = text.Substring(i, 1);
                }
            }
            else
            {
                int start = 0;
                while (true)
                {
                    int found = text.IndexOf(separator, start, StringComparison.Ordinal);
                    if (count >= MaxTableBuildLength)
                    {
                        throw SplitTooLong(ctx.State, count + 1);
                    }

                    if (found < 0)
                    {
                        pieces[++count] = text.Substring(start);
                        break;
                    }

                    pieces[++count] = text.Substring(start, found - start);
                    start = found + separator.Length;
                }
            }

            ChargeLibraryWork(ctx.State, count, SplitStepsPerPiece);
            return new ValueTask<int>(ctx.Return(pieces));
        }

        private static LuaRuntimeException SplitTooLong(LuaState state, long pieces)
        {
            return LibraryRefusal(state, $"{SandboxLinePrefix}string.split would build more than "
                                         + $"{MaxTableBuildLength} pieces ({pieces} so far).");
        }

        // ---- string.format ---------------------------------------------------------------------------

        /// <summary>
        /// <c>string.format</c>: the conversions Luau prints differently from Lua-CSharp are formatted here and
        /// handed on as <c>%s</c> of their text; <c>%s</c>, <c>%q</c> and <c>%%</c> go to the capped native
        /// path unchanged (<see cref="FormatAsync"/>).
        /// </summary>
        private static ValueTask<int> LuauFormat(LuaFunctionExecutionContext ctx, CancellationToken ct,
            LuaValue nativeFormat, LuaValue nativeToString)
        {
            LuaValue[] args = ctx.Arguments.ToArray();
            if (args.Length > 0 && args[0].Type == LuaValueType.String)
            {
                string rewritten = RewriteLuauFormat(ctx.State, args[0].Read<string>(), args);
                if (rewritten != null)
                {
                    args[0] = rewritten;
                }
            }

            return FormatAsync(ctx, ct, args, nativeFormat, nativeToString);
        }

        /// <summary>
        /// Walks <paramref name="format"/> as Luau's <c>str_format</c> does and formats every numeric conversion
        /// into <paramref name="args"/> as text, returning the format with those conversions replaced by
        /// <c>%s</c>; null when nothing needed rewriting. Raises Luau's errors for a malformed conversion.
        /// </summary>
        private static string RewriteLuauFormat(LuaState state, string format, LuaValue[] args)
        {
            StringBuilder output = null;
            int copied = 0;
            int argIndex = 0;
            int i = 0;
            while (i < format.Length)
            {
                if (format[i] != '%')
                {
                    i++;
                    continue;
                }

                int specStart = i;
                i++;
                if (i < format.Length && format[i] == '%')
                {
                    i++;
                    continue;
                }

                int flagsStart = i;
                while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0)
                {
                    i++;
                }

                string flags = format.Substring(flagsStart, i - flagsStart);
                int width = ReadFormatDigits(format, ref i);
                int precision = -1;
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    precision = ReadFormatDigits(format, ref i);
                    if (precision < 0)
                    {
                        precision = 0;
                    }
                }

                if (i < format.Length && format[i] >= '0' && format[i] <= '9')
                {
                    throw LibraryRefusal(state, "invalid format (width or precision too long)");
                }

                char conversion = i < format.Length ? format[i] : '\0';
                int specEnd = Math.Min(i + 1, format.Length);
                string replacement;
                switch (conversion)
                {
                    case 'd':
                    case 'i':
                        argIndex++;
                        replacement = FormatSignedInteger(LuauToInt64(ReadFormatNumber(state, args, argIndex)),
                            flags, width, precision);
                        break;
                    case 'u':
                    case 'o':
                    case 'x':
                    case 'X':
                        argIndex++;
                        replacement = FormatUnsignedInteger(LuauToUInt64(ReadFormatNumber(state, args, argIndex)),
                            conversion, flags, width, precision);
                        break;
                    case 'c':
                        argIndex++;
                        replacement = PadFormatted("",
                            ((char)(LuauToInt(ReadFormatNumber(state, args, argIndex)) & 0xFF)).ToString(), width,
                            flags.IndexOf('-') >= 0, false);
                        break;
                    case 'e':
                    case 'E':
                    case 'f':
                    case 'F':
                    case 'g':
                    case 'G':
                        argIndex++;
                        replacement = FormatFloat(ReadFormatNumber(state, args, argIndex), conversion, flags, width,
                            precision);
                        break;
                    case '*':
                        if (specEnd - specStart != 2)
                        {
                            throw LibraryRefusal(state, "'%*' does not take a form");
                        }

                        argIndex++;
                        replacement = null;
                        output ??= new StringBuilder(format.Length + 16);
                        output.Append(format, copied, specStart - copied).Append("%s");
                        copied = specEnd;
                        break;
                    case 's':
                    case 'q':
                        argIndex++;
                        replacement = null;
                        break;
                    default:
                        throw LibraryRefusal(state,
                            ("invalid conversion '" + format.Substring(specStart, specEnd - specStart)
                             + "' to 'format'"));
                }

                i = specEnd;
                if (replacement == null)
                {
                    continue;
                }

                output ??= new StringBuilder(format.Length + 16);
                output.Append(format, copied, specStart - copied).Append("%s");
                copied = specEnd;
                args[argIndex] = replacement;
            }

            if (output == null)
            {
                return null;
            }

            output.Append(format, copied, format.Length - copied);
            return output.ToString();
        }

        /// <summary>Reads at most two digits of a width or precision; -1 when there are none.</summary>
        private static int ReadFormatDigits(string format, ref int i)
        {
            int value = -1;
            for (int digits = 0; digits < 2 && i < format.Length && format[i] >= '0' && format[i] <= '9'; digits++)
            {
                value = (value < 0 ? 0 : value * 10) + (format[i] - '0');
                i++;
            }

            return value;
        }

        /// <summary>Luau's <c>luaL_checknumber</c> on format argument <paramref name="argIndex"/> (1-based after the format).</summary>
        private static double ReadFormatNumber(LuaState state, LuaValue[] args, int argIndex)
        {
            if (argIndex < args.Length && LuaCsValueMarshaller.TryCoerceNumber(args[argIndex], out double number))
            {
                return number;
            }

            string got = argIndex < args.Length ? args[argIndex].TypeToString() : "no value";
            LuaRuntimeException.BadArgument(state, argIndex + 1, "number expected, got " + got);
            throw new LuaRuntimeException(state, (LuaValue)("bad argument #" + (argIndex + 1) + " (number expected)"));
        }

        /// <summary>
        /// The C <c>(long long)</c> cast on x86-64: the fraction truncated; NaN and values outside the range give
        /// <see cref="long.MinValue"/>.
        /// </summary>
        internal static long LuauToInt64(double value)
        {
            if (double.IsNaN(value) || value >= 9223372036854775808d || value < -9223372036854775808d)
            {
                return long.MinValue;
            }

            return (long)Math.Truncate(value);
        }

        /// <summary>
        /// Luau's unsigned conversion for <c>%u %o %x %X</c>: a negative value through <c>(long long)</c>, a
        /// positive one through <c>(unsigned long long)</c>; NaN and values outside both give 2^63.
        /// </summary>
        internal static ulong LuauToUInt64(double value)
        {
            if (double.IsNaN(value))
            {
                return 0x8000000000000000UL;
            }

            if (value < 0d)
            {
                return unchecked((ulong)LuauToInt64(value));
            }

            if (value >= 18446744073709551616d)
            {
                return 0x8000000000000000UL;
            }

            return (ulong)Math.Truncate(value);
        }

        internal static string FormatSignedInteger(long value, string flags, int width, int precision)
        {
            ulong magnitude = value < 0 ? unchecked((ulong)(-(value + 1)) + 1UL) : (ulong)value;
            string digits = value == 0 && precision == 0 ? "" : magnitude.ToString(CultureInfo.InvariantCulture);
            if (precision > digits.Length)
            {
                digits = new string('0', precision - digits.Length) + digits;
            }

            string sign = value < 0 ? "-" : flags.IndexOf('+') >= 0 ? "+" : flags.IndexOf(' ') >= 0 ? " " : "";
            return PadFormatted(sign, digits, width, flags.IndexOf('-') >= 0,
                flags.IndexOf('0') >= 0 && precision < 0);
        }

        internal static string FormatUnsignedInteger(ulong value, char conversion, string flags, int width,
            int precision)
        {
            string digits;
            switch (conversion)
            {
                case 'o':
                    digits = value == 0 ? "0" : ToBase(value, 8, false);
                    break;
                case 'x':
                    digits = ToBase(value, 16, false);
                    break;
                case 'X':
                    digits = ToBase(value, 16, true);
                    break;
                default:
                    digits = value.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            if (value == 0 && precision == 0)
            {
                digits = conversion == 'o' && flags.IndexOf('#') >= 0 ? "0" : "";
            }

            if (precision > digits.Length)
            {
                digits = new string('0', precision - digits.Length) + digits;
            }

            string prefix = "";
            if (flags.IndexOf('#') >= 0)
            {
                if (conversion == 'o' && (digits.Length == 0 || digits[0] != '0'))
                {
                    digits = "0" + digits;
                }
                else if (conversion == 'x' && value != 0)
                {
                    prefix = "0x";
                }
                else if (conversion == 'X' && value != 0)
                {
                    prefix = "0X";
                }
            }

            return PadFormatted(prefix, digits, width, flags.IndexOf('-') >= 0,
                flags.IndexOf('0') >= 0 && precision < 0);
        }

        private static string ToBase(ulong value, int radix, bool upper)
        {
            if (value == 0)
            {
                return "0";
            }

            string alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            char[] buffer = new char[64];
            int at = buffer.Length;
            while (value != 0)
            {
                buffer[--at] = alphabet[(int)(value % (ulong)radix)];
                value /= (ulong)radix;
            }

            return new string(buffer, at, buffer.Length - at);
        }

        /// <summary>C's field padding: left-justified with spaces, zero-filled after the sign, or right-justified.</summary>
        private static string PadFormatted(string sign, string body, int width, bool left, bool zeroFill)
        {
            int length = sign.Length + body.Length;
            if (width <= length)
            {
                return sign + body;
            }

            if (left)
            {
                return sign + body + new string(' ', width - length);
            }

            return zeroFill
                ? sign + new string('0', width - length) + body
                : new string(' ', width - length) + sign + body;
        }

        /// <summary>
        /// C's <c>%e %f %g</c> (and the upper-case forms) of <paramref name="value"/>: the exact binary value
        /// rounded half to even at the requested digit, as glibc prints it; <c>inf</c>, <c>-inf</c>, <c>nan</c>
        /// and <c>-nan</c> by the sign bit.
        /// </summary>
        internal static string FormatFloat(double value, char conversion, string flags, int width, int precision)
        {
            bool upper = conversion == 'E' || conversion == 'F' || conversion == 'G';
            bool negative = BitConverter.DoubleToInt64Bits(value) < 0;
            string sign = negative ? "-" : flags.IndexOf('+') >= 0 ? "+" : flags.IndexOf(' ') >= 0 ? " " : "";
            bool left = flags.IndexOf('-') >= 0;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                string word = double.IsNaN(value) ? "nan" : "inf";
                return PadFormatted(sign, upper ? word.ToUpperInvariant() : word, width, left, false);
            }

            bool alternate = flags.IndexOf('#') >= 0;
            int digits = precision < 0 ? 6 : precision;
            ExactDecimal exact = ExactDecimal.Of(Math.Abs(value));
            string body;
            switch (conversion)
            {
                case 'f':
                case 'F':
                    body = exact.Fixed(digits, alternate);
                    break;
                case 'e':
                case 'E':
                    body = exact.Exponent(digits, alternate, upper);
                    break;
                default:
                    body = exact.General(digits == 0 ? 1 : digits, alternate, upper);
                    break;
            }

            return PadFormatted(sign, body, width, left, flags.IndexOf('0') >= 0);
        }

        /// <summary>
        /// A non-negative finite double as the exact decimal <c>Digits * 10^-Scale</c>: every double is such a
        /// number, so rounding it at any digit is exact and the same on every runtime.
        /// </summary>
        private readonly struct ExactDecimal
        {
            private readonly BigInteger _digits;
            private readonly int _scale;

            private ExactDecimal(BigInteger digits, int scale)
            {
                _digits = digits;
                _scale = scale;
            }

            public static ExactDecimal Of(double value)
            {
                long bits = BitConverter.DoubleToInt64Bits(value) & 0x7FFFFFFFFFFFFFFFL;
                int biased = (int)(bits >> 52);
                long mantissa = bits & 0xFFFFFFFFFFFFFL;
                int exponent;
                if (biased == 0)
                {
                    exponent = -1074;
                }
                else
                {
                    mantissa |= 1L << 52;
                    exponent = biased - 1075;
                }

                if (mantissa == 0)
                {
                    return new ExactDecimal(BigInteger.Zero, 0);
                }

                if (exponent >= 0)
                {
                    return new ExactDecimal(new BigInteger(mantissa) << exponent, 0);
                }

                // WHY 5^k: m * 2^-k = m * 5^k / 10^k, so the fraction has exactly k decimal digits.
                return new ExactDecimal(new BigInteger(mantissa) * PowerOf(5, -exponent), -exponent);
            }

            /// <summary><c>%.{p}f</c> without the sign.</summary>
            public string Fixed(int precision, bool alternate)
            {
                BigInteger rounded = RoundToScale(precision);
                string text = rounded.ToString(CultureInfo.InvariantCulture);
                if (text.Length <= precision)
                {
                    text = new string('0', precision + 1 - text.Length) + text;
                }

                string whole = text.Substring(0, text.Length - precision);
                if (precision == 0)
                {
                    return alternate ? whole + "." : whole;
                }

                return whole + "." + text.Substring(text.Length - precision);
            }

            /// <summary><c>%.{p}e</c> without the sign.</summary>
            public string Exponent(int precision, bool alternate, bool upper)
            {
                (string significand, int exponent) = Significant(precision + 1);
                StringBuilder sb = new(precision + 8);
                sb.Append(significand[0]);
                if (precision > 0 || alternate)
                {
                    sb.Append('.');
                }

                sb.Append(significand, 1, significand.Length - 1);
                AppendExponent(sb, exponent, upper);
                return sb.ToString();
            }

            /// <summary><c>%.{p}g</c> without the sign; <paramref name="precision"/> is at least 1.</summary>
            public string General(int precision, bool alternate, bool upper)
            {
                (string significand, int exponent) = Significant(precision);
                string body;
                if (exponent < precision && exponent >= -4)
                {
                    body = Fixed(precision - 1 - exponent, alternate);
                    if (!alternate)
                    {
                        body = TrimFraction(body);
                    }

                    return body;
                }

                StringBuilder sb = new(precision + 8);
                sb.Append(significand[0]);
                string fraction = significand.Substring(1);
                if (!alternate)
                {
                    fraction = fraction.TrimEnd('0');
                }

                if (fraction.Length > 0 || alternate)
                {
                    sb.Append('.').Append(fraction);
                }

                AppendExponent(sb, exponent, upper);
                return sb.ToString();
            }

            private static string TrimFraction(string text)
            {
                if (text.IndexOf('.') < 0)
                {
                    return text;
                }

                text = text.TrimEnd('0');
                return text.EndsWith(".", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
            }

            private static void AppendExponent(StringBuilder sb, int exponent, bool upper)
            {
                sb.Append(upper ? 'E' : 'e').Append(exponent < 0 ? '-' : '+');
                int magnitude = Math.Abs(exponent);
                if (magnitude < 10)
                {
                    sb.Append('0');
                }

                sb.Append(magnitude.ToString(CultureInfo.InvariantCulture));
            }

            /// <summary>
            /// The value rounded half to even to <paramref name="count"/> significant digits, as those digits and
            /// the decimal exponent of the first; zero is <c>"000..."</c> with exponent 0.
            /// </summary>
            private (string Digits, int Exponent) Significant(int count)
            {
                if (_digits.IsZero)
                {
                    return (new string('0', count), 0);
                }

                string all = _digits.ToString(CultureInfo.InvariantCulture);
                int exponent = all.Length - 1 - _scale;
                BigInteger rounded;
                int drop = all.Length - count;
                if (drop <= 0)
                {
                    rounded = _digits * PowerOf(10, -drop);
                }
                else
                {
                    rounded = RoundHalfEven(_digits, PowerOf(10, drop));
                }

                string text = rounded.ToString(CultureInfo.InvariantCulture);
                if (text.Length > count)
                {
                    exponent++;
                    text = text.Substring(0, count);
                }

                return (text, exponent);
            }

            /// <summary><c>round(value * 10^scale)</c>, half to even.</summary>
            private BigInteger RoundToScale(int scale)
            {
                if (scale >= _scale)
                {
                    return _digits * PowerOf(10, scale - _scale);
                }

                return RoundHalfEven(_digits, PowerOf(10, _scale - scale));
            }

            // WHY boxed in object arrays: a BigInteger is two fields, and a racing reader of a struct array could see
            // half of a write; a reference is written whole. A race only computes the same power twice.
            private static readonly object[] PowersOfFive = new object[1100];
            private static readonly object[] PowersOfTen = new object[1200];

            /// <summary><paramref name="radix"/> (5 or 10) to the power <paramref name="exponent"/>, cached.</summary>
            private static BigInteger PowerOf(int radix, int exponent)
            {
                object[] cache = radix == 5 ? PowersOfFive : PowersOfTen;
                if (exponent < 0 || exponent >= cache.Length)
                {
                    return BigInteger.Pow(radix, exponent);
                }

                object cached = cache[exponent];
                if (cached != null)
                {
                    return (BigInteger)cached;
                }

                BigInteger power = BigInteger.Pow(radix, exponent);
                cache[exponent] = power;
                return power;
            }

            private static BigInteger RoundHalfEven(BigInteger value, BigInteger divisor)
            {
                BigInteger quotient = BigInteger.DivRem(value, divisor, out BigInteger remainder);
                int compare = (remainder * 2).CompareTo(divisor);
                if (compare > 0 || (compare == 0 && !quotient.IsEven))
                {
                    quotient += BigInteger.One;
                }

                return quotient;
            }
        }
    }
}
