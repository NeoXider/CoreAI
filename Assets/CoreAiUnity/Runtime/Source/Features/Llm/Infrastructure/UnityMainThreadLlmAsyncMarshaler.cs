using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Runs MEAI tool bodies on Unity's player loop when <see cref="Application"/>.isPlaying (<c>true</c> in
    /// Player/Play Mode) — required when callers use thread-pool continuations
    /// (<see cref="SmartToolCallingChatClient"/> uses <c>ConfigureAwait(false)</c>).
    /// In the Unity Editor outside Play Mode (<c>!Application.isPlaying</c>), invokes the factory inline without
    /// <c>SwitchToMainThread</c> so Edit Mode stacks that synchronously wait on the main thread do not deadlock.
    /// Editor **thread-pool** stacks **must not** call <see cref="Application"/>.isPlaying — native getters can fault
    /// in ways that propagate as <see cref="AggregateException"/> and bypass typed catches. Record the Unity **script**
    /// main <see cref="Thread.ManagedThreadId"/> beside the **isPlaying** mirror (see <c>Application.onBeforeRender</c>)
    /// and probe <see cref="Application.isPlaying"/> only when <c>ManagedThreadId</c>s match.
    /// </summary>
    public sealed class UnityMainThreadLlmAsyncMarshaler : ILlmAsyncMarshaler
    {
        public static readonly ILlmAsyncMarshaler Instance = new UnityMainThreadLlmAsyncMarshaler();

        private UnityMainThreadLlmAsyncMarshaler()
        {
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
        {
#if UNITY_EDITOR
            // Same bypass as !isPlaying below, but must not call Application.isPlaying off the managed
            // main thread — MEAI continuations often resume on the CLR thread pool (ConfigureAwait(false)).
            if (ShouldInvokeToolBodyInlineInEditor())
            {
                return await factory().ConfigureAwait(false);
            }
#endif
            await UniTask.SwitchToMainThread(PlayerLoopTiming.Update, cancellationToken);
            return await factory();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Last <see cref="Application.isPlaying"/> observed alongside the scripted Unity main thread
        /// (<see cref="Thread.ManagedThreadId"/>) (<c>-1</c> = not yet mirrored).
        /// </summary>
        private static volatile int _editorMirrorIsPlaying = -1;

        private static volatile int _editorMirroredUnityMainManagedThreadId = -1;

        private static int _onBeforeRenderHooked;

        private static class EditorIsPlayingMirrorRegistration
        {
            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
            private static void Register()
            {
                // Do not probe Application.isPlaying here — SubsystemRegistration may not run on the Unity script thread.
                EnsureEditorIsPlayingMirrorHook();
            }
        }

        private static void EnsureEditorIsPlayingMirrorHook()
        {
            if (Interlocked.Exchange(ref _onBeforeRenderHooked, 1) != 0)
            {
                return;
            }

            Application.onBeforeRender += UpdateEditorIsPlayingMirror;
        }

        private static void UpdateEditorIsPlayingMirror()
        {
            try
            {
                Volatile.Write(ref _editorMirroredUnityMainManagedThreadId, Thread.CurrentThread.ManagedThreadId);
                Volatile.Write(ref _editorMirrorIsPlaying, Application.isPlaying ? 1 : 0);
            }
            catch
            {
            }
        }

        /// <summary>
        /// True when we're in the Editor and the tool body should run inline (no SwitchToMainThread).
        /// Thread-pool / worker threads never call <see cref="Application.isPlaying"/>; they compare
        /// <see cref="Thread.ManagedThreadId"/> to the mirrored script-main id and branch on <c>_editorMirrorIsPlaying</c>.
        /// </summary>
        private static bool ShouldInvokeToolBodyInlineInEditor()
        {
            EnsureEditorIsPlayingMirrorHook();

            int mirroredMainId = Volatile.Read(ref _editorMirroredUnityMainManagedThreadId);
            if (mirroredMainId >= 0 && Thread.CurrentThread.ManagedThreadId == mirroredMainId)
            {
                try
                {
                    return !Application.isPlaying;
                }
                catch (Exception ex) when (IsUnhandledIsPlayingProbeFailure(ex))
                {
                    return Volatile.Read(ref _editorMirrorIsPlaying) != 1;
                }
            }

            return Volatile.Read(ref _editorMirrorIsPlaying) != 1;
        }

        /// <summary>Unity/native failures when probing <see cref="Application.isPlaying"/> (including wrappers).</summary>
        private static bool IsUnhandledIsPlayingProbeFailure(Exception ex)
        {
            for (Exception scan = ex; scan != null; scan = scan.InnerException)
            {
                if (scan is UnityException)
                {
                    return true;
                }

                if (IsMainThreadOnlyUnityApiException(scan))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMainThreadOnlyUnityApiException(Exception ex)
        {
            string m = ex?.Message;
            if (string.IsNullOrEmpty(m))
            {
                return false;
            }

            return m.IndexOf("main thread", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   m.IndexOf("loading thread", StringComparison.OrdinalIgnoreCase) >= 0;
        }
#endif
    }
}
