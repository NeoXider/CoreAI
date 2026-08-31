using System;
using System.Collections.Generic;
using CoreAI.Audit;
using CoreAI.Messaging;
using MessagePipe;
using UnityEngine;

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
                Debug.LogWarning(
                    "[LlmAuditInterceptor] MessagePipe is not initialized; LLM audit events will not be captured.");
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
                            evt.ActorId,
                            AuditContext.GetModel(evt.TraceId),
                            AuditContext.GetPromptHash(evt.TraceId),
                            evt.RoutingProfileId,
                            evt.Streaming,
                            evt.RoleId));
                    }));

            _subscriptions.Add(
                GlobalMessagePipe.GetSubscriber<LlmRequestCompleted>()
                    .Subscribe(evt =>
                    {
                        _auditLog.Record(AuditEntry.ForLlmResponse(
                            0,
                            evt.TraceId,
                            evt.ActorId,
                            AuditContext.GetModel(evt.TraceId),
                            AuditContext.GetPromptHash(evt.TraceId),
                            evt.Success,
                            evt.Error,
                            evt.RoleId));

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
