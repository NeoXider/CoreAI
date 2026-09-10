using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// No-op <see cref="ISkillStore"/>: authored skills live only in the in-memory catalog and do not
    /// survive a restart. Used by minimal containers (tests, headless tools, platforms without a file
    /// system) so the authoring path still works without persistence.
    /// </summary>
    public sealed class NullSkillStore : ISkillStore, IAsyncSkillStore
    {
        private readonly InlineAsyncSkillStoreAdapter _async;
        public NullSkillStore() { _async = new InlineAsyncSkillStoreAdapter(this); }
        public Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default) => _async.LoadAsync(id, cancellationToken);
        public Task<IReadOnlyList<SkillRecord>> ListAsync(CancellationToken cancellationToken = default) => _async.ListAsync(cancellationToken);
        public Task<TResult> MutateAndPublishAsync<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
            Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext, CancellationToken cancellationToken = default)
            => _async.MutateAndPublishAsync(id, prepare, publish, callbackContext, cancellationToken);

        /// <inheritdoc />
        public void Save(SkillRecord record)
        {
        }

        /// <inheritdoc />
        public bool TryLoad(string id, out SkillRecord record)
        {
            record = null;
            return false;
        }

        /// <inheritdoc />
        public IReadOnlyList<SkillRecord> List()
        {
            return Array.Empty<SkillRecord>();
        }

        /// <inheritdoc />
        public void Delete(string id)
        {
        }
    }
}
