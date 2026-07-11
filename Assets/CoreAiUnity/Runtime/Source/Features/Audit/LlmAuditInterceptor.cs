using System;
using System.Collections.Generic;
using CoreAI.Audit;
using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Features.Audit
{
    public sealed class LlmAuditInterceptor : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new();
        private readonly IAuditLog _auditLog;

        public LlmAuditInterceptor(IAuditLog auditLog)
        {
            _auditLog = auditLog;

            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            _subscriptions.Add(
                GlobalMessagePipe.GetSubscriber<LlmBackendSelected>()
                    .Subscribe(evt => AuditContext.SetModel(evt.TraceId, evt.ClientType)));

            _subscriptions.Add(
                GlobalMessagePipe.GetSubscriber<LlmRequestStarted>()
                    .Subscribe(evt =>
                    {
                        _auditLog.Record(AuditEntry.ForLlmRequest(
                            0,
                            evt.TraceId,
                            evt.RoleId,
                            AuditContext.GetModel(evt.TraceId),
                            AuditContext.GetPromptHash(evt.TraceId),
                            evt.RoutingProfileId,
                            evt.Streaming));
                    }));

            _subscriptions.Add(
                GlobalMessagePipe.GetSubscriber<LlmRequestCompleted>()
                    .Subscribe(evt =>
                    {
                        _auditLog.Record(AuditEntry.ForLlmResponse(
                            0,
                            evt.TraceId,
                            evt.RoleId,
                            AuditContext.GetModel(evt.TraceId),
                            AuditContext.GetPromptHash(evt.TraceId),
                            evt.Success,
                            evt.Error));

                        AuditContext.Cleanup(evt.TraceId);
                    }));
        }

        public void Dispose()
        {
            foreach (IDisposable sub in _subscriptions)
            {
                sub.Dispose();
            }

            _subscriptions.Clear();
        }
    }
}