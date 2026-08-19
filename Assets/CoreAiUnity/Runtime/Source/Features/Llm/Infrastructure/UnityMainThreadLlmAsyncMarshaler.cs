using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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

        /// <summary>
        /// Player-loop delay instead of the default <see cref="Task.Delay(int, CancellationToken)"/>.
        /// <para>
        /// A Unity WebGL player has no <c>System.Threading.Timer</c>, so a timer-backed delay never
        /// fires there and any deadline built on it is silently absent. <c>UniTask.Delay</c> is driven
        /// by the player loop, which runs in every player including WebGL. <see cref="DelayType.Realtime"/>
        /// so a paused or time-scaled game does not stretch an internal deadline.
        /// </para>
        /// </summary>
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
            UniTask.Delay(milliseconds, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken)
                .AsTask();

        public async Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
        {
#if UNITY_EDITOR
            // WHY: CAIU001 (no ConfigureAwait(false)) targets WebGL-reachable code; this whole branch is
            // #if UNITY_EDITOR and can never compile into a WebGL player. ConfigureAwait(false) here is
            // required: Edit Mode tests/tooling sometimes block the managed main thread on Task.Wait while
            // this continues on the thread pool, and resuming on the captured Editor main-thread context
            // would deadlock against that blocked wait (see UnityMainThreadLlmAsyncMarshalerEditModeTests).
#pragma warning disable CAIU001
            // WHY: Same bypass as !isPlaying below, but must not call Application.isPlaying off the managed
            if (ShouldInvokeToolBodyInlineInEditor())
            {
                return await factory().ConfigureAwait(false);
            }

            return await InvokeFactoryOnEditorUnityMainThreadAsync(factory, cancellationToken).ConfigureAwait(false);
#pragma warning restore CAIU001
#else
            await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(cancellationToken);
            return await factory();
#endif
        }

#if UNITY_EDITOR
        /// <summary>How long a posted main-thread call may wait to START before it is failed as unpumped.</summary>
        private const int MainThreadPostStartTimeoutMs = 30000;

        /// <summary>
        /// Last <see cref="Application.isPlaying"/> observed alongside the scripted Unity main thread
        /// (<see cref="Thread.ManagedThreadId"/>) (<c>-1</c> = not yet mirrored).
        /// </summary>
        private static int _editorMirrorIsPlaying = -1;

        private static int _editorMirroredUnityMainManagedThreadId = -1;

        private static SynchronizationContext _editorMirroredUnityMainSynchronizationContext;

        private static int _editorRuntimePlayModeEntered;

        private static int _editorMirrorHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterEditorPlayModeMirrorForRuntime()
        {
            MarkEditorRuntimePlayModeEntered();
            EnsureEditorIsPlayingMirrorHook();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrimeEditorPlayModeMirrorBeforeSceneLoad()
        {
            MarkEditorRuntimePlayModeEntered();
            EnsureEditorIsPlayingMirrorHook();
            UpdateEditorIsPlayingMirrorFromEditorState();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void PrimeEditorPlayModeMirrorAfterSceneLoad()
        {
            MarkEditorRuntimePlayModeEntered();
            EnsureEditorIsPlayingMirrorHook();
            UpdateEditorIsPlayingMirrorFromEditorState();
        }

        [InitializeOnLoad]
        private static class EditorIsPlayingMirrorEditorInitializer
        {
            static EditorIsPlayingMirrorEditorInitializer()
            {
                EnsureEditorIsPlayingMirrorHook();
                UpdateEditorIsPlayingMirrorFromEditorState();
            }
        }

        private static class EditorIsPlayingMirrorRegistration
        {
            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
            private static void Register()
            {
                MarkEditorRuntimePlayModeEntered();
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
                MarkEditorRuntimePlayModeEntered();
                EnsureEditorIsPlayingMirrorHook();
                UpdateEditorIsPlayingMirrorFromEditorState();
            }

            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
            private static void PrimeAfterSceneLoad()
            {
                MarkEditorRuntimePlayModeEntered();
                EnsureEditorIsPlayingMirrorHook();
                UpdateEditorIsPlayingMirrorFromEditorState();
            }
        }

        private static void EnsureEditorIsPlayingMirrorHook()
        {
            if (Interlocked.Exchange(ref _editorMirrorHooked, 1) != 0)
            {
                return;
            }

            Application.onBeforeRender += UpdateEditorIsPlayingMirror;
            EditorApplication.update += UpdateEditorIsPlayingMirrorFromEditorState;
            EditorApplication.playModeStateChanged += UpdateEditorPlayModeStateMirror;
        }

        private static void UpdateEditorIsPlayingMirror()
        {
            try
            {
                Volatile.Write(ref _editorMirroredUnityMainManagedThreadId, ResolveUnityMainManagedThreadId());
                CaptureEditorUnitySynchronizationContext();
                bool isPlaying = Application.isPlaying;
                Volatile.Write(ref _editorMirrorIsPlaying, isPlaying ? 1 : 0);
                if (isPlaying)
                {
                    Volatile.Write(ref _editorRuntimePlayModeEntered, 1);
                }
            }
            catch
            {
            }
        }

        private static void UpdateEditorIsPlayingMirrorFromEditorState()
        {
            try
            {
                Volatile.Write(ref _editorMirroredUnityMainManagedThreadId, ResolveUnityMainManagedThreadId());
                CaptureEditorUnitySynchronizationContext();
                bool isPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
                Volatile.Write(ref _editorMirrorIsPlaying, isPlaying ? 1 : 0);
                if (isPlaying)
                {
                    Volatile.Write(ref _editorRuntimePlayModeEntered, 1);
                }
            }
            catch
            {
            }
        }

        private static int ResolveUnityMainManagedThreadId()
        {
            int playerLoopMainId = PlayerLoopHelper.MainThreadId;
            return playerLoopMainId > 0 ? playerLoopMainId : Thread.CurrentThread.ManagedThreadId;
        }

        private static void UpdateEditorPlayModeStateMirror(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.ExitingEditMode)
            {
                MarkEditorRuntimePlayModeEntered();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                Volatile.Write(ref _editorRuntimePlayModeEntered, 0);
            }

            UpdateEditorIsPlayingMirrorFromEditorState();
        }

        private static void MarkEditorRuntimePlayModeEntered()
        {
            Volatile.Write(ref _editorRuntimePlayModeEntered, 1);
            Volatile.Write(ref _editorMirrorIsPlaying, 1);
        }

        private static void CaptureEditorUnitySynchronizationContext()
        {
            SynchronizationContext context = SynchronizationContext.Current;
            if (context != null)
            {
                Interlocked.Exchange(ref _editorMirroredUnityMainSynchronizationContext, context);
            }
        }

        /// <summary>
        /// Refreshes the editor play-state mirror from a known Unity thread. Test Runner suites can
        /// transition between PlayMode fixtures without a domain reload, so a previous callback may
        /// leave the mirror in an Edit-idle state even though <see cref="Application.isPlaying"/> is
        /// already true for the current player loop.
        /// </summary>
        public static void RefreshEditorPlayModeMirrorForCurrentThread()
        {
            EnsureEditorIsPlayingMirrorHook();
            MarkEditorRuntimePlayModeEntered();
            CaptureEditorUnitySynchronizationContext();
            UpdateEditorIsPlayingMirror();
        }

        private static async Task<T> InvokeFactoryOnEditorUnityMainThreadAsync<T>(
            Func<Task<T>> factory,
            CancellationToken cancellationToken)
        {
            int mirroredMainId = Volatile.Read(ref _editorMirroredUnityMainManagedThreadId);
            if (mirroredMainId >= 0 && Thread.CurrentThread.ManagedThreadId == mirroredMainId)
            {
                return await factory();
            }

            SynchronizationContext context = Volatile.Read(ref _editorMirroredUnityMainSynchronizationContext);
            if (context == null)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(cancellationToken);
                return await factory();
            }

            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellationRegistration = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            TaskCompletionSource<bool> callbackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(async _ =>
            {
                callbackStarted.TrySetResult(true);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    T result = await factory();
                    tcs.TrySetResult(result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    cancellationRegistration.Dispose();
                }
            }, null);

            // WHY: See the CAIU001 suppression note in InvokeAsync above: this method is #if UNITY_EDITOR-only
            // and ConfigureAwait(false) here is required to avoid deadlocking a main thread that is
            // synchronously blocked (Task.Wait/.Result) waiting on this same call.
#pragma warning disable CAIU001
            // WHY: the posted callback is the only thing that completes tcs; if the sync context stops
            // pumping (domain reload, play-mode exit) an uncancelable token would leave this await hanging
            // forever. Bound the wait for the callback to START, not for the tool body to finish.
            Task firstCompleted = await Task.WhenAny(
                callbackStarted.Task,
                Task.Delay(MainThreadPostStartTimeoutMs)).ConfigureAwait(false);
            if (!ReferenceEquals(firstCompleted, callbackStarted.Task))
            {
                cancellationRegistration.Dispose();
                tcs.TrySetException(new TimeoutException(
                    "UnityMainThreadLlmAsyncMarshaler: the Unity main-thread queue did not start the posted " +
                    $"LLM call within {MainThreadPostStartTimeoutMs / 1000}s (domain reload or play-mode exit)."));
            }

            return await tcs.Task.ConfigureAwait(false);
#pragma warning restore CAIU001
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

            if (Volatile.Read(ref _editorRuntimePlayModeEntered) == 1)
            {
                return false;
            }

            if (TryProbeApplicationIsPlayingOnCurrentThread())
            {
                return false;
            }

            if (IsEditorPlayingOrWillEnterPlayMode())
            {
                return false;
            }

            // WHY: Inline only on explicit Edit idle (0). Unknown (-1) must marshal: a Play Mode domain whose
            // primers raced this pool-thread call would otherwise run the tool body on the pool. Any real
            // Edit session primes the mirror to 0 via [InitializeOnLoad]/EditorApplication.update long
            // before a tool body runs, so SmartToolCallingChatClientEditModeTests (Task.Run + .Wait on
            // the main thread) still take the inline path and never deadlock.
            return Volatile.Read(ref _editorMirrorIsPlaying) == 0;
        }

        private static bool TryProbeApplicationIsPlayingOnCurrentThread()
        {
            try
            {
                bool isPlaying = Application.isPlaying;
                Volatile.Write(ref _editorMirrorIsPlaying, isPlaying ? 1 : 0);
                if (isPlaying)
                {
                    Volatile.Write(ref _editorRuntimePlayModeEntered, 1);
                }

                return isPlaying;
            }
            catch (Exception ex) when (IsUnhandledIsPlayingProbeFailure(ex))
            {
                return false;
            }
        }

        private static bool IsEditorPlayingOrWillEnterPlayMode()
        {
            try
            {
                bool isPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
                if (isPlaying)
                {
                    Volatile.Write(ref _editorMirrorIsPlaying, 1);
                }

                return isPlaying;
            }
            catch (Exception ex) when (IsUnhandledIsPlayingProbeFailure(ex))
            {
                return false;
            }
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
