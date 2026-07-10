#if !COREAI_NO_LUA
using System;
using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// Shared scope-resolution helper for demo controllers built around the Lua mods module. Every demo
    /// controller needs the same package-owned child mods scope container, so
    /// it lives here once instead of being copy-pasted per controller.
    /// </summary>
    internal static class CoreAiDemoScope
    {
        /// <summary>
        /// Resolves the DI container demo controllers should pull Lua/mods services from: the scene's
        /// <see cref="CoreAiModsLifetimeScope"/> container. Callers must have already verified
        /// <paramref name="coreAiScope"/> and its <see cref="CoreAILifetimeScope.Container"/> are non-null.
        /// </summary>
        public static IObjectResolver ResolveModsContainer(CoreAILifetimeScope coreAiScope)
        {
            CoreAiModsLifetimeScope modsScope =
                UnityEngine.Object.FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
            if (modsScope?.Container == null)
            {
                throw new InvalidOperationException(
                    "CoreAiModsLifetimeScope is missing or not initialized. Add an active CoreAiMods child " +
                    "scope under CoreAILifetimeScope before starting a Lua/mods demo.");
            }

            return modsScope.Container;
        }

        /// <summary>Convenience for the common case: resolves the container, then <see cref="ILuaModRuntime"/> from it.</summary>
        public static ILuaModRuntime ResolveModsRuntime(CoreAILifetimeScope coreAiScope)
        {
            return ResolveModsContainer(coreAiScope).Resolve<ILuaModRuntime>();
        }
    }
}
#endif
