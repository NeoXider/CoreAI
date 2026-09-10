using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Lua script version store used when runtime version history is disabled.</summary>
    public sealed class NullLuaScriptVersionStore : ILuaScriptVersionStore, IAsyncLuaScriptVersionStore
    {
        /// <inheritdoc />
        public Task<LuaScriptVersionRecord> GetSnapshotAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(TryGetSnapshot(key, out LuaScriptVersionRecord snapshot) ? snapshot : null); }
        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetKnownKeysAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(GetKnownKeys()); }
        /// <inheritdoc />
        public Task SeedOriginalAsync(string key, string source, bool overwriteExistingOriginal = false, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); SeedOriginal(key, source, overwriteExistingOriginal); return Task.CompletedTask; }
        /// <inheritdoc />
        public Task RecordSuccessfulExecutionAsync(string key, string source, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); RecordSuccessfulExecution(key, source); return Task.CompletedTask; }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            snapshot = null;
            return false;
        }

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
        }

        public void ResetToOriginal(string scriptKey)
        {
        }

        public void ResetToRevision(string scriptKey, int revisionIndex)
        {
        }

        public void ResetAllToOriginal()
        {
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            return System.Array.Empty<string>();
        }

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            return "";
        }
    }
}
