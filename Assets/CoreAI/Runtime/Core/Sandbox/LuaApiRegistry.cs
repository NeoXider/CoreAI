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

        /// <summary>Exposes all registered Lua API callbacks on the script global table.</summary>
        public void ApplyToGlobals(Table globals)
        {
            foreach (KeyValuePair<string, Delegate> kv in _apis)
            {
                Delegate inner = kv.Value;
                string key = kv.Key;
                globals[key] = DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        DynValue[] arr = args.GetArray();
                        object[] clr = new object[arr.Length];
                        for (int i = 0; i < arr.Length; i++)
                        {
                            clr[i] = arr[i].ToObject();
                        }

                        object result = inner.DynamicInvoke(clr);
                        return DynValue.FromObject(ctx.GetScript(), result);
                    }
                    catch (Exception ex)
                    {
                        throw new ScriptRuntimeException($"api '{key}': {ex.InnerException?.Message ?? ex.Message}");
                    }
                }, key);
            }
        }
    }
}
#endif
