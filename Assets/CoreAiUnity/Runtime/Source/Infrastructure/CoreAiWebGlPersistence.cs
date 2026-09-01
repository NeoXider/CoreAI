using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cysharp.Threading.Tasks;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using AOT;
#endif

namespace CoreAI.Infrastructure
{
    /// <summary>
    /// Shared WebGL IDBFS-to-IndexedDB flush helper for CoreAI file-backed stores. Wraps the single
    /// <c>CoreAi_PersistFsSync</c> jslib export (<c>CoreAiPersistFs.jslib</c>) so callers share one
    /// <c>DllImport</c> declaration instead of redeclaring it per store.
    /// </summary>
    public static class CoreAiWebGlPersistence
    {
        internal readonly struct CompletionWaitResult
        {
            public CompletionWaitResult(bool completed, bool succeeded)
            {
                Completed = completed;
                Succeeded = succeeded;
            }

            public bool Completed { get; }

            public bool Succeeded { get; }
        }

        public static readonly TimeSpan DefaultSyncTimeout = TimeSpan.FromSeconds(30d);

#if UNITY_WEBGL && !UNITY_EDITOR
        private static readonly Dictionary<int, UniTaskCompletionSource<bool>> Pending = new();
        private static readonly CompletionCallback CompletionDelegate = OnCompletion;
        private static int _nextCallId;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CompletionCallback(int callId, int succeeded, IntPtr errorPtr);

        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();

        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSyncAsync(int callId, IntPtr onCompletion);
#endif

        /// <summary>
        /// On WebGL pushes the in-memory IDBFS tree into IndexedDB so a preceding write survives a
        /// reload or tab close that never runs <c>Application.Quit</c>. On other platforms this is a
        /// no-op (the OS filesystem is already durable once the write call returns).
        /// </summary>
        /// <returns>
        /// False only when the WebGL flush threw (already logged here), so callers that need to report
        /// durability honestly can; true otherwise, including on non-WebGL platforms.
        /// </returns>
        public static bool Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                CoreAi_PersistFsSync();
            }
            catch (System.Exception ex)
            {
                // WHY: A failed flush must be visible because the preceding write may not survive reload.
                UnityEngine.Debug.LogWarning(
                    $"[CoreAiWebGlPersistence] IndexedDB flush failed; last write may not survive a reload: {ex.Message}");
                return false;
            }
#endif
            return true;
        }

        /// <summary>
        /// Completes only after the browser reports the matching IDBFS <c>syncfs</c> result. On
        /// non-WebGL platforms the filesystem write is already complete, so the returned task is true.
        /// </summary>
        public static UniTask<bool> SyncAsync(
            CancellationToken cancellationToken = default,
            TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            TimeSpan effectiveTimeout = timeout ?? DefaultSyncTimeout;
            if (effectiveTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return SyncWebGlAsync(effectiveTimeout, cancellationToken);
#else
            return UniTask.FromResult(true);
#endif
        }

        internal static async UniTask<CompletionWaitResult> WaitForCompletionAsync(
            UniTask<bool> completion,
            UniTask timeoutOrCancellation)
        {
            (bool HasResultLeft, bool Result) outcome = await UniTask.WhenAny(
                completion,
                timeoutOrCancellation);
            return new CompletionWaitResult(outcome.HasResultLeft, outcome.Result);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static async UniTask<bool> SyncWebGlAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            int callId = NextCallId();
            UniTaskCompletionSource<bool> completion = new();
            using CancellationTokenSource waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Pending.Add(callId, completion);
            try
            {
                CoreAi_PersistFsSyncAsync(
                    callId,
                    Marshal.GetFunctionPointerForDelegate(CompletionDelegate));
                CompletionWaitResult outcome = await WaitForCompletionAsync(
                    completion.Task,
                    UniTask.Delay(
                        timeout,
                        DelayType.Realtime,
                        PlayerLoopTiming.Update,
                        waitCancellation.Token));
                if (outcome.Completed)
                {
                    waitCancellation.Cancel();
                    return outcome.Succeeded;
                }

                LogFailure("syncfs completion callback timed out after " + timeout.TotalSeconds
                    + " seconds");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogFailure(ex.Message);
                return false;
            }
            finally
            {
                waitCancellation.Cancel();
                Pending.Remove(callId);
            }
        }

        private static int NextCallId()
        {
            _nextCallId++;
            if (_nextCallId <= 0)
            {
                _nextCallId = 1;
            }

            while (Pending.ContainsKey(_nextCallId))
            {
                _nextCallId++;
            }

            return _nextCallId;
        }

        [MonoPInvokeCallback(typeof(CompletionCallback))]
        private static void OnCompletion(int callId, int succeeded, IntPtr errorPtr)
        {
            if (!Pending.TryGetValue(callId, out UniTaskCompletionSource<bool> completion))
            {
                return;
            }

            Pending.Remove(callId);
            if (succeeded == 0)
            {
                string message = Marshal.PtrToStringUTF8(errorPtr) ?? "Unknown syncfs error";
                LogFailure(message);
            }

            completion.TrySetResult(succeeded != 0);
        }

        private static void LogFailure(string message)
        {
            UnityEngine.Debug.LogWarning(
                "[CoreAiWebGlPersistence] IndexedDB flush failed; last write may not survive a reload: "
                + message);
        }
#endif
    }
}
