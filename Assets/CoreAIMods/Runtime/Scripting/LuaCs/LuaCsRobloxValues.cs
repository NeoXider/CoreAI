using System;
using System.Threading.Tasks;
using CoreAI.Mods.Roblox.Datatypes;
using CoreAI.Mods.Roblox.Instances;
using Lua;
using Lua.Runtime;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Userdata box carrying one immutable Roblox datatype value (RbxVector3, RbxCFrame,
    /// RbxEnumItem, RbxRandom, RbxScriptSignal, ...) across the Lua boundary. Behavior lives in
    /// the shared per-kind metatable; the box itself is a dumb holder so wrappers may be created
    /// freely (value identity is provided by <c>__eq</c> comparing the unwrapped values).
    /// </summary>
    internal sealed class LuaCsRobloxValueBox : ILuaUserData
    {
        public LuaCsRobloxValueBox(object value, LuaTable metatable)
        {
            Value = value;
            Metatable = metatable;
        }

        public object Value { get; }

        public LuaTable Metatable { get; set; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }

    /// <summary>
    /// Lua-side thin proxy for one <see cref="RbxInstance"/> (roadmap §5.1.5: instances cross by
    /// reference). Holds the live instance plus the owning mod context so member dispatch can
    /// enforce capabilities and attribute created children to the right owner.
    /// </summary>
    internal sealed class LuaCsRobloxInstanceProxy : ILuaUserData
    {
        public LuaCsRobloxInstanceProxy(RbxInstance instance, LuaCsRobloxModContext context,
            LuaTable metatable)
        {
            Instance = instance;
            Context = context;
            Metatable = metatable;
        }

        public RbxInstance Instance { get; }

        public LuaCsRobloxModContext Context { get; }

        public LuaTable Metatable { get; set; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }

    /// <summary>
    /// Shared plumbing for the Roblox Lua surface: guarded host functions that convert
    /// <see cref="RbxError"/>/<see cref="RobloxApiStubException"/> into Lua errors preserving the
    /// §5.2.7 machine-parsable message verbatim, plus typed argument readers whose BAD_ARGUMENT
    /// fixes name the expected type and position.
    /// </summary>
    internal static class LuaCsRobloxLua
    {
        /// <summary>Builds a host function whose body may throw Roblox-layer errors.</summary>
        public static LuaFunction Fn(string name, Func<LuaFunctionExecutionContext, LuaValue> body)
        {
            return new LuaFunction(name, (ctx, _) =>
            {
                try
                {
                    return new ValueTask<int>(ctx.Return(body(ctx)));
                }
                catch (Exception ex)
                {
                    throw ToLuaError(ctx.State, ex);
                }
            });
        }

        /// <summary>Multi-return variant of <see cref="Fn"/>.</summary>
        public static LuaFunction FnMulti(string name, Func<LuaFunctionExecutionContext, LuaValue[]> body)
        {
            return new LuaFunction(name, (ctx, _) =>
            {
                try
                {
                    return new ValueTask<int>(ctx.Return(body(ctx)));
                }
                catch (Exception ex)
                {
                    throw ToLuaError(ctx.State, ex);
                }
            });
        }

        /// <summary>
        /// Converts a host exception to the VM error type without decorating the message: the
        /// §5.2.7 "CODE: message | fix: ..." line must stay byte-stable for the AI self-repair
        /// contract, so no function-name prefix is added here (unlike the generic registry path).
        /// </summary>
        public static LuaRuntimeException ToLuaError(LuaState state, Exception ex)
        {
            if (ex is LuaRuntimeException lua)
            {
                return lua;
            }

            string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return new LuaRuntimeException(state, new InvalidOperationException(message, ex));
        }

        // ---- Wrap / unwrap ------------------------------------------------------------------

        public static LuaValue Box(object value, LuaTable metatable)
        {
            return new LuaValue(new LuaCsRobloxValueBox(value, metatable));
        }

        public static bool TryUnbox<T>(LuaValue value, out T result)
        {
            if (value.TryRead(out LuaCsRobloxValueBox box) && box.Value is T typed)
            {
                result = typed;
                return true;
            }

            result = default;
            return false;
        }

        public static bool TryGetInstance(LuaValue value, out LuaCsRobloxInstanceProxy proxy)
        {
            return value.TryRead(out proxy) && proxy != null;
        }

        // ---- Typed argument readers ---------------------------------------------------------

        public static LuaValue Arg(LuaFunctionExecutionContext ctx, int index)
        {
            return ctx.HasArgument(index) ? ctx.GetArgument(index) : LuaValue.Nil;
        }

        public static float ReadFloat(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    what + " expects a number at argument " + (index + 1),
                    "pass a number, got " + Describe(value) + " at argument " + (index + 1));
            }

            return (float)value.Read<double>();
        }

        public static float ReadFloatOr(LuaFunctionExecutionContext ctx, int index, float fallback)
        {
            LuaValue value = Arg(ctx, index);
            return value.Type == LuaValueType.Number ? (float)value.Read<double>() : fallback;
        }

        public static double ReadDouble(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    what + " expects a number at argument " + (index + 1),
                    "pass a number, got " + Describe(value) + " at argument " + (index + 1));
            }

            return value.Read<double>();
        }

        public static string ReadString(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.String)
            {
                throw RbxError.BadArgument(
                    what + " expects a string at argument " + (index + 1),
                    "pass a string, got " + Describe(value) + " at argument " + (index + 1));
            }

            return value.Read<string>();
        }

        public static RbxVector3 ReadVector3(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            throw RbxError.BadArgument(
                what + " expects a Vector3 at argument " + (index + 1),
                "pass a Vector3, got " + Describe(value) + " at argument " + (index + 1));
        }

        public static RbxCFrame ReadCFrame(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            throw RbxError.BadArgument(
                what + " expects a CFrame at argument " + (index + 1),
                "pass a CFrame, got " + Describe(value) + " at argument " + (index + 1));
        }

        /// <summary>Human name for BAD_ARGUMENT fixes ("got string at argument 2").</summary>
        public static string Describe(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil: return "nil";
                case LuaValueType.Boolean: return "boolean";
                case LuaValueType.Number: return "number";
                case LuaValueType.String: return "string";
                case LuaValueType.Table: return "table";
                case LuaValueType.Function: return "function";
                default:
                    if (value.TryRead(out LuaCsRobloxValueBox box))
                    {
                        return DatatypeName(box.Value);
                    }

                    return value.TryRead(out LuaCsRobloxInstanceProxy _) ? "Instance" : "userdata";
            }
        }

        private static string DatatypeName(object value)
        {
            switch (value)
            {
                case RbxVector3 _: return "Vector3";
                case RbxVector2 _: return "Vector2";
                case RbxCFrame _: return "CFrame";
                case RbxColor3 _: return "Color3";
                case RbxUDim _: return "UDim";
                case RbxUDim2 _: return "UDim2";
                case RbxEnumItem _: return "EnumItem";
                case RbxEnum _: return "Enum";
                case RbxRandom _: return "Random";
                case RbxScriptSignal _: return "RBXScriptSignal";
                default: return value?.GetType().Name ?? "nil";
            }
        }

        /// <summary>Locks a metatable so scripts cannot mutate shared behavior tables.</summary>
        public static LuaTable Lock(LuaTable metatable)
        {
            metatable[Metamethods.Metatable] = "The metatable is locked";
            return metatable;
        }
    }
}
