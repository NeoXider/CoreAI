#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Registry of host callbacks exposed to secured Lua scripts.
    /// </summary>
    public sealed class LuaApiRegistry
    {
        private readonly Dictionary<string, Delegate> _apis = new(StringComparer.Ordinal);

        /// <summary>Registers a new value or callback with the target runtime registry.</summary>
        public void Register(string name, Delegate callback)
        {
            _apis[name] = callback;
        }

        /// <summary>Attempts to resolve a registered Lua API callback by name.</summary>
        public bool TryGet(string name, out Delegate callback)
        {
            return _apis.TryGetValue(name, out callback);
        }

        /// <summary>True when <paramref name="name"/> is registered (tests / introspection).</summary>
        public bool Contains(string name)
        {
            return _apis.ContainsKey(name);
        }

        /// <summary>
        /// Exposes registered callbacks on the script global table. MoonSharp converts CLR
        /// delegates to Lua functions with typed marshalling — no manual DynamicInvoke layer.
        /// </summary>
        public void ApplyToGlobals(Table globals)
        {
            foreach (KeyValuePair<string, Delegate> kv in _apis)
            {
                globals[kv.Key] = kv.Value;
            }
        }
    }
}
#endif
