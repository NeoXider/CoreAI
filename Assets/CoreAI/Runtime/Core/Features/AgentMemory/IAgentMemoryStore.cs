using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>Outcome of one role-memory load attempt.</summary>
    public enum AgentMemoryLoadStatus
    {
        /// <summary>Persisted state was read successfully.</summary>
        Loaded = 0,

        /// <summary>Nothing is persisted for the role yet; starting from an empty state is safe.</summary>
        NotFound = 1,

        /// <summary>Persisted state exists but could not be read; its content is unknown.</summary>
        Failed = 2
    }

    /// <summary>
    /// Optional store capability that separates "nothing persisted yet" from "persisted state exists but
    /// could not be read". <see cref="IAgentMemoryStore.TryLoad"/> collapses both into <c>false</c>, which
    /// makes a read/modify/write cycle overwrite a live memory document with an empty one whenever a read
    /// fails transiently (a locked file, a partial write).
    /// </summary>
    public interface IAgentMemoryLoadDiagnostics
    {
        /// <summary>Attempts to load persisted memory state and reports why the attempt did not succeed.</summary>
        AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state);
    }

    /// <summary>
    /// Raised when persisted role memory exists but could not be read, so a read/modify/write cycle must
    /// abort instead of persisting a state built on top of an unknown baseline.
    /// </summary>
    public sealed class AgentMemoryLoadException : Exception
    {
        /// <summary>Creates the exception for a role whose persisted memory could not be read.</summary>
        public AgentMemoryLoadException(string roleId)
            : base($"Persisted memory for role '{roleId}' exists but could not be read; " +
                   "the mutation was aborted to avoid overwriting it.")
        {
            RoleId = roleId ?? "";
        }

        /// <summary>Agent role id whose memory could not be read.</summary>
        public string RoleId { get; }
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
        /// <summary>
        /// Mutation locks keyed by role id, held <b>per store instance</b> - the fallback path used only
        /// when a store does not implement <see cref="IAtomicAgentMemoryStore"/> itself. Two independent
        /// stores (different directories) must not serialize against each other, and the whole table must
        /// die with the store rather than pinning entries for the process lifetime. Entries within one store
        /// are intentionally never evicted: a caller could already hold the <see cref="SemaphoreSlim"/>
        /// fetched from the dictionary while a concurrent eviction-then-<c>GetOrAdd</c> hands a second
        /// caller a fresh instance, silently breaking the mutual exclusion this lock exists for.
        /// </summary>
        private static readonly
            ConditionalWeakTable<IAgentMemoryStore, ConcurrentDictionary<string, SemaphoreSlim>>
            MutationLocks = new();

        /// <summary>
        /// Atomically loads, mutates, and saves one role state. Stores that can map to a durable file/key
        /// should implement <see cref="IAtomicAgentMemoryStore"/>; the fallback still serializes callers
        /// per store instance and role id.
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

            ConcurrentDictionary<string, SemaphoreSlim> gates = MutationLocks.GetValue(
                store, _ => new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal));
            SemaphoreSlim gate = gates.GetOrAdd(roleId ?? "", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AgentMemoryState state = LoadBaselineOrThrow(store, roleId);

                TResult result = mutator(state);
                store.Save(roleId, state);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Reads the baseline state a mutation will be built on. A missing document is a legitimate empty
        /// start; a document that exists but cannot be READ is not - continuing there would persist a
        /// mutation applied to an empty baseline and wipe the real memory plus its whole version history.
        /// </summary>
        /// <exception cref="AgentMemoryLoadException">
        /// The store reported that persisted state exists but could not be read.
        /// </exception>
        private static AgentMemoryState LoadBaselineOrThrow(IAgentMemoryStore store, string roleId)
        {
            AgentMemoryState state;
            if (store is IAgentMemoryLoadDiagnostics diagnostics)
            {
                AgentMemoryLoadStatus status = diagnostics.TryLoadDetailed(roleId, out state);
                if (status == AgentMemoryLoadStatus.Failed)
                {
                    throw new AgentMemoryLoadException(roleId);
                }

                return state ?? new AgentMemoryState();
            }

            if (!store.TryLoad(roleId, out state) || state == null)
            {
                return new AgentMemoryState();
            }

            return state;
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
