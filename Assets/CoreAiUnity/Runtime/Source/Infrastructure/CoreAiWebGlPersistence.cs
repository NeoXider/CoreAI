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
        private static extern int CoreAi_PersistFsSync();

        [DllImport("__Internal")]
        private static extern int CoreAi_PersistFsSyncAsync(int callId, IntPtr onCompletion);

        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsCancelWaiter(int callId);

        [DllImport("__Internal")]
        private static extern int CoreAi_PersistFsPendingRequestCount();

        [DllImport("__Internal")]
        private static extern int CoreAi_PersistFsPendingFlushCount();
#endif

        /// <summary>
        /// Actual JS-retained waiter entries across queued, in-flight, and scheduled-delivery
        /// states. The JS bridge admits at most 64; <see cref="SyncAsync"/> reports false when
        /// saturated instead of queueing unboundedly. Zero outside WebGL players.
        /// </summary>
        public static int PendingRequestCount
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                try
                {
                    return CoreAi_PersistFsPendingRequestCount();
                }
                catch (Exception)
                {
                    return 0;
                }
#else
                return 0;
#endif
            }
        }

        /// <summary>
        /// 0 when idle, 1 while one <c>FS.syncfs</c> flush is in flight, 2 when a follow-up
        /// flush is also required. Zero outside WebGL players.
        /// </summary>
        public static int PendingFlushCount
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                try
                {
                    return CoreAi_PersistFsPendingFlushCount();
                }
                catch (Exception)
                {
                    return 0;
                }
#else
                return 0;
#endif
            }
        }

        /// <summary>
        /// On WebGL <b>queues</b> an IDBFS-to-IndexedDB flush and returns immediately; the browser runs
        /// <c>FS.syncfs</c> asynchronously (single-flight, later requests coalesce behind the active one).
        /// A preceding write therefore survives a reload only once that flush has completed — a tab closed
        /// before the completion callback can still lose it. Callers that must know whether the data is
        /// durable use <see cref="SyncAsync"/>, which completes with the browser's result. On other
        /// platforms this is a no-op: the OS filesystem is durable once the write call returns.
        /// </summary>
        /// <returns>
        /// False when the flush could not be queued or its immediate start was rejected.
        /// True means "queued", NOT "persisted" — on non-WebGL platforms it means "already durable".
        /// </returns>
        public static bool Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                return CoreAi_PersistFsSync() != 0;
            }
            catch (System.Exception ex)
            {
                // WHY: A failed flush must be visible because the preceding write may not survive reload.
                UnityEngine.Debug.LogWarning(
                    $"[CoreAiWebGlPersistence] IndexedDB flush failed; last write may not survive a reload: {ex.Message}");
                return false;
            }
#else
            return true;
#endif
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
                int admitted = CoreAi_PersistFsSyncAsync(
                    callId,
                    Marshal.GetFunctionPointerForDelegate(CompletionDelegate));
                if (admitted == 0)
                {
                    Pending.Remove(callId);
                    LogFailure("persistence request not admitted: queue saturated or scheduler unavailable");
                    return false;
                }

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
                try
                {
                    // WHY: The C# timeout/cancellation path only drops our own waiter. The JS
                    // bridge physically removes the entry so a late flush callback is
                    // suppressed and capacity is freed; the in-flight FS.syncfs (if any) and
                    // dirty intent are untouched.
                    CoreAi_PersistFsCancelWaiter(callId);
                }
                catch (Exception)
                {
                }
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
            // WHY: The JS bridge reports each failed physical flush once, including flushes
            // without waiters; repeating that warning here would produce one log per waiter.
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
