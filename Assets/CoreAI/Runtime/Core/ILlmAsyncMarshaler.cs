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
        /// Invokes <paramref name="factory"/> after applying any host-specific thread marshaling.
        /// </summary>
        Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default implementation that runs the factory immediately on the current context.
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