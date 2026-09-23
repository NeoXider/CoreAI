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

        [Header("Rbx world")]
        [Tooltip("Optional scene RbxWorldHost. When set, the Rbx API (Instance.new/Part properties) " +
                 "materializes parts as GameObjects under the host via its binder; when empty, the Rbx " +
                 "world is headless in-memory.")]
        [SerializeField]
        private Mods.Rbx.Binding.RbxWorldHost robloxWorldHost;

        [Tooltip("Optional character motor provider. When set, every Humanoid that gets a body "
            + "asks it first, so the game can drive Rbx characters with its own controller; "
            + "CoreAI's own motor answers for whatever the provider declines. See "
            + "Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md.")]
        [SerializeField]
        private Mods.Rbx.Binding.RbxCharacterMotorProviderBehaviour characterMotorProvider;

        [Header("Network transport")]
        [Tooltip("Optional network bridge provider, e.g. the CoreAI Mirror package's "
            + "CoreAiMirrorNetworkBridgeProvider. When set, the Rbx world's RemoteEvents and "
            + "RemoteFunctions travel over its transport and remote players can be admitted. When "
            + "empty, the world runs headless/offline on the in-process null bridge: no remote "
            + "peers, remotes loop back locally.")]
        [SerializeField]
        private Mods.Rbx.Binding.RbxNetworkBridgeProviderBehaviour networkBridgeProvider;

        [Header("Lua coroutine resume budget")]
        [Tooltip("Per-resume budget every guarded Lua coroutine arms by default (instruction-step cap "
            + "and wall-clock cap) — the same mechanism a runaway 'while true do end' handler is cut "
            + "by. Values <= 0 fall back to CoreAI's documented defaults "
            + "(LuaCsCoroutineHandle.DefaultBudgetPerResume / DefaultResumeTimeoutMs). The wall-clock "
            + "half can also be changed live at runtime by a mod's ScriptContext:SetTimeout(seconds), "
            + "which requires the composition-issued unrestricted (host) grant.")]
        [SerializeField]
        private Sandbox.LuaCs.LuaCsCoroutineBudgetSettings coroutineResumeBudget = new();

        [Header("Mod store")]
        [Tooltip("Optional namespace for this composition's persisted mods. Empty = the shared default " +
                 "store (main game). Set a distinct id per demo/scene so mods saved by one composition " +
                 "never rehydrate in another with a different Lua tier.")]
        [SerializeField]
        private string storeId;

        /// <summary>
        /// Whether this composition grants the Full Lua tier (reflection over arbitrary
        /// GameObjects/components) to <c>execute_lua</c>, <c>manage_mods</c> and the mods they load.
        /// This is the only inspector switch that grants Full: <see cref="Configure"/> passes it to
        /// <see cref="CoreAiModsInstaller.RegisterCoreAiMods"/>. Scene helpers that load persisted mods
        /// themselves read it so they request the same tier the host grants.
        /// </summary>
        public bool FullLuaAccessEnabled => enableFullLuaAccess;

        /// <summary>
        /// Whether Full-tier Lua reflection in this composition may touch non-public members.
        /// Has an effect only while <see cref="FullLuaAccessEnabled"/> is on.
        /// </summary>
        public bool FullLuaPrivateAccessEnabled => enableFullLuaPrivateAccess;

        // WHY: Parenting to the CoreAI scope is done via VContainer's `parentReference` (set in the scene to
        // CoreAILifetimeScope). That path defers this child's build until the parent container exists —
        // overriding FindParent to return the parent directly would bypass the deferral and NRE when this
        // scope awakes before the parent.

        protected override void Configure(IContainerBuilder builder)
        {
            // WHY: execute_lua's rate limiter is module-owned: the Lua-free core no longer registers it, so the
            // mods module supplies it here. The Lua-CSharp sandbox is created inside the factory, so no
            // sandbox registration is needed at this scope.
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

            if (robloxWorldHost != null)
            {
                // WHY: explicit serialized reference per ARCHITECTURE_RULES par.2 (no scene reflection,
                // no static singleton) - the host's world/binder become the Rbx API backing so Lua
                // parts materialize as GameObjects instead of the headless in-memory default.
                builder.RegisterInstance(robloxWorldHost);
            }

            if (characterMotorProvider != null)
            {
                // WHY registered as the interface: composition asks for
                // IRbxCharacterMotorProvider, and a host with no scene state registers its own
                // implementation the same way without deriving from the behaviour.
                builder.RegisterInstance<Mods.Rbx.Binding.IRbxCharacterMotorProvider>(
                    characterMotorProvider);
            }

            if (networkBridgeProvider != null)
            {
                // WHY a factory and not RegisterInstance: Configure runs in Awake, and registering a
                // transport must not build one there. The installer resolves INetworkBridge inside
                // its own world factory, so this defers construction to that first resolve — the
                // latest point the composition allows — and a scope built after Mirror started never
                // touches Mirror in Awake at all. Singleton keeps it one bridge per scope.
                builder.Register(_ => networkBridgeProvider.Bridge, Lifetime.Singleton)
                    .As<Mods.Rbx.Instances.Networking.INetworkBridge>();
            }

            // WHY always registered, never conditionally: unlike the motor provider this field is
            // never null (a fresh LuaCsCoroutineBudgetSettings already resolves to CoreAI's documented
            // defaults), and registering the SAME serialized instance is what lets
            // ScriptContext:SetTimeout mutate one object that every coroutine-handle construction site
            // in this composition reads live — see CoreAiModsInstaller and LuaCsCoroutineBudgetSettings.
            builder.RegisterInstance(coroutineResumeBudget ?? new Sandbox.LuaCs.LuaCsCoroutineBudgetSettings());

            IEnumerable<string> scenes = allowedLuaScenes is { Length: > 0 } ? allowedLuaScenes : null;
            builder.RegisterCoreAiMods(
                scenes,
                enableFullLuaAccess,
                enableFullLuaPrivateAccess,
                blacklistPolicy as IFullLuaAccessBlacklistPolicy,
                storeId);
        }
    }
}
