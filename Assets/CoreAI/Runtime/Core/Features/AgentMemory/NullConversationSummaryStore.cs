using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Empty summary store used when an application does not persist compacted conversation summaries.
    /// </summary>
    public sealed class NullConversationSummaryStore : IConversationSummaryStore, IAsyncConversationSummaryStore,
        ITransactionalConversationSummaryStore
    {
        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            return "";
        }

        /// <inheritdoc />
        public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("");
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
        }

        /// <inheritdoc />
        public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ConversationSummaryTransaction OpenSummaryTransaction(string roleId)
        {
            return new ConversationSummaryTransaction("", _ => true, (_, _) => Task.FromResult(true), null,
                (_, token, _) => Task.FromResult(!token.IsCancellationRequested));
        }

        /// <inheritdoc />
        public Task<ConversationSummaryTransaction> OpenSummaryTransactionAsync(
            string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenSummaryTransaction(roleId));
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
        }

        /// <inheritdoc />
        public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
