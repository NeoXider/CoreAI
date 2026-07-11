using System;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreDefaultLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsDefaultRuntimeBindings
    {
        public void Register(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
