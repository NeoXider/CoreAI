using System;
using CoreAI;
using CoreAI.Audit;
using CoreAI.Messaging;

namespace CoreAI.Features.Audit
{
    public sealed class ToolCallAuditInterceptor : IDisposable
    {
        private readonly IAuditLog _auditLog;
        private Func<string, string, string> _actorIdentityResolver;
        private bool _disposed;

        public ToolCallAuditInterceptor(IAuditLog auditLog)
        {
            _auditLog = auditLog;
            CoreAi.OnToolCallCompleted += OnCompleted;
            CoreAi.OnToolCallFailed += OnFailed;
        }

        /// <summary>Sets the trusted runtime identity lookup shaped as trace id and role id to actor id.</summary>
        public void SetActorIdentityResolver(Func<string, string, string> actorIdentityResolver)
        {
            _actorIdentityResolver = actorIdentityResolver ?? throw new ArgumentNullException(nameof(actorIdentityResolver));
        }

        private void OnCompleted(LlmToolCallCompleted evt)
        {
            if (_disposed)
            {
                return;
            }

            _auditLog.Record(AuditEntry.ForToolCall(
                0,
                evt.TraceId,
                ResolveActorId(evt.TraceId, evt.RoleId),
                AuditContext.GetModel(evt.TraceId),
                AuditContext.GetPromptHash(evt.TraceId),
                evt.ToolName,
                evt.ArgumentsJson,
                "allowed",
                "ok",
                evt.ResultJson,
                evt.DurationMs));
        }

        private void OnFailed(LlmToolCallFailed evt)
        {
            if (_disposed)
            {
                return;
            }

            _auditLog.Record(AuditEntry.ForToolCall(
                0,
                evt.TraceId,
                ResolveActorId(evt.TraceId, evt.RoleId),
                AuditContext.GetModel(evt.TraceId),
                AuditContext.GetPromptHash(evt.TraceId),
                evt.ToolName,
                evt.ArgumentsJson,
                "denied",
                "error",
                evt.Error,
                evt.DurationMs));
        }

        private string ResolveActorId(string traceId, string roleId)
        {
            Func<string, string, string> resolver = _actorIdentityResolver;
            return resolver != null ? resolver(traceId ?? "", roleId ?? "") ?? "" : "";
        }

        public void Dispose()
        {
            _disposed = true;
            CoreAi.OnToolCallCompleted -= OnCompleted;
            CoreAi.OnToolCallFailed -= OnFailed;
        }
    }
}
