using System;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Sandbox;
#endif

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for game lua runtime bindings implementations.
    /// </summary>
    public interface IGameLuaRuntimeBindings
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        /// <summary>Registers gameplay-facing Lua APIs in the provided registry.</summary>
        void RegisterGameplayApis(LuaApiRegistry registry);
#endif
    }

    /// <summary>
    /// Optional extension of <see cref="IGameLuaRuntimeBindings"/> for hosts that need
    /// per-consumer capability scoping (e.g. <see cref="LuaModRuntime"/> loading a read-only mod
    /// from a full-capability aggregator). Implementations must only register binding groups
    /// included in the requested tier.
    /// </summary>
    public interface ICapabilityScopedLuaBindings
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        /// <summary>Registers only the Lua APIs allowed by <paramref name="capabilities"/>.</summary>
        void RegisterGameplayApis(LuaApiRegistry registry, LuaCapabilities capabilities);
#endif
    }

#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
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
#else
    /// <summary>No-op default used when Lua is disabled.</summary>
    public sealed class CoreDefaultLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
    }
#endif
}