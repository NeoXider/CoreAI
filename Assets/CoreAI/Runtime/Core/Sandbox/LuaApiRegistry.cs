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
        private readonly Dictionary<string, Delegate> _apis = new(StringComparer.Ordinal);

        /// <summary>Зарегистрировать глобальную функцию Lua с именем <paramref name="name"/>.</summary>
        public void Register(string name, Delegate callback)
        {
            _apis[name] = callback;
        }

        /// <summary>Проверить наличие API (для тестов и расширений).</summary>
        public bool TryGet(string name, out Delegate callback)
        {
            return _apis.TryGetValue(name, out callback);
        }

        /// <summary>Пробросить все зарегистрированные делегаты в таблицу глобалов MoonSharp.</summary>
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