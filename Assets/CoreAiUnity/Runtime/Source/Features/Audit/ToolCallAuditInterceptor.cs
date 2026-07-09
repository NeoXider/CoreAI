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
            if (_disposed) return;
            _auditLog.Record(AuditEntry.ForToolCall(
                seq: 0,
                traceId: evt.TraceId,
                actor: evt.RoleId,
                model: AuditContext.GetModel(evt.TraceId),
                promptHash: AuditContext.GetPromptHash(evt.TraceId),
                toolName: evt.ToolName,
                args: evt.ArgumentsJson,
                policyDecision: "allowed",
                result: "ok",
                resultDetail: evt.ResultJson,
                durationMs: evt.DurationMs));
        }

        private void OnFailed(LlmToolCallFailed evt)
        {
            if (_disposed) return;
            _auditLog.Record(AuditEntry.ForToolCall(
                seq: 0,
                traceId: evt.TraceId,
                actor: evt.RoleId,
                model: AuditContext.GetModel(evt.TraceId),
                promptHash: AuditContext.GetPromptHash(evt.TraceId),
                toolName: evt.ToolName,
                args: evt.ArgumentsJson,
                policyDecision: "denied",
                result: "error",
                resultDetail: evt.Error,
                durationMs: evt.DurationMs));
        }

        public void Dispose()
        {
            _disposed = true;
            CoreAi.OnToolCallCompleted -= OnCompleted;
            CoreAi.OnToolCallFailed -= OnFailed;
        }
    }
}
