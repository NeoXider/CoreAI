using System;
using System.Globalization;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
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
    internal sealed class LuaCsRbxValueBox : ILuaUserData
    {
        public LuaCsRbxValueBox(object value, LuaTable metatable,
            LuaCsRbxModContext signalOwner = null)
        {
            Value = value;
            Metatable = metatable;
            SignalOwner = signalOwner;
        }

        public object Value { get; }

        public LuaTable Metatable { get; set; }

        /// <summary>
        /// For an <see cref="RbxScriptSignal"/> box, the mod context that owns scheduler callbacks
        /// and connection teardown; null only for context-free non-connectable wraps.
        /// </summary>
        public LuaCsRbxModContext SignalOwner { get; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }

    /// <summary>
    /// Lua-side thin proxy for one <see cref="RbxInstance"/> (roadmap §5.1.5: instances cross by
    /// reference). Holds the live instance plus the owning mod context so member dispatch can
    /// enforce capabilities and attribute created children to the right owner.
    /// </summary>
    internal sealed class LuaCsRbxInstanceProxy : ILuaUserData
    {
        public LuaCsRbxInstanceProxy(RbxInstance instance, LuaCsRbxModContext context,
            LuaTable metatable)
        {
            Instance = instance;
            Context = context;
            Metatable = metatable;
        }

        public RbxInstance Instance { get; }

        public LuaCsRbxModContext Context { get; }

        public LuaTable Metatable { get; set; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }

    /// <summary>
    /// Shared plumbing for the Roblox Lua surface: guarded host functions that convert
    /// <see cref="RbxError"/>/<see cref="RbxApiStubException"/> into Lua errors preserving the
    /// §5.2.7 machine-parsable message verbatim, plus typed argument readers whose BAD_ARGUMENT
    /// fixes name the expected type and position.
    /// </summary>
    internal static class LuaCsRbxLua
    {
        private const string ModMainScript = "main.lua";

        /// <summary>Builds a host function whose body may throw Roblox-layer errors.</summary>
        public static LuaFunction Fn(string name, Func<LuaFunctionExecutionContext, LuaValue> body,
            LuaCsRbxModContext context = null)
        {
            return new LuaFunction(name, (ctx, _) =>
            {
                try
                {
                    return new ValueTask<int>(ctx.Return(body(ctx)));
                }
                catch (Exception ex)
                {
                    throw ToLuaError(ctx.State, WithProductionContext(ctx.State, ex, context));
                }
            });
        }

        /// <summary>Multi-return variant of <see cref="Fn"/>.</summary>
        public static LuaFunction FnMulti(string name, Func<LuaFunctionExecutionContext, LuaValue[]> body)
        {
            return FnMulti(name, body, null);
        }

        /// <summary>
        /// Multi-return variant of <see cref="Fn"/> whose errors carry the same production
        /// <c>[mod:&lt;id&gt; script:&lt;path&gt; line:&lt;n&gt;]</c> prefix as single-return host functions.
        /// </summary>
        public static LuaFunction FnMulti(string name, Func<LuaFunctionExecutionContext, LuaValue[]> body,
            LuaCsRbxModContext context)
        {
            return new LuaFunction(name, (ctx, _) =>
            {
                try
                {
                    return new ValueTask<int>(ctx.Return(body(ctx)));
                }
                catch (Exception ex)
                {
                    throw ToLuaError(ctx.State, WithProductionContext(ctx.State, ex, context));
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

        private static Exception WithProductionContext(LuaState state, Exception ex,
            LuaCsRbxModContext context)
        {
            RbxError error = ex as RbxError;
            RbxApiStubException stub = ex as RbxApiStubException;
            if ((error == null && stub == null) || error?.ModId != null)
            {
                return ex;
            }

            // WHY: the datatype metatables are process-wide statics built without a mod context, so
            // their errors used to reach the AI without the [mod: line:] prefix every instance error
            // carries. The running state's own game proxy names the mod that owns the state (the
            // HttpService adapter resolves its context the same way); the one-off surface's proxy
            // has no owner id, so its errors stay unprefixed exactly as before.
            string ownerModId = (context ?? ResolveStateContext(state))?.OwnerModId;
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                return ex;
            }

            // WHY: Persistent mods currently have one author source and Lua-CSharp names the VM chunk
            // generically. The live traceback still carries the post-downlevel author line, so expose
            // that line under the virtual author-relative main.lua path until multi-file chunks land.
            int line = state.GetTraceback().LastLine;
            if (error != null)
            {
                return error.WithContext(ownerModId, ModMainScript, line);
            }

            return FromStubException(stub, ownerModId, ModMainScript, line) ?? ex;
        }

        /// <summary>The mod context that owns a state, read from its <c>game</c> proxy; null when absent.</summary>
        private static LuaCsRbxModContext ResolveStateContext(LuaState state)
        {
            LuaValue game = state?.Environment["game"] ?? LuaValue.Nil;
            return TryGetInstance(game, out LuaCsRbxInstanceProxy proxy) ? proxy.Context : null;
        }

        /// <summary>
        /// Re-expresses a datatype-layer <see cref="RbxApiStubException"/> as the equivalent
        /// <see cref="RbxError"/> carrying the mod/line prefix; null for an unknown code.
        /// </summary>
        private static RbxError FromStubException(RbxApiStubException stub, string modId,
            string script, int line)
        {
            if (!TryParseWireCode(stub.Code, out RbxErrorCode code))
            {
                return null;
            }

            string message = stub.Message;
            string head = stub.Code + ": ";
            string tail = " | fix: " + stub.Fix;
            if (message.StartsWith(head, StringComparison.Ordinal)
                && message.EndsWith(tail, StringComparison.Ordinal)
                && message.Length >= head.Length + tail.Length)
            {
                message = message.Substring(head.Length, message.Length - head.Length - tail.Length);
            }

            return new RbxError(code, message, stub.Fix, modId, script, line);
        }

        private static bool TryParseWireCode(string wireName, out RbxErrorCode code)
        {
            foreach (RbxErrorCode candidate in (RbxErrorCode[])Enum.GetValues(typeof(RbxErrorCode)))
            {
                if (string.Equals(RbxError.ToWireName(candidate), wireName, StringComparison.Ordinal))
                {
                    code = candidate;
                    return true;
                }
            }

            code = default;
            return false;
        }

        // ---- Wrap / unwrap ------------------------------------------------------------------

        public static LuaValue Box(object value, LuaTable metatable)
        {
            return new LuaValue(new LuaCsRbxValueBox(value, metatable));
        }

        public static bool TryUnbox<T>(LuaValue value, out T result)
        {
            if (value.TryRead(out LuaCsRbxValueBox box) && box.Value is T typed)
            {
                result = typed;
                return true;
            }

            result = default;
            return false;
        }

        public static bool TryGetInstance(LuaValue value, out LuaCsRbxInstanceProxy proxy)
        {
            return value.TryRead(out proxy) && proxy != null;
        }

        // ---- Typed argument readers ---------------------------------------------------------

        public static LuaValue Arg(LuaFunctionExecutionContext ctx, int index)
        {
            return ctx.HasArgument(index) ? ctx.GetArgument(index) : LuaValue.Nil;
        }

        // WHY: the legacy readers below name `index + 1`, which is the user's argument number only
        // for a static function (Vector3.new). For a method, VM slot 0 is `self`, and Roblox numbers
        // method arguments without it ("Argument 1 missing or nil"). The overloads taking
        // `argumentNumber` let every caller name the position the author actually typed; the legacy
        // ones stay for callers that have not migrated yet.

        public static float ReadFloat(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadFloat(ctx, index, what, index + 1);
        }

        /// <summary>Reads a number at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/> in errors.</summary>
        public static float ReadFloat(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            return (float)ReadDouble(ctx, index, what, argumentNumber);
        }

        /// <summary>
        /// Legacy optional-number reader; delegates to the strict overload with a generic label.
        /// </summary>
        public static float ReadFloatOr(LuaFunctionExecutionContext ctx, int index, float fallback)
        {
            return ReadFloatOr(ctx, index, fallback, "function", index + 1);
        }

        /// <summary>
        /// Optional number with Luau's argument coercion: nil (or absent) yields
        /// <paramref name="fallback"/>, a number is used as is, a numeric string is converted like
        /// <c>tonumber</c>, and anything else is a BAD_ARGUMENT naming the position.
        /// </summary>
        public static float ReadFloatOr(LuaFunctionExecutionContext ctx, int index, float fallback,
            string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (TryCoerceNumber(value, out double number))
            {
                return (float)number;
            }

            throw ExpectedArgument(what, "a number", value, argumentNumber);
        }

        public static double ReadDouble(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadDouble(ctx, index, what, index + 1);
        }

        /// <summary>Reads a number at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/> in errors.</summary>
        public static double ReadDouble(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.Number)
            {
                throw ExpectedArgument(what, "a number", value, argumentNumber);
            }

            return value.Read<double>();
        }

        public static string ReadString(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadString(ctx, index, what, index + 1);
        }

        /// <summary>Reads a string at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/> in errors.</summary>
        public static string ReadString(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.String)
            {
                throw ExpectedArgument(what, "a string", value, argumentNumber);
            }

            return value.Read<string>();
        }

        public static RbxVector3 ReadVector3(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadVector3(ctx, index, what, index + 1);
        }

        /// <summary>Reads a Vector3 at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/> in errors.</summary>
        public static RbxVector3 ReadVector3(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            throw ExpectedArgument(what, "a Vector3", value, argumentNumber);
        }

        public static RbxCFrame ReadCFrame(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadCFrame(ctx, index, what, index + 1);
        }

        /// <summary>Reads a CFrame at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/> in errors.</summary>
        public static RbxCFrame ReadCFrame(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            throw ExpectedArgument(what, "a CFrame", value, argumentNumber);
        }

        /// <summary>
        /// The §5.2.7 BAD_ARGUMENT for a call argument of the wrong type:
        /// "<c>what</c> expects a Vector3 at argument 2 | fix: pass a Vector3, got string at argument 2".
        /// </summary>
        /// <param name="expected">The expected type with its article, e.g. "a Vector3" or "an EnumItem".</param>
        public static RbxError ExpectedArgument(string what, string expected, LuaValue got,
            int argumentNumber)
        {
            return RbxError.BadArgument(
                what + " expects " + expected + " at argument " + argumentNumber,
                "pass " + expected + ", got " + Describe(got) + " at argument " + argumentNumber);
        }

        // ---- Property assignment ------------------------------------------------------------

        /// <summary>
        /// The BAD_ARGUMENT for a property write of the wrong type. A property assignment has no
        /// argument list, so the message names the property instead of a position:
        /// "Part.Name expects a string, got number | fix: assign a string to Part.Name".
        /// </summary>
        /// <param name="expected">The expected type with its article, e.g. "a string" or "a Vector3".</param>
        public static RbxError PropertyAssignmentError(string ownerName, string property,
            string expected, LuaValue got)
        {
            string target = ownerName + "." + property;
            return RbxError.BadArgument(
                target + " expects " + expected + ", got " + Describe(got),
                "assign " + expected + " to " + target);
        }

        /// <summary>Reads the value of a property write as a string or raises <see cref="PropertyAssignmentError"/>.</summary>
        public static string ReadAssignedString(LuaValue value, string ownerName, string property)
        {
            if (value.Type != LuaValueType.String)
            {
                throw PropertyAssignmentError(ownerName, property, "a string", value);
            }

            return value.Read<string>();
        }

        /// <summary>
        /// Reads the value of a property write as a number (numeric strings coerce like
        /// <c>tonumber</c>) or raises <see cref="PropertyAssignmentError"/>.
        /// </summary>
        public static double ReadAssignedNumber(LuaValue value, string ownerName, string property)
        {
            if (TryCoerceNumber(value, out double number))
            {
                return number;
            }

            throw PropertyAssignmentError(ownerName, property, "a number", value);
        }

        /// <summary>
        /// Reads the value of a property write as a boolean or raises
        /// <see cref="PropertyAssignmentError"/>: Lua truthiness would turn "false" into true.
        /// </summary>
        public static bool ReadAssignedBoolean(LuaValue value, string ownerName, string property)
        {
            if (value.Type != LuaValueType.Boolean)
            {
                throw PropertyAssignmentError(ownerName, property, "a boolean", value);
            }

            return value.Read<bool>();
        }

        /// <summary>
        /// Luau's number-argument coercion: a number as is, or a string <c>tonumber</c> would accept
        /// (decimal with optional exponent, or 0x hexadecimal, surrounding whitespace allowed).
        /// </summary>
        public static bool TryCoerceNumber(LuaValue value, out double number)
        {
            if (value.Type == LuaValueType.Number)
            {
                number = value.Read<double>();
                return true;
            }

            if (value.Type == LuaValueType.String)
            {
                return TryParseLuaNumber(value.Read<string>(), out number);
            }

            number = 0d;
            return false;
        }

        private static bool TryParseLuaNumber(string text, out double number)
        {
            number = 0d;
            string trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            int start = trimmed[0] == '-' || trimmed[0] == '+' ? 1 : 0;
            bool negative = trimmed[0] == '-';
            if (trimmed.Length > start + 2 && trimmed[start] == '0'
                                           && (trimmed[start + 1] == 'x' || trimmed[start + 1] == 'X'))
            {
                if (!ulong.TryParse(trimmed.Substring(start + 2), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out ulong hex))
                {
                    return false;
                }

                number = negative ? -(double)hex : hex;
                return true;
            }

            // WHY: double.TryParse also accepts "Infinity", "NaN" and culture symbols, none of which
            // tonumber turns into a number, so only digits, a point and an exponent get through.
            for (int index = start; index < trimmed.Length; index++)
            {
                char c = trimmed[index];
                bool allowed = (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E'
                               || ((c == '+' || c == '-') && index > start
                                                          && (trimmed[index - 1] == 'e'
                                                              || trimmed[index - 1] == 'E'));
                if (!allowed)
                {
                    return false;
                }
            }

            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
                out number);
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
                    if (value.TryRead(out LuaCsRbxValueBox box))
                    {
                        return DatatypeName(box.Value);
                    }

                    return value.TryRead(out LuaCsRbxInstanceProxy _) ? "Instance" : "userdata";
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
                case RbxTweenInfo _: return "TweenInfo";
                case RbxEnumItem _: return "EnumItem";
                case RbxEnum _: return "Enum";
                case RbxRandom _: return "Random";
                case RbxScriptSignal _: return "RBXScriptSignal";
                case RbxScriptConnection _: return "RBXScriptConnection";
                case RbxInputObject _: return "InputObject";
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
