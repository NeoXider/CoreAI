using System;
using System.Collections.Generic;
using CoreAI;
using CoreAI.Audit;
using CoreAI.Features.Audit;
using CoreAI.Messaging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Audit
{
    /// <summary>Regression tests for actor identity and refusal detail in tool-call audit entries.</summary>
    public sealed class ToolCallAuditInterceptorEditModeTests
    {
        [Test]
        public void FailedToolCall_RecordsResolvedActorAndDenialReason()
        {
            RecordingAuditLog auditLog = new();
            using (ToolCallAuditInterceptor interceptor = new(auditLog))
            {
                interceptor.SetActorIdentityResolver(
                    (string traceId, string roleId) => traceId == "trace-a" ? "player-42" : "");

                CoreAi.NotifyToolCallFailed(new LlmToolCallFailed(
                    "trace-a",
                    "builder",
                    "spawn",
                    "{}",
                    "quota exhausted",
                    5d));
            }

            Assert.AreEqual(1, auditLog.Entries.Count);
            Assert.AreEqual("player-42", auditLog.Entries[0].Actor);
            Assert.AreNotEqual("builder", auditLog.Entries[0].Actor);
            Assert.AreEqual("denied", auditLog.Entries[0].PolicyDecision);
            Assert.AreEqual("quota exhausted", auditLog.Entries[0].ResultDetail);
        }

        private sealed class RecordingAuditLog : IAuditLog
        {
            public List<AuditEntry> Entries { get; } = new();

            public void Record(AuditEntry entry)
            {
                Entries.Add(entry);
            }
        }
    }
}
