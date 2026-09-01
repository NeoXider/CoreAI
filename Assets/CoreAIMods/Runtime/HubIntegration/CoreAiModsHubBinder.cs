using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.WorldPackages;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using UnityEngine;
using VContainer;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Lights up the CoreAI Hub's live <b>Mods</b> tab when the Lua mods module is present. The Hub package
    /// is core-level and cannot reference the Lua-CSharp runtime, so this module-owned component bridges the
    /// two: it resolves the running <see cref="LuaCsModRuntime"/> from the child
    /// <see cref="CoreAiModsLifetimeScope"/> and registers <see cref="HubModsPages"/> into a sibling (or
    /// scene) <see cref="CoreAiHubWindow"/>'s registry. Built-in tabs (About/Chat/Settings/Statistics) come
    /// from the Hub's own bootstrap; this only adds Mods — so dropping the module in "lights up" the tab.
    /// </summary>
    public sealed class CoreAiModsHubBinder : MonoBehaviour
    {
        [Tooltip("Hub window to add the Mods tab to. Leave empty to find one on this GameObject or the scene.")]
        [SerializeField]
        private CoreAiHubWindow hubWindow;

        /// <summary>
        /// Host opt-in to grant the <see cref="LuaCapabilities.Full"/> tier (which unlocks reflection) to
        /// mods loaded through the Mods tab. Defaults to <c>false</c> (safe): imported/shared/rehydrated
        /// mods can then never self-escalate to Full from their own <c>@coreai</c> header. Enable this ONLY
        /// for trusted, first-party, or singleplayer content — it is a deliberate host decision, never
        /// derived from an untrusted mod.
        /// </summary>
        [Tooltip("SECURITY: Grant Full tier (reflection) to mods loaded through the Mods tab. Leave OFF " +
                 "unless the content is trusted/first-party/singleplayer — imported or shared mods can " +
                 "never self-escalate to Full unless this host flag is explicitly enabled.")]
        [SerializeField]
        private bool allowFullTier;

        private HubWorldLoadConfirmationPage _worldLoadConfirmationPage;

        private void Start()
        {
            CoreAiHubWindow window = hubWindow != null
                ? hubWindow
                : GetComponent<CoreAiHubWindow>() ?? FindFirstObjectByType<CoreAiHubWindow>();
            if (window == null)
            {
                return; // no Hub in the scene — nothing to light up
            }

            CoreAiModsLifetimeScope modsScope = FindFirstObjectByType<CoreAiModsLifetimeScope>();
            if (modsScope == null || modsScope.Container == null)
            {
                return; // mods module not wired in this scene
            }

            // WHY: The Hub bootstrap owns the registry (built-in tabs). Add the Mods page to it so the window's
            // PageRegistered event rebuilds the tab bar with a Mods tab; create one only if none exists yet.
            HubPageRegistry registry = window.Registry ?? new HubPageRegistry();

            IObjectResolver container = modsScope.Container;

            // WHY: Light up the built-in Settings/Statistics tabs with live DI sources. The Hub's own DI-free
            // bootstrap (CoreAiHubDemo) registers those pages with null sources, so they render a setup
            // note; re-registering by the same id (last-writer-wins) upgrades them to the running config
            // and live orchestration metrics wherever this module is present.
            ICoreAISettings settings = container.ResolveOrDefault<ICoreAISettings>();
            InMemoryAiOrchestrationMetrics metrics = container.ResolveOrDefault<InMemoryAiOrchestrationMetrics>();
            if (settings != null || metrics != null)
            {
                registry.Register(
                    HubSettingsPage.DefaultPageId,
                    () => new HubSettingsPage(settings),
                    100);
                registry.Register(
                    HubStatisticsPage.DefaultPageId,
                    () => new HubStatisticsPage(metrics, settings),
                    200);
            }

            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            IActorIdentityProvider actorIdentityProvider = container.Resolve<IActorIdentityProvider>();
            ActorContext actorContext = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            ILuaModSourceStore sourceStore = container.ResolveOrDefault<ILuaModSourceStore>();
            LuaCapabilities grant = allowFullTier
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            HubModsPages.Register(registry, runtime, actorContext, sourceStore, grant, allowFullTier);

            IRbxWorldRuntimeService worldRuntimeService =
                container.ResolveOrDefault<IRbxWorldRuntimeService>();
            if (worldRuntimeService != null)
            {
                _worldLoadConfirmationPage = HubModsPages.RegisterWorldLoadConfirmation(
                    registry,
                    worldRuntimeService,
                    () =>
                    {
                        window.SetCollapsed(false);
                        window.ActivatePage(HubModsPages.WorldLoadsPageId);
                    });
            }

            if (window.Registry == null)
            {
                window.Registry = registry;
            }
        }

        private void OnDestroy()
        {
            _worldLoadConfirmationPage?.OnDestroyed();
            _worldLoadConfirmationPage = null;
        }
    }
}
