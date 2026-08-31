using System;
using System.Collections.Generic;
using CoreAI;
using CoreAI.Audit;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Features.Audit;
using CoreAI.Messaging;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode.Audit
{
    /// <summary>Regression tests for actor identity and refusal detail in tool-call audit entries.</summary>
    public sealed class ToolCallAuditInterceptorEditModeTests
    {
        [Test]
        public void FailedToolCall_RecordsResolvedActorAndDenialReason()
        {
            RecordingAuditLog auditLog = new();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterAuditLog();
            builder.RegisterInstance<IActorIdentityProvider>(new LocalActorIdentityProvider(
                "player-42",
                "session-42",
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty));
            builder.RegisterInstance<IAuditLog>(auditLog);

            using (IObjectResolver container = builder.Build())
            {
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
