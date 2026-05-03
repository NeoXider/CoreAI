using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class AiOrchestratorHistoryEditModeTests
    {
        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks { get; set; } = true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class TestLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "Hello" });
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public List<CoreAI.Ai.ChatMessage> FakeHistory { get; set; } = new();
            public List<(string Role, string Content)> Appended { get; } = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                Appended.Add((role, content));
            }

            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (maxMessages > 0 && FakeHistory.Count > maxMessages)
                {
                    int skip = FakeHistory.Count - maxMessages;
                    return FakeHistory.ToArray()[skip..];
                }
                return FakeHistory.ToArray();
            }
        }

        private sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) { }
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot() => new();
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
            public bool TryGetSystemPrompt(string roleId, out string prompt) { prompt = null; return false; }
        }
        
        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template) { template = null; return false; }
        }

        private sealed class StaticContextProvider : IAiPromptContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return $"slot={request.SourceTag};role={roleId};trace={traceId}";
            }
        }

        private sealed class StaticRoleContextProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return $"role-context={roleId};slot={request.SourceTag}";
            }
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "stub";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        [Test]
        public async Task RunTaskAsync_TruncatesChatHistory_ByMaxMessages()
        {
            // Arrange
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            
            // Generate 50 fake messages
            for (int i = 0; i < 50; i++)
            {
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage { Role = "user", Content = $"Short msg {i}" });
            }

            // Настраиваем агента с лимитом в 15 сообщений
            policy.ConfigureChatHistory("test_role", enabled: true, tokens: 8192, persist: false, maxChatHistoryMessages: 15);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            // Act
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "Hi" });

            // Assert
            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(15, llm.LastRequest.ChatHistory.Count, "History should be truncated to exactly MaxChatHistoryMessages");
            
            // Check that we got the *most recent* 15
            Assert.IsTrue(llm.LastRequest.ChatHistory[14].Text.Contains("Short msg 49"), "Last message should match the latest");
            Assert.IsTrue(llm.LastRequest.ChatHistory[0].Text.Contains("Short msg 35"), "First message in truncated history should match sequence");
        }

        [Test]
        public async Task RunTaskAsync_TruncatesChatHistory_ByTokenBudget()
        {
            // Arrange
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            
            // ContextTokens = 300. Composer must receive TestSettings: otherwise CoreAISettings'
            // default universal prefix balloons system prompt size and eats the whole budget.
            // DefaultContextBudgetPolicy reserves completion headroom; disable memory/tools for a slim fixed prompt.
            policy.ConfigureChatHistory("test_role", enabled: true, tokens: 300, persist: false, maxChatHistoryMessages: 50);
            policy.DisableMemoryTool("test_role");
            policy.SetToolsForRole("test_role", System.Array.Empty<ILlmTool>());
            for (int i = 0; i < 20; i++)
            {
                string content = "A".PadRight(100, 'A') + i; // 100 chars + number
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage { Role = "user", Content = content });
            }

            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            // Act
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            // Assert
            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            
            // Expected: several recent lines (not full 20); count depends on heuristic estimator + budget policy.
            int expectedCount = llm.LastRequest.ChatHistory.Count;
            Assert.Less(expectedCount, 20, "History should be significantly truncated due to token budget");
            Assert.GreaterOrEqual(expectedCount, 3, "At least a few messages should be kept within budget");
            
            // Verify most recent messages were kept
            Assert.IsTrue(llm.LastRequest.ChatHistory[^1].Text.Contains("19"), "Should keep the most recent message");
        }

        [Test]
        public async Task RunTaskAsync_CompactsOldHistory_IntoSystemSummary()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();

            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(90, 'x');
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", enabled: true, tokens: 60, persist: false, maxChatHistoryMessages: 50);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.Less(llm.LastRequest.ChatHistory.Count, memory.FakeHistory.Count);
            StringAssert.Contains("## Conversation Summary", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("old-context-0", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("old-context-9", llm.LastRequest.ChatHistory[^1].Text);
        }

        [Test]
        public async Task RunTaskAsync_DisableHistorySummarization_KeepsFullChatTail()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();

            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(90, 'x');
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", enabled: true, tokens: 60, persist: false, maxChatHistoryMessages: 50);

            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(10, llm.LastRequest.ChatHistory.Count);
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
        }

        [Test]
        public async Task RunTaskAsync_RecentHistoryTokenBudgetOverride_ForcesTighterTail()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();

            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(90, 'x');
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", enabled: true, tokens: 60, persist: false, maxChatHistoryMessages: 50);

            TestSettings settings = new() { ConversationHistoryRecentTokenBudgetOverride = 32 };
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Assert.AreEqual(1, llm.LastRequest.ChatHistory.Count);
            StringAssert.Contains("## Conversation Summary", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("old-context-0", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("old-context-9", llm.LastRequest.ChatHistory[^1].Text);
        }

        [Test]
        public async Task RunTaskAsync_AppendsRuntimePromptContext()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiPromptComposer composer = new(
                new NullSys(),
                new NullUsr(),
                null,
                null,
                policy,
                settings,
                new IAiPromptContextProvider[] { new StaticContextProvider() });
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "Hi",
                TraceId = "trace-context",
                SourceTag = "practice-slot"
            });

            StringAssert.Contains("## Runtime Context", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("slot=practice-slot", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("trace=trace-context", llm.LastRequest.SystemPrompt);
        }

        [Test]
        public async Task RunTaskAsync_AppendsPerRoleRuntimeContext()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.SetRuntimeContextProvider("Teacher", new StaticRoleContextProvider());
            TestSettings settings = new();
            AiPromptComposer composer = new(new NullSys(), new NullUsr(), null, null, policy, settings);
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "theory"
            });

            StringAssert.Contains("role-context=Teacher", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("slot=theory", llm.LastRequest.SystemPrompt);
        }

        [Test]
        public async Task RunTaskAsync_EmptyAllowedToolNames_SendsNoTools()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[]
            {
                new StubTool("spawn_quiz"),
                new StubTool("spawn_drag_and_drop")
            });
            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = Array.Empty<string>()
            });

            Assert.AreEqual(0, llm.LastRequest.Tools.Count);
            CollectionAssert.AreEqual(Array.Empty<string>(), llm.LastRequest.AllowedToolNames);
        }

        [Test]
        public async Task RunStreamingAsync_UsesSameToolFiltering_AsRunTaskAsync()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[]
            {
                new StubTool("spawn_quiz"),
                new StubTool("spawn_drag_and_drop")
            });
            AiTaskRequest task = new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "hi",
                AllowedToolNames = new[] { "spawn_drag_and_drop" },
                ForcedToolMode = LlmToolChoiceMode.RequireAny
            };

            TestSettings settings = new();
            AiPromptComposer composer = new(new NullSys(), new NullUsr(), null, null, policy, settings);
            AiOrchestrator orchestratorSync = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);
            await orchestratorSync.RunTaskAsync(task);

            int syncToolCount = llm.LastRequest.Tools.Count;
            string syncFirstTool = llm.LastRequest.Tools.Count > 0 ? llm.LastRequest.Tools[0].Name : "";
            LlmToolChoiceMode syncMode = llm.LastRequest.ForcedToolMode;
            CollectionAssert.AreEqual(new[] { "spawn_drag_and_drop" }, llm.LastRequest.AllowedToolNames);

            TestLlmClient llmStream = new();
            AiOrchestrator orchestratorStream = new AiOrchestrator(
                new TestAuthority(), llmStream, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);

            await foreach (LlmStreamChunk _ in orchestratorStream.RunStreamingAsync(task, default))
            {
            }

            Assert.AreEqual(syncToolCount, llmStream.LastRequest.Tools.Count, "Streaming path must attach the same tool count as non-streaming.");
            Assert.AreEqual(syncFirstTool, llmStream.LastRequest.Tools.Count > 0 ? llmStream.LastRequest.Tools[0].Name : "");
            Assert.AreEqual(syncMode, llmStream.LastRequest.ForcedToolMode);
            CollectionAssert.AreEqual(new[] { "spawn_drag_and_drop" }, llmStream.LastRequest.AllowedToolNames);
        }

        [Test]
        public async Task RunTaskAsync_FiltersTools_ByAllowedToolNames()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[]
            {
                new StubTool("spawn_quiz"),
                new StubTool("spawn_drag_and_drop")
            });
            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = new[] { "spawn_drag_and_drop" }
            });

            Assert.AreEqual(1, llm.LastRequest.Tools.Count);
            Assert.AreEqual("spawn_drag_and_drop", llm.LastRequest.Tools[0].Name);
            CollectionAssert.AreEqual(new[] { "spawn_drag_and_drop" }, llm.LastRequest.AllowedToolNames);
        }

        private sealed class ContextFailThenOkLlm : ILlmClient
        {
            public int Calls { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                if (Calls == 1)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "context_length_exceeded",
                        ErrorCode = LlmErrorCode.ContextLengthExceeded
                    });
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "after-compact" });
            }
        }

        [Test]
        public async Task RunTaskAsync_RetriesOnce_OnContextLengthExceeded()
        {
            ContextFailThenOkLlm llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            for (int i = 0; i < 24; i++)
            {
                memory.FakeHistory.Add(new CoreAI.Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = new string('w', 80) + i
                });
            }

            policy.ConfigureChatHistory("role_ctx", enabled: true, tokens: 2048, persist: false,
                maxChatHistoryMessages: 50);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            string content = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "role_ctx", Hint = "Hi" });

            Assert.AreEqual(2, llm.Calls);
            Assert.AreEqual("after-compact", content);
        }

        [Test]
        public async Task RunTaskAsync_ChatOnly_SendsNoTools()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[] { new StubTool("spawn_quiz") });
            TestSettings settings = new();
            AiOrchestrator orchestrator = new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                ForcedToolMode = LlmToolChoiceMode.None
            });

            Assert.AreEqual(0, llm.LastRequest.Tools.Count);
        }

        [Test]
        public async Task RunTaskAsync_WithFileStore_AndPersistChatHistory_WritesDiskReadableByNewStore()
        {
            string roleId = "EditMode_OrchPersist_" + Guid.NewGuid().ToString("N");
            string dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
            string safeName = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            string filePath = Path.Combine(dir, $"{safeName}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            try
            {
                FileAgentMemoryStore store1 = new();
                AgentMemoryPolicy policy = new();
                policy.ConfigureChatHistory(roleId, enabled: true, tokens: 8192, persist: true, maxChatHistoryMessages: 50);
                policy.DisableMemoryTool(roleId);
                policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());

                TestLlmClient llm = new();
                TestSettings settings = new();
                AiOrchestrator orchestrator = new AiOrchestrator(
                    new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                    new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                    store1, policy, null, null, settings);

                await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = roleId, Hint = "persist hint" });

                CoreAI.Ai.ChatMessage[] h1 = store1.GetChatHistory(roleId);
                Assert.GreaterOrEqual(h1.Length, 2, "After a successful turn the store should contain user + assistant lines.");

                FileAgentMemoryStore store2 = new();
                CoreAI.Ai.ChatMessage[] h2 = store2.GetChatHistory(roleId);
                Assert.AreEqual(h1.Length, h2.Length, "A new FileAgentMemoryStore should reload the same persisted chat from disk.");
                Assert.AreEqual(h1[^1].Content, h2[^1].Content);
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
    }
}
