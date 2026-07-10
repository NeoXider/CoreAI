using System;
using System.Collections.Generic;
using CoreAI.Audit;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;

namespace CoreAI.Features.Audit
{
    public sealed class AuditedWorldCommandExecutor : ICoreAiWorldCommandExecutor
    {
        private readonly ICoreAiWorldCommandExecutor _inner;
        private readonly IAuditLog _auditLog;

        public AuditedWorldCommandExecutor(ICoreAiWorldCommandExecutor inner, IAuditLog auditLog)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        }

        public string[] LastListedAnimations => _inner.LastListedAnimations;

        public List<Dictionary<string, object>> LastListedObjects => _inner.LastListedObjects;

        public IReadOnlyList<string> LastListedPrefabKeys => _inner.LastListedPrefabKeys;

        public string LastErrorMessage => _inner.LastErrorMessage;

        public CoreAiSpawnBatchResult LastSpawnBatchResult => _inner.LastSpawnBatchResult;

        public bool TryExecute(ApplyAiGameCommand cmd)
        {
            bool success = _inner.TryExecute(cmd);

            _auditLog.Record(AuditEntry.ForWorldMutation(
                seq: 0,
                traceId: cmd?.TraceId ?? "",
                actor: cmd?.SourceRoleId ?? "",
                commandTypeId: cmd?.CommandTypeId ?? "",
                jsonPayload: cmd?.JsonPayload ?? "",
                sourceTag: cmd?.SourceTag ?? "",
                success: success));

            return success;
        }
    }
}
