using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Whitelist вызываемых из Lua делегатов (DGF_SPEC §8).
    /// </summary>
    public sealed class LuaApiRegistry
    {
        private readonly Dictionary<string, Delegate> _apis = new Dictionary<string, Delegate>(StringComparer.Ordinal);

        public void Register(string name, Delegate callback) => _apis[name] = callback;

        public bool TryGet(string name, out Delegate callback) => _apis.TryGetValue(name, out callback);

        public void ApplyToGlobals(Table globals)
        {
            foreach (var kv in _apis)
            {
                var inner = kv.Value;
                var key = kv.Key;
                globals[key] = DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var arr = args.GetArray();
                        var clr = new object[arr.Length];
                        for (var i = 0; i < arr.Length; i++)
                            clr[i] = arr[i].ToObject();
                        var result = inner.DynamicInvoke(clr);
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
