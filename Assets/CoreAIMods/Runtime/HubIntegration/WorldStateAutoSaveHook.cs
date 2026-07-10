using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Optional per-scene override for the world-state auto-save interval. <see cref="WorldStateManager"/>
    /// itself now runs an always-on periodic auto-save (default <see cref="WorldStateManager.DefaultAutoSaveIntervalSeconds"/>)
    /// and a single quit-save via <c>Application.quitting</c> (see <c>WorldStateEntryPoint</c>), so every
    /// scene gets crash protection without needing this component. This hook exists only to configure a
    /// different interval for a specific scene; it deliberately does NOT save on quit itself, since that
    /// would double the manager's own quit-save (dynamic-worlds audit finding W8).
    /// </summary>
    public sealed class WorldStateAutoSaveHook : MonoBehaviour
    {
        [Tooltip("Seconds between automatic world-state saves. 0 disables periodic saving. Leave at the " +
                 "default to use WorldStateManager's own always-on interval unmodified.")]
        [SerializeField] private float saveIntervalSeconds = WorldStateManager.DefaultAutoSaveIntervalSeconds;

        private void Start()
        {
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

            // The manager already started its own loop at the default interval; only restart it when
            // this scene asks for something different (including 0, to disable periodic saving here).
            if (!Mathf.Approximately(saveIntervalSeconds, WorldStateManager.DefaultAutoSaveIntervalSeconds))
            {
                manager.StartAutoSave(saveIntervalSeconds);
            }
        }
    }
}
