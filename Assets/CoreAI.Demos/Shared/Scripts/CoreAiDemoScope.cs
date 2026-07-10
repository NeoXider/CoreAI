#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// Shared scope-resolution helper for demo controllers built around the Lua mods module. Every demo
    /// controller needs the same "prefer the child mods scope, fall back to the core scope" container, so
    /// it lives here once instead of being copy-pasted per controller.
    /// </summary>
    internal static class CoreAiDemoScope
    {
        /// <summary>
        /// Resolves the DI container demo controllers should pull Lua/mods services from: the scene's
        /// <see cref="CoreAiModsLifetimeScope"/> container when one is present, else the given
        /// <paramref name="coreAiScope"/>'s container. Callers must have already verified
        /// <paramref name="coreAiScope"/> and its <see cref="CoreAILifetimeScope.Container"/> are non-null.
        /// </summary>
        public static IObjectResolver ResolveModsContainer(CoreAILifetimeScope coreAiScope)
        {
            CoreAiModsLifetimeScope modsScope = Object.FindFirstObjectByType<CoreAiModsLifetimeScope>();
            return (modsScope != null && modsScope.Container != null) ? modsScope.Container : coreAiScope.Container;
        }

        /// <summary>Convenience for the common case: resolves the container, then <see cref="ILuaModRuntime"/> from it.</summary>
        public static ILuaModRuntime ResolveModsRuntime(CoreAILifetimeScope coreAiScope)
        {
            return ResolveModsContainer(coreAiScope).Resolve<ILuaModRuntime>();
        }
    }
}
#endif
