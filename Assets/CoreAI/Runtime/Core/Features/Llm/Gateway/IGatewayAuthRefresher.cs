#if COREAI_LLM
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Host-owned session refresh; implementations must coordinate concurrent callers.</summary>
    public interface IGatewayAuthRefresher
    {
        /// <summary>Refreshes the bound credential; true permits one safe raw-request replay.</summary>
        Task<bool> RefreshAsync(CancellationToken cancellationToken);
    }
}
#endif
