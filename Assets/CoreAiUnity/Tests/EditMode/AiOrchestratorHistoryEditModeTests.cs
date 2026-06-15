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

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "Hello" });
            }
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

        private sealed class ToolTraceLlmClient : ILlmClient
        {
            private readonly Queue<LlmCompletionResult> _results;

            public ToolTraceLlmClient(params LlmCompletionResult[] results)
            {
                _results = new Queue<LlmCompletionResult>(results ?? Array.Empty<LlmCompletionResult>());
            }

            public LlmCompletionRequest LastRequest { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                if (_results.Count > 0)
                {
                    return Task.FromResult(_results.Dequeue());
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "Next" });
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public List<Ai.ChatMessage> FakeHistory { get; set; } = new();
            public List<(string Role, string Content, bool Persist)> Appended { get; } = new();

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
                Appended.Add((role, content, persistToDisk));
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (maxMessages > 0 && FakeHistory.Count > maxMessages)
                {
                    int skip = FakeHistory.Count - maxMessages;
                    return FakeHistory.ToArray()[skip..];
                }

                return FakeHistory.ToArray();
            }
        }

        [Test]
        public async Task RunTaskAsync_ChatSource_EnablesShortTermHistory_ForProgrammer()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage
            {
                Role = "user",
                Content = "{\"hint\":\"отвечай на русском\",\"ai_task_source\":\"Chat\"}"
            });
            memory.FakeHistory.Add(new Ai.ChatMessage
            {
                Role = "assistant",
                Content = "Понял, буду отвечать на русском языке."
            });

            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "какие моды есть",
                SourceTag = "Chat"
            });

            Assert.IsFalse(policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer).WithChatHistory,
                "Chat source should not mutate the global Programmer role policy.");
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(2, llm.LastRequest.ChatHistory.Count);
            StringAssert.Contains("отвечай на русском", llm.LastRequest.ChatHistory[0].Text);
            Assert.AreEqual(2, memory.Appended.Count);
            Assert.IsFalse(memory.Appended[0].Persist,
                "Programmer chat history is session context unless the role explicitly enables persistence.");
            Assert.IsFalse(memory.Appended[1].Persist);
        }

        [Test]
        public async Task RunTaskAsync_NonChatSource_KeepsProgrammerHistoryDisabled()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage
            {
                Role = "user",
                Content = "prior chat"
            });

            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "run lua"
            });

            Assert.IsNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(0, memory.Appended.Count);
        }

        [Test]
        public async Task RunTaskAsync_ToolOnlyWhitespaceResult_ReturnsToolFailureInsteadOfEmptyValidation()
        {
            ToolOnlyWhitespaceLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, new CompositeRoleStructuredResponsePolicy(), null, settings);

            string result = await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "сделай награду за босса",
                SourceTag = "Chat"
            });

            StringAssert.Contains("Tool call failed: manage_mods", result);
            StringAssert.Contains("attempt to index a function value", result);
            Assert.That(result, Does.Not.Contain("structured validation failed"));
        }

        [Test]
        public async Task RunStreamingAsync_ToolOnlyWhitespaceResult_StreamsToolFailureInsteadOfEmptyValidation()
        {
            ToolOnlyWhitespaceLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, new CompositeRoleStructuredResponsePolicy(), null, settings);

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
        }

        [Test]
        public async Task RunTaskAsync_PersistsToolResults_ByPolicy_AndReplaysAsUserHistory()
        {
            LlmToolCallTrace[] traces =
            {
                new("lookup_inventory", true, 3d, "native", "{\"message\":\"found sword\"}"),
                new("lookup_inventory", true, 4d, "native", "{\"message\":\"found sword\"}"),
                new("write_memory", false, 5d, "native", "{\"error\":\"permission denied\"}")
            };

            TestMemoryStore compactMemory = new();
            ToolTraceLlmClient compactLlm = new(
                new LlmCompletionResult
                {
                    Ok = true,
                    Content = "Done",
                    ExecutedToolCalls = traces
                },
                new LlmCompletionResult { Ok = true, Content = "Next" });
            AgentMemoryPolicy compactPolicy = BuildToolResultPolicy("tool_role");
            AiOrchestrator compactOrchestrator = BuildOrchestrator(compactLlm, compactMemory, compactPolicy);

            await compactOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "tool_role", Hint = "tools" });

            Assert.AreEqual(3, compactMemory.Appended.Count);
            Assert.AreEqual("tool", compactMemory.Appended[2].Role);
            string compactToolMessage = compactMemory.Appended[2].Content;
            StringAssert.Contains("## Tool Results", compactToolMessage);
            StringAssert.Contains("lookup_inventory", compactToolMessage);
            StringAssert.Contains("write_memory", compactToolMessage);
            Assert.AreEqual(1, CountOccurrences(compactToolMessage, "lookup_inventory"),
                "Duplicate tool traces in one turn should be collapsed.");

            foreach ((string role, string content, bool _) in compactMemory.Appended)
            {
                compactMemory.FakeHistory.Add(new Ai.ChatMessage { Role = role, Content = content });
            }

            await compactOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "tool_role", Hint = "next" });

            Assert.IsNotNull(compactLlm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage replayedToolMessage = null;
            foreach (Microsoft.Extensions.AI.ChatMessage message in compactLlm.LastRequest.ChatHistory)
            {
                if ((message.Text ?? "").Contains("## Tool Results"))
                {
                    replayedToolMessage = message;
                    break;
                }
            }

            Assert.IsNotNull(replayedToolMessage);
            Assert.AreEqual(ChatRole.User, replayedToolMessage.Role,
                "Stored tool results must replay as provider-safe user observations.");

            TestMemoryStore errorsOnlyMemory = new();
            ToolTraceLlmClient errorsOnlyLlm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = "Done",
                ExecutedToolCalls = traces
            });
            AgentMemoryPolicy errorsOnlyPolicy = BuildToolResultPolicy("tool_role");
            errorsOnlyPolicy.SetToolResultMemoryPolicy("tool_role", ToolResultMemoryPolicy.ErrorsOnly);
            AiOrchestrator errorsOnlyOrchestrator =
                BuildOrchestrator(errorsOnlyLlm, errorsOnlyMemory, errorsOnlyPolicy);

            await errorsOnlyOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "tool_role", Hint = "tools" });

            Assert.AreEqual(3, errorsOnlyMemory.Appended.Count);
            string errorsOnlyToolMessage = errorsOnlyMemory.Appended[2].Content;
            StringAssert.Contains("write_memory", errorsOnlyToolMessage);
            StringAssert.DoesNotContain("lookup_inventory", errorsOnlyToolMessage);

            TestMemoryStore noneMemory = new();
            ToolTraceLlmClient noneLlm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = "Done",
                ExecutedToolCalls = traces
            });
            AgentMemoryPolicy nonePolicy = BuildToolResultPolicy("tool_role");
            nonePolicy.SetToolResultMemoryPolicy("tool_role", ToolResultMemoryPolicy.None);
            AiOrchestrator noneOrchestrator = BuildOrchestrator(noneLlm, noneMemory, nonePolicy);

            await noneOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "tool_role", Hint = "tools" });

            Assert.AreEqual(2, noneMemory.Appended.Count);
            Assert.IsFalse(noneMemory.Appended.Exists(m => m.Role == "tool"));
        }

        [Test]
        public async Task RunTaskAsync_FullToolResultMemory_PersistsCompleteOutput_CompactKeepsSummary()
        {
            string completeOutput = "summary " + new string('a', 260) +
                                    "\nFULL_SENTINEL: exact tool output line";
            LlmToolCallTrace[] traces =
            {
                new("inspect_scene", true, 7d, "native", completeOutput)
            };

            TestMemoryStore fullMemory = new();
            ToolTraceLlmClient fullLlm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = "Done",
                ExecutedToolCalls = traces
            });
            AgentMemoryPolicy fullPolicy = BuildToolResultPolicy("full_tool_role");
            fullPolicy.SetToolResultMemoryPolicy("full_tool_role", ToolResultMemoryPolicy.Full);
            AiOrchestrator fullOrchestrator = BuildOrchestrator(fullLlm, fullMemory, fullPolicy);

            await fullOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "full_tool_role", Hint = "tools" });

            string fullToolMessage = fullMemory.Appended[2].Content;
            StringAssert.Contains("## Tool Results", fullToolMessage);
            StringAssert.Contains("Detail:", fullToolMessage);
            StringAssert.Contains("FULL_SENTINEL: exact tool output line", fullToolMessage);

            TestMemoryStore compactMemory = new();
            ToolTraceLlmClient compactLlm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = "Done",
                ExecutedToolCalls = traces
            });
            AgentMemoryPolicy compactPolicy = BuildToolResultPolicy("compact_tool_role");
            AiOrchestrator compactOrchestrator = BuildOrchestrator(compactLlm, compactMemory, compactPolicy);

            await compactOrchestrator.RunTaskAsync(new AiTaskRequest
                { RoleId = "compact_tool_role", Hint = "tools" });

            string compactToolMessage = compactMemory.Appended[2].Content;
            StringAssert.Contains("## Tool Results", compactToolMessage);
            StringAssert.DoesNotContain("Detail:", compactToolMessage);
            StringAssert.DoesNotContain("FULL_SENTINEL: exact tool output line", compactToolMessage);
            Assert.AreEqual(2, compactToolMessage.Split('\n').Length,
                "CompactSummary should persist the heading plus one compact line for one tool call.");
        }

        private static AgentMemoryPolicy BuildToolResultPolicy(string roleId)
        {
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(roleId, true, 8192, false, 10);
            policy.DisableMemoryTool(roleId);
            policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            TestMemoryStore memory,
            AgentMemoryPolicy policy)
        {
            TestSettings settings = new();
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);
        }

        private static int CountOccurrences(string value, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
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
            public int MaxContextOverflowRetries { get; set; } = 3;
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
            public bool PlaceLiveContextInTail { get; set; }
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

        private sealed class SlideRuntimeContextProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return "CURRENT SLIDE: 3";
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

        private sealed class RecordingBudgetPolicy : IContextBudgetPolicy
        {
            private readonly DefaultContextBudgetPolicy _inner = new();

            public List<int> RetryLevels { get; } = new();

            public ContextBudget Compute(ContextBudgetRequest request, ITokenEstimator estimator)
            {
                RetryLevels.Add(request.ContextRetryLevel);
                return _inner.Compute(request, estimator);
            }
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
                memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = $"Short msg {i}" });
            }

            // Настраиваем агента с лимитом в 15 сообщений
            policy.ConfigureChatHistory("test_role", true, 8192, false, 15);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings);

            // Act
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "Hi" });

            // Assert
            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(15, llm.LastRequest.ChatHistory.Count,
                "History should be truncated to exactly MaxChatHistoryMessages");

            // Check that we got the *most recent* 15
            Assert.IsTrue(llm.LastRequest.ChatHistory[14].Text.Contains("Short msg 49"),
                "Last message should match the latest");
            Assert.IsTrue(llm.LastRequest.ChatHistory[0].Text.Contains("Short msg 35"),
                "First message in truncated history should match sequence");
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
            policy.ConfigureChatHistory("test_role", true, 300, false, 50);
            policy.DisableMemoryTool("test_role");
            policy.SetToolsForRole("test_role", Array.Empty<ILlmTool>());
            for (int i = 0; i < 20; i++)
            {
                string content = "A".PadRight(100, 'A') + i; // 100 chars + number
                memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = content });
            }

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
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
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 60, false, 50);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
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
        public async Task RunTaskAsync_PlaceLiveContextInTail_TogglesSummaryPlacement()
        {
            TestLlmClient legacyLlm = new();
            await RunSummaryPlacementRequestAsync(legacyLlm, placeLiveContextInTail: false);

            Assert.IsNotNull(legacyLlm.LastRequest);
            StringAssert.Contains("## Conversation Summary", legacyLlm.LastRequest.SystemPrompt);
            Assert.IsNotNull(legacyLlm.LastRequest.ChatHistory);
            Assert.AreNotEqual(
                ChatRole.System,
                legacyLlm.LastRequest.ChatHistory[^1].Role,
                "Legacy mode must not append a trailing system chat-history message.");

            TestLlmClient tailLlm = new();
            await RunSummaryPlacementRequestAsync(tailLlm, placeLiveContextInTail: true);

            Assert.IsNotNull(tailLlm.LastRequest);
            Assert.IsFalse(
                tailLlm.LastRequest.SystemPrompt.Contains("## Conversation Summary"),
                "Tail mode should keep volatile summary out of the stable system prefix.");
            Assert.IsNotNull(tailLlm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage summaryMessage = tailLlm.LastRequest.ChatHistory[0];
            Assert.AreEqual(ChatRole.System, summaryMessage.Role);
            StringAssert.Contains("## Conversation Summary", summaryMessage.Text);
            StringAssert.Contains("old-context-0", summaryMessage.Text);
            Assert.Greater(tailLlm.LastRequest.ChatHistory.Count, 1);
            Assert.AreNotEqual(ChatRole.System, tailLlm.LastRequest.ChatHistory[1].Role);
            StringAssert.Contains("old-context-9", tailLlm.LastRequest.ChatHistory[^1].Text);
        }

        [Test]
        public async Task RunTaskAsync_PlaceLiveContextInTail_MovesWorldStateToLastTailMessage()
        {
            TestLlmClient legacyLlm = new();
            await RunWorldStatePlacementRequestAsync(legacyLlm, placeLiveContextInTail: false);

            Assert.IsNotNull(legacyLlm.LastRequest);
            StringAssert.Contains("CURRENT SLIDE: 3", legacyLlm.LastRequest.SystemPrompt);
            Assert.IsNull(legacyLlm.LastRequest.ChatHistory);

            TestLlmClient tailLlm = new();
            await RunWorldStatePlacementRequestAsync(tailLlm, placeLiveContextInTail: true);

            Assert.IsNotNull(tailLlm.LastRequest);
            Assert.IsFalse(
                tailLlm.LastRequest.SystemPrompt.Contains("CURRENT SLIDE: 3"),
                "Tail mode should keep live world-state out of the stable system prefix.");
            Assert.IsNotNull(tailLlm.LastRequest.ChatHistory);
            Assert.AreEqual(1, tailLlm.LastRequest.ChatHistory.Count);
            Microsoft.Extensions.AI.ChatMessage worldState = tailLlm.LastRequest.ChatHistory[^1];
            Assert.AreEqual(ChatRole.System, worldState.Role);
            StringAssert.Contains("## World State", worldState.Text);
            StringAssert.Contains("CURRENT SLIDE: 3", worldState.Text);

            TestLlmClient summaryTailLlm = new();
            await RunSummaryAndWorldStatePlacementRequestAsync(summaryTailLlm);

            Assert.IsNotNull(summaryTailLlm.LastRequest);
            Assert.IsFalse(summaryTailLlm.LastRequest.SystemPrompt.Contains("CURRENT SLIDE: 3"));
            Assert.IsNotNull(summaryTailLlm.LastRequest.ChatHistory);
            Assert.GreaterOrEqual(summaryTailLlm.LastRequest.ChatHistory.Count, 3);
            Microsoft.Extensions.AI.ChatMessage summary = summaryTailLlm.LastRequest.ChatHistory[0];
            Assert.AreEqual(ChatRole.System, summary.Role);
            StringAssert.Contains("## Conversation Summary", summary.Text);
            Microsoft.Extensions.AI.ChatMessage last = summaryTailLlm.LastRequest.ChatHistory[^1];
            Assert.AreEqual(ChatRole.System, last.Role);
            StringAssert.Contains("## World State", last.Text);
            StringAssert.Contains("CURRENT SLIDE: 3", last.Text);
            Assert.AreNotEqual(ChatRole.System, summaryTailLlm.LastRequest.ChatHistory[1].Role);
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
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 60, false, 50);

            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
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

        private static async Task RunSummaryPlacementRequestAsync(TestLlmClient llm, bool placeLiveContextInTail)
        {
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();

            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(90, 'x');
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 60, false, 50);

            TestSettings settings = new() { PlaceLiveContextInTail = placeLiveContextInTail };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });
        }

        private static async Task RunWorldStatePlacementRequestAsync(
            TestLlmClient llm,
            bool placeLiveContextInTail)
        {
            AgentMemoryPolicy policy = new();
            policy.SetRuntimeContextProvider("Teacher", new SlideRuntimeContextProvider());
            TestSettings settings = new() { PlaceLiveContextInTail = placeLiveContextInTail };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "slide?" });
        }

        private static async Task RunSummaryAndWorldStatePlacementRequestAsync(TestLlmClient llm)
        {
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            policy.SetRuntimeContextProvider("Teacher", new SlideRuntimeContextProvider());

            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(90, 'x');
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("Teacher", true, 60, false, 50);

            TestSettings settings = new() { PlaceLiveContextInTail = true };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "budget test" });
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
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 60, false, 50);

            TestSettings settings = new() { ConversationHistoryRecentTokenBudgetOverride = 32 };
            AiOrchestrator orchestrator = new(
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
            AiOrchestrator orchestrator = new(
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
            AiOrchestrator orchestrator = new(
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
            AiOrchestrator orchestrator = new(
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
            AiTaskRequest task = new()
            {
                RoleId = "Teacher",
                Hint = "hi",
                AllowedToolNames = new[] { "spawn_drag_and_drop" },
                ForcedToolMode = LlmToolChoiceMode.RequireAny
            };

            TestSettings settings = new();
            AiPromptComposer composer = new(new NullSys(), new NullUsr(), null, null, policy, settings);
            AiOrchestrator orchestratorSync = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);
            await orchestratorSync.RunTaskAsync(task);

            int syncToolCount = llm.LastRequest.Tools.Count;
            string syncFirstTool = llm.LastRequest.Tools.Count > 0 ? llm.LastRequest.Tools[0].Name : "";
            LlmToolChoiceMode syncMode = llm.LastRequest.ForcedToolMode;
            CollectionAssert.AreEqual(new[] { "spawn_drag_and_drop" }, llm.LastRequest.AllowedToolNames);

            TestLlmClient llmStream = new();
            AiOrchestrator orchestratorStream = new(
                new TestAuthority(), llmStream, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings);

            await foreach (LlmStreamChunk _ in orchestratorStream.RunStreamingAsync(task, default))
            {
            }

            Assert.AreEqual(syncToolCount, llmStream.LastRequest.Tools.Count,
                "Streaming path must attach the same tool count as non-streaming.");
            Assert.AreEqual(syncFirstTool,
                llmStream.LastRequest.Tools.Count > 0 ? llmStream.LastRequest.Tools[0].Name : "");
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
            AiOrchestrator orchestrator = new(
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

        private sealed class FailsThenOkLlm : ILlmClient
        {
            private readonly int _failuresBeforeSuccess;
            private readonly LlmErrorCode _failureCode;

            public FailsThenOkLlm(int failuresBeforeSuccess, LlmErrorCode failureCode)
            {
                _failuresBeforeSuccess = failuresBeforeSuccess;
                _failureCode = failureCode;
            }

            public List<LlmCompletionRequest> Requests { get; } = new();
            public int Calls => Requests.Count;

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (Calls <= _failuresBeforeSuccess)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = false,
                        Error = _failureCode.ToString(),
                        ErrorCode = _failureCode
                    });
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "after-compact" });
            }
        }

        [Test]
        public async Task RunTaskAsync_RetriesTwice_OnContextLengthExceeded()
        {
            FailsThenOkLlm llm = new(2, LlmErrorCode.ContextLengthExceeded);
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            RecordingBudgetPolicy budgetPolicy = new();
            for (int i = 0; i < 24; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = new string('w', 80) + i
                });
            }

            policy.ConfigureChatHistory("role_ctx", true, 2048, false,
                50);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()),
                contextBudgetPolicy: budgetPolicy);

            string content = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "role_ctx", Hint = "Hi" });

            Assert.AreEqual(3, llm.Calls);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, budgetPolicy.RetryLevels);
            Assert.AreEqual("after-compact", content);
        }

        [Test]
        public async Task RunTaskAsync_GivesUpAfterMaxContextOverflowRetries()
        {
            FailsThenOkLlm llm = new(2, LlmErrorCode.ContextLengthExceeded);
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            RecordingBudgetPolicy budgetPolicy = new();
            policy.ConfigureChatHistory("role_ctx", true, 2048, false, 50);
            TestSettings settings = new() { MaxContextOverflowRetries = 1 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()),
                contextBudgetPolicy: budgetPolicy);

            string content = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "role_ctx", Hint = "Hi" });

            Assert.AreEqual(2, llm.Calls);
            CollectionAssert.AreEqual(new[] { 0, 1 }, budgetPolicy.RetryLevels);
            Assert.IsNull(content);
        }

        [Test]
        public async Task RunTaskAsync_DoesNotRetry_NonOverflowFailure()
        {
            FailsThenOkLlm llm = new(1, LlmErrorCode.ProviderError);
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            RecordingBudgetPolicy budgetPolicy = new();
            policy.ConfigureChatHistory("role_ctx", true, 2048, false, 50);
            TestSettings settings = new() { MaxContextOverflowRetries = 3 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()),
                contextBudgetPolicy: budgetPolicy);

            string content = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "role_ctx", Hint = "Hi" });

            Assert.AreEqual(1, llm.Calls);
            CollectionAssert.AreEqual(new[] { 0 }, budgetPolicy.RetryLevels);
            Assert.IsNull(content);
        }

        [Test]
        public async Task RunTaskAsync_ChatOnly_SendsNoTools()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[] { new StubTool("spawn_quiz") });
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
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
                policy.ConfigureChatHistory(roleId, true, 8192, true, 50);
                policy.DisableMemoryTool(roleId);
                policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());

                TestLlmClient llm = new();
                TestSettings settings = new();
                AiOrchestrator orchestrator = new(
                    new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                    new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                    store1, policy, null, null, settings);

                await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = roleId, Hint = "persist hint" });

                Ai.ChatMessage[] h1 = store1.GetChatHistory(roleId);
                Assert.GreaterOrEqual(h1.Length, 2,
                    "After a successful turn the store should contain user + assistant lines.");

                FileAgentMemoryStore store2 = new();
                Ai.ChatMessage[] h2 = store2.GetChatHistory(roleId);
                Assert.AreEqual(h1.Length, h2.Length,
                    "A new FileAgentMemoryStore should reload the same persisted chat from disk.");
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
