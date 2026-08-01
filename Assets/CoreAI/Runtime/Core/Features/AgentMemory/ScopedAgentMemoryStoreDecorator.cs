using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Decorates an existing memory store and maps role ids to scoped keys.
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecorator : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore
    {
        private readonly IAgentMemoryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;

        /// <summary>
        /// Creates a scoped memory store wrapper.
        /// </summary>
        public ScopedAgentMemoryStoreDecorator(
            IAgentMemoryStore inner,
            IAgentMemoryScopeProvider scopeProvider)
        {
            _inner = inner ?? new NullAgentMemoryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return _inner.TryLoad(ToScopedKey(roleId), out state);
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            string key = ToScopedKey(roleId);
            if (_inner is IAgentMemoryLoadDiagnostics diagnostics)
            {
                return diagnostics.TryLoadDetailed(key, out state);
            }

            // WHY: An inner store without the capability cannot tell "missing" from "unreadable"; report
            // the optimistic NotFound rather than blocking every first write behind a false Failed.
            return _inner.TryLoad(key, out state) && state != null
                ? AgentMemoryLoadStatus.Loaded
                : AgentMemoryLoadStatus.NotFound;
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            _inner.Save(ToScopedKey(roleId), state);
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            _inner.Clear(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            _inner.ClearChatHistory(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            _inner.AppendChatMessage(ToScopedKey(roleId), role, content, persistToDisk);
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            return _inner.GetChatHistory(ToScopedKey(roleId), maxMessages);
        }

        /// <inheritdoc />
        public Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            // WHY: The extension delegates to an inner IAtomicAgentMemoryStore when available; otherwise it owns
            // a per-inner-store, per-scoped-key gate. Passing the scoped key here is essential: locking the raw
            // role id would serialize unrelated students and delegating the raw role would leak their state.
            return _inner.MutateAsync(ToScopedKey(roleId), mutator, cancellationToken);
        }

        private string ToScopedKey(string roleId)
        {
            return AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
        }
    }
}
