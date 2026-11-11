using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The Luau extensions of the <c>math</c> library that Lua 5.2 lacks: <c>math.noise</c>,
    /// <c>math.clamp</c>, <c>math.round</c> and <c>math.sign</c>, with Luau's own argument rules (a numeric
    /// string coerces like <c>tonumber</c>; anything else is a bad-argument error naming the position).
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>Luau's reason text when <c>math.clamp</c> gets a <c>max</c> below its <c>min</c>.</summary>
        public const string ClampRangeMessage = "max must be greater than or equal to min";

        /// <summary>Adds the Luau math extensions to the state's <c>math</c> table; a state without one is left alone.</summary>
        internal static void InstallLuauMathExtensions(LuaState state)
        {
            LuaValue mathValue = state.Environment["math"];
            if (mathValue.Type != LuaValueType.Table)
            {
                return;
            }

            LuaTable math = mathValue.Read<LuaTable>();
            math["noise"] = new LuaFunction("math.noise", LuauNoise);
            math["clamp"] = new LuaFunction("math.clamp", LuauClamp);
            math["round"] = new LuaFunction("math.round", LuauRound);
            math["sign"] = new LuaFunction("math.sign", LuauSign);
        }

        /// <summary>Luau <c>math.round</c>: the nearest integer, halves away from zero; NaN and infinities unchanged.</summary>
        public static double LuauRoundValue(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x))
            {
                return x;
            }

            // WHY not Math.Round(x, MidpointRounding.AwayFromZero): C's round is the contract, and an
            // exact split into whole and fraction (x - trunc(x) is exact for every double) gives it on
            // every runtime, the sign of a zero result included (round(-0.4) is -0).
            double whole = Math.Truncate(x);
            double fraction = x - whole;
            if (fraction >= 0.5d)
            {
                return whole + 1d;
            }

            if (fraction <= -0.5d)
            {
                return whole - 1d;
            }

            return whole;
        }

        /// <summary>Luau <c>math.sign</c>: 1 above zero, -1 below, 0 for zero and NaN.</summary>
        public static double LuauSignValue(double x)
        {
            return x > 0d ? 1d : x < 0d ? -1d : 0d;
        }

        private static ValueTask<int> LuauNoise(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            double x = ReadLuauNumber(ctx, 0);
            double y = ReadOptionalLuauNumber(ctx, 1);
            double z = ReadOptionalLuauNumber(ctx, 2);
            return new ValueTask<int>(ctx.Return(RbxPerlinNoise.Noise(x, y, z)));
        }

        private static ValueTask<int> LuauClamp(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            double value = ReadLuauNumber(ctx, 0);
            double min = ReadLuauNumber(ctx, 1);
            double max = ReadLuauNumber(ctx, 2);
            if (!(min <= max))
            {
                LuaRuntimeException.BadArgument(ctx.State, 3, ClampRangeMessage);
            }

            double clamped = value < min ? min : value;
            clamped = clamped > max ? max : clamped;
            return new ValueTask<int>(ctx.Return(clamped));
        }

        private static ValueTask<int> LuauRound(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            return new ValueTask<int>(ctx.Return(LuauRoundValue(ReadLuauNumber(ctx, 0))));
        }

        private static ValueTask<int> LuauSign(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            return new ValueTask<int>(ctx.Return(LuauSignValue(ReadLuauNumber(ctx, 0))));
        }

        /// <summary>
        /// Luau's <c>luaL_checknumber</c>: a number, or a string <c>tonumber</c> accepts; otherwise the
        /// sandbox's bad-argument line, "bad argument #n to 'name' (number expected, got type)".
        /// </summary>
        private static double ReadLuauNumber(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx.ArgumentCount > index)
            {
                LuaValue value = ctx.GetArgument(index);
                if (LuaCsValueMarshaller.TryCoerceNumber(value, out double number))
                {
                    return number;
                }

                return RaiseNumberExpected(ctx, index, value.TypeToString());
            }

            return RaiseNumberExpected(ctx, index, "no value");
        }

        /// <summary><c>math.noise</c>'s optional axes: absent or nil reads 0, as <c>lua_tonumber</c> gives it.</summary>
        private static double ReadOptionalLuauNumber(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx.ArgumentCount <= index || ctx.GetArgument(index).Type == LuaValueType.Nil)
            {
                return 0d;
            }

            return ReadLuauNumber(ctx, index);
        }

        private static double RaiseNumberExpected(LuaFunctionExecutionContext ctx, int index, string got)
        {
            string reason = "number expected, got " + got;
            LuaRuntimeException.BadArgument(ctx.State, index + 1, reason);
            // WHY: BadArgument always throws; this line only satisfies the compiler.
            throw new LuaRuntimeException(ctx.State, (LuaValue)("bad argument #" + (index + 1) + " (" + reason + ")"));
        }
    }
}
