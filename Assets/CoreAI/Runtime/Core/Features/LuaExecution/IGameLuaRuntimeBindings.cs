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

    /// <summary>
    /// Optional extension of <see cref="IGameLuaRuntimeBindings"/> for binding sets that hold
    /// mutable per-run transaction state on a shared (singleton) instance. Top-level executors
    /// call <see cref="ResetTransactions"/> around every chunk so a transaction left open by a
    /// script that died between begin and commit (error/budget) cannot bleed into the next
    /// script and silently buffer its world commands. Aggregators forward the call to every
    /// wrapped binding set that implements this interface.
    /// </summary>
    public interface ILuaTransactionScope
    {
        /// <summary>
        /// Discards any unfinished transaction so the next chunk starts from a clean state.
        /// Safe to call when no transaction is active.
        /// </summary>
        void ResetTransactions();
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