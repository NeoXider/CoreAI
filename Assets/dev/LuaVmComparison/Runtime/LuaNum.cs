using System.Globalization;

namespace LuaVmComparison
{
    /// <summary>Formats a Lua number the way both VMs' tostring would: integers without a decimal point.</summary>
    internal static class LuaNum
    {
        public static string Format(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            if (d == System.Math.Floor(d) && !double.IsInfinity(d) &&
                d >= long.MinValue && d <= long.MaxValue)
            {
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            }
            return d.ToString("G14", CultureInfo.InvariantCulture);
        }
    }
}
