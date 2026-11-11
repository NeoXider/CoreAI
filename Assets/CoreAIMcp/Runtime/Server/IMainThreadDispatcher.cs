using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Marshals work onto the Unity main thread. HTTP requests arrive on <see cref="System.Net.HttpListener"/>
    /// worker threads, but tool handlers touch live game state, so every <c>tools/call</c> is funnelled
    /// through this seam. The Unity implementation queues the work and drains it from <c>Update</c>; the
    /// HTTP thread awaits the returned task. Engine-free so the RPC dispatcher is testable with an inline
    /// (run-immediately) implementation.
    /// </summary>
    public interface IMainThreadDispatcher
    {
        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and completes when its task completes,
        /// propagating result, exception, and cancellation. Callers may await this from any thread.
        /// </summary>
        Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work);
    }

    /// <summary>
    /// A main-thread dispatcher that can withdraw queued work. The RPC dispatcher uses it when present, so
    /// a <c>notifications/cancelled</c> for a call still waiting for the game thread frees its queue slot
    /// at once instead of when the next frame (or the queue deadline) reaches it.
    /// </summary>
    public interface ICancellableMainThreadDispatcher : IMainThreadDispatcher
    {
        /// <summary>
        /// Like <see cref="IMainThreadDispatcher.RunOnMainThreadAsync{T}(Func{Task{T}})"/>, but a
        /// cancellation that arrives before <paramref name="work"/> starts removes it from the queue and
        /// completes the task as cancelled without running it. Work that has started is not interrupted.
        /// </summary>
        Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runs work immediately on the calling thread. Used by tests and headless callers that have no
    /// Unity player loop; never use it when handlers touch the scene from a background thread.
    /// </summary>
    public sealed class InlineMainThreadDispatcher : ICancellableMainThreadDispatcher
    {
        /// <inheritdoc />
        public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return work();
        }

        /// <inheritdoc />
        public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return cancellationToken.IsCancellationRequested ? Task.FromCanceled<T>(cancellationToken) : work();
        }
    }
}
