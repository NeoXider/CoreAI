using System;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting.LuaCs;
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
    /// fixes name the expected type and position. Every reader converts by the one rule set of
    /// <see cref="LuaCsValueMarshaller"/> (the mod-core surface reads by it too): a number for a
    /// string, a numeric string for a number, an item's Name or Value for an Enum of an instance
    /// member; a boolean is never converted.
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
        /// That line is also the whole error value a script's <c>pcall</c>, <c>xpcall</c> or
        /// <c>coroutine.resume</c> receives, while C# reads <paramref name="ex"/> back from
        /// <see cref="LuaCsHostFunctionException.HostException"/>.
        /// </summary>
        public static LuaRuntimeException ToLuaError(LuaState state, Exception ex)
        {
            if (ex is LuaRuntimeException lua)
            {
                return lua;
            }

            string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            // WHY not LuaRuntimeException(state, new InvalidOperationException(message, ex)): with an inner
            // exception set, pcall gives the mod (and through execute_lua the model) that wrapper's
            // ToString() - CLR type names, the host stack trace, absolute source paths - and
            // coroutine.resume gives it nil. See LuaCsHostFunctionException.
            return new LuaCsHostFunctionException(state, message, ex);
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

        /// <summary>Optional number read by <see cref="ReadDoubleOr"/>, narrowed to a float.</summary>
        public static float ReadFloatOr(LuaFunctionExecutionContext ctx, int index, float fallback,
            string what, int argumentNumber)
        {
            return (float)ReadDoubleOr(ctx, index, fallback, what, argumentNumber);
        }

        /// <summary>
        /// Optional number with Luau's argument coercion (<c>luaL_optnumber</c>): nil (or absent)
        /// yields <paramref name="fallback"/>, a number is used as is, a numeric string is converted
        /// like <c>tonumber</c>, and anything else is a BAD_ARGUMENT naming the position.
        /// </summary>
        public static double ReadDoubleOr(LuaFunctionExecutionContext ctx, int index, double fallback,
            string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (TryCoerceNumber(value, out double number))
            {
                return number;
            }

            throw ExpectedArgument(what, "a number", value, argumentNumber);
        }

        public static double ReadDouble(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadDouble(ctx, index, what, index + 1);
        }

        /// <summary>
        /// Reads a number at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/>
        /// in errors; a numeric string is converted like <c>tonumber</c> (<see cref="TryCoerceNumber"/>).
        /// </summary>
        public static double ReadDouble(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (!TryCoerceNumber(value, out double number))
            {
                throw ExpectedArgument(what, "a number", value, argumentNumber);
            }

            return number;
        }

        public static string ReadString(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadString(ctx, index, what, index + 1);
        }

        /// <summary>
        /// Reads a string at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/>
        /// in errors; a number becomes the text <c>tostring</c> gives it (<see cref="TryCoerceString"/>).
        /// </summary>
        public static string ReadString(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (!TryCoerceString(value, out string text))
            {
                throw ExpectedArgument(what, "a string", value, argumentNumber);
            }

            return text;
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

        /// <summary>
        /// Reads the value of a property write as a string (a number becomes the text
        /// <c>tostring</c> gives it, as Roblox converts it) or raises <see cref="PropertyAssignmentError"/>.
        /// </summary>
        public static string ReadAssignedString(LuaValue value, string ownerName, string property)
        {
            if (!TryCoerceString(value, out string text))
            {
                throw PropertyAssignmentError(ownerName, property, "a string", value);
            }

            return text;
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
        /// Luau's number-argument coercion, shared with the mod-core surface: a number as is, or a
        /// string <c>tonumber</c> would accept (<see cref="LuaCsValueMarshaller.TryCoerceNumber"/>).
        /// </summary>
        public static bool TryCoerceNumber(LuaValue value, out double number)
        {
            return LuaCsValueMarshaller.TryCoerceNumber(value, out number);
        }

        /// <summary>
        /// Luau's string-argument coercion, shared with the mod-core surface: a string as is, or a
        /// number as the text <c>tostring</c> gives it (<see cref="LuaCsValueMarshaller.TryCoerceString"/>).
        /// </summary>
        public static bool TryCoerceString(LuaValue value, out string text)
        {
            return LuaCsValueMarshaller.TryCoerceString(value, out text);
        }

        // ---- Enum coercion ------------------------------------------------------------------

        /// <summary>
        /// Roblox's coercion for an Enum-typed instance member (a property write or a method
        /// argument): an item of <paramref name="enumName"/> as is, a string naming one of its items
        /// (or a registered alias), or a whole number equal to an item's Value. Anything else, an
        /// item of another enum or a numeric string included, is false.
        /// </summary>
        /// <param name="enums">The world's registry, so a converted item is the interned one
        /// <c>Enum.X.Y</c> answers; null accepts only an item.</param>
        public static bool TryCoerceEnumItem(LuaValue value, RbxEnumRegistry enums, string enumName,
            out RbxEnumItem item)
        {
            if (TryUnbox(value, out RbxEnumItem boxed))
            {
                item = boxed.EnumType != null && boxed.EnumType.Name == enumName ? boxed : null;
                return item != null;
            }

            item = null;
            if (enums == null || !enums.TryGet(enumName, out RbxEnum enumType))
            {
                return false;
            }

            if (value.Type == LuaValueType.String)
            {
                return enumType.TryGetItem(value.Read<string>(), out item);
            }

            if (value.Type == LuaValueType.Number)
            {
                double number = value.Read<double>();
                // WHY whole numbers only: every item Value is an int, so a fraction names no item,
                // exactly as Enum.X:FromValue answers nil for it.
                return number == Math.Floor(number) && number >= int.MinValue && number <= int.MaxValue
                       && enumType.TryGetItemByValue((int)number, out item);
            }

            return false;
        }

        /// <summary>
        /// Reads an Enum-typed method argument by <see cref="TryCoerceEnumItem"/>, or raises a
        /// BAD_ARGUMENT naming the position and, for a string or number, the value that named no item.
        /// </summary>
        public static RbxEnumItem ReadEnumArgument(LuaFunctionExecutionContext ctx, int index,
            RbxEnumRegistry enums, string enumName, string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryCoerceEnumItem(value, enums, enumName, out RbxEnumItem item))
            {
                return item;
            }

            string expected = "an Enum." + enumName + " item";
            throw RbxError.BadArgument(
                what + " expects " + expected + " at argument " + argumentNumber,
                "pass " + expected + ", its Name or its Value, got " + DescribeEnumCandidate(value)
                + " at argument " + argumentNumber);
        }

        /// <summary>
        /// Reads the value of an Enum-typed property write by <see cref="TryCoerceEnumItem"/>, or
        /// raises the property-shaped BAD_ARGUMENT, which names the property and the refused value:
        /// <c>Part.Material expects an Enum.Material item, got string "Plastik"</c>.
        /// </summary>
        public static RbxEnumItem ReadAssignedEnumItem(LuaValue value, RbxEnumRegistry enums,
            string enumName, string ownerName, string property)
        {
            if (TryCoerceEnumItem(value, enums, enumName, out RbxEnumItem item))
            {
                return item;
            }

            string target = ownerName + "." + property;
            string expected = "an Enum." + enumName + " item";
            throw RbxError.BadArgument(
                target + " expects " + expected + ", got " + DescribeEnumCandidate(value),
                "assign " + expected + ", its Name or its Value to " + target);
        }

        /// <summary>
        /// <see cref="Describe"/>, plus the text itself for a string or number, so a refused
        /// <c>"Plastik"</c> or <c>999</c> names the value that matched no item.
        /// </summary>
        private static string DescribeEnumCandidate(LuaValue value)
        {
            if (value.Type == LuaValueType.String)
            {
                string text = value.Read<string>();
                char[] shown = (text.Length > 64 ? text.Substring(0, 64) : text).ToCharArray();
                // WHY: the §5.2.7 error is one line, so a control character from the script's
                // string must not split it.
                for (int index = 0; index < shown.Length; index++)
                {
                    if (char.IsControl(shown[index]))
                    {
                        shown[index] = ' ';
                    }
                }

                return "string \"" + new string(shown) + (text.Length > 64 ? "..." : "") + "\"";
            }

            return value.Type == LuaValueType.Number ? "number " + value : Describe(value);
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
