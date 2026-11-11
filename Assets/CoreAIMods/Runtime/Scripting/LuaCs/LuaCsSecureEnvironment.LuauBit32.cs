using System;
using System.Threading;
using System.Threading.Tasks;
using Lua;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Luau's <c>bit32</c> (32-bit unsigned results; an argument converts by Luau's
    /// <c>(unsigned)(long long)</c> cast, so <c>bit32.band(5.7)</c> is 5 and <c>-1</c> is <c>0xFFFFFFFF</c>) with
    /// <c>countlz</c>, <c>countrz</c> and <c>byteswap</c>, replacing Lua-CSharp's, which refuses a fraction; and the
    /// Luau <c>math</c> members the slice in <c>LuauMath</c> leaves out: <c>log10</c>, <c>lerp</c>, <c>map</c>,
    /// <c>isnan</c>, <c>isinf</c>, <c>isfinite</c> and the constants <c>e</c>, <c>nan</c>, <c>phi</c>, <c>sqrt2</c>,
    /// <c>tau</c>.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        private const int Bit32Width = 32;

        private static void InstallLuauBit32Library(LuaTable environment)
        {
            LuaTable bit32 = new();
            bit32["band"] = new LuaFunction("bit32.band", (ctx, ct) => Bit32Result(ctx, Bit32Fold(ctx, 0xFFFFFFFFu, 0)));
            bit32["bor"] = new LuaFunction("bit32.bor", (ctx, ct) => Bit32Result(ctx, Bit32Fold(ctx, 0u, 1)));
            bit32["bxor"] = new LuaFunction("bit32.bxor", (ctx, ct) => Bit32Result(ctx, Bit32Fold(ctx, 0u, 2)));
            bit32["btest"] = new LuaFunction("bit32.btest",
                (ctx, ct) => new ValueTask<int>(ctx.Return(Bit32Fold(ctx, 0xFFFFFFFFu, 0) != 0)));
            bit32["bnot"] = new LuaFunction("bit32.bnot", (ctx, ct) => Bit32Result(ctx, ~ReadBit32(ctx, 0)));
            bit32["lshift"] = new LuaFunction("bit32.lshift",
                (ctx, ct) => Bit32Result(ctx, Bit32Shift(ReadBit32(ctx, 0), ReadLuauInt(ctx, 1))));
            bit32["rshift"] = new LuaFunction("bit32.rshift",
                (ctx, ct) => Bit32Result(ctx, Bit32Shift(ReadBit32(ctx, 0), -(long)ReadLuauInt(ctx, 1))));
            bit32["arshift"] = new LuaFunction("bit32.arshift", Bit32ArShift);
            bit32["lrotate"] = new LuaFunction("bit32.lrotate",
                (ctx, ct) => Bit32Result(ctx, Bit32Rotate(ReadBit32(ctx, 0), ReadLuauInt(ctx, 1))));
            bit32["rrotate"] = new LuaFunction("bit32.rrotate",
                (ctx, ct) => Bit32Result(ctx, Bit32Rotate(ReadBit32(ctx, 0), -(long)ReadLuauInt(ctx, 1))));
            bit32["extract"] = new LuaFunction("bit32.extract", Bit32Extract);
            bit32["replace"] = new LuaFunction("bit32.replace", Bit32Replace);
            bit32["countlz"] = new LuaFunction("bit32.countlz", (ctx, ct) =>
                new ValueTask<int>(ctx.Return((double)CountLeadingZeros(ReadBit32(ctx, 0)))));
            bit32["countrz"] = new LuaFunction("bit32.countrz", (ctx, ct) =>
                new ValueTask<int>(ctx.Return((double)CountTrailingZeros(ReadBit32(ctx, 0)))));
            bit32["byteswap"] = new LuaFunction("bit32.byteswap", (ctx, ct) =>
            {
                uint n = ReadBit32(ctx, 0);
                uint swapped = (n << 24) | ((n << 8) & 0xFF0000u) | ((n >> 8) & 0xFF00u) | (n >> 24);
                return Bit32Result(ctx, swapped);
            });
            environment["bit32"] = bit32;
        }

        /// <summary>Luau's <c>luaL_checkunsigned</c>: <see cref="ReadLuauNumber"/>, then <c>(unsigned)(long long)</c>.</summary>
        private static uint ReadBit32(LuaFunctionExecutionContext ctx, int index)
        {
            return unchecked((uint)LuauToInt64(ReadLuauNumber(ctx, index)));
        }

        private static uint Bit32Fold(LuaFunctionExecutionContext ctx, uint seed, int operation)
        {
            uint result = seed;
            for (int i = 0; i < ctx.ArgumentCount; i++)
            {
                uint value = ReadBit32(ctx, i);
                result = operation == 0 ? result & value : operation == 1 ? result | value : result ^ value;
            }

            return result;
        }

        private static ValueTask<int> Bit32Result(LuaFunctionExecutionContext ctx, uint value)
        {
            return new ValueTask<int>(ctx.Return((double)value));
        }

        /// <summary>Luau's <c>b_shift</c>: left for a positive count, logical right for a negative one, 0 past 31.</summary>
        internal static uint Bit32Shift(uint value, long count)
        {
            if (count < 0)
            {
                return -count >= Bit32Width ? 0u : value >> (int)-count;
            }

            return count >= Bit32Width ? 0u : value << (int)count;
        }

        /// <summary>Luau's <c>b_rot</c>: the count taken modulo 32.</summary>
        internal static uint Bit32Rotate(uint value, long count)
        {
            int shift = (int)(count & (Bit32Width - 1));
            return shift == 0 ? value : (value << shift) | (value >> (Bit32Width - shift));
        }

        private static ValueTask<int> Bit32ArShift(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            uint value = ReadBit32(ctx, 0);
            int count = ReadLuauInt(ctx, 1);
            if (count < 0 || (value & 0x80000000u) == 0)
            {
                return Bit32Result(ctx, Bit32Shift(value, -(long)count));
            }

            uint result = count >= Bit32Width ? 0xFFFFFFFFu : (value >> count) | ~(0xFFFFFFFFu >> count);
            return Bit32Result(ctx, result);
        }

        /// <summary>Luau's <c>fieldargs</c>: the field start and width, refused past bit 31.</summary>
        private static int Bit32Field(LuaFunctionExecutionContext ctx, int fieldIndex, out int width)
        {
            int field = ReadLuauInt(ctx, fieldIndex);
            width = ReadOptionalLuauInt(ctx, fieldIndex + 1, 1);
            if (field < 0)
            {
                LuaRuntimeException.BadArgument(ctx.State, fieldIndex + 1, "field cannot be negative");
            }

            if (width <= 0)
            {
                LuaRuntimeException.BadArgument(ctx.State, fieldIndex + 2, "width must be positive");
            }

            if ((long)field + width > Bit32Width)
            {
                throw LibraryRefusal(ctx.State, "trying to access non-existent bits");
            }

            return field;
        }

        private static uint Bit32Mask(int width)
        {
            return width >= Bit32Width ? 0xFFFFFFFFu : (1u << width) - 1u;
        }

        private static ValueTask<int> Bit32Extract(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            uint value = ReadBit32(ctx, 0);
            int field = Bit32Field(ctx, 1, out int width);
            return Bit32Result(ctx, (value >> field) & Bit32Mask(width));
        }

        private static ValueTask<int> Bit32Replace(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            uint value = ReadBit32(ctx, 0);
            uint replacement = ReadBit32(ctx, 1);
            int field = Bit32Field(ctx, 2, out int width);
            uint mask = Bit32Mask(width);
            replacement &= mask;
            return Bit32Result(ctx, (value & ~(mask << field)) | (replacement << field));
        }

        internal static int CountLeadingZeros(uint value)
        {
            if (value == 0)
            {
                return Bit32Width;
            }

            int count = 0;
            while ((value & 0x80000000u) == 0)
            {
                value <<= 1;
                count++;
            }

            return count;
        }

        internal static int CountTrailingZeros(uint value)
        {
            if (value == 0)
            {
                return Bit32Width;
            }

            int count = 0;
            while ((value & 1u) == 0)
            {
                value >>= 1;
                count++;
            }

            return count;
        }

        // ---- math ----------------------------------------------------------------------------------

        private static void InstallLuauMathAdditions(LuaTable environment)
        {
            if (!environment["math"].TryRead(out LuaTable math))
            {
                return;
            }

            math["log10"] = new LuaFunction("math.log10",
                (ctx, ct) => new ValueTask<int>(ctx.Return(Math.Log10(ReadLuauNumber(ctx, 0)))));
            math["lerp"] = new LuaFunction("math.lerp", (ctx, ct) => new ValueTask<int>(ctx.Return(
                LuauLerpValue(ReadLuauNumber(ctx, 0), ReadLuauNumber(ctx, 1), ReadLuauNumber(ctx, 2)))));
            math["map"] = new LuaFunction("math.map", (ctx, ct) =>
            {
                double x = ReadLuauNumber(ctx, 0);
                double inMin = ReadLuauNumber(ctx, 1);
                double inMax = ReadLuauNumber(ctx, 2);
                double outMin = ReadLuauNumber(ctx, 3);
                double outMax = ReadLuauNumber(ctx, 4);
                return new ValueTask<int>(ctx.Return(outMin + (x - inMin) * (outMax - outMin) / (inMax - inMin)));
            });
            math["isnan"] = new LuaFunction("math.isnan",
                (ctx, ct) => new ValueTask<int>(ctx.Return(double.IsNaN(ReadLuauNumber(ctx, 0)))));
            math["isinf"] = new LuaFunction("math.isinf",
                (ctx, ct) => new ValueTask<int>(ctx.Return(double.IsInfinity(ReadLuauNumber(ctx, 0)))));
            math["isfinite"] = new LuaFunction("math.isfinite", (ctx, ct) =>
            {
                double x = ReadLuauNumber(ctx, 0);
                return new ValueTask<int>(ctx.Return(!double.IsNaN(x) && !double.IsInfinity(x)));
            });
            math["e"] = Math.E;
            math["nan"] = double.NaN;
            math["phi"] = 1.6180339887498949d;
            math["sqrt2"] = 1.4142135623730951d;
            math["tau"] = 2d * Math.PI;
        }

        /// <summary>
        /// Luau <c>math.lerp(a, b, t)</c>: <c>a + (b - a) * t</c>, and exactly <c>b</c> at <c>t == 1</c>, so both
        /// ends are exact.
        /// </summary>
        public static double LuauLerpValue(double a, double b, double t)
        {
            return t == 1d ? b : a + (b - a) * t;
        }
    }
}
