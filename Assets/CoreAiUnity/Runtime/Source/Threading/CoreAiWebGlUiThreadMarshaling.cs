using System.Threading;
using Cysharp.Threading.Tasks;

namespace CoreAI.Threading
{
    /// <summary>
    /// Handles optional Unity main-thread marshaling for WebGL UI updates.
    /// </summary>
    public static class CoreAiWebGlUiThreadMarshaling
    {
        public static UniTask SwitchToMainThreadForUiOptional(CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _ = cancellationToken;
            return UniTask.CompletedTask;
#else
            return SwitchCore(cancellationToken);
#endif
        }

        private static async UniTask SwitchCore(CancellationToken cancellationToken)
        {
            await UniTask.SwitchToMainThread(PlayerLoopTiming.Update, cancellationToken);
        }
    }
}
