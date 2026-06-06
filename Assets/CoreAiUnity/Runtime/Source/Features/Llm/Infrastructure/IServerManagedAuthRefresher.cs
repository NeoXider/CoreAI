using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Optional companion to <see cref="IServerManagedAuthProvider"/> that refreshes the
    /// backend session when the LLM proxy responds with <c>401</c>/<c>AuthExpired</c>.
    /// Implementations typically call into the project's auth subsystem (refresh-token
    /// exchange, silent re-login, etc.) and update <see cref="ServerManagedAuthorization"/>
    /// before signalling success. When the refresher is not registered or refresh fails,
    /// CoreAI surfaces <see cref="LlmAuthExpired"/> on the message pipe so UI can prompt
    /// the user to re-authenticate.
    /// </summary>
    public interface IServerManagedAuthRefresher
    {
        /// <summary>
        /// Attempts to refresh the backend authorization. Returns <c>true</c> when the
        /// caller should retry the original request exactly once. Implementations must be
        /// safe to call from concurrent requests; single-flight behavior or equivalent is recommended.
        /// </summary>
        Task<bool> RefreshAsync(CancellationToken cancellationToken);
    }
}