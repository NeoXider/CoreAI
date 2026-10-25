using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Process-wide summary store for <see cref="DeterministicConversationContextManager"/>.
    /// Keeps accumulated <c>## Conversation Summary</c> text per role in memory until cleared or the process exits.
    /// </summary>
    public sealed class InMemoryConversationSummaryStore : IConversationSummaryStore, IAsyncConversationSummaryStore,
        ITransactionalConversationSummaryStore
    {
        private readonly object _lock = new();
        private readonly SharedPathStateRegistry<RevisionState> _revisions = new(
            keyNormalizer: key => key, keyComparer: StringComparer.Ordinal);
        private sealed class RevisionState { public RevisionState() { } internal long Revision; }

        private readonly Dictionary<string, string> _byRole =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                return _byRole.TryGetValue(key, out string s) ? s : "";
            }
        }

        /// <inheritdoc />
        public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LoadSummary(roleId));
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                using SharedPathStateRegistry<RevisionState>.Lease revision = _revisions.Acquire(key);
                revision.State.Revision++;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    _byRole.Remove(key);
                }
                else
                {
                    _byRole[key] = summary;
                }
            }
        }

        /// <inheritdoc />
        public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveSummary(roleId, summary);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                using SharedPathStateRegistry<RevisionState>.Lease revision = _revisions.Acquire(key);
                revision.State.Revision++;
                _byRole.Remove(key);
            }
        }

        /// <inheritdoc />
        public ConversationSummaryTransaction OpenSummaryTransaction(string roleId)
        {
            string key = roleId?.Trim() ?? "";
            lock (_lock)
            {
                SharedPathStateRegistry<RevisionState>.Lease lease = _revisions.Acquire(key);
                long baseline = lease.State.Revision;
                string summary = _byRole.TryGetValue(key, out string current) ? current : "";
                bool Commit(string value)
                {
                    lock (_lock)
                    {
                        if (lease.State.Revision != baseline) return false;
                        lease.State.Revision++;
                        if (string.IsNullOrWhiteSpace(value)) _byRole.Remove(key);
                        else _byRole[key] = value;
                        return true;
                    }
                }
                return new ConversationSummaryTransaction(summary, Commit, (value, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(Commit(value));
                }, lease);
            }
        }

        /// <inheritdoc />
        public Task<ConversationSummaryTransaction> OpenSummaryTransactionAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenSummaryTransaction(roleId));
        }

        /// <inheritdoc />
        public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearSummary(roleId);
            return Task.CompletedTask;
        }
    }
}
