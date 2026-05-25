using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Marshals LLM continuations back to Unity main thread when required.
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
            if (ShouldInvokeToolBodyInlineInEditor())
            {
                return await factory().ConfigureAwait(false);
            }
#endif
            await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(cancellationToken);
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
                EnsureEditorIsPlayingMirrorHook();
            }
        }

        /// <summary>
        /// Primes the mirror on the main thread before <see cref="Application.onBeforeRender"/> so Play Mode
        /// rarely observes a stale <c>0</c> while <see cref="Application.isPlaying"/> is already true.
        /// </summary>
        private static class EditorIsPlayingMirrorScenePriming
        {
            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
            private static void PrimeBeforeSceneLoad()
            {
                EnsureEditorIsPlayingMirrorHook();
                UpdateEditorIsPlayingMirror();
            }

            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
            private static void PrimeAfterSceneLoad()
            {
                EnsureEditorIsPlayingMirrorHook();
                UpdateEditorIsPlayingMirror();
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

            // Not confirmed Play (mirror != 1): inline on the pool. Treat unknown (-1) like Edit idle so
            // SmartToolCallingChatClientEditModeTests (Task.Run + .Wait on the main thread) never deadlock.
            // Stale 0 while Play is mitigated by RuntimeInitialize primers + first-frame mirror updates.
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
