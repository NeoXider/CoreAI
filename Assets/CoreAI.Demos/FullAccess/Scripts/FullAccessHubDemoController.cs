#if !COREAI_NO_LUA
using System;
using System.Collections;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// Host for the Full Access demo Hub. Builds a <see cref="HubPageRegistry"/> and registers the three
    /// UI Toolkit demo pages (Full Access info, Full-mode mod, Lua platform) alongside the built-in
    /// Chat / Settings / Statistics pages and the live Mods page, then assigns the registry to the sibling
    /// <see cref="CoreAiHubWindow"/>. It also preserves the behavior the old IMGUI controllers owned:
    /// auto-creating the scene <c>TargetCube</c> and enabling the chat agent dropdown at startup.
    /// </summary>
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class FullAccessHubDemoController : MonoBehaviour
    {
        [Tooltip("Object the LLM / mods manipulate via unity_* APIs. Auto-created as 'TargetCube' when empty.")]
        [SerializeField]
        private Transform targetCube;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("GUI-less Lua platform driver (self-test + Tetris). Auto-found when left empty.")]
        [SerializeField]
        private LuaPlatformExampleController luaPlatformDriver;


        [Tooltip("Optional Lua source override for the Full-mode mod. Uses the embedded default when unset.")]
        [SerializeField]
        private TextAsset fullModeModSourceOverride;

        [Tooltip("Backend config asset edited by the Settings tab (Base URL / API key / model). " +
                 "This is the UITK replacement for the old CoreAiBackendPanel.")]
        [SerializeField]
        private CoreAiChatConfig chatConfig;

        private ILuaModRuntime _modsRuntime;
        private bool _modsResolved;

        /// <summary>The registry created and owned by this host.</summary>
        public HubPageRegistry Registry { get; private set; }

        private void Awake()
        {
            EnsureTargetCube();

            Registry = new HubPageRegistry();
            Registry.Register(
                FullAccessInfoHubPage.DefaultPageId,
                () => new FullAccessInfoHubPage(() => targetCube),
                0);
            Registry.Register(
                FullModeModHubPage.DefaultPageId,
                () => new FullModeModHubPage(ResolveModsRuntime, () => targetCube, ModSourceOverrideText()),
                10);
            Registry.Register(
                LuaPlatformHubPage.DefaultPageId,
                () => new LuaPlatformHubPage(ResolveDriver),
                20);
            Registry.Register(
                TokenBudgetHubPage.DefaultPageId,
                () => new TokenBudgetHubPage(),
                90);

            // WHY: no Chat tab here — this scene keeps its dedicated CoreAiChatUI panel. The Hub is the
            // tools companion: Settings (the UITK backend config that replaces CoreAiBackendPanel) and
            // Statistics, both fed live values from the scene scope when available (null-tolerant).
            ICoreAISettings settings = ResolveFromCore<ICoreAISettings>();
            InMemoryAiOrchestrationMetrics metrics = ResolveFromCore<InMemoryAiOrchestrationMetrics>();
            Registry.Register(
                HubSettingsPage.DefaultPageId,
                () => new HubSettingsPage(settings, chatConfig),
                100);
            Registry.Register(
                HubStatisticsPage.DefaultPageId,
                () => new HubStatisticsPage(metrics, settings),
                200);

            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            if (window != null)
            {
                window.Registry = Registry;
            }
        }

        private IEnumerator Start()
        {
            // WHY: DI scopes finish initializing during Awake/Start; wait one frame so the mods container
            // and chat panel are live before we register the Mods tab and flip the agent dropdown.
            yield return null;

            RegisterModsPage();
            yield return EnableAgentDropdownWhenReady();
        }

        private void EnsureTargetCube()
        {
            // Guarantee unity_find('TargetCube') resolves to something even on a bare scene, so the demo
            // works out of the box once Full Lua access is enabled on the scope.
            if (targetCube == null)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(cube);
                cube.name = "TargetCube";
                cube.transform.position = new Vector3(0f, 0.5f, 0f);
                targetCube = cube.transform;
            }
            else if (targetCube.name != "TargetCube")
            {
                // The prompts and mods refer to it by name; keep find-by-name reliable.
                targetCube.name = "TargetCube";
            }
        }

        private void RegisterModsPage()
        {
            if (Registry == null)
            {
                return;
            }

            try
            {
                CoreAiModsLifetimeScope modsScope =
                    FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
                if (modsScope?.Container == null)
                {
                    return;
                }

                IObjectResolver container = modsScope.Container;
                LuaCsModRuntime runtime = container.Resolve<LuaCsModRuntime>();
                ILuaModSourceStore sourceStore = TryResolve<ILuaModSourceStore>(container);

                // WHY: last-writer-wins registration into the window's live registry; PageRegistered
                // rebuilds the tab bar so the Mods tab lights up after the scopes are ready.
                HubModsPages.Register(Registry, runtime, sourceStore, LuaCapabilities.All, allowFull: false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FullAccessHubDemo] Mods tab not registered: {ex.Message}");
            }
        }

        private IEnumerator EnableAgentDropdownWhenReady()
        {
            // Turn on the chat agent/role dropdown so testers can switch the responding agent (Programmer,
            // SmartChat, AINpc, ...) at runtime without editing the scene config.
            CoreAiChatPanel panel = null;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                panel = FindFirstObjectByType<CoreAiChatPanel>(FindObjectsInactive.Include);
                if (panel != null)
                {
                    break;
                }

                yield return null;
            }

            if (panel != null)
            {
                panel.EnableAgentSwitching();
                // Surface the demo example prompts (Tetris / castle / fix arena) via the chat's own "≡"
                // menu — the single prompt-templates location, demo-only (off in the base chat).
                panel.EnableExamplePrompts();
                Debug.Log("[FullAccessHubDemo] Agent dropdown + example prompts enabled on CoreAiChatPanel.");
            }
            else
            {
                Debug.LogWarning("[FullAccessHubDemo] CoreAiChatPanel not found; agent dropdown not enabled.");
            }
        }

        private ILuaModRuntime ResolveModsRuntime()
        {
            if (_modsResolved)
            {
                return _modsRuntime;
            }

            _modsResolved = true;
            try
            {
                if (coreAiScope == null)
                {
                    coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
                }

                if (coreAiScope == null || coreAiScope.Container == null)
                {
                    return null;
                }

                _modsRuntime = CoreAiDemoScope.ResolveModsContainer(coreAiScope).Resolve<ILuaModRuntime>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FullAccessHubDemo] Failed to resolve mods runtime: {ex.Message}");
                _modsRuntime = null;
            }

            return _modsRuntime;
        }

        private T ResolveFromCore<T>() where T : class
        {
            try
            {
                if (coreAiScope == null)
                {
                    coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
                }

                return coreAiScope != null && coreAiScope.Container != null
                    ? TryResolve<T>(coreAiScope.Container)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private LuaPlatformExampleController ResolveDriver()
        {
            if (luaPlatformDriver == null)
            {
                luaPlatformDriver = FindFirstObjectByType<LuaPlatformExampleController>(FindObjectsInactive.Include);
            }

            return luaPlatformDriver;
        }

        private string ModSourceOverrideText()
        {
            return fullModeModSourceOverride != null && !string.IsNullOrWhiteSpace(fullModeModSourceOverride.text)
                ? fullModeModSourceOverride.text
                : null;
        }

        private static T TryResolve<T>(IObjectResolver container) where T : class
        {
            try
            {
                return container.Resolve<T>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#else
using UnityEngine;

namespace CoreAI.Demos
{
    public sealed class FullAccessHubDemoController : MonoBehaviour
    {
        private void Start()
        {
            Debug.LogWarning("[FullAccessHubDemo] Lua disabled; demo inactive.");
            enabled = false;
        }
    }
}
#endif
