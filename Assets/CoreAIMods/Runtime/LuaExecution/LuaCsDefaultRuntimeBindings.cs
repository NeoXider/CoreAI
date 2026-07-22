using System;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreDefaultLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsDefaultRuntimeBindings
    {
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
