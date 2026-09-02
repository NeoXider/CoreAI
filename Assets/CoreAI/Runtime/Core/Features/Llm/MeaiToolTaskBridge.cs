using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Publishes an awaiting tool body to MEAI so that its result is observed in a host without a
    /// thread pool (the Unity WebGL player).
    /// <para>
    /// MEAI awaits the delegate's task inside its binary with <c>ConfigureAwait(false)</c>. When that
    /// task completes on a thread whose <see cref="SynchronizationContext"/> is a derived type
    /// (<c>UnitySynchronizationContext</c>), .NET refuses to inline the continuation and queues it to
    /// the thread pool. The WebGL player has no thread pool, so the tool result is silently never
    /// delivered and the model turn waits forever: no exception, no timeout, no second request.
    /// Completing the surfaced task while the current context is cleared makes that continuation run
    /// inline, on the completing call stack, before the context is restored.
    /// </para>
    /// <para>
    /// A synchronously completed body is returned as-is: awaiting a completed task registers no
    /// continuation. On hosts that do have a thread pool the bridge changes nothing observable.
    /// </para>
    /// </summary>
    public static class MeaiToolTaskBridge
    {
        /// <summary>
        /// Surfaces <paramref name="body"/> as a task that completes with the host
        /// <see cref="SynchronizationContext"/> cleared, so a <c>ConfigureAwait(false)</c> awaiter in
        /// MEAI continues inline instead of on a thread pool.
        /// </summary>
        public static Task<T> Publish<T>(Task<T> body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.IsCompleted)
            {
                return body;
            }

            // WHY: TaskCreationOptions.None on purpose — RunContinuationsAsynchronously would force
            // MEAI's continuation back onto the pool this bridge exists to avoid.
            TaskCompletionSource<T> surfaced = new(TaskCreationOptions.None);
            body.ContinueWith(
                (completed, state) => CompleteWithoutCapturedContext((TaskCompletionSource<T>)state, completed),
                surfaced,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return surfaced.Task;
        }

        private static void CompleteWithoutCapturedContext<T>(TaskCompletionSource<T> surfaced, Task<T> completed)
        {
            SynchronizationContext captured = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                if (completed.IsCanceled)
                {
                    surfaced.TrySetCanceled();
                }
                else if (completed.IsFaulted)
                {
                    surfaced.TrySetException(completed.Exception.InnerExceptions);
                }
                else
                {
                    surfaced.TrySetResult(completed.Result);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(captured);
            }
        }
    }
}
