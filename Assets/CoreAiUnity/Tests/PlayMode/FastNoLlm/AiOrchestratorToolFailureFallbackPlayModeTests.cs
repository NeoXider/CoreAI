using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class AiOrchestratorToolFailureFallbackPlayModeTests
    {
        [UnityTest]
        public IEnumerator RunStreamingAsync_ToolOnlyFailure_ShowsToolErrorInsteadOfStructuredValidation()
        {
            return UniTask.ToCoroutine(async () =>
            {
                AgentMemoryPolicy memoryPolicy = new();
                TestSettings settings = new();
                AiOrchestrator orchestrator = new(
                    new TestAuthority(),
                    new ToolOnlyWhitespaceLlmClient(),
                    new TestSink(),
                    new TestTelemetry(),
                    new AiPromptComposer(new NullSys(), new NullUsr(), null, null, memoryPolicy, settings),
                    new TestMemoryStore(),
                    memoryPolicy,
                    new CompositeRoleStructuredResponsePolicy(),
                    null,
                    settings,
                    new LocalActorIdentityProvider("tool-failure-test"));

                string text = "";
                List<string> errors = new();
                bool sawDone = false;
                bool sawFallbackBeforeDone = false;
                await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(new AiTaskRequest
                               {
                                   RoleId = BuiltInAgentRoleIds.Programmer,
                                   Hint = "сделай награду за босса",
                                   SourceTag = "Chat"
                               }))
                {
                    text += chunk.Text ?? "";
                    if (!sawDone && (chunk.Text ?? "").Contains("Tool call failed: manage_mods"))
                    {
                        sawFallbackBeforeDone = true;
                    }

                    if (chunk.IsDone)
                    {
                        sawDone = true;
                    }

                    if (!string.IsNullOrEmpty(chunk.Error))
                    {
                        errors.Add(chunk.Error);
                    }
                }

                Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
                Assert.IsTrue(sawDone, "Streaming should still emit a terminal chunk after the fallback text.");
                Assert.IsTrue(sawFallbackBeforeDone, "Fallback text must arrive before IsDone for collect helpers.");
                StringAssert.Contains("Tool call failed: manage_mods", text);
                StringAssert.Contains("attempt to index a function value", text);
                Assert.That(text, Does.Not.Contain("structured validation failed"));
            });
        }

        private sealed class ToolOnlyWhitespaceLlmClient : ILlmClient
        {
            private static readonly LlmToolCallTrace[] Traces =
            {
                new(
                    "manage_mods",
                    false,
                    12d,
                    "native",
                    "{\"success\":false,\"message\":\"manage_mods 'load' failed: attempt to index a function value\"}")
            };

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = " \n ",
                    ExecutedToolCalls = Traces
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Text = " \n ",
                    ExecutedToolCalls = Traces
                };
                await Task.CompletedTask;
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return System.Array.Empty<ChatMessage>();
            }
        }

        private sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.7f;
            public int ContextWindowTokens => 8192;
            public int MaxLlmRequestRetries => 1;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string UniversalSystemPromptPrefix => "";
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public int MaxLuaRepairRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
            public bool EnableConversationHistorySummarization { get; set; } = true;
            public int ConversationHistoryRecentTokenBudgetOverride { get; set; }
            public int ConversationRolledSummaryMaxTokens { get; set; }
        }

        private sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }
    }
}
