using System;
using CoreAI.Sandbox;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for game lua runtime bindings implementations.
    /// </summary>
    public interface IGameLuaRuntimeBindings
    {
        /// <summary>Registers gameplay-facing Lua APIs in the provided registry.</summary>
        void RegisterGameplayApis(LuaApiRegistry registry);
    }

    /// <summary>Registers the default CoreAI Lua runtime APIs.</summary>
    public sealed class CoreDefaultLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        /// <inheritdoc />
        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
