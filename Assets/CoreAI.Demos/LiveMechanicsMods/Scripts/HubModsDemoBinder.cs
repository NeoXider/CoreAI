using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo glue that gives the CoreAI Hub a live <b>Mods</b> tab. The Hub package (core-level) cannot
    /// reference the Lua-CSharp runtime, so this binder — living in the Demos assembly, which sees both
    /// packages — resolves the running <see cref="LuaCsModRuntime"/> from the child
    /// <see cref="CoreAiModsLifetimeScope"/> and registers the Mods page (plus About / Settings /
    /// Statistics) into a fresh <see cref="HubPageRegistry"/> fed to the sibling <see cref="CoreAiHubWindow"/>.
    /// Drop this on a GameObject with a <see cref="UnityEngine.UIElements.UIDocument"/> +
    /// <see cref="CoreAiHubWindow"/> and the Hub shows real, editable mods at runtime.
    /// </summary>
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class HubModsDemoBinder : MonoBehaviour
    {
        [Tooltip("Grant Full tier to mods created/edited through the Hub's Mods page (host/singleplayer only).")]
        [SerializeField] private bool allowFullTier = true;

        private void Start()
        {
            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            if (window == null)
            {
                return;
            }

            HubPageRegistry registry = new();

            // About tab is always available so the Hub is never empty.
            registry.Register(HubAboutPage.DefaultPageId, () => new HubAboutPage(), order: 1000);

            // Resolve the live mod runtime from the child mods scope (its container also sees core regs).
            CoreAiModsLifetimeScope modsScope = FindFirstObjectByType<CoreAiModsLifetimeScope>();
            if (modsScope == null || modsScope.Container == null)
            {
                Debug.LogWarning("[HubModsDemoBinder] CoreAiModsLifetimeScope not found — Hub shows About only.");
                window.Registry = registry;
                return;
            }

            IObjectResolver container = modsScope.Container;

            // Settings / Statistics tabs (Settings shows the live backend config; Statistics shows a note
            // unless metrics are wired). Chat is omitted here — the scene's standalone chat drives the tasks.
            ICoreAISettings settings = container.ResolveOrDefault<ICoreAISettings>();
            HubBuiltInPages.RegisterAll(registry, settings: settings);

            // The live Mods tab: list / search / add / edit / enable / disable / delete the mods the AI
            // (or the task buttons) create, backed by the running Lua-CSharp runtime.
            LuaCsModRuntime runtime = container.Resolve<LuaCsModRuntime>();
            ILuaModSourceStore sourceStore = container.ResolveOrDefault<ILuaModSourceStore>();
            LuaCapabilities grant = allowFullTier
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            HubModsPages.Register(registry, runtime, sourceStore, grant, allowFull: allowFullTier);

            window.Registry = registry;
        }
    }
}
