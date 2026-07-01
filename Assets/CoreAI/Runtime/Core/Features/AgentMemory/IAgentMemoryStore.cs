using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Persistence contract for role-scoped agent memory and chat history.</summary>
    public interface IAgentMemoryStore
    {
        /// <summary>Attempts to load persisted memory state for the requested role.</summary>
        bool TryLoad(string roleId, out AgentMemoryState state);

        /// <summary>Persists memory state for the requested role.</summary>
        void Save(string roleId, AgentMemoryState state);

        /// <summary>Clears all memory state for the requested role.</summary>
        void Clear(string roleId);

        /// <summary>Clears only the chat history stored for the requested role.</summary>
        void ClearChatHistory(string roleId);

        /// <summary>
        /// Appends one chat message to a role's persisted conversation history.
        /// </summary>
        /// <param name="roleId">Agent role id that owns the history.</param>
        /// <param name="role">Message role, such as <c>user</c>, <c>assistant</c>, or <c>system</c>.</param>
        /// <param name="content">Message text.</param>
        /// <param name="persistToDisk">Whether the store should flush the change immediately.</param>
        void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true);

        /// <summary>
        /// Returns recent chat history for a role.
        /// </summary>
        /// <param name="roleId">Agent role id that owns the history.</param>
        /// <param name="maxMessages">Maximum messages to return; <c>0</c> means store default/all.</param>
        /// <returns>Chat messages in chronological order as provided by the store.</returns>
        ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0);
    }

    /// <summary>
    /// Optional store capability for atomic role-memory read/modify/write transactions.
    /// </summary>
    public interface IAtomicAgentMemoryStore
    {
        /// <summary>
        /// Runs <paramref name="mutator"/> while the store holds a process-wide role key lock, then persists
        /// the resulting state before releasing the lock.
        /// </summary>
        Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Serializable chat transcript entry.</summary>
    [Serializable]
    public struct ChatMessage
    {
        public string Role; // "user" | "assistant" | "system"
        public string Content;
        public long Timestamp; // Unix timestamp

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>
    /// Backward-compatible memory version APIs layered over <see cref="IAgentMemoryStore"/>.
    /// Stores that preserve <see cref="AgentMemoryState.Versions"/> get version listing and rollback
    /// without adding required interface members to existing implementations.
    /// </summary>
    public static class AgentMemoryStoreExtensions
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks = new();

        /// <summary>
        /// Atomically loads, mutates, and saves one role state. Stores that can map to a durable file/key
        /// should implement <see cref="IAtomicAgentMemoryStore"/>; the fallback still serializes callers
        /// process-wide by store type and role id.
        /// </summary>
        public static async Task<TResult> MutateAsync<TResult>(
            this IAgentMemoryStore store,
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            if (store is IAtomicAgentMemoryStore atomic)
            {
                return await atomic.MutateAsync(roleId, mutator, cancellationToken).ConfigureAwait(false);
            }

            string key = $"{store.GetType().FullName}:{roleId ?? ""}";
            SemaphoreSlim gate = MutationLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!store.TryLoad(roleId, out AgentMemoryState state) || state == null)
                {
                    state = new AgentMemoryState();
                }

                TResult result = mutator(state);
                store.Save(roleId, state);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Returns retained memory versions for a role in chronological order.</summary>
        public static IReadOnlyList<AgentMemoryVersionSnapshot> ListVersions(this IAgentMemoryStore store,
            string roleId)
        {
            if (store == null ||
                string.IsNullOrWhiteSpace(roleId) ||
                !store.TryLoad(roleId, out AgentMemoryState state) ||
                state?.Versions == null)
            {
                return Array.Empty<AgentMemoryVersionSnapshot>();
            }

            return new List<AgentMemoryVersionSnapshot>(state.Versions);
        }

        /// <summary>
        /// Restores the memory document to a retained version and records the rollback as a new version.
        /// </summary>
        public static bool Revert(this IAgentMemoryStore store, string roleId, int version)
        {
            return store.Revert(roleId, version, out _);
        }

        /// <summary>
        /// Restores the memory document to a retained version and records the rollback as a new version.
        /// </summary>
        public static bool Revert(this IAgentMemoryStore store, string roleId, int version, out string error)
        {
            error = null;
            if (store == null)
            {
                error = "Store is required";
                return false;
            }

            if (string.IsNullOrWhiteSpace(roleId))
            {
                error = "Role id is required";
                return false;
            }

            if (!store.TryLoad(roleId, out AgentMemoryState state) || state == null)
            {
                error = $"No memory state exists for role: {roleId}";
                return false;
            }

            AgentMemoryVersionSnapshot target = FindVersion(state, version);
            if (target == null)
            {
                error = $"Version {version} was not found for role: {roleId}";
                return false;
            }

            state.Memory = target.ContentAfter ?? "";
            state.RecordVersion("revert", state.Memory, $"Reverted to version {version}.");
            store.Save(roleId, state);
            return true;
        }

        private static AgentMemoryVersionSnapshot FindVersion(AgentMemoryState state, int version)
        {
            if (state?.Versions == null)
            {
                return null;
            }

            for (int i = 0; i < state.Versions.Length; i++)
            {
                AgentMemoryVersionSnapshot snapshot = state.Versions[i];
                if (snapshot != null && snapshot.Version == version)
                {
                    return snapshot;
                }
            }

            return null;
        }
    }
}
