using System;
using System.Collections.Generic;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IScriptTable"/>: a read view over a <see cref="LuaTable"/> with
    /// host-projected values (nil to null, boolean/number/string to bool/double/string, nested tables to
    /// nested views, anything else to the boxed raw value).
    /// </summary>
    public sealed class LuaCsScriptTable : IScriptTable
    {
        internal LuaCsScriptTable(LuaTable table)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <summary>The wrapped VM table (adapter-internal).</summary>
        internal LuaTable Table { get; }

        /// <inheritdoc />
        public object this[string key] => Project(Table[key]);

        /// <inheritdoc />
        public bool Has(string key)
        {
            return Table[key].Type != LuaValueType.Nil;
        }

        /// <inheritdoc />
        public IEnumerable<KeyValuePair<object, object>> Pairs
        {
            get
            {
                foreach (KeyValuePair<LuaValue, LuaValue> pair in Table)
                {
                    yield return new KeyValuePair<object, object>(Project(pair.Key), Project(pair.Value));
                }
            }
        }

        private static object Project(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return null;
                case LuaValueType.Boolean:
                    return value.Read<bool>();
                case LuaValueType.Number:
                    return value.Read<double>();
                case LuaValueType.String:
                    return value.Read<string>();
                case LuaValueType.Table:
                    return new LuaCsScriptTable(value.Read<LuaTable>());
                default:
                    return LuaCsValueMarshaller.Box(value);
            }
        }
    }
}
