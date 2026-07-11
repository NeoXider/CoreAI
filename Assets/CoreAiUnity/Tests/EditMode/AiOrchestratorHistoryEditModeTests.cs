using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Newtonsoft.Json.Linq;
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
            public List<LlmCompletionRequest> Requests { get; } = new();

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                Requests.Add(request);
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
            public AgentMemoryState MemoryState { get; set; }
            public int SaveCount { get; private set; }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = MemoryState;
                return state != null;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                SaveCount++;
                MemoryState = state;
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

        [Test]
        public async Task RunTaskAsync_PersistedAssistantHistory_StripsThinkReasoning()
        {
            // FINDING-7: hidden reasoning (observed up to ~16k chars) was persisted verbatim into
            // conversation history; the chat panel clamp is visual only. Persisting must strip it.
            string reasoning = "<think>" + new string('r', 16000) + "</think>";
            TestMemoryStore memory = new();
            ToolTraceLlmClient llm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = reasoning + "Visible answer"
            });
            AgentMemoryPolicy policy = BuildToolResultPolicy("think_role");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            string result = await orchestrator.RunTaskAsync(new AiTaskRequest
                { RoleId = "think_role", Hint = "hi" });

            StringAssert.Contains("Visible answer", result);
            Assert.AreEqual("assistant", memory.Appended[1].Role);
            Assert.AreEqual("Visible answer", memory.Appended[1].Content,
                "Persisted assistant history must contain only the visible answer.");
            Assert.That(memory.Appended[1].Content, Does.Not.Contain("<think>"));
        }

        [Test]
        public void StripReasoningForHistory_HandlesThinkBlockShapes()
        {
            Assert.AreEqual("Visible",
                AiOrchestrator.StripReasoningForHistory("<think>hidden</think>Visible"));
            Assert.AreEqual("answer",
                AiOrchestrator.StripReasoningForHistory("orphan hidden</think>answer"),
                "Orphan close tag: leading text is hidden reasoning.");
            Assert.AreEqual("",
                AiOrchestrator.StripReasoningForHistory("<think>" + new string('x', 16000)),
                "Unterminated reasoning blob must not be persisted.");

            string plain = "No reasoning here.";
            Assert.AreSame(plain, AiOrchestrator.StripReasoningForHistory(plain),
                "Content without think markers is returned unchanged (no allocation).");
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
        public async Task RunTaskAsync_CompactsOldHistory_IntoTailSummary()
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
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Assert.AreEqual(ChatRole.System, llm.LastRequest.ChatHistory[0].Role);
            StringAssert.Contains("## Conversation Summary", llm.LastRequest.ChatHistory[0].Text);
            StringAssert.Contains("old-context-0", llm.LastRequest.ChatHistory[0].Text);
            StringAssert.Contains("old-context-9", llm.LastRequest.ChatHistory[^1].Text);
        }

        [Test]
        public async Task RunTaskAsync_Compaction_AddsSummaryAsFirstTailMessage()
        {
            TestLlmClient tailLlm = new();
            await RunSummaryPlacementRequestAsync(tailLlm);

            Assert.IsNotNull(tailLlm.LastRequest);
            Assert.IsFalse(
                tailLlm.LastRequest.SystemPrompt.Contains("## Conversation Summary"),
                "Volatile summary should stay out of the stable system prefix.");
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
        public async Task RunTaskAsync_RuntimeContext_MovesWorldStateToLastTailMessage()
        {
            TestLlmClient tailLlm = new();
            await RunWorldStatePlacementRequestAsync(tailLlm);

            Assert.IsNotNull(tailLlm.LastRequest);
            Assert.IsFalse(
                tailLlm.LastRequest.SystemPrompt.Contains("CURRENT SLIDE: 3"),
                "Live world-state should stay out of the stable system prefix.");
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
        public async Task RunTaskAsync_Memory_UsesSnapshotAndTailUpdates()
        {
            TestLlmClient tailLlm = new();
            TestMemoryStore tailMemory = await RunMemoryPlacementRequestAsync(tailLlm);

            Assert.IsNotNull(tailLlm.LastRequest);
            StringAssert.Contains("## Memory", tailLlm.LastRequest.SystemPrompt);
            StringAssert.Contains("Learner likes geometry puzzles.", tailLlm.LastRequest.SystemPrompt);
            Assert.IsNull(tailLlm.LastRequest.ChatHistory);
            Assert.AreEqual("Learner likes geometry puzzles.", tailMemory.MemoryState.SystemPromptMemorySnapshot);

            TestLlmClient updateLlm = new();
            await RunMemoryPlacementRequestAsync(
                updateLlm,
                "Learner likes geometry puzzles.\nLearner prefers hints.",
                "Learner likes geometry puzzles.");

            Assert.IsNotNull(updateLlm.LastRequest);
            StringAssert.Contains("Learner likes geometry puzzles.", updateLlm.LastRequest.SystemPrompt);
            Assert.IsFalse(
                updateLlm.LastRequest.SystemPrompt.Contains("Learner prefers hints."),
                "Pending memory updates should not rewrite the cached system prefix before a boundary.");
            Assert.IsNotNull(updateLlm.LastRequest.ChatHistory);
            Assert.AreEqual(1, updateLlm.LastRequest.ChatHistory.Count);
            Microsoft.Extensions.AI.ChatMessage memoryUpdates = updateLlm.LastRequest.ChatHistory[0];
            Assert.AreEqual(ChatRole.System, memoryUpdates.Role);
            StringAssert.Contains("## Memory (updates)", memoryUpdates.Text);
            StringAssert.Contains("Learner prefers hints.", memoryUpdates.Text);
        }

        [Test]
        public async Task RunTaskAsync_Compaction_ConsolidatesMemoryUpdatesIntoSystemPrefix()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new()
            {
                MemoryState = new AgentMemoryState
                {
                    Memory = "Learner likes geometry puzzles.\nLearner prefers hints.",
                    SystemPromptMemorySnapshot = "Learner likes geometry puzzles.",
                    SystemPromptMemoryVersion = 1
                }
            };
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("Teacher", true, 60, false, 50);
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"old-context-{i}-".PadRight(90, 'x')
                });
            }

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            StringAssert.Contains("Learner likes geometry puzzles.", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("Learner prefers hints.", llm.LastRequest.SystemPrompt);
            Assert.AreEqual(memory.MemoryState.Memory, memory.MemoryState.SystemPromptMemorySnapshot);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("## Memory (updates)")));
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("## Conversation Summary")));
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetry_ConsolidatesMemoryUpdatesIntoSystemPrefix()
        {
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = "context too long"
                },
                new LlmCompletionResult { Ok = true, Content = "ok" });
            TestMemoryStore memory = new()
            {
                MemoryState = new AgentMemoryState
                {
                    Memory = "Learner likes geometry puzzles.\nLearner prefers hints.",
                    SystemPromptMemorySnapshot = "Learner likes geometry puzzles.",
                    SystemPromptMemoryVersion = 1
                }
            };
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("Teacher", true, 4096, false, 50);

            TestSettings settings = new()
            {
                MaxContextOverflowRetries = 1
            };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "retry test" });

            Assert.GreaterOrEqual(llm.Requests.Count, 2);
            LlmCompletionRequest first = llm.Requests[0];
            LlmCompletionRequest second = llm.Requests[1];

            StringAssert.Contains("Learner likes geometry puzzles.", first.SystemPrompt);
            StringAssert.DoesNotContain("Learner prefers hints.", first.SystemPrompt);
            Assert.IsTrue(first.ChatHistory.Any(m => (m.Text ?? "").Contains("## Memory (updates)")));

            StringAssert.Contains("Learner likes geometry puzzles.", second.SystemPrompt);
            StringAssert.Contains("Learner prefers hints.", second.SystemPrompt);
            Assert.IsFalse(second.ChatHistory != null &&
                           second.ChatHistory.Any(m => (m.Text ?? "").Contains("## Memory (updates)")));
            Assert.AreEqual(memory.MemoryState.Memory, memory.MemoryState.SystemPromptMemorySnapshot);
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

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetry_WhenSummarizationDisabled_KeepsFullChatTail()
        {
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = "context too long"
                },
                new LlmCompletionResult { Ok = true, Content = "ok" });
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"old-context-{i}-".PadRight(90, 'x')
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 60, false, 50);
            TestSettings settings = new()
            {
                EnableConversationHistorySummarization = false,
                MaxContextOverflowRetries = 1
            };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "retry" });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual(10, llm.Requests[0].ChatHistory.Count);
            Assert.AreEqual(10, llm.Requests[1].ChatHistory.Count);
            Assert.IsFalse(llm.Requests[1].ChatHistory.Any(m =>
                (m.Text ?? "").Contains("## Conversation Summary")));
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetriesFail_DoesNotPersistAttemptSummaries()
        {
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = "context too long"
                },
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = "still too long"
                });
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"old-context-{i}-".PadRight(90, 'x')
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 60, false, 50);
            TestSettings settings = new() { MaxContextOverflowRetries = 1 };
            InMemoryConversationSummaryStore summaryStore = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(summaryStore));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "retry" });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual("", summaryStore.LoadSummary("test_role"));
        }

        [Test]
        public void ConversationRolledSummaryDefault_IsBounded()
        {
            Assert.Greater(ICoreAISettings.DefaultConversationRolledSummaryMaxTokens, 0);
        }

        private static async Task RunSummaryPlacementRequestAsync(TestLlmClient llm)
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

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });
        }

        private static async Task RunWorldStatePlacementRequestAsync(TestLlmClient llm)
        {
            AgentMemoryPolicy policy = new();
            policy.SetRuntimeContextProvider("Teacher", new SlideRuntimeContextProvider());
            TestSettings settings = new();
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

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "budget test" });
        }

        private static async Task<TestMemoryStore> RunMemoryPlacementRequestAsync(
            TestLlmClient llm,
            string memoryText = "Learner likes geometry puzzles.",
            string cachedSnapshot = "")
        {
            TestMemoryStore store = new()
            {
                MemoryState = new AgentMemoryState
                {
                    Memory = memoryText,
                    SystemPromptMemorySnapshot = cachedSnapshot
                }
            };
            AgentMemoryPolicy policy = new();
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                store, policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "memory?" });
            return store;
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
            Assert.AreEqual(2, llm.LastRequest.ChatHistory.Count);
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Assert.AreEqual(ChatRole.System, llm.LastRequest.ChatHistory[0].Role);
            StringAssert.Contains("## Conversation Summary", llm.LastRequest.ChatHistory[0].Text);
            StringAssert.Contains("old-context-0", llm.LastRequest.ChatHistory[0].Text);
            StringAssert.Contains("old-context-9", llm.LastRequest.ChatHistory[^1].Text);
        }

        [Test]
        public async Task RunTaskAsync_AppendsRuntimePromptContextToTail()
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

            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Runtime Context"));
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage worldState = llm.LastRequest.ChatHistory[^1];
            Assert.AreEqual(ChatRole.System, worldState.Role);
            StringAssert.Contains("## World State", worldState.Text);
            StringAssert.Contains("## Runtime Context", worldState.Text);
            StringAssert.Contains("slot=practice-slot", worldState.Text);
            StringAssert.Contains("trace=trace-context", worldState.Text);
        }

        [Test]
        public async Task RunTaskAsync_AppendsPerRoleRuntimeContextToTail()
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

            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("role-context=Teacher"));
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage worldState = llm.LastRequest.ChatHistory[^1];
            Assert.AreEqual(ChatRole.System, worldState.Role);
            StringAssert.Contains("## World State", worldState.Text);
            StringAssert.Contains("role-context=Teacher", worldState.Text);
            StringAssert.Contains("slot=theory", worldState.Text);
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

        [Test]
        public async Task RunTaskAsync_AllowedSkillToolNames_AttachesRestrictedMetaTools()
        {
            TestLlmClient llm = new();
            AgentMemoryPolicy policy = new();
            string called = "";
            SkillSet skill = new(
                "Crafting",
                "Crafting tools",
                "Use only the allowed crafting function.",
                new DelegateLlmTool("allowed_skill_tool", "Allowed skill tool",
                    new Func<string, string>(value =>
                    {
                        called = value;
                        return "{\"success\":true,\"value\":\"" + value + "\"}";
                    })),
                new DelegateLlmTool("blocked_skill_tool", "Blocked skill tool",
                    new Func<string>(() => "{\"success\":true}")));

            AgentConfig config = new AgentBuilder("Teacher")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(skill)
                .Build();
            config.ApplyToPolicy(policy);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = new[] { "allowed_skill_tool" }
            });

            Assert.AreEqual(2, llm.LastRequest.Tools.Count);
            CollectionAssert.AreEquivalent(
                new[] { "read_skill", "call_skill_tool" },
                llm.LastRequest.Tools.Select(t => t.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "allowed_skill_tool" }, llm.LastRequest.AllowedToolNames);

            ILlmTool readSkill = llm.LastRequest.Tools.First(t => t.Name == "read_skill");
            AIFunction readFn = ((IAIFunctionLlmTool)readSkill).CreateAIFunction();
            string readJson = (await readFn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object> { ["skill_name"] = "Crafting" }),
                CancellationToken.None))?.ToString();
            Assert.That(readJson, Does.Contain("allowed_skill_tool"));
            Assert.That(readJson, Does.Not.Contain("blocked_skill_tool"));

            ILlmTool callSkill = llm.LastRequest.Tools.First(t => t.Name == "call_skill_tool");
            AIFunction callFn = ((IAIFunctionLlmTool)callSkill).CreateAIFunction();
            string okJson = (await callFn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tool_name"] = "allowed_skill_tool",
                    ["arguments_json"] = "{\"value\":\"ok\"}"
                }),
                CancellationToken.None))?.ToString();
            Assert.That(okJson, Does.Contain("ok"));
            Assert.AreEqual("ok", called);

            string blockedJson = (await callFn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tool_name"] = "blocked_skill_tool",
                    ["arguments_json"] = "{}"
                }),
                CancellationToken.None))?.ToString();
            Assert.IsFalse(JObject.Parse(blockedJson).Value<bool>("success"));
            Assert.That(blockedJson, Does.Contain("not found"));
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

        private sealed class StreamingFailsThenOkLlm : ILlmClient
        {
            private readonly int _failuresBeforeSuccess;

            public StreamingFailsThenOkLlm(int failuresBeforeSuccess)
            {
                _failuresBeforeSuccess = failuresBeforeSuccess;
            }

            public List<LlmCompletionRequest> Requests { get; } = new();
            public int Calls => Requests.Count;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Assert.Fail("Streaming retry test must use CompleteStreamingAsync.");
                return Task.FromResult<LlmCompletionResult>(null);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                await Task.Yield();
                if (Calls <= _failuresBeforeSuccess)
                {
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "context overflow",
                        ErrorCode = LlmErrorCode.ContextLengthExceeded
                    };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "after-compact" };
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    PromptTokens = 123,
                    TotalTokens = 130
                };
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
        public async Task RunStreamingAsync_RetriesTwice_OnContextLengthExceeded()
        {
            StreamingFailsThenOkLlm llm = new(2);
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            RecordingBudgetPolicy budgetPolicy = new();
            for (int i = 0; i < 24; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = new string('s', 80) + i
                });
            }

            policy.ConfigureChatHistory("role_ctx", true, 2048, false, 50);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()),
                contextBudgetPolicy: budgetPolicy);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "role_ctx", Hint = "Hi" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(3, llm.Calls);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, budgetPolicy.RetryLevels);
            Assert.AreEqual("after-compact", string.Concat(chunks.ConvertAll(static c => c.Text ?? "")));
            Assert.IsFalse(chunks.Exists(static c => c.ErrorCode == LlmErrorCode.ContextLengthExceeded),
                "Retryable overflow chunks must not leak to the caller before the successful retry.");
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
