using CoreAI.Chat;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Tiny self-contained demo controller: creates a <see cref="HubPageRegistry"/>, registers the
    /// built-in pages (About plus the optional Chat / Settings / Statistics set), and feeds the registry
    /// to a sibling <see cref="CoreAiHubWindow"/> so the Hub shows real tabs out of the box.
    /// </summary>
    /// <remarks>
    /// This is intentionally not a DI wiring point — it exists so the package can be dropped onto a
    /// GameObject (with a <see cref="UnityEngine.UIElements.UIDocument"/> + <see cref="CoreAiHubWindow"/>)
    /// and render immediately. Real integrations should build their own registry (via
    /// <see cref="HubBuiltInPages.RegisterAll"/> with live DI-resolved settings/metrics) and assign it via
    /// <see cref="CoreAiHubWindow.Registry"/>.
    /// </remarks>
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class CoreAiHubDemo : MonoBehaviour
    {
        [Tooltip("Register the built-in About page. Off by default to keep the tab bar focused on " +
                 "functional pages; flip on if you want a dedicated About tab.")]
        [SerializeField]
        private bool registerAboutPage = false;

        [Tooltip("Register the built-in Chat, Settings, and Statistics pages.")]
        [SerializeField]
        private bool registerBuiltInPages = true;

        [Header("Chat page (optional)")]
        [Tooltip("Chat UXML cloned by the Chat page. Leave empty to show a setup note instead of the chat.")]
        [SerializeField]
        private VisualTreeAsset chatTemplate;

        [Tooltip("Optional chat stylesheet layered on the embedded chat.")]
        [SerializeField]
        private StyleSheet chatStyleSheet;

        [Tooltip("Optional chat configuration asset (also shown on the Settings page).")]
        [SerializeField]
        private CoreAiChatConfig chatConfig;

        /// <summary>The registry created and owned by this demo controller.</summary>
        public HubPageRegistry Registry { get; private set; }

        private void Awake()
        {
            Registry = new HubPageRegistry();

            if (registerBuiltInPages)
            {
                // Settings/Statistics data sources (ICoreAISettings / metrics) are DI-owned; the demo
                // leaves them null so those tabs render a setup note. A real host passes them in.
                HubBuiltInPages.RegisterAll(Registry, chatTemplate, chatStyleSheet, chatConfig);
            }

            if (registerAboutPage)
            {
                Registry.Register(HubAboutPage.DefaultPageId, () => new HubAboutPage(), 1000);
            }

            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            if (window != null)
            {
                window.Registry = Registry;
            }
        }
    }
}
