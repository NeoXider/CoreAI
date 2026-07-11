using System;
using CoreAI;
using CoreAI.Audit;
using CoreAI.Messaging;

namespace CoreAI.Features.Audit
{
    public sealed class ToolCallAuditInterceptor : IDisposable
    {
        private readonly IAuditLog _auditLog;
        private bool _disposed;

        public ToolCallAuditInterceptor(IAuditLog auditLog)
        {
            _auditLog = auditLog;
            CoreAi.OnToolCallCompleted += OnCompleted;
            CoreAi.OnToolCallFailed += OnFailed;
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
                evt.RoleId,
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
                evt.RoleId,
                AuditContext.GetModel(evt.TraceId),
                AuditContext.GetPromptHash(evt.TraceId),
                evt.ToolName,
                evt.ArgumentsJson,
                "denied",
                "error",
                evt.Error,
                evt.DurationMs));
        }

        public void Dispose()
        {
            _disposed = true;
            CoreAi.OnToolCallCompleted -= OnCompleted;
            CoreAi.OnToolCallFailed -= OnFailed;
        }
    }
}
