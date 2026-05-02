using System.Threading;
using Cysharp.Threading.Tasks;

namespace CoreAI.Threading
{
    /// <summary>
    /// Brings await continuations back to Unity’s player loop before touching UI Toolkit after LLM/orchestrator
    /// work that used <c>ConfigureAwait(false)</c>.
    /// <para>
    /// <b>Editor</b> and <b>standalone players</b>: delegates to <see cref="UniTask.SwitchToMainThread"/>.
    /// </para>
    /// <para>
    /// <b>WebGL player</b> (built browser build, not Editor Play): execution is single-threaded; an extra
    /// <c>SwitchToMainThread</c> hop has been observed to throw
    /// <c>ArgumentException: Unknown platform Unix …</c> on some IL2CPP/browser stacks, leaving the chat stuck
    /// on the typing dots even though HTTP/JSON completed. Editor WebGL Play keeps <see cref="UNITY_EDITOR"/>
    /// defined, so the full switch path remains — matching “works in Editor, fails in build”.
    /// </para>
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
