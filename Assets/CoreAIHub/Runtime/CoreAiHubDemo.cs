using CoreAI.Hub;
using UnityEngine;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Tiny self-contained demo controller: creates a <see cref="HubPageRegistry"/>, registers the
    /// built-in <see cref="HubAboutPage"/>, and feeds the registry to a sibling
    /// <see cref="CoreAiHubWindow"/> so the Hub shows at least one tab out of the box.
    /// </summary>
    /// <remarks>
    /// This is intentionally not a DI wiring point — it exists so the package can be dropped onto a
    /// GameObject (with a <see cref="UnityEngine.UIElements.UIDocument"/> + <see cref="CoreAiHubWindow"/>)
    /// and render immediately. Real integrations should build their own registry and assign it via
    /// <see cref="CoreAiHubWindow.Registry"/>.
    /// </remarks>
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class CoreAiHubDemo : MonoBehaviour
    {
        [Tooltip("Register the built-in About page so the Hub always has one tab.")]
        [SerializeField]
        private bool registerAboutPage = true;

        /// <summary>The registry created and owned by this demo controller.</summary>
        public HubPageRegistry Registry { get; private set; }

        private void Awake()
        {
            Registry = new HubPageRegistry();

            if (registerAboutPage)
            {
                Registry.Register(HubAboutPage.DefaultPageId, () => new HubAboutPage(), order: 1000);
            }

            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            if (window != null)
            {
                window.Registry = Registry;
            }
        }
    }
}
