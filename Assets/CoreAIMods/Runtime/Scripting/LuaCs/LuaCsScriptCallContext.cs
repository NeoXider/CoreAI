using System.Threading;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="ScriptCallContext"/> wrapping one
    /// <see cref="LuaFunctionExecutionContext"/>. Typed accessors mirror the typed-delegate coercion:
    /// nil/absent maps to null/0/false; a number read as a string becomes the text Lua's <c>tostring</c>
    /// gives it, as Lua's own library coerces it; any other wrong non-nil kind raises Lua's own
    /// "bad argument #n (x expected, got y)" error (<see cref="LuaCsBadArgumentException"/>), never the
    /// engine's CLR conversion text.
    /// </summary>
    internal sealed class LuaCsScriptCallContext : ScriptCallContext
    {
        private readonly LuaFunctionExecutionContext _ctx;
        private LuaCsScriptState _state;

        internal LuaCsScriptCallContext(LuaFunctionExecutionContext ctx, CancellationToken cancellationToken)
        {
            _ctx = ctx;
            CancellationToken = cancellationToken;
        }

        /// <summary>
        /// The token the VM called this host function with: the running context's own, which a guard
        /// trip or a kill of the calling thread cancels. A host function that runs mod code on behalf
        /// of its caller passes it on.
        /// </summary>
        internal CancellationToken CancellationToken { get; }

        /// <inheritdoc />
        public override IScriptState State => _state ??= new LuaCsScriptState(_ctx.State);

        /// <inheritdoc />
        public override IValueMarshaller Marshaller => LuaCsValueMarshaller.Instance;

        /// <inheritdoc />
        public override int ArgumentCount => _ctx.ArgumentCount;

        /// <inheritdoc />
        public override bool HasArgument(int index)
        {
            return _ctx.HasArgument(index);
        }

        /// <inheritdoc />
        public override object GetArgument(int index)
        {
            return LuaCsValueMarshaller.Box(Raw(index));
        }

        /// <inheritdoc />
        public override string GetString(int index)
        {
            LuaValue value = Raw(index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (value.Type == LuaValueType.Number)
            {
                return LuaCsValueMarshaller.ReadStringArgument(value);
            }

            return value.TryRead(out string text)
                ? text
                : throw LuaCsBadArgumentException.TypeMismatch(index + 1, "string", value);
        }

        /// <inheritdoc />
        public override double GetNumber(int index)
        {
            LuaValue value = Raw(index);
            if (value.Type == LuaValueType.Nil)
            {
                return 0d;
            }

            return value.TryRead(out double number)
                ? number
                : throw LuaCsBadArgumentException.TypeMismatch(index + 1, "number", value);
        }

        /// <inheritdoc />
        public override bool GetBoolean(int index)
        {
            LuaValue value = Raw(index);
            if (value.Type == LuaValueType.Nil)
            {
                return false;
            }

            return value.TryRead(out bool flag)
                ? flag
                : throw LuaCsBadArgumentException.TypeMismatch(index + 1, "boolean", value);
        }

        /// <inheritdoc />
        public override IScriptTable GetTable(int index)
        {
            LuaValue value = Raw(index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            return value.TryRead(out LuaTable table)
                ? new LuaCsScriptTable(table)
                : throw LuaCsBadArgumentException.TypeMismatch(index + 1, "table", value);
        }

        /// <inheritdoc />
        public override ScriptValueKind GetKind(int index)
        {
            return LuaCsValueMarshaller.Instance.GetKind(LuaCsValueMarshaller.Box(Raw(index)));
        }

        /// <inheritdoc />
        public override string DescribeArgument(int index)
        {
            return Raw(index).ToString();
        }

        private LuaValue Raw(int index)
        {
            return _ctx.HasArgument(index) ? _ctx.GetArgument(index) : LuaValue.Nil;
        }
    }
}
