using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional asynchronous conversation preparation (AI compaction summaries) used by <see cref="AiOrchestrator"/>.
    /// Synchronous implementations can return <see cref="Task.FromResult"/> of <see cref="IConversationContextManager.BuildSnapshot"/>.
    /// </summary>
    public interface IAsyncConversationContextManager : IConversationContextManager
    {
        /// <summary>
        /// Builds snapshot; may call <see cref="ILlmClient"/> when older turns are rolled into a summary.
        /// </summary>
        /// <param name="orchestrationTraceId">
        /// Caller trace prefix; compaction requests typically append <c>:compact</c>.
        /// </param>
        Task<ConversationContextSnapshot> BuildSnapshotAsync(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs,
            string orchestrationTraceId,
            CancellationToken cancellationToken);
    }
}