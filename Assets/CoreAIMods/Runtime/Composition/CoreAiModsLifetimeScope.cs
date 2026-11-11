using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
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
        [Header("Host gameplay APIs")]
        [Tooltip("Explicit scene components implementing ICoreAiLuaGameplayBindingsProvider. " +
                 "Each receives the current VM's effective capabilities; providers must tolerate pre-Start rehydrate.")]
        [SerializeField]
        private MonoBehaviour[] gameplayBindingsProviders = System.Array.Empty<MonoBehaviour>();

        /// <summary>Authored providers for this scope; no scene discovery or global provider is used.</summary>
        public IReadOnlyList<MonoBehaviour> GameplayBindingsProviders => gameplayBindingsProviders;

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

        [Header("Instant edit loop")]
        [Tooltip("How AI edits are backed up (write-behind), recorded for the next start (debounce), undone "
                 + "(history length) and rate limited. See Docs/CoreAIMods/ROBLOX_API_ROADMAP.md, P3.")]
        [SerializeField]
        private CoreAiInstantEditSettings instantEdit = new();

        [Header("Chat commands")]
        [Tooltip("Add /undo, /redo (the player's AI changes to the world) and /fly to the in-world chat of the "
                 + "parent CoreAI scope. A command the game registered first under the same name is kept.")]
        [SerializeField]
        private bool worldChatCommands = true;

        [Header("AI from Lua")]
        [Tooltip("Framework setting of this game: mods in every world may create agents (AIService) and call "
                 + "models (TextGenerator). Off by default; a world cannot turn it on.")]
        [SerializeField]
        private bool allowAgentsFromLua;

        [Header("World between starts")]
        [Tooltip("Keep the world between starts: the default world opens again as the player and the AI left it "
                 + "(recorded a few seconds after a change; /save records it now), and a newer bundled sample "
                 + "replaces its older copy. Off by default.")]
        [SerializeField]
        private bool keepWorldBetweenStarts;

        [Header("Player camera")]
        [Tooltip("The default third-person camera around the local character. Turn it off when the game owns "
                 + "the camera.")]
        [SerializeField]
        private CoreAiPlayerCameraSettings playerCamera = new();

        [Header("Self-repair and mod console")]
        [Tooltip("Runtime self-repair of failing AI-written mods and the in-game mod console (F8, /console). "
                 + "See Docs/CoreAIMods/MOD_CONSOLE.md.")]
        [SerializeField]
        private CoreAiModSelfRepairSettings selfRepair = new();

        /// <summary>The self-repair and console settings this scope registers when it builds.</summary>
        public CoreAiModSelfRepairSettings SelfRepair => selfRepair ??= new CoreAiModSelfRepairSettings();

        [System.NonSerialized]
        private IRbxWorldPackageStore runtimeWorldPackageStore;

        /// <summary>The instant edit settings this scope registers when it builds.</summary>
        public CoreAiInstantEditSettings InstantEdit => instantEdit ??= new CoreAiInstantEditSettings();

        /// <summary>
        /// The framework setting <see cref="Ai.LuaCs.CoreAiLuaAiOptions.AllowAgentsFromLua"/> this scope registers
        /// when the host registered no <see cref="Ai.LuaCs.CoreAiLuaAiOptions"/> of its own.
        /// </summary>
        public bool AllowAgentsFromLua => allowAgentsFromLua;

        /// <summary>Sets <see cref="AllowAgentsFromLua"/> before this scope builds its container.</summary>
        public void SetAllowAgentsFromLua(bool allow)
        {
            DemandNotBuilt(nameof(SetAllowAgentsFromLua));
            allowAgentsFromLua = allow;
        }

        /// <summary>The play world setting <see cref="CoreAiWorldKeepOptions.KeepWorldBetweenStarts"/> this scope registers.</summary>
        public bool KeepWorldBetweenStarts => keepWorldBetweenStarts;

        /// <summary>
        /// The options instance this scope registered while it builds (null before, or when the host registered its own);
        /// an installer that runs after this scope's own configuration may still change it before the container builds.
        /// </summary>
        public CoreAiWorldKeepOptions WorldKeepOptions { get; private set; }

        /// <summary>Sets <see cref="KeepWorldBetweenStarts"/> before this scope builds its container.</summary>
        public void SetKeepWorldBetweenStarts(bool keep)
        {
            DemandNotBuilt(nameof(SetKeepWorldBetweenStarts));
            keepWorldBetweenStarts = keep;
        }

        /// <summary>The player camera preset this scope registers when it builds.</summary>
        public CoreAiPlayerCameraSettings PlayerCamera => playerCamera ??= new CoreAiPlayerCameraSettings();

        /// <summary>Supplies the player camera preset before this scope builds its container.</summary>
        public void SetPlayerCameraSettings(CoreAiPlayerCameraSettings settings)
        {
            DemandNotBuilt(nameof(SetPlayerCameraSettings));
            playerCamera = settings ?? throw new System.ArgumentNullException(nameof(settings));
        }

        /// <summary>Supplies the instant edit settings before this scope builds its container.</summary>
        public void SetInstantEditSettings(CoreAiInstantEditSettings settings)
        {
            DemandNotBuilt(nameof(SetInstantEditSettings));
            instantEdit = settings ?? throw new System.ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Points the Rbx API at a scene world host before this scope builds its container; the code path
        /// of the <c>robloxWorldHost</c> Inspector field, used by the one-click world bootstrap.
        /// </summary>
        public void SetRobloxWorldHost(Mods.Rbx.Binding.RbxWorldHost host)
        {
            DemandNotBuilt(nameof(SetRobloxWorldHost));
            robloxWorldHost = host;
        }

        /// <summary>The scene world host this scope registers; null runs the world headless.</summary>
        public Mods.Rbx.Binding.RbxWorldHost RobloxWorldHost => robloxWorldHost;

        /// <summary>Sets the mods store namespace (see the Inspector field) before this scope builds.</summary>
        public void SetModStoreId(string id)
        {
            DemandNotBuilt(nameof(SetModStoreId));
            storeId = id;
        }

        private void DemandNotBuilt(string member)
        {
            if (Container != null)
            {
                throw new System.InvalidOperationException(member + " must be called before the mods scope builds its container.");
            }
        }

        /// <summary>Supplies the host's world-package persistence before this scope builds its container.</summary>
        public void SetWorldPackageStore(IRbxWorldPackageStore store)
        {
            if (Container != null)
            {
                throw new System.InvalidOperationException("SetWorldPackageStore must be called before the mods scope builds its container.");
            }

            runtimeWorldPackageStore = store ?? throw new System.ArgumentNullException(nameof(store));
        }

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
            // mods module supplies it here, configured by the instant edit settings together with the undo
            // history and the gate's write-behind. The Lua-CSharp sandbox is created inside the factory, so
            // no sandbox registration is needed at this scope.
            builder.RegisterCoreAiInstantEditLoop(InstantEdit);
            builder.RegisterInstance(PlayerCamera);

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

            if (!builder.Exists(typeof(CoreAiWorldKeepOptions), false, true))
            {
                WorldKeepOptions = new CoreAiWorldKeepOptions { KeepWorldBetweenStarts = keepWorldBetweenStarts };
                builder.RegisterInstance(WorldKeepOptions);
            }

            if (!builder.Exists(typeof(Ai.LuaCs.CoreAiLuaAiOptions), false, true))
            {
                builder.RegisterInstance(new Ai.LuaCs.CoreAiLuaAiOptions { AllowAgentsFromLua = allowAgentsFromLua });
            }

            IEnumerable<string> scenes = allowedLuaScenes is { Length: > 0 } ? allowedLuaScenes : null;
            builder.RegisterCoreAiMods(
                scenes,
                enableFullLuaAccess,
                enableFullLuaPrivateAccess,
                blacklistPolicy as IFullLuaAccessBlacklistPolicy,
                storeId,
                Mods.Rbx.Instances.InstanceRegistry.CurrentWorldAclVersion,
                null, null, null, runtimeWorldPackageStore,
                CreateAdditionalGameplayBindings());

            if (worldChatCommands)
            {
                builder.RegisterCoreAiWorldChatCommands();
            }

            builder.RegisterCoreAiModSelfRepair(SelfRepair);
        }

        private System.Action<LuaCsApiRegistry, LuaCapabilities> CreateAdditionalGameplayBindings()
        {
            List<MonoBehaviour> providers = new List<MonoBehaviour>();
            foreach (MonoBehaviour provider in gameplayBindingsProviders ?? System.Array.Empty<MonoBehaviour>())
            {
                if (provider == null || providers.Contains(provider))
                {
                    continue;
                }
                if (provider is not ICoreAiLuaGameplayBindingsProvider)
                {
                    throw new System.ArgumentException(
                        "Each assigned gameplay bindings provider must implement ICoreAiLuaGameplayBindingsProvider.",
                        nameof(gameplayBindingsProviders));
                }
                providers.Add(provider);
            }
            if (providers.Count == 0)
            {
                return null;
            }
            // WHY: runtime inspector changes must not retarget the already composed scope's callbacks.
            MonoBehaviour[] snapshot = providers.ToArray();
            return (registry, capabilities) =>
            {
                foreach (MonoBehaviour provider in snapshot)
                {
                    if (provider != null)
                    {
                        ((ICoreAiLuaGameplayBindingsProvider)provider).RegisterGameplayBindings(registry, capabilities);
                    }
                }
            };
        }
    }

    /// <summary>
    /// Whether the play world keeps its world between starts: the default world is recorded as the world that opens on
    /// the next start as the player and the AI change it, and a restored world takes a newer bundled sample
    /// (<see cref="Mods.WorldPackages.RbxWorldRuntimeSessionController.KeepWorldBetweenStarts"/>). Off by default.
    /// </summary>
    public sealed class CoreAiWorldKeepOptions
    {
        /// <summary>True keeps the world between starts.</summary>
        public bool KeepWorldBetweenStarts { get; set; }
    }
}
