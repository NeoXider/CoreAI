using System.Collections;
using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Drives world-state persistence from the Hub scene: saves on application quit (Editor Play Mode
    /// exit included) and on a periodic interval as crash protection between explicit saves.
    /// </summary>
    public sealed class WorldStateAutoSaveHook : MonoBehaviour
    {
        [Tooltip("Seconds between automatic world-state saves. 0 disables periodic saving.")]
        [SerializeField] private float saveIntervalSeconds = 60f;

        private IWorldStateManager _manager;

        private void Start()
        {
            CoreAILifetimeScope scope = FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null || scope.Container == null)
            {
                return;
            }

            _manager = scope.Container.Resolve<IWorldStateManager>();

            if (_manager != null && saveIntervalSeconds > 0f)
            {
                StartCoroutine(PeriodicSave());
            }
        }

        private IEnumerator PeriodicSave()
        {
            WaitForSeconds wait = new(saveIntervalSeconds);
            while (_manager != null)
            {
                yield return wait;
                _manager.Save();
            }
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
