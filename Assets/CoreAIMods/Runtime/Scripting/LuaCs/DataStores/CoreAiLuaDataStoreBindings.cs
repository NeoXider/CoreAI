using System;
using CoreAI.Mods.Rbx.Instances.DataStores;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The one-line hook of <c>DataStoreService</c> into a mod stack: it gives one Rbx world its service over the
    /// composition's <see cref="RbxDataStoreBackend"/> and returns the <c>LuaCsModStackOptions.AdditionalGameplayBindings</c>
    /// callback that installs the Lua surface into every mod and one-off chunk, chained after the host's own additions.
    /// The service lives as long as the world; the data lives as long as the backend.
    /// </summary>
    public static class CoreAiLuaDataStoreBindings
    {
        /// <summary>
        /// Attaches the service to <paramref name="rbxApi"/> and returns the chained callback; returns
        /// <paramref name="existing"/> unchanged when there is no Rbx world.
        /// </summary>
        /// <param name="rbxApi">The world's Rbx bindings.</param>
        /// <param name="backend">The composition's data; null keeps this world's data in memory (headless hosts, tests).</param>
        /// <param name="existing">The host's own gameplay additions, kept and run first.</param>
        public static Action<LuaCsApiRegistry, LuaCapabilities> Attach(LuaCsRbxApiBindings rbxApi,
            RbxDataStoreBackend backend, Action<LuaCsApiRegistry, LuaCapabilities> existing = null)
        {
            return Attach(rbxApi, backend, existing, out _);
        }

        internal static Action<LuaCsApiRegistry, LuaCapabilities> Attach(LuaCsRbxApiBindings rbxApi,
            RbxDataStoreBackend backend, Action<LuaCsApiRegistry, LuaCapabilities> existing,
            out LuaCsRbxDataStoreAdapter adapter)
        {
            adapter = null;
            if (rbxApi == null)
            {
                return existing;
            }

            RbxDataStoreBackend resolved = backend ?? new RbxDataStoreBackend(report: rbxApi.LogSink);
            if (backend == null)
            {
                rbxApi.RegisterRuntimeLifetime(resolved);
            }

            LuaCsRbxDataStoreAdapter created = new(rbxApi, resolved);
            adapter = created;
            return (registry, capabilities) =>
            {
                existing?.Invoke(registry, capabilities);
                created.Register(registry);
            };
        }
    }
}
