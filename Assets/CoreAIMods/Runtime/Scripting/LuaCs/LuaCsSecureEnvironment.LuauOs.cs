using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lua;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// <c>os.date</c> and <c>os.difftime</c> for the sandbox's <c>os</c> table, which the Rbx bindings build
    /// (<c>os.time</c>, <c>os.clock</c>). Dates are UTC with or without <c>"!"</c>, as the sandbox's
    /// <c>os.time</c> reads a date table as UTC, so <c>os.time(os.date("*t", t)) == t</c> on every machine
    /// (DEV-110); names and <c>%c</c>, <c>%x</c>, <c>%X</c> follow the C locale of a Linux server.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>The conversions Luau's <c>os.date</c> accepts (<c>LUA_STRFTIMEOPTIONS</c>).</summary>
        public const string OsDateConversions = "aAbBcdHIjmMpSUwWxXyYzZ%";

        private static readonly string[] WeekdayNames =
            { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

        private static readonly string[] MonthNames =
        {
            "January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
            "November", "December"
        };

        /// <summary>
        /// Adds <c>date</c> and <c>difftime</c> to <paramref name="os"/>; <paramref name="unixNow"/> is the clock
        /// <c>os.time()</c> reads, so the default time of <c>os.date</c> is the same moment.
        /// </summary>
        internal static void InstallLuauOsExtensions(LuaTable os, Func<double> unixNow)
        {
            os["date"] = new LuaFunction("os.date", (ctx, ct) => OsDate(ctx, unixNow));
            os["difftime"] = new LuaFunction("os.difftime", (ctx, ct) =>
            {
                double later = Math.Truncate(ReadLuauNumber(ctx, 0));
                double earlier = ctx.ArgumentCount > 1 && ctx.GetArgument(1).Type != LuaValueType.Nil
                    ? Math.Truncate(ReadLuauNumber(ctx, 1))
                    : 0d;
                return new ValueTask<int>(ctx.Return(later - earlier));
            });
        }

        private static ValueTask<int> OsDate(LuaFunctionExecutionContext ctx, Func<double> unixNow)
        {
            string format = ReadOptionalLuauString(ctx, 0, "%c");
            double seconds = ctx.ArgumentCount > 1 && ctx.GetArgument(1).Type != LuaValueType.Nil
                ? Math.Truncate(ReadLuauNumber(ctx, 1))
                : Math.Truncate(unixNow());
            bool utc = format.StartsWith("!", StringComparison.Ordinal);
            if (utc)
            {
                format = format.Substring(1);
            }

            // WHY nil before the epoch without "!": Luau refuses it on the local-time path (Windows' localtime
            // fails there), and the sandbox keeps that answer although it computes the date in UTC.
            if ((!utc && seconds < 0d) || !TryUtcDate(seconds, out DateTime date))
            {
                return new ValueTask<int>(ctx.Return(LuaValue.Nil));
            }

            if (format == "*t")
            {
                LuaTable fields = new(0, 9);
                fields["sec"] = date.Second;
                fields["min"] = date.Minute;
                fields["hour"] = date.Hour;
                fields["day"] = date.Day;
                fields["month"] = date.Month;
                fields["year"] = date.Year;
                fields["wday"] = (int)date.DayOfWeek + 1;
                fields["yday"] = date.DayOfYear;
                fields["isdst"] = false;
                return new ValueTask<int>(ctx.Return(fields));
            }

            StringBuilder text = new(format.Length + 16);
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c != '%' || i + 1 >= format.Length)
                {
                    text.Append(c);
                    continue;
                }

                char conversion = format[++i];
                if (OsDateConversions.IndexOf(conversion) < 0)
                {
                    LuaRuntimeException.BadArgument(ctx.State, 1, "invalid conversion specifier");
                }

                AppendDateConversion(text, conversion, date);
                if (text.Length > MaxStringRepLength)
                {
                    throw ResultTooLong(ctx.State, "os.date", MaxStringRepLength);
                }
            }

            return new ValueTask<int>(ctx.Return(text.ToString()));
        }

        /// <summary>The UTC date of Unix second <paramref name="seconds"/>; false outside what DateTime holds.</summary>
        internal static bool TryUtcDate(double seconds, out DateTime date)
        {
            // WHY these bounds: DateTime spans years 1 to 9999; Luau's gmtime reaches further, but no game asks.
            if (double.IsNaN(seconds) || seconds < -62135596800d || seconds > 253402300799d)
            {
                date = default;
                return false;
            }

            date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            return true;
        }

        /// <summary>One <c>strftime</c> conversion of the C locale.</summary>
        internal static void AppendDateConversion(StringBuilder text, char conversion, DateTime date)
        {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            int weekday = (int)date.DayOfWeek;
            int yearDay = date.DayOfYear - 1;
            switch (conversion)
            {
                case 'a':
                    text.Append(WeekdayNames[weekday], 0, 3);
                    break;
                case 'A':
                    text.Append(WeekdayNames[weekday]);
                    break;
                case 'b':
                    text.Append(MonthNames[date.Month - 1], 0, 3);
                    break;
                case 'B':
                    text.Append(MonthNames[date.Month - 1]);
                    break;
                case 'c':
                    AppendDateConversion(text, 'a', date);
                    text.Append(' ');
                    AppendDateConversion(text, 'b', date);
                    text.Append(' ').Append(date.Day.ToString(invariant).PadLeft(2)).Append(' ');
                    AppendDateConversion(text, 'X', date);
                    text.Append(' ').Append(date.Year.ToString(invariant));
                    break;
                case 'd':
                    text.Append(date.Day.ToString("00", invariant));
                    break;
                case 'H':
                    text.Append(date.Hour.ToString("00", invariant));
                    break;
                case 'I':
                    int hour12 = date.Hour % 12;
                    text.Append((hour12 == 0 ? 12 : hour12).ToString("00", invariant));
                    break;
                case 'j':
                    text.Append(date.DayOfYear.ToString("000", invariant));
                    break;
                case 'm':
                    text.Append(date.Month.ToString("00", invariant));
                    break;
                case 'M':
                    text.Append(date.Minute.ToString("00", invariant));
                    break;
                case 'p':
                    text.Append(date.Hour < 12 ? "AM" : "PM");
                    break;
                case 'S':
                    text.Append(date.Second.ToString("00", invariant));
                    break;
                case 'U':
                    text.Append(((yearDay + 7 - weekday) / 7).ToString("00", invariant));
                    break;
                case 'w':
                    text.Append(weekday.ToString(invariant));
                    break;
                case 'W':
                    text.Append(((yearDay + 7 - (weekday + 6) % 7) / 7).ToString("00", invariant));
                    break;
                case 'x':
                    AppendDateConversion(text, 'm', date);
                    text.Append('/');
                    AppendDateConversion(text, 'd', date);
                    text.Append('/');
                    AppendDateConversion(text, 'y', date);
                    break;
                case 'X':
                    AppendDateConversion(text, 'H', date);
                    text.Append(':');
                    AppendDateConversion(text, 'M', date);
                    text.Append(':');
                    AppendDateConversion(text, 'S', date);
                    break;
                case 'y':
                    text.Append((date.Year % 100).ToString("00", invariant));
                    break;
                case 'Y':
                    text.Append(date.Year.ToString(invariant));
                    break;
                case 'z':
                    text.Append("+0000");
                    break;
                case 'Z':
                    text.Append("UTC");
                    break;
                default:
                    text.Append('%');
                    break;
            }
        }
    }
}
