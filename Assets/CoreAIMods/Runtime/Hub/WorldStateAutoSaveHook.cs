using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Ai.Hub
{
    public sealed class WorldStateAutoSaveHook : MonoBehaviour
    {
        private IWorldStateManager _manager;

        private void Start()
        {
            CoreAILifetimeScope scope = FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null || scope.Container == null)
            {
                return;
            }

            _manager = scope.Container.Resolve<IWorldStateManager>();
        }

        private void OnApplicationQuit()
        {
            if (_manager != null)
            {
                _manager.Save();
            }
        }
    }
}
