using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Child <see cref="LifetimeScope"/> that installs the Lua-CSharp mod subsystem on top of a CoreAI
    /// scope. Because the dependency is inverted (<c>CoreAI.Mods</c> -&gt; <c>CoreAI.Source</c> -&gt;
    /// <c>CoreAI.Core</c>), the core scope cannot register Lua services itself; this package-owned scope
    /// does. Parent it to your <c>CoreAILifetimeScope</c> (nest it in the hierarchy, or set this
    /// component's parent reference) so it resolves the command sink, agent policy, settings, logger,
    /// and version store from the core container and grafts <c>execute_lua</c> + <c>manage_mods</c> onto
    /// the Programmer role once built.
    /// </summary>
    public sealed class CoreAiModsLifetimeScope : LifetimeScope
    {
        [Header("Lua capability grant")]
        [Tooltip("When on, mods loaded by this composition may reach the Full tier (reflection over " +
                 "arbitrary GameObjects/components). Host/singleplayer only — never grant to a networked client.")]
        [SerializeField]
        private bool enableFullLuaAccess;

        [Tooltip("When on, Full-tier Lua reflection may touch non-public members. Requires Full access.")]
        [SerializeField]
        private bool enableFullLuaPrivateAccess;

        [Tooltip(
            "Scenes the Lua coreai_world_load_scene binding is allowed to load. Empty = any scene in Build Settings.")]
        [SerializeField]
        private string[] allowedLuaScenes;

        [Tooltip("Optional ScriptableObject implementing IFullLuaAccessBlacklistPolicy to deny Full-tier " +
                 "reflection access to specific component types/members. Leave empty to allow all (default).")]
        [SerializeField]
        private ScriptableObject blacklistPolicy;

        // Parenting to the CoreAI scope is done via VContainer's `parentReference` (set in the scene to
        // CoreAILifetimeScope). That path defers this child's build until the parent container exists —
        // overriding FindParent to return the parent directly would bypass the deferral and NRE when this
        // scope awakes before the parent.

        protected override void Configure(IContainerBuilder builder)
        {
            // execute_lua's rate limiter is module-owned: the Lua-free core no longer registers it, so the
            // mods module supplies it here. The Lua-CSharp sandbox is created inside the factory, so no
            // sandbox registration is needed at this scope.
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

            IEnumerable<string> scenes = allowedLuaScenes is { Length: > 0 } ? allowedLuaScenes : null;
            builder.RegisterCoreAiMods(
                scenes,
                enableFullLuaAccess,
                enableFullLuaPrivateAccess,
                blacklistPolicy as IFullLuaAccessBlacklistPolicy);
        }
    }
}