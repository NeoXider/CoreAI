using CoreAI.Hub;
using CoreAI.Hub.UI;
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>
    /// Drop-in binder that adds the shared demo UI Toolkit pages to a scene's <see cref="CoreAiHubWindow"/>
    /// without a bespoke host per demo. It waits for the window's registry (set by the generic
    /// <c>CoreAiHubDemo</c>/<c>CoreAiModsHubBinder</c> hosts), then registers a demo page for each known
    /// driver present in the scene plus the always-on Token Budget diagnostics page. Each page self-resolves
    /// its driver via <c>FindFirstObjectByType</c>, so nothing needs wiring in the Inspector.
    /// </summary>
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class DemoHubPagesBinder : MonoBehaviour
    {
        [Tooltip("Register the live Token Budget diagnostics page (replaces the F10 IMGUI overlay).")]
        [SerializeField]
        private bool registerTokenBudget = true;

        private void Start()
        {
            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            HubPageRegistry registry = window != null ? window.Registry : null;
            if (registry == null)
            {
                // WHY: the generic host assigns the registry in Awake; a null here means no host ran, so
                // there is nothing to extend. Fail quiet — the scene simply keeps its built-in tabs.
                CoreAI.Logging.Log.Instance.Warn(
                    "[DemoHubPagesBinder] No HubPageRegistry on the CoreAiHubWindow; demo pages not added.");
                return;
            }

#if !COREAI_NO_LUA
            if (FindFirstObjectByType<WaveAutoBattlerModsDemoController>(FindObjectsInactive.Include) != null)
            {
                registry.Register(
                    WaveAutoBattlerHubPage.DefaultPageId,
                    () => new WaveAutoBattlerHubPage(
                        () => FindFirstObjectByType<WaveAutoBattlerModsDemoController>(FindObjectsInactive.Include)),
                    0);
            }

            if (FindFirstObjectByType<LuaPlatformExampleController>(FindObjectsInactive.Include) != null)
            {
                registry.Register(
                    LuaPlatformHubPage.DefaultPageId,
                    () => new LuaPlatformHubPage(
                        () => FindFirstObjectByType<LuaPlatformExampleController>(FindObjectsInactive.Include)),
                    20);
            }

            if (registerTokenBudget)
            {
                registry.Register(TokenBudgetHubPage.DefaultPageId, () => new TokenBudgetHubPage(), 90);
            }
#endif

            if (FindFirstObjectByType<ChatPromptButtonsController>(FindObjectsInactive.Include) != null)
            {
                registry.Register(
                    ChatPromptsHubPage.DefaultPageId,
                    () => new ChatPromptsHubPage(
                        () => FindFirstObjectByType<ChatPromptButtonsController>(FindObjectsInactive.Include)),
                    15);
            }
        }
    }
}
