using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="ScriptCallContext"/> wrapping one
    /// <see cref="LuaFunctionExecutionContext"/>. Typed accessors mirror the typed-delegate coercion:
    /// nil/absent maps to null/0/false; wrong non-nil kinds surface the engine's own cast error.
    /// </summary>
    internal sealed class LuaCsScriptCallContext : ScriptCallContext
    {
        private readonly LuaFunctionExecutionContext _ctx;
        private LuaCsScriptState _state;

        internal LuaCsScriptCallContext(LuaFunctionExecutionContext ctx)
        {
            _ctx = ctx;
        }

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
            return value.Type == LuaValueType.Nil ? null : value.Read<string>();
        }

        /// <inheritdoc />
        public override double GetNumber(int index)
        {
            LuaValue value = Raw(index);
            return value.Type == LuaValueType.Nil ? 0d : value.Read<double>();
        }

        /// <inheritdoc />
        public override bool GetBoolean(int index)
        {
            LuaValue value = Raw(index);
            return value.Type != LuaValueType.Nil && value.Read<bool>();
        }

        /// <inheritdoc />
        public override IScriptTable GetTable(int index)
        {
            LuaValue value = Raw(index);
            return value.Type == LuaValueType.Nil ? null : new LuaCsScriptTable(value.Read<LuaTable>());
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
