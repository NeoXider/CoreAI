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
    /// Runs MEAI tool bodies on Unity's player loop when <see cref="Application"/>.isPlaying (<c>true</c> in
    /// Player/Play Mode) — required when callers use thread-pool continuations
    /// (<see cref="SmartToolCallingChatClient"/> uses <c>ConfigureAwait(false)</c>).
    /// In the Unity Editor outside Play Mode (<c>!Application.isPlaying</c>), invokes the factory inline without
    /// <c>SwitchToMainThread</c> so Edit Mode stacks that synchronously wait on the main thread do not deadlock.
    /// Editor **thread-pool** stacks **must not** call <see cref="Application"/>.isPlaying — native getters can fault
    /// in ways that propagate as <see cref="AggregateException"/> and bypass typed catches. Record the Unity **script**
    /// main <see cref="Thread.ManagedThreadId"/> beside the **isPlaying** mirror (see <c>Application.onBeforeRender</c>)
    /// and probe <see cref="Application.isPlaying"/> only when <c>ManagedThreadId</c>s match.
    /// Off the mirrored main thread, inline when the mirror is not <c>1</c> (Edit idle <c>0</c> or unknown <c>-1</c>).
    /// Unknown must inline so Edit Mode tests that use <c>Task.Run(...).Wait()</c> on the main thread while MEAI
    /// continues on the pool do not deadlock on <c>SwitchToMainThread</c>. Scene-load priming reduces stale <c>0</c>
    /// during Play Mode (see <c>EditorIsPlayingMirrorScenePriming</c>).
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
                // Do not probe Application.isPlaying here — SubsystemRegistration may not run on the Unity script thread.
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
