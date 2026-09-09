using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Awaitable revision operations needed by persistent skill authoring.</summary>
    public interface IAsyncLuaScriptVersionStore
    {
        Task<LuaScriptVersionRecord> GetSnapshotAsync(string key, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<string>> GetKnownKeysAsync(CancellationToken cancellationToken = default);
        Task SeedOriginalAsync(string key, string source, bool overwriteExistingOriginal = false, CancellationToken cancellationToken = default);
        Task RecordSuccessfulExecutionAsync(string key, string source, CancellationToken cancellationToken = default);
    }

    /// <summary>Explicit promise that a legacy version store performs only fast, nonblocking inline work.</summary>
    public sealed class InlineAsyncLuaScriptVersionStoreAdapter : ILuaScriptVersionStore, IAsyncLuaScriptVersionStore
    {
        private readonly ILuaScriptVersionStore _store;
        internal ILuaScriptVersionStore OrderingIdentity => _store;
        public InlineAsyncLuaScriptVersionStoreAdapter(ILuaScriptVersionStore store) { _store = store is InlineAsyncLuaScriptVersionStoreAdapter adapter ? adapter._store : store ?? throw new ArgumentNullException(nameof(store)); }
        public bool TryGetSnapshot(string key, out LuaScriptVersionRecord snapshot) => _store.TryGetSnapshot(key, out snapshot);
        public IReadOnlyList<string> GetKnownKeys() => _store.GetKnownKeys();
        public void SeedOriginal(string key, string source, bool overwriteExistingOriginal = false) => _store.SeedOriginal(key, source, overwriteExistingOriginal);
        public void RecordSuccessfulExecution(string key, string source) => _store.RecordSuccessfulExecution(key, source);
        public void ResetToOriginal(string key) => _store.ResetToOriginal(key);
        public void ResetToRevision(string key, int revisionIndex) => _store.ResetToRevision(key, revisionIndex);
        public void ResetAllToOriginal() => _store.ResetAllToOriginal();
        public string BuildProgrammerPromptSection(string key) => _store.BuildProgrammerPromptSection(key);
        public Task<LuaScriptVersionRecord> GetSnapshotAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(TryGetSnapshot(key, out LuaScriptVersionRecord snapshot) ? snapshot : null); }
        public Task<IReadOnlyList<string>> GetKnownKeysAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(GetKnownKeys()); }
        public Task SeedOriginalAsync(string key, string source, bool overwriteExistingOriginal = false, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); SeedOriginal(key, source, overwriteExistingOriginal); return Task.CompletedTask; }
        public Task RecordSuccessfulExecutionAsync(string key, string source, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); RecordSuccessfulExecution(key, source); return Task.CompletedTask; }
    }

    /// <summary>
    /// Tracks original and current Lua script versions.
    /// </summary>
    public interface ILuaScriptVersionStore
    {
        /// <summary>Attempts to read the current version snapshot for a Lua script.</summary>
        bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot);

        /// <summary>
        /// Records Lua source that executed successfully as the current script revision.
        /// </summary>
        void RecordSuccessfulExecution(string scriptKey, string executedLuaSource);

        /// <summary>
        /// <summary>
        /// Stores the original Lua source for a script before later revisions are applied.
        /// </summary>
        void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false);

        /// <summary>Restores the requested script to its original version.</summary>
        void ResetToOriginal(string scriptKey);

        /// <summary>
        /// Restores a tracked script to a specific revision index.
        /// </summary>
        void ResetToRevision(string scriptKey, int revisionIndex);

        /// <summary>Restores all tracked versioned values to their original payloads.</summary>
        void ResetAllToOriginal();

        /// <summary>Returns all script keys known to the version store.</summary>
        IReadOnlyList<string> GetKnownKeys();

        /// <summary>Builds the programmer prompt section that describes the current script version.</summary>
        string BuildProgrammerPromptSection(string scriptKey);
    }
}
