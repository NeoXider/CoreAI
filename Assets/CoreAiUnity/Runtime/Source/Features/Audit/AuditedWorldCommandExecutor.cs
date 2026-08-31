using System;
using System.Collections.Generic;
using CoreAI.Ai;
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
            LlmRequestContextFrame traceContext = LlmRequestContext.Current;
            string traceId = !string.IsNullOrWhiteSpace(cmd?.TraceId)
                ? cmd.TraceId
                : traceContext?.TraceId ?? "";
            string actorId = !string.IsNullOrWhiteSpace(cmd?.SourceActorId)
                ? cmd.SourceActorId
                : traceContext?.ActorId ?? "";
            string roleId = !string.IsNullOrWhiteSpace(cmd?.SourceRoleId)
                ? cmd.SourceRoleId
                : traceContext?.AgentRoleId ?? "";
            bool success = _inner.TryExecute(cmd);

            _auditLog.Record(AuditEntry.ForWorldMutation(
                0,
                traceId,
                actorId,
                cmd?.CommandTypeId ?? "",
                cmd?.JsonPayload ?? "",
                cmd?.SourceTag ?? "",
                success,
                role: roleId));

            return success;
        }
    }
}
