using CoreAI.Composition;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Ai.Hub
{
    [RequireComponent(typeof(CoreAiHubWindow))]
    public sealed class WorldStateHubBinder : MonoBehaviour
    {
        private void Start()
        {
            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            if (window == null)
            {
                return;
            }

            CoreAILifetimeScope scope = FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null || scope.Container == null)
            {
                return;
            }

            IWorldStateManager manager = scope.Container.Resolve<IWorldStateManager>();
            if (manager == null)
            {
                return;
            }

            HubPageRegistry registry = window.Registry ?? new HubPageRegistry();
            registry.Register(
                WorldStateHubPage.DefaultPageId,
                () => new WorldStateHubPage(manager),
                300);

            if (window.Registry == null)
            {
                window.Registry = registry;
            }
        }
    }
}
