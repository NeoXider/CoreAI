using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Audit;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using VContainer;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode.Audit
{
    /// <summary>Regression tests for actor identity and refusal detail in tool-call audit entries.</summary>
    public sealed class ToolCallAuditInterceptorEditModeTests
    {
        [Test]
        public async Task FailedToolCall_ProductionTraceKeepsCausingActorWhenCurrentActorDiffers()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();
            RecordingAuditLog auditLog = new();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterAuditLog();
            builder.RegisterInstance<IActorIdentityProvider>(new LocalActorIdentityProvider(
                "actor-b",
                "session-b",
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty));
            builder.RegisterInstance<IAuditLog>(auditLog);

            using (IObjectResolver container = builder.Build())
            {
                MeaiLlmClient client = new(
                    new ToolThenTextChatClient(),
                    GameLoggerUnscopedFallback.Instance,
                    new CoreAISettingsOptions());
                DelegateLlmTool tool = new(
                    "spawn",
                    "Fails for audit coverage.",
                    (Func<string>)(() => "{\"Success\":false,\"Error\":\"quota exhausted\"}"));
                await client.CompleteAsync(new LlmCompletionRequest
                {
                    ActorId = "actor-a",
                    AgentRoleId = "builder",
                    TraceId = "trace-a",
                    SystemPrompt = "system",
                    UserPayload = "spawn",
                    Tools = new[] { tool }
                }, CancellationToken.None);
            }

            Assert.AreEqual(1, auditLog.Entries.Count);
            Assert.AreEqual("actor-a", auditLog.Entries[0].Actor);
            Assert.AreEqual("builder", auditLog.Entries[0].Role);
            Assert.AreNotEqual("actor-b", auditLog.Entries[0].Actor);
            Assert.AreNotEqual("builder", auditLog.Entries[0].Actor);
            Assert.AreEqual("denied", auditLog.Entries[0].PolicyDecision);
        }

        private sealed class ToolThenTextChatClient : MEAI.IChatClient
        {
            private int _callCount;

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                _callCount++;
                if (_callCount == 1)
                {
                    MEAI.FunctionCallContent call = new(
                        "call-a",
                        "spawn",
                        new Dictionary<string, object?>());
                    MEAI.ChatMessage message = new(
                        MEAI.ChatRole.Assistant,
                        new List<MEAI.AIContent> { call });
                    return Task.FromResult(new MEAI.ChatResponse(message));
                }

                return Task.FromResult(new MEAI.ChatResponse(
                    new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done")));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
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
