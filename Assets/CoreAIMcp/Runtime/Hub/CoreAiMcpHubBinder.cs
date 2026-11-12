using CoreAI.Hub;
using CoreAI.Hub.UI;
using UnityEngine;

namespace CoreAI.Mcp.Hub
{
    /// <summary>
    /// Adds the <see cref="HubMcpPage"/> tab to the <see cref="CoreAiHubWindow"/> on the same GameObject. Nobody adds
    /// it by hand: while the MCP and Hub packages are both installed, every Hub window that is enabled gets one
    /// (<see cref="CoreAiHubWindow.AnyWindowEnabled"/>), so the tab needs no scene edit. It registers from
    /// <c>Start</c>, when the window's registry is settled, and again whenever the window is given another
    /// registry.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class CoreAiMcpHubBinder : MonoBehaviour
    {
        private CoreAiHubWindow _window;
        private HubPageRegistry _registered;

        /// <summary>Registers the MCP page into <paramref name="registry"/> unless it already holds one.</summary>
        public static void Register(HubPageRegistry registry)
        {
            if (registry != null && !registry.TryGet(HubMcpPage.DefaultPageId, out _))
            {
                registry.Register(HubMcpPage.DefaultPageId, () => new HubMcpPage(), HubMcpPage.DefaultOrder);
            }
        }

        private void Start()
        {
            _window = GetComponent<CoreAiHubWindow>();
            Bind();
        }

        private void Update()
        {
            if (_window != null && !ReferenceEquals(_window.Registry, _registered))
            {
                Bind();
            }
        }

        private void Bind()
        {
            if (_window == null)
            {
                return;
            }

            if (_window.Registry == null)
            {
                _window.Registry = new HubPageRegistry();
            }

            _registered = _window.Registry;
            Register(_registered);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ListenForHubWindows()
        {
            CoreAiHubWindow.AnyWindowEnabled -= Attach;
            CoreAiHubWindow.AnyWindowEnabled += Attach;
        }

        private static void Attach(CoreAiHubWindow window)
        {
            if (window != null && window.GetComponent<CoreAiMcpHubBinder>() == null)
            {
                window.gameObject.AddComponent<CoreAiMcpHubBinder>();
            }
        }
    }
}
