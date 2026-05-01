using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI
{
    /// <summary>
    /// Runs async LLM-hosted work that must execute on the host's primary synchronization context.
    /// Unity tools and <c>UnityWebRequest</c> require the player loop thread; portable hosts use passthrough.
    /// </summary>
    public interface ILlmAsyncMarshaler
    {
        /// <summary>
        /// Executes <paramref name="factory"/> (after host-specific thread marshaling if configured).
        /// </summary>
        Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default: runs <paramref name="factory"/> immediately on the current context (existing behaviour).
    /// </summary>
    public sealed class PassThroughLlmAsyncMarshaler : ILlmAsyncMarshaler
    {
        public static readonly ILlmAsyncMarshaler Instance = new PassThroughLlmAsyncMarshaler();

        private PassThroughLlmAsyncMarshaler()
        {
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return factory();
        }
    }
}
