using UnityEngine;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Frame driver of <see cref="UnityRuntimePerformanceMonitor"/>: a <c>DontDestroyOnLoad</c> object the monitor
    /// creates in play mode, recording one frame interval per frame. Not meant to be added by hand.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class CoreAiRuntimePerformanceDriver : MonoBehaviour
    {
        private UnityRuntimePerformanceMonitor _monitor;

        internal void Initialize(UnityRuntimePerformanceMonitor monitor)
        {
            _monitor = monitor;
        }

        private void LateUpdate()
        {
            _monitor?.RecordFrame();
        }

        private void OnDestroy()
        {
            _monitor?.OnDriverDestroyed(this);
        }
    }
}
