using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
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
        [SerializeField] private CoreAiHubWindow hubWindow;

        [Tooltip("Grant Full tier to mods created/edited through the Mods tab (host/singleplayer only).")]
        [SerializeField] private bool allowFullTier = true;

        private void Start()
        {
            CoreAiHubWindow window = hubWindow != null
                ? hubWindow
                : (GetComponent<CoreAiHubWindow>() ?? FindFirstObjectByType<CoreAiHubWindow>());
            if (window == null)
            {
                return; // no Hub in the scene — nothing to light up
            }

            CoreAiModsLifetimeScope modsScope = FindFirstObjectByType<CoreAiModsLifetimeScope>();
            if (modsScope == null || modsScope.Container == null)
            {
                return; // mods module not wired in this scene
            }

            // The Hub bootstrap owns the registry (built-in tabs). Add the Mods page to it so the window's
            // PageRegistered event rebuilds the tab bar with a Mods tab; create one only if none exists yet.
            HubPageRegistry registry = window.Registry ?? new HubPageRegistry();

            IObjectResolver container = modsScope.Container;

            // Light up the built-in Settings/Statistics tabs with live DI sources. The Hub's own DI-free
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
                    order: 100);
                registry.Register(
                    HubStatisticsPage.DefaultPageId,
                    () => new HubStatisticsPage(metrics, settings),
                    order: 200);
            }

            LuaCsModRuntime runtime = container.Resolve<LuaCsModRuntime>();
            ILuaModSourceStore sourceStore = container.ResolveOrDefault<ILuaModSourceStore>();
            LuaCapabilities grant = allowFullTier
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            HubModsPages.Register(registry, runtime, sourceStore, grant, allowFull: allowFullTier);

            if (window.Registry == null)
            {
                window.Registry = registry;
            }
        }
    }
}
