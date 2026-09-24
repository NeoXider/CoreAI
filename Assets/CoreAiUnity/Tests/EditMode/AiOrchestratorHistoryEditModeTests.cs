using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class AiOrchestratorHistoryEditModeTests
    {
        internal static IActorIdentityProvider TestActorIdentityProvider =>
            new LocalActorIdentityProvider("orchestrator-history-test");

        internal sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks { get; set; } = true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        internal sealed class TestLlmClient : ILlmClient
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
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "какие моды есть",
                SourceTag = "Chat"
            });

            Assert.IsFalse(policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer).WithChatHistory,
                "Chat source should not mutate the global Programmer role policy.");
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            List<Microsoft.Extensions.AI.ChatMessage> transcript = llm.LastRequest.ChatHistory
                .Where(m => m.Role != ChatRole.System)
                .ToList();
            Assert.AreEqual(2, transcript.Count);
            StringAssert.Contains("отвечай на русском", transcript[0].Text);
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
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "run lua"
            });

            Assert.IsFalse(llm.LastRequest.ChatHistory?.Any(m => m.Role != ChatRole.System) ?? false,
                "A non-chat Programmer request must not inherit prior user/assistant turns; dynamic system tail is allowed.");
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
                new TestMemoryStore(), policy, new CompositeRoleStructuredResponsePolicy(), null, settings,
                TestActorIdentityProvider);

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
                new TestMemoryStore(), policy, new CompositeRoleStructuredResponsePolicy(), null, settings,
                TestActorIdentityProvider);

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
                if ((message.Text ?? "").Contains("lookup_inventory"))
                {
                    replayedToolMessage = message;
                    break;
                }
            }

            Assert.IsNotNull(replayedToolMessage);
            Assert.AreEqual(ChatRole.User, replayedToolMessage.Role,
                "Stored tool results must replay as provider-safe user observations.");
            StringAssert.Contains("tool_result name=lookup_inventory status=ok", replayedToolMessage.Text,
                "Tool results reach the model as machine records.");
            StringAssert.DoesNotContain("## Tool Results", replayedToolMessage.Text,
                "The markdown heading is what a model copies back into its own reply; it must not reach it.");

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

        /// <summary>
        /// Store double whose appends are visible to the next read, unlike <see cref="TestMemoryStore"/>.
        /// </summary>
        private sealed class LiveTestMemoryStore : IAgentMemoryStore
        {
            public List<Ai.ChatMessage> History { get; } = new();

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
                History.Clear();
            }

            public void ClearChatHistory(string roleId)
            {
                History.Clear();
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                Appended.Add((role, content, persistToDisk));
                History.Add(new Ai.ChatMessage { Role = role, Content = content });
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (maxMessages > 0 && History.Count > maxMessages)
                {
                    return History.ToArray()[(History.Count - maxMessages)..];
                }

                return History.ToArray();
            }
        }

        private sealed class ThrowOnUserMemoryStore : IAgentMemoryStore
        {
            public List<(string Role, string Content)> Appended { get; } = new();

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
                if (role == "user")
                {
                    throw new InvalidOperationException("user append failed");
                }

                Appended.Add((role, content));
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
            }
        }

        private sealed class ThrowAfterCommittedUserAppendMemoryStore : IAgentMemoryStore
        {
            private bool _throwNextUserAppend = true;

            public List<(string Role, string Content)> Appended { get; } = new();

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
                Appended.Add((role, content));
                if (role == "user" && _throwNextUserAppend)
                {
                    _throwNextUserAppend = false;
                    throw new InvalidOperationException("committed user append failed");
                }
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
            }
        }

        private sealed class RoleScopedLiveMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, List<Ai.ChatMessage>> _history = new();

            public List<(string RoleId, string MessageRole, string Content, bool Persist)> Appended { get; } = new();

            /// <summary>How many times <see cref="GetChatHistory"/> was called, whatever the cap.</summary>
            public int HistoryReads { get; private set; }

            public void Seed(string roleId, string role, string content)
            {
                GetOrCreate(roleId).Add(new Ai.ChatMessage { Role = role, Content = content });
            }

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
                _history.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                Appended.Add((roleId, role, content, persistToDisk));
                GetOrCreate(roleId).Add(new Ai.ChatMessage { Role = role, Content = content });
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                HistoryReads++;
                if (!_history.TryGetValue(roleId, out List<Ai.ChatMessage> messages))
                {
                    return Array.Empty<Ai.ChatMessage>();
                }

                if (maxMessages > 0 && messages.Count > maxMessages)
                {
                    return messages.ToArray()[(messages.Count - maxMessages)..];
                }

                return messages.ToArray();
            }

            private List<Ai.ChatMessage> GetOrCreate(string roleId)
            {
                if (!_history.TryGetValue(roleId, out List<Ai.ChatMessage> messages))
                {
                    messages = new List<Ai.ChatMessage>();
                    _history[roleId] = messages;
                }

                return messages;
            }
        }

        private sealed class CancelingContextManager : IAsyncConversationContextManager
        {
            public ConversationContextSnapshot BuildSnapshot(
                string roleId,
                Ai.ChatMessage[] history,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                ConversationContextBuildArgs buildArgs = null)
            {
                throw new InvalidOperationException("async path expected");
            }

            public Task<ConversationContextSnapshot> BuildSnapshotAsync(
                string roleId,
                Ai.ChatMessage[] history,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                ConversationContextBuildArgs buildArgs,
                string orchestrationTraceId,
                CancellationToken cancellationToken)
            {
                return Task.FromCanceled<ConversationContextSnapshot>(cancellationToken);
            }
        }

        private sealed class CancelingLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromException<LlmCompletionResult>(
                    new OperationCanceledException("provider timeout"));
            }
        }

        private sealed class CancelOnceThenSucceedLlmClient : ILlmClient
        {
            private bool _cancelNext = true;

            public List<LlmCompletionRequest> Requests { get; } = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (_cancelNext)
                {
                    _cancelNext = false;
                    return Task.FromException<LlmCompletionResult>(
                        new OperationCanceledException("provider timeout"));
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "recovered" });
            }
        }

        /// <summary>
        /// Streams some visible text and then dies mid-turn (dropped connection, 402, provider fault).
        /// Flip <see cref="FailNextStream"/> to let the next turn answer normally, so a test can inspect
        /// what the FOLLOW-UP request carried as history.
        /// </summary>
        private sealed class FailingMidStreamLlmClient : ILlmClient
        {
            public List<LlmCompletionRequest> Requests { get; } = new();

            public bool FailNextStream { get; set; } = true;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "buffered" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                Requests.Add(request);
                if (FailNextStream)
                {
                    yield return new LlmStreamChunk { Text = "начинаю отве" };
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "HTTP 402 payment required",
                        ErrorCode = LlmErrorCode.PaymentRequired
                    };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "полный ответ", IsDone = true };
            }
        }

        [Test]
        public async Task RunTaskAsync_TurnFails_UserMessageStillReachesTheNextRequestAsHistory()
        {
            // WHY: the user turn was persisted only by the success path, while the host had already
            // rendered the message and stored it server-side. A 402 / timeout / cancelled turn therefore
            // erased the question from the model's view - the learner was answered as if they never asked.
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    Error = "HTTP 402 payment required",
                    ErrorCode = LlmErrorCode.PaymentRequired
                },
                new LlmCompletionResult { Ok = true, Content = "Отвечаю" });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "почему цикл не останавливается?"
            });

            Assert.AreEqual(1, memory.Appended.Count,
                "A failed turn must persist the user message and nothing else.");
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("почему цикл не останавливается?", memory.Appended[0].Content,
                "History must contain the exact raw Hint, without telemetry/runtime envelopes.");
            Assert.IsFalse(
                llm.Requests[0].ChatHistory != null &&
                llm.Requests[0].ChatHistory.Any(m => (m.Text ?? "").Contains("почему цикл не останавливается?")),
                "The turn's own message travels as the user payload; it must not also sit in that turn's history.");

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "и что теперь?" });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual(1,
                llm.Requests[1].ChatHistory.Count(m =>
                    m.Role == ChatRole.User && m.Text == "почему цикл не останавливается?"),
                "The next request must see the failed question exactly once in the correct role history.");
            Assert.AreEqual(2, memory.Appended.Count(m => m.MessageRole == "user"),
                "The latch must be per turn rather than shared across orchestrator calls.");
        }

        [Test]
        public async Task RunTaskAsync_SuccessfulTurn_RecordsUserMessageExactlyOnce()
        {
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "Ответ" });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "как устроен list?" });

            Assert.AreEqual(1, memory.Appended.Count(m => m.MessageRole == "user"),
                "Recording the user turn on failure must not double-write it on success.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "The user turn must still be persisted before the assistant answer.");
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("как устроен list?", memory.Appended[0].Content);
        }

        [Test]
        public async Task RunTaskAsync_EmptyResponse_RecordsUserAndNoAssistant()
        {
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = string.Empty });
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "empty" });

            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray());
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetry_DoesNotLeakUserMessageIntoTheRetryHistory()
        {
            // WHY: the overflow retry rebuilds the request from the store. Recording the user turn before the
            // retry would send the same message twice AND grow the prompt the retry exists to shrink.
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = "context too long"
                },
                new LlmCompletionResult { Ok = true, Content = "ok" });
            // WHY: a store whose appends are visible to the next read lets the retry expose self-history.
            LiveTestMemoryStore memory = new();
            memory.History.Add(new Ai.ChatMessage { Role = "user", Content = "прошлый вопрос" });
            memory.History.Add(new Ai.ChatMessage { Role = "assistant", Content = "прошлый ответ" });
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new() { MaxContextOverflowRetries = 1 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "переполняющий вопрос"
            });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.IsTrue(
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains("прошлый вопрос")),
                "precondition: the retry really does rebuild its history from the store.");
            Assert.IsFalse(
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains("переполняющий вопрос")),
                "The retry must not replay the in-flight user message as history.");
            Assert.AreEqual(1, memory.Appended.Count(m => m.Role == "user"),
                "Two internal passes are still ONE turn and one user message.");
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetriesExhausted_RecordsUserAfterTheLastRequest()
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
            LiveTestMemoryStore memory = new();
            memory.History.Add(new Ai.ChatMessage { Role = "user", Content = "previous" });
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new() { MaxContextOverflowRetries = 1 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "current" });

            Assert.AreEqual(2, llm.Requests.Count, "precondition: the configured retry must be exhausted.");
            Assert.IsTrue(llm.Requests.All(r =>
                    r.ChatHistory == null || r.ChatHistory.All(m => !(m.Text ?? "").Contains("current"))),
                "The in-flight turn must not enter any overflow retry's history.");
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray());
        }

        [Test]
        public async Task RunStreamingAsync_StreamDiesMidTurn_StillRecordsUserMessageOnce()
        {
            FailingMidStreamLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "Teacher",
                               Hint = "объясни рекурсию"
                           }))
            {
            }

            Assert.AreEqual(1, memory.Appended.Count,
                "A stream that died mid-turn persists the user message and no assistant turn.");
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("объясни рекурсию", memory.Appended[0].Content);

            llm.FailNextStream = false;
            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "Teacher",
                               Hint = "продолжай"
                           }))
            {
            }

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual(1,
                llm.Requests[1].ChatHistory.Count(m =>
                    m.Role == ChatRole.User && m.Text == "объясни рекурсию"),
                "The next streamed turn must see the broken-stream question exactly once.");
            Assert.AreEqual(2, memory.Appended.Count(m => m.MessageRole == "user"),
                "A following stream must get a fresh per-turn latch.");
        }

        [Test]
        public async Task RunStreamingAsync_SuccessfulTurn_RecordsUserBeforeAssistantExactlyOnce()
        {
            FailingMidStreamLlmClient llm = new() { FailNextStream = false };
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "stream success" }))
            {
            }

            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "The core success path must record user first; wrapper teardown must not duplicate it.");
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("stream success", memory.Appended[0].Content);
        }

        /// <summary>
        /// One stream carries SEVERAL replies: after every tool round the model starts speaking again. The
        /// accumulator must separate them with a blank line; in production it glued them together and the learner
        /// read "Проверь себя:**Ход завершён — ждём ответ ученика на карточке.**" - and that same glued string
        /// travelled into the role history and into <c>ApplyAiGameCommand</c>.
        /// </summary>
        [Test]
        public async Task RunStreamingAsync_ChunkStartsNewMessage_SeparatesMessagesInAccumulatedTurn()
        {
            SegmentedStreamLlmClient llm = new(
                new LlmStreamChunk { Text = "Проверь себя:" },
                new LlmStreamChunk { Text = "**Ход завершён.**", StartsNewMessage = true });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "две реплики" }))
            {
            }

            string assistant = memory.Appended.Single(m => m.MessageRole == "assistant").Content;
            Assert.That(assistant, Does.Not.Contain("себя:**Ход"),
                "Two replies from the teacher have no right to be glued together.");
            Assert.That(assistant, Does.Contain("Проверь себя:\n\n**Ход завершён.**"));
        }

        /// <summary>
        /// Without a boundary flag the accumulator behaves exactly as before: ordinary deltas of one reply are
        /// concatenated tightly, and a separator never appears by itself.
        /// </summary>
        [Test]
        public async Task RunStreamingAsync_WithoutNewMessageFlag_KeepsPlainConcatenation()
        {
            SegmentedStreamLlmClient llm = new(
                new LlmStreamChunk { Text = "Проверь " },
                new LlmStreamChunk { Text = "себя." });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "одна реплика" }))
            {
            }

            Assert.AreEqual("Проверь себя.",
                memory.Appended.Single(m => m.MessageRole == "assistant").Content);
        }

        /// <summary>
        /// A client that emits pre-scripted chunks: it gives the test direct control over the message boundary flag,
        /// which in production is set by <c>MeaiLlmClient</c> on the first visible chunk of a new iteration.
        /// </summary>
        private sealed class SegmentedStreamLlmClient : ILlmClient
        {
            private readonly LlmStreamChunk[] _chunks;

            public SegmentedStreamLlmClient(params LlmStreamChunk[] chunks)
            {
                _chunks = chunks;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "buffered" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                foreach (LlmStreamChunk chunk in _chunks)
                {
                    yield return chunk;
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }
        }

        [Test]
        public async Task RunStreamingAsync_ConsumerAbandonsTurn_RecordsUserMessageOnceAndNothingElse()
        {
            // WHY: the learner presses Stop (or the panel drops a superseded turn) and the consumer stops
            // pulling. Their message stays on screen, so it must stay in history too - exactly once, with
            // no half-written assistant turn beside it.
            FailingMidStreamLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "Teacher",
                               Hint = "что такое словарь?"
                           }))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    break;
                }
            }

            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "An abandoned turn leaves exactly the user message - no duplicate, no partial assistant turn.");
            StringAssert.Contains("что такое словарь?", memory.Appended[0].Content);
        }

        [Test]
        public async Task RunTaskAsync_CancelledDuringInitialBuild_StillRecordsUserOnce()
        {
            ToolTraceLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Programmer", "user", "previous");
            AgentMemoryPolicy policy = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy, new CancelingContextManager());
            using CancellationTokenSource cts = new();
            cts.Cancel();

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest
                {
                    RoleId = "Programmer",
                    SourceTag = "Chat",
                    Hint = "cancelled build"
                }, cts.Token));

            CollectionAssert.AreEqual(new[] { "Programmer" }, memory.Appended.Select(m => m.RoleId).ToArray());
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.MessageRole).ToArray());
            Assert.AreEqual("cancelled build", memory.Appended[0].Content);
            Assert.IsFalse(memory.Appended[0].Persist,
                "Chat source must use the transient fallback history config when bundle construction cancels.");
            Assert.AreEqual(0, llm.Requests.Count, "The cancellation must happen before provider dispatch.");
        }

        [Test]
        public async Task RunStreamingAsync_CancelledDuringInitialBuild_StillRecordsUserOnce()
        {
            ToolTraceLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Programmer", "user", "previous");
            AgentMemoryPolicy policy = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy, new CancelingContextManager());
            using CancellationTokenSource cts = new();
            cts.Cancel();

            await CaptureExceptionAsync<OperationCanceledException>(async () =>
            {
                await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                                   new AiTaskRequest
                                   {
                                       RoleId = "Programmer",
                                       SourceTag = "Chat",
                                       Hint = "cancelled stream build"
                                   },
                                   cts.Token))
                {
                }
            });

            CollectionAssert.AreEqual(new[] { "Programmer" }, memory.Appended.Select(m => m.RoleId).ToArray());
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.MessageRole).ToArray());
            Assert.AreEqual("cancelled stream build", memory.Appended[0].Content);
            Assert.IsFalse(memory.Appended[0].Persist);
            Assert.AreEqual(0, llm.Requests.Count, "The cancellation must happen before provider dispatch.");
        }

        [Test]
        public async Task RunTaskAsync_ProviderCancellation_RecordsExactRawTurnAndNextRequestReadsItOnce()
        {
            CancelOnceThenSucceedLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest
                {
                    RoleId = "Teacher",
                    SourceTag = "Chat",
                    Hint = "provider-cancelled raw question"
                }));

            Assert.AreEqual(1, memory.Appended.Count);
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("provider-cancelled raw question", memory.Appended[0].Content);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "follow-up"
            });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual(1, llm.Requests[1].ChatHistory.Count(m =>
                    m.Role == ChatRole.User && m.Text == "provider-cancelled raw question"),
                "The healthy live store must expose the cancelled question exactly once on the next request.");
        }

        [Test]
        public async Task RunTaskAsync_ResendAfterProviderError_StoresAndSendsTheMessageOnce()
        {
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    Error = "HTTP 503",
                    ErrorCode = LlmErrorCode.BackendUnavailable
                },
                new LlmCompletionResult { Ok = true, Content = "answer" });
            RoleScopedLiveMemoryStore memory = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, BuildToolResultPolicy("Teacher"));
            const string payload = "retry after an outage";

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload });
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload });

            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "A failed turn and its retry are one user message, then the answer.");
            Assert.IsFalse(
                llm.Requests[1].ChatHistory != null &&
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains(payload)));
        }

        [TestCase("tool")]
        [TestCase("system")]
        public async Task RunTaskAsync_MessageAfterTheUserTurn_TailIsNotAResend_EvenWithAOneMessageCap(string tailRole)
        {
            // WHY: the decision reads the RAW store tail. With a one-message cap the prompt window holds only
            // the tail message, and the earlier user turn is followed by something - it is not unanswered.
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "answer" });
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Teacher", "user", "same question");
            memory.Seed("Teacher", tailRole, "## Tool Results\n- quiz_tool: ok");
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("Teacher", true, 8192, false, 1);
            policy.DisableMemoryTool("Teacher");
            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy, settings: settings);

            await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "same question" });

            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                $"A user turn followed by a '{tailRole}' message is not unanswered; the new one is stored.");
        }

        [Test]
        public async Task RunTaskAsync_PrunedToolTail_PromptAndStoreAgreeThatItIsNotAResend()
        {
            // WHY: [user X, tool] with every tool result pruned from the prompt ends in "user X" on the PROMPT
            // side only. Deciding there dropped X from the prompt while the store appended it again; the
            // decision is made once, from the raw store tail, and both sides follow it.
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "answer" });
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Teacher", "user", "quiz me");
            memory.Seed("Teacher", "tool", "## Tool Results\n- quiz_tool: ok card shown");
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new()
            {
                EnableConversationHistorySummarization = false,
                MaxRetainedToolResultMessages = 0
            };
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy, settings: settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "quiz me" });

            Assert.IsFalse(llm.Requests[0].ChatHistory.Any(m => (m.Text ?? "").Contains("card shown")),
                "precondition: the tool result really is pruned from the prompt.");
            Assert.AreEqual(1, llm.Requests[0].ChatHistory.Count(m => m.Role == ChatRole.User && m.Text == "quiz me"),
                "The earlier, answered request stays in the prompt history.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "...and the new one is stored: both sides agree it is not a resend.");
        }

        [Test]
        public async Task RunTaskAsync_SameTextWithAttachments_IsNeverCollapsed()
        {
            // WHY: history keeps only a descriptor (name, type, size); two different screenshots of the same
            // size produce byte-identical text, so a turn with attachments is never treated as a resend.
            CancelOnceThenSucceedLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, BuildToolResultPolicy("Teacher"));
            byte[] first = new byte[4 * 1024];
            byte[] second = new byte[4 * 1024];
            second[0] = 1;

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "what is wrong here?",
                Attachments = new[] { AiAttachment.Image(first, "image/png", "shot.png") }
            }));
            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "what is wrong here?",
                Attachments = new[] { AiAttachment.Image(second, "image/png", "shot.png") }
            });

            Assert.AreEqual(memory.Appended[0].Content, memory.Appended[1].Content,
                "precondition: the two different images really describe identically in history.");
            CollectionAssert.AreEqual(new[] { "user", "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray());
            Assert.AreEqual(1, llm.Requests[1].ChatHistory.Count(m => m.Role == ChatRole.User),
                "The earlier message with its own image stays in the prompt history.");
        }

        [Test]
        public async Task RunTaskAsync_ResendAfterCancelledTurn_StoresAndSendsTheMessageOnce()
        {
            // WHY: a host cancelled a service turn (a help request) and re-sent the same payload. The cancelled
            // attempt had already recorded the message, so the resend stored it a second time and the model
            // read it twice - once as history, once as the live payload.
            CancelOnceThenSucceedLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Teacher", "assistant", "earlier answer");
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);
            const string payload = "[help] the learner is stuck on task 3";

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload }));
            string answer = await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload });

            Assert.AreEqual("recovered", answer);
            Assert.AreEqual(2, llm.Requests.Count);
            Assert.IsFalse(
                llm.Requests[1].ChatHistory != null &&
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains(payload)),
                "The resend carries the message as its payload; the unanswered copy must not ride along as history.");
            Assert.IsTrue(
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains("earlier answer")),
                "precondition: the resend still reads the rest of the history.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "One user message for the cancelled attempt and its resend, then the answer.");
            Ai.ChatMessage[] stored = memory.GetChatHistory("Teacher");
            Assert.AreEqual(1, stored.Count(m => m.Role == "user" && m.Content == payload));
        }

        [Test]
        public async Task RunStreamingAsync_CallerCancelsThenResendsSamePayload_StoresAndSendsTheMessageOnce()
        {
            CallerCancelledFirstStreamLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);
            const string payload = "[help] repeated service payload";

            LlmStreamChunk terminal = null;
            using (CancellationTokenSource caller = new())
            {
                await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                                   new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload },
                                   caller.Token))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        caller.Cancel();
                    }

                    if (chunk.IsDone)
                    {
                        terminal = chunk;
                    }
                }
            }

            Assert.IsNotNull(terminal, "precondition: the cancelled stream ends with a terminal chunk.");
            Assert.AreEqual(LlmErrorCode.Cancelled, terminal.ErrorCode,
                "A stream the caller cancelled must end as a cancellation, never as a timeout.");

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload }))
            {
            }

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.IsFalse(
                llm.Requests[1].ChatHistory != null &&
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains(payload)),
                "The streamed resend must not see its own unanswered copy as history.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray());
        }

        [Test]
        public async Task RunTaskAsync_SameMessageAfterAnAnswer_IsStoredAgain()
        {
            // WHY: the resend rule covers only an UNANSWERED tail. A learner who says "ok" twice, with the
            // teacher's answer between, said it twice.
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult { Ok = true, Content = "first answer" },
                new LlmCompletionResult { Ok = true, Content = "second answer" });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "ok" });
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "ok" });

            CollectionAssert.AreEqual(new[] { "user", "assistant", "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray());
            Assert.AreEqual(1, llm.Requests[1].ChatHistory.Count(m => m.Role == ChatRole.User && m.Text == "ok"),
                "The answered earlier message stays in the second request's history.");
        }

        [Test]
        public async Task RunTaskAsync_DifferentMessageAfterCancelledTurn_KeepsBoth()
        {
            CancelOnceThenSucceedLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "first question" }));
            await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "second question" });

            CollectionAssert.AreEqual(new[] { "user", "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "Only an identical resend collapses; a new question after a cancelled one is its own turn.");
            Assert.AreEqual(1, llm.Requests[1].ChatHistory.Count(m =>
                m.Role == ChatRole.User && m.Text == "first question"));
        }

        [Test]
        public async Task RunTaskAsync_HistoryTailUnreadable_StillRecordsTheUserTurn()
        {
            ThrowingTailReadMemoryStore memory = new();
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "answer" });
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" });

            CollectionAssert.AreEqual(new[] { "user", "assistant" }, memory.Appended.ToArray(),
                "A failed resend check must never cost the learner's words.");
        }

        [Test]
        public async Task RunStreamingAsync_ProviderThrowsLibraryTimeout_EndsWithTimeoutNotCancelled()
        {
            TimeoutThrowingStreamLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            LlmStreamChunk terminal = null;
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "slow" }))
            {
                if (chunk.IsDone)
                {
                    terminal = chunk;
                }
            }

            Assert.IsNotNull(terminal);
            Assert.AreEqual(LlmErrorCode.Timeout, terminal.ErrorCode,
                "A library timeout thrown mid-stream used to be relabelled Cancelled, so the chat could not " +
                "tell a dead backend from a stopped turn.");
        }

        [Test]
        public async Task RunStreamingAsync_TurnEndedByTool_ProseShownAndStoredOnce_ToolResultRecorded()
        {
            // WHY: a turn closed by an EndsTurn tool ends on a text-less terminal chunk that carries the
            // traces. The prose of that round must reach the reader and the history exactly once, and the
            // tool result must still be recorded although it never went back to the model.
            ScriptedStreamLlmClient llm = new(
                new LlmStreamChunk { Text = "Check yourself: " },
                new LlmStreamChunk { Text = "what is 2+2?" },
                new LlmStreamChunk
                {
                    IsDone = true,
                    Text = string.Empty,
                    ExecutedToolCalls = new[]
                    {
                        new LlmToolCallTrace("quiz_tool", true, 1d, "native",
                            "{\"success\":true,\"message\":\"card shown\"}")
                    }
                });
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "quiz me" }))
            {
                chunks.Add(chunk);
            }

            string visible = string.Concat(chunks.Select(c => c.Text));
            Assert.AreEqual(1, CountOccurrences(visible, "Check yourself: what is 2+2?"), visible);
            Assert.AreEqual(1, llm.StreamCalls);
            LlmStreamChunk last = chunks.Last();
            Assert.IsTrue(last.IsDone);
            Assert.AreEqual(LlmErrorCode.None, last.ErrorCode);
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Name == "quiz_tool" && t.Success));

            CollectionAssert.AreEqual(new[] { "user", "assistant", "tool" },
                memory.Appended.Select(m => m.Role).ToArray());
            Assert.AreEqual("Check yourself: what is 2+2?", memory.Appended[1].Content,
                "The stored answer is the round's prose, once.");
            StringAssert.Contains("quiz_tool", memory.Appended[2].Content);
        }

        private sealed class ScriptedStreamLlmClient : ILlmClient
        {
            private readonly LlmStreamChunk[] _chunks;

            public ScriptedStreamLlmClient(params LlmStreamChunk[] chunks)
            {
                _chunks = chunks;
            }

            public int StreamCalls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "buffered" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                foreach (LlmStreamChunk chunk in _chunks)
                {
                    await Task.Yield();
                    yield return chunk;
                }
            }
        }

        /// <summary>First stream shows text and then waits for the caller to cancel; later streams answer.</summary>
        private sealed class CallerCancelledFirstStreamLlmClient : ILlmClient
        {
            private bool _first = true;

            public List<LlmCompletionRequest> Requests { get; } = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "buffered" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                Requests.Add(request);
                if (_first)
                {
                    _first = false;
                    yield return new LlmStreamChunk { Text = "partial" };
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                yield return new LlmStreamChunk { Text = "full answer", IsDone = true };
            }
        }

        private sealed class TimeoutThrowingStreamLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromException<LlmCompletionResult>(new LlmOperationTimeoutException());
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                throw new LlmOperationTimeoutException();
#pragma warning disable CS0162 // unreachable code: needed only so the compiler treats this as an iterator.
                yield break;
#pragma warning restore CS0162
            }
        }

        private sealed class ThrowingTailReadMemoryStore : IAgentMemoryStore
        {
            public List<string> Appended { get; } = new();

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
                Appended.Add(role);
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (maxMessages == 1)
                {
                    throw new IOException("history file is locked");
                }

                return Array.Empty<Ai.ChatMessage>();
            }
        }

        [Test]
        public async Task RunTaskAsync_AuthorityDenied_RecordsRawTurnInResolvedRoleAndNextRequestReadsIt()
        {
            TestAuthority authority = new() { CanRunAiTasks = false };
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "recovered" });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                authority, llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = " Teacher ",
                SourceTag = "Chat",
                Hint = "authority-denied raw question"
            });

            Assert.AreEqual(0, llm.Requests.Count, "Authority denial must happen before provider dispatch.");
            Assert.AreEqual(1, memory.Appended.Count);
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("authority-denied raw question", memory.Appended[0].Content);

            authority.CanRunAiTasks = true;
            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "follow-up"
            });

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.AreEqual(1, llm.Requests[0].ChatHistory.Count(m =>
                m.Role == ChatRole.User && m.Text == "authority-denied raw question"));
        }

        [Test]
        public async Task RunStreamingAsync_AuthorityDenied_RecordsRawTurnInResolvedRole()
        {
            TestAuthority authority = new() { CanRunAiTasks = false };
            FailingMidStreamLlmClient llm = new() { FailNextStream = false };
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                authority, llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = " Teacher ",
                               SourceTag = "Chat",
                               Hint = "authority-denied stream raw"
                           }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual("authority denied", chunks[0].Error);
            Assert.AreEqual(0, llm.Requests.Count);
            Assert.AreEqual(1, memory.Appended.Count);
            Assert.AreEqual("Teacher", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("authority-denied stream raw", memory.Appended[0].Content);
        }

        [Test]
        public async Task QueuedAiOrchestrator_PreCancelledProductionInner_UsesUnstartedPersistenceCapability()
        {
            ToolTraceLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            AiOrchestrator core = BuildOrchestrator(llm, memory, policy);
            QueuedAiOrchestrator queue = new(core, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cts = new();
            cts.Cancel();

            Task turn = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Programmer",
                SourceTag = "Chat",
                Hint = "queue production raw"
            }, cts.Token);

            await CaptureExceptionAsync<OperationCanceledException>(() => turn);
            Assert.AreEqual(0, llm.Requests.Count);
            Assert.AreEqual(1, memory.Appended.Count);
            Assert.AreEqual("Programmer", memory.Appended[0].RoleId);
            Assert.AreEqual("user", memory.Appended[0].MessageRole);
            Assert.AreEqual("queue production raw", memory.Appended[0].Content);
            Assert.IsFalse(memory.Appended[0].Persist,
                "Chat fallback history for an unconfigured role remains transient.");
        }

        [Test]
        public async Task FailedTurn_WithAttachment_PersistsRawHintPlusExactCompactPlaceholder()
        {
            ToolTraceLlmClient llm = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "HTTP 402 payment required",
                ErrorCode = LlmErrorCode.PaymentRequired
            });
            RoleScopedLiveMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "inspect this image",
                Attachments = new[]
                {
                    AiAttachment.Image(new byte[12 * 1024], "image/png", "diagram.png")
                }
            });

            Assert.AreEqual(1, memory.Appended.Count);
            Assert.AreEqual(
                "inspect this image\n[attachment: diagram.png image/png 12 KB]",
                memory.Appended[0].Content,
                "Binary attachment bytes must never enter text history; only the deterministic placeholder may follow raw Hint.");
        }

        [Test]
        public async Task RunTaskAsync_UserHistoryAppendFails_DoesNotPersistAssistantAlone()
        {
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "answer" });
            ThrowOnUserMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            InvalidOperationException thrown = await CaptureExceptionAsync<InvalidOperationException>(() =>
                orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "write failure" }));

            Assert.AreEqual("user append failed", thrown.Message);
            Assert.IsEmpty(memory.Appended, "An assistant turn must never be persisted without its user turn.");
        }

        [Test]
        public async Task RunTaskAsync_CancelAndHistoryAppendFail_PreservesOriginalCancellation()
        {
            ThrowOnUserMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(new CancelingLlmClient(), memory, policy);

            OperationCanceledException thrown = await CaptureExceptionAsync<OperationCanceledException>(() =>
                orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "timeout" }));

            StringAssert.DoesNotContain("history store failed", thrown.Message,
                "A store exception in teardown must not replace the provider cancellation.");
            Assert.IsEmpty(memory.Appended);
        }

        [Test]
        public async Task RunTaskAsync_UserAppendCommitsThenThrows_DoesNotRetryOrPersistAssistant()
        {
            ThrowAfterCommittedUserAppendMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = BuildOrchestrator(
                new ToolTraceLlmClient(new LlmCompletionResult { Ok = true, Content = "answer" }),
                memory,
                policy);

            InvalidOperationException thrown = await CaptureExceptionAsync<InvalidOperationException>(() =>
                orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "commit then throw" }));
            Assert.AreEqual("committed user append failed", thrown.Message);

            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "An ambiguous committed append must never be retried or followed by an assistant append.");
        }

        [Test]
        public async Task RunStreamingAsync_UserAppendCommitsThenThrows_DoesNotRetryOrPersistAssistant()
        {
            ThrowAfterCommittedUserAppendMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            FailingMidStreamLlmClient llm = new() { FailNextStream = false };
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            InvalidOperationException thrown = null;
            try
            {
                await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                                   new AiTaskRequest { RoleId = "Teacher", Hint = "stream commit then throw" }))
                {
                }
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }

            Assert.NotNull(thrown, "The first committed user append must still surface its store failure.");
            Assert.AreEqual("committed user append failed", thrown.Message);
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "Streaming teardown must not retry an append that may already have committed.");
        }

        private const string SpawnQuizPayload =
            "{\"success\":true,\"tool\":\"spawn_quiz\",\"status\":\"card_shown_waiting_for_student\"}";

        private sealed class QuizStubTool : LlmToolBase
        {
            public override string Name => "spawn_quiz";
            public override string Description => "shows a quiz card and waits for the student";
        }

        /// <summary>
        /// A client with no streaming of its own: the interface default turns one completion into a single
        /// terminal chunk carrying the text AND the executed calls together. This is the shape of the
        /// fallback path this filter is for, and the shape where it can actually act — the repetition and
        /// the strings it repeats become known in the same chunk.
        /// </summary>
        private sealed class EchoingToolResultClient : ILlmClient
        {
            private readonly string _content;
            private readonly bool _native;
            private readonly LlmToolCallTrace[] _traces;

            public EchoingToolResultClient(LlmToolCallTrace[] traces, string content, bool native = false)
            {
                _traces = traces;
                _content = content;
                _native = native;
            }

            public bool SupportsNativeToolCalling => _native;

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = _content,
                    ExecutedToolCalls = _traces
                });
            }
        }

        /// <summary>
        /// Streams the way a real streaming client does on the fallback path: prose first, executed calls
        /// reported only on the terminal chunk. A stand that announced the calls earlier would guard the
        /// wording of the test instead of the behaviour of the product — and it would hide the honest
        /// limit asserted here: a repetition streamed before that report is cleaned in the stored and
        /// published turn, but the reader has already seen it.
        /// </summary>
        private sealed class LateReportingEchoStreamClient : ILlmClient
        {
            private readonly string[] _textChunks;
            private readonly LlmToolCallTrace[] _traces;

            public LateReportingEchoStreamClient(LlmToolCallTrace[] traces, params string[] textChunks)
            {
                _traces = traces;
                _textChunks = textChunks;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = string.Concat(_textChunks),
                    ExecutedToolCalls = _traces
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                foreach (string text in _textChunks)
                {
                    yield return new LlmStreamChunk { Text = text };
                }

                yield return new LlmStreamChunk { IsDone = true, ExecutedToolCalls = _traces };
            }
        }

        [Test]
        public async Task RunStreamingAsync_FallbackPath_RepetitionNeverReachesReaderOrHistory()
        {
            LlmToolCallTrace[] traces = { new("spawn_quiz", true, 6d, "text", SpawnQuizPayload) };
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            EchoingToolResultClient llm = new(traces, "Вопрос на карточке. " + SpawnQuizPayload);
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            StringBuilder streamed = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "quiz" }))
            {
                streamed.Append(chunk.Text ?? "");
            }

            string persistedAssistant = memory.Appended.First(m => m.Role == "assistant").Content;
            StringAssert.DoesNotContain("card_shown_waiting_for_student", streamed.ToString());
            StringAssert.Contains("Вопрос на карточке.", streamed.ToString(),
                "Only the repetition is removed; the teacher's own words stay.");
            Assert.AreEqual(streamed.ToString(), persistedAssistant,
                "What the reader saw and what is stored must not diverge — a reload would change the reply.");
        }

        /// <summary>
        /// The honest limit of this filter on a streaming client: it can only remove a repetition of a
        /// result it has already been told about, and the executed calls are reported on the terminal
        /// chunk. Text streamed before that report is cleaned in history and in the published turn — which
        /// is what this asserts — but it was already displayed. The guard against the incident itself is
        /// the prompt projection, not this filter.
        /// </summary>
        [Test]
        public async Task RunStreamingAsync_ResultReportedOnlyAtTheEnd_StillCleansTheStoredTurn()
        {
            LlmToolCallTrace[] traces = { new("spawn_quiz", true, 6d, "text", SpawnQuizPayload) };
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            LateReportingEchoStreamClient llm = new(traces, "Итог: ", SpawnQuizPayload);
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "quiz" }))
            {
            }

            string persistedAssistant = memory.Appended.First(m => m.Role == "assistant").Content;
            StringAssert.DoesNotContain("card_shown_waiting_for_student", persistedAssistant);
            StringAssert.Contains("Итог:", persistedAssistant);
        }

        /// <summary>
        /// On an endpoint with a native tool channel the orchestrator must not edit the answer by its
        /// CONTENT at all — neither the repetition filter nor the leaked-call strip. Both belong to the
        /// path where tool traffic travels as text; where it does not, JSON in the answer is a teacher
        /// showing JSON to a learner, and cutting it out takes the lesson away to defend against something
        /// that cannot happen. The answer below carries both shapes: a tool RESULT and a tool CALL.
        /// </summary>
        [Test]
        public async Task RunStreamingAsync_NativeToolCalling_LeavesTheAnswerExactlyAsTheModelWroteIt()
        {
            LlmToolCallTrace[] traces = { new("spawn_quiz", true, 6d, "native", SpawnQuizPayload) };
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            string answer = "Результат выглядит так: " + SpawnQuizPayload +
                            ", а вызов — так: {\"name\":\"spawn_quiz\",\"arguments\":{}} — это примеры.";
            EchoingToolResultClient llm = new(traces, answer, true);
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            StringBuilder streamed = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Teacher", Hint = "quiz" }))
            {
                streamed.Append(chunk.Text ?? "");
            }

            Assert.AreEqual(answer, streamed.ToString());
            Assert.AreEqual(answer, memory.Appended.First(m => m.Role == "assistant").Content);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ExplicitTextChannel_CleansOnlyDeclaredCallsFromCompletedTurn(bool streaming, bool native)
        {
            const string knownCall = "{\"name\":\"spawn_quiz\",\"arguments\":{}}";
            const string lessonExample = "{\"name\":\"lesson_example\",\"arguments\":{\"value\":1}}";
            string answer = "Explanation: " + lessonExample + " Action: " + knownCall;
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            EchoingToolResultClient llm = new(Array.Empty<LlmToolCallTrace>(), answer, native);
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);
            AiTaskRequest request = new()
            {
                RoleId = "Teacher", Hint = "quiz",
                AllowTextShapedToolCallsOnNativeEndpoint = native ? true : null
            };

            string completed = null;
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(request))
                {
                    Assert.IsTrue(string.IsNullOrEmpty(chunk.Error));
                }
            }
            else
            {
                completed = await orchestrator.RunTaskAsync(request);
            }

            string persisted = memory.Appended.First(message => message.Role == "assistant").Content;
            Assert.That(persisted, Does.Not.Contain(knownCall));
            Assert.That(persisted, Does.Contain(lessonExample), "An undeclared name is teaching content, not tool traffic.");
            Assert.That(persisted, Does.Contain("Explanation:"));
            if (!streaming) Assert.AreEqual(persisted, completed);
        }

        /// <summary>
        /// The escape hatch must be reachable from the package's own public API. It is the documented cure
        /// for an endpoint that advertises a native tool channel and then answers with JSON in the text; if
        /// it existed only for someone hand-building an <c>LlmCompletionRequest</c>, the docs would be
        /// prescribing a treatment no host can actually apply.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_TextShapedToolCallOptIn_ReachesTheCompletionRequest()
        {
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "ok" });
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "go",
                AllowTextShapedToolCallsOnNativeEndpoint = true
            });

            Assert.AreEqual(true, llm.LastRequest.AllowTextShapedToolCallsOnNativeEndpoint,
                "AiTaskRequest is the only way a host can reach this opt-in.");
        }

        [Test]
        public async Task RunTaskAsync_WithoutOptIn_LeavesProseInterpretationOff()
        {
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher", new QuizStubTool());
            ToolTraceLlmClient llm = new(new LlmCompletionResult { Ok = true, Content = "ok" });
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "go" });

            Assert.IsNull(llm.LastRequest.AllowTextShapedToolCallsOnNativeEndpoint,
                "The safe default must stay untouched when nobody asked for the opt-in.");
        }

        /// <summary>
        /// A role whose answer is a machine contract is validated BEFORE the turn is sanitized and then
        /// published as the command payload. Such a role may legitimately carry a tool's own output inside
        /// that payload, so cutting an identical span out of it would hand the game a payload that is
        /// invalid AFTER it passed the validator — worse than the leak this filter removes.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_StructuredRole_KeepsToolOutputInsideItsPayload()
        {
            const string script = "function spawn() return 'a very long generated script body' end";
            LlmToolCallTrace[] traces = { new("write_script", true, 4d, "native", script) };
            string payload = "{\"lua\":\"" + script + "\"}";
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Programmer", new QuizStubTool());
            ToolTraceLlmClient llm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = payload,
                ExecutedToolCalls = traces
            });
            AiOrchestrator orchestrator = BuildOrchestrator(
                llm,
                memory,
                policy,
                structuredPolicy: new AlwaysStructuredPolicy());

            string answer = await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Programmer", Hint = "write a script" });

            Assert.AreEqual(payload, answer,
                "A validated structured payload must not be edited behind the validator's back.");
        }

        private sealed class AlwaysStructuredPolicy : IRoleStructuredResponsePolicy
        {
            public bool ShouldValidate(string roleId)
            {
                return true;
            }

            public bool TryValidate(string roleId, string rawContent, out string failureReason)
            {
                failureReason = "";
                return true;
            }
        }

        /// <summary>
        /// Regression: the prompt window dropped stored messages with no trace. With summarization off
        /// nothing retells them, so the turn logs ONE warning with the numbers.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_WindowDropsMessages_SummarizationOff_OneWarningWithNumbers()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"window-{i}-".PadRight(90, 'x')
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);
            TestSettings settings = new()
            {
                EnableConversationHistorySummarization = false,
                ConversationHistoryRecentTokenBudgetOverride = 100
            };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "window" });

            int sent = llm.LastRequest.ChatHistory.Count(m => m.Role != ChatRole.System);
            Assert.Less(sent, 10, "precondition: the budget must leave messages out");
            string[] lines = log.Warnings.Where(l => l.Contains("history window:")).ToArray();
            Assert.AreEqual(1, lines.Length, string.Join("\n", log.All));
            StringAssert.Contains(
                $"history window: 10 stored message(s) -> {sent} sent verbatim, {10 - sent} left out by the window " +
                "with no rolling summary (history summarization is off): the model does not see them.",
                lines[0]);
        }

        [Test]
        public async Task RunTaskAsync_WindowFoldsMessagesIntoSummary_OneInfoLineWithNumbers()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"folded-{i}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 4);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "fold" });

            string[] lines = log.All.Where(l => l.Contains("history window:")).ToArray();
            Assert.AreEqual(1, lines.Length, string.Join("\n", log.All));
            StringAssert.Contains("history window: 10 stored message(s) -> 4 sent verbatim, 6 folded into the " +
                                  "rolling summary (~", lines[0]);
            Assert.IsFalse(log.Warnings.Any(l => l.Contains("history window:")),
                "a retold prefix is not a loss, so it is not a warning");
        }

        /// <summary>
        /// Regression (audit F1): with default settings every turn after a few tool calls warned that messages were
        /// "left out" - they were superseded tool results removed by pruning, a routine cut, not a loss.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_FiveToolTurnsWithoutCompaction_PruningIsNotAWarning()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int turn = 0; turn < 5; turn++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = $"question {turn}" });
                memory.FakeHistory.Add(new Ai.ChatMessage { Role = "assistant", Content = $"answer {turn}" });
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = "tool",
                    Content = $"## Tool Results\n- quiz_tool: ok result-{turn}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "next" });

            Assert.IsFalse(log.Warnings.Any(l => l.Contains("history window:")),
                "pruned tool results are not a loss: " + string.Join("\n", log.Warnings));
            string line = log.All.Single(l => l.Contains("history window:"));
            StringAssert.Contains("pruned (superseded tool results / exact duplicates)", line);
            StringAssert.DoesNotContain("left out", line);
        }

        /// <summary>
        /// Audit F2: with summarization off the store applies MaxChatHistoryMessages itself, so the older messages
        /// never reached the orchestrator and no log said so. One extra message is read to detect it.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_SummarizationOff_MessageCapHit_WarnsAndSendsTheCap()
        {
            TruncationMarker.ResetLogOnce();
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"capped-{i}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 4);
            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "cap" });

            Assert.AreEqual(4, llm.LastRequest.ChatHistory.Count(m => m.Role != ChatRole.System),
                "the extra message read for detection is never sent");
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("capped-5")));
            string line = log.Warnings.Single(l => l.Contains("history window:"));
            StringAssert.Contains("≥1 older message(s) not sent (MaxChatHistoryMessages=4)", line);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "cap again" });

            Assert.AreEqual(1, log.Warnings.Count(l => l.Contains("history window:")),
                "the configured cap is news once per role; later turns are Info");
            Assert.AreEqual(2, log.All.Count(l => l.Contains("MaxChatHistoryMessages=4")));
        }

        /// <summary>
        /// Messages the compactor could not take this turn are not in the prompt and not yet in the summary; the
        /// window line must not report them as folded.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_DeferredFoldMessages_AreNotReportedAsFolded()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"deferred-{i}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);
            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeferringContextManager());

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "defer" });

            string line = log.All.Single(l => l.Contains("history window:"));
            StringAssert.Contains("10 stored message(s) -> 2 sent verbatim, 5 folded into the rolling summary", line);
            StringAssert.Contains("3 deferred to the next compaction (not in this prompt, not yet in the summary)", line);
        }

        /// <summary>Folds the prefix, keeps the last two messages, and reports three messages as deferred.</summary>
        private sealed class DeferringContextManager : IConversationContextManager
        {
            public ConversationContextSnapshot BuildSnapshot(
                string roleId,
                Ai.ChatMessage[] history,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                ConversationContextBuildArgs buildArgs = null)
            {
                return new ConversationContextSnapshot
                {
                    Summary = "recap of the early turns",
                    RecentMessages = history.Skip(history.Length - 2).ToArray(),
                    WasCompacted = true,
                    DeferredFoldMessageCount = 3
                };
            }
        }

        [Test]
        public async Task RunTaskAsync_SummarizationOff_WithinMessageCap_NoWindowLine()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            for (int i = 0; i < 4; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"fits-{i}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 4);
            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "fits" });

            Assert.AreEqual(4, llm.LastRequest.ChatHistory.Count(m => m.Role != ChatRole.System));
            Assert.IsFalse(log.All.Any(l => l.Contains("history window:")), string.Join("\n", log.All));
        }

        [Test]
        public async Task RunTaskAsync_SummaryCutToRequestReserve_WarningNamesBothSources()
        {
            using TruncationLogCapture log = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "earlier question" });
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "assistant", Content = "earlier answer" });
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", 4096);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(SeedOversizedSummary("test_role")));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "short" });

            string line = log.Warnings.FirstOrDefault(l => l.Contains("Rolling summary for role 'test_role' trimmed"));
            Assert.IsNotNull(line, string.Join("\n", log.All));
            StringAssert.Contains("~0 by the context manager's MaxRolledSummaryTokens cap", line);
            StringAssert.Contains("-token request reserve; ~", line);
            StringAssert.Contains(" tokens sent.", line);
        }

        [Test]
        public void BuildToolResultsMemoryBlock_FullPolicy_LongDetail_NamesTheCutAndReportsIt()
        {
            string detail = "HEAD-" + new string('d', 5000) + "-TAIL";
            LlmToolCallTrace[] traces = { new("big_tool", true, 1, "test", detail) };

            string block = AiOrchestrator.BuildToolResultsMemoryBlock(
                traces, ToolResultMemoryPolicy.Full, out AiOrchestrator.ToolResultsClipStats clip);

            Assert.AreEqual(1, clip.ClippedEntries);
            Assert.AreEqual(detail.Length, clip.OriginalChars);
            StringAssert.Contains("...[truncated " + clip.DroppedChars + " chars]...", block);
            StringAssert.Contains("HEAD-", block);
            StringAssert.Contains("-TAIL", block);
            Assert.LessOrEqual(detail.Length - clip.DroppedChars, AiOrchestrator.ToolResultDetailMaxChars);
        }

        [Test]
        public void BuildToolResultsMemoryBlock_CompactPolicy_LongMessage_CarriesCountMarker()
        {
            LlmToolCallTrace[] traces = { new("chatty_tool", false, 1, "test", new string('e', 1000)) };

            string block = AiOrchestrator.BuildToolResultsMemoryBlock(
                traces, ToolResultMemoryPolicy.CompactSummary, out AiOrchestrator.ToolResultsClipStats clip);

            StringAssert.Contains("- chatty_tool: FAILED " + new string('e', 240) + "…[+760 chars]", block);
            Assert.AreEqual(1, clip.ClippedEntries);
            Assert.AreEqual(1000, clip.OriginalChars);
            Assert.AreEqual(760, clip.DroppedChars);
        }

        [Test]
        public void TruncateHeadTail_StaysWithinLimit_AndCountsWhatItDropped()
        {
            string value = new string('h', 3000) + new string('t', 3000);

            string cut = AiOrchestrator.TruncateHeadTail(value, 2000, out int dropped);

            Assert.LessOrEqual(cut.Length, 2000);
            Assert.AreEqual(value.Length, cut.Replace("\n...[truncated " + dropped + " chars]...\n", "").Length + dropped);
        }

        [Test]
        public void ExtractToolTraceMessage_LongPlainText_CarriesCountMarker()
        {
            string message = AiOrchestrator.ExtractToolTraceMessage(new string('p', 300));

            Assert.AreEqual(new string('p', 240) + "…[+60 chars]", message);
        }

        /// <summary>Captures CoreAI log lines for one test and restores the previous log.</summary>
        private sealed class TruncationLogCapture : ILog, IDisposable
        {
            private readonly ILog _previous = Log.Instance;

            public TruncationLogCapture()
            {
                Log.Instance = this;
            }

            public List<string> All { get; } = new();
            public List<string> Warnings { get; } = new();

            public void Debug(string message, string tag = null) => All.Add(message);
            public void Info(string message, string tag = null) => All.Add(message);

            public void Warn(string message, string tag = null)
            {
                All.Add(message);
                Warnings.Add(message);
            }

            public void Error(string message, string tag = null) => All.Add(message);

            public void Dispose()
            {
                Log.Instance = _previous;
            }
        }

        private static AgentMemoryPolicy BuildToolResultPolicy(string roleId, params ILlmTool[] tools)
        {
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(roleId, true, 8192, false, 10);
            policy.DisableMemoryTool(roleId);
            policy.SetToolsForRole(roleId, tools ?? Array.Empty<ILlmTool>());
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            IAgentMemoryStore memory,
            AgentMemoryPolicy policy,
            IConversationContextManager contextManager = null,
            IRoleStructuredResponsePolicy structuredPolicy = null,
            TestSettings settings = null)
        {
            settings ??= new TestSettings();
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, structuredPolicy, null, settings, TestActorIdentityProvider,
                contextManager);
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

        private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException ex)
            {
                return ex;
            }

            Assert.Fail($"Expected {typeof(TException).Name}, but the operation completed successfully.");
            return null;
        }

        internal sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        internal sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        internal sealed class TestSettings : ICoreAISettings
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
            public bool EnableStreaming { get; set; } = true;
            public bool EnableConversationHistorySummarization { get; set; } = true;
            public int ConversationHistoryRecentTokenBudgetOverride { get; set; }
            public int ConversationRolledSummaryMaxTokens { get; set; }
            public int MaxRetainedToolResultMessages { get; set; } = 3;
        }

        internal sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        internal sealed class NullUsr : IAgentUserPromptTemplateProvider
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

            // Configure the agent with a limit of 15 messages
            int maxMessages = 15;
            string[] sourceTranscript = memory.FakeHistory.Select(message => message.Content).ToArray();
            policy.ConfigureChatHistory("test_role", true, 8192, false, maxMessages);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            // Act
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "Hi" });

            // Assert
            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            string[] transcript = llm.LastRequest.ChatHistory.Select(message => message.Text)
                .Where(text => sourceTranscript.Contains(text)).ToArray();
            CollectionAssert.AreEqual(sourceTranscript.Skip(sourceTranscript.Length - maxMessages), transcript,
                "The configured transcript tail survives independently of any generated summary.");
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
                memory, policy, null, null, settings, TestActorIdentityProvider);

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

            // WHY: Ten 1000-char turns under a 4096-token window: the recent tail keeps the newest five,
            // the rest fold, and the summary reserve derived from the window holds the whole retelling.
            // The former 60-token window floored the allowance at 32 tokens and only ever passed because
            // the summary travelled outside every budget (audit 17 [1]).
            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(1000, 'x');
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 4096, false, 50);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.Less(llm.LastRequest.ChatHistory.Count, memory.FakeHistory.Count);
            Assert.LessOrEqual(EstimateRequestTokens(llm.LastRequest), 4096,
                "Summary + tail + fixed prompt must fit the window the role was configured with.");
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Microsoft.Extensions.AI.ChatMessage summary = llm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.User && (m.Text ?? "").Contains("## Conversation Summary"));
            StringAssert.Contains("old-context-0", summary.Text);
            Microsoft.Extensions.AI.ChatMessage newestTranscript = llm.LastRequest.ChatHistory.Last(m =>
                m.Role != ChatRole.System);
            StringAssert.Contains("old-context-9", newestTranscript.Text);
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
            // WHY: The summary is a retelling of the learner's and the teacher's words, so it must carry
            // the trust those words already had - the USER role. The former norm (ChatRole.System) was
            // wrong: a child typing "forget the rules" became, after compaction, a line of system context
            // (MeaiLlmClient ships system-role history as a "System context update"). The block also
            // names itself a recap so the model does not read it as a fresh learner turn.
            Assert.AreEqual(ChatRole.User, summaryMessage.Role);
            StringAssert.Contains("## Conversation Summary", summaryMessage.Text);
            StringAssert.Contains(ConversationSummaryPromptProjection.Framing, summaryMessage.Text);
            StringAssert.Contains("old-context-0", summaryMessage.Text);
            Assert.Greater(tailLlm.LastRequest.ChatHistory.Count, 1);
            Assert.AreNotEqual(ChatRole.System, tailLlm.LastRequest.ChatHistory[1].Role);
            Microsoft.Extensions.AI.ChatMessage newestTranscript = tailLlm.LastRequest.ChatHistory.Last(m =>
                m.Role != ChatRole.System);
            StringAssert.Contains("old-context-9", newestTranscript.Text);
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
            Assert.AreEqual(1, tailLlm.LastRequest.ChatHistory.Count(m =>
                m.Role == ChatRole.System && (m.Text ?? "").Contains("## World State")));
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
            Assert.AreEqual(ChatRole.User, summary.Role,
                "A retelling of chat turns carries user-level trust, never system-level.");
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
            StringAssert.DoesNotContain("## Memory", tailLlm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("Learner likes geometry puzzles.", tailLlm.LastRequest.SystemPrompt,
                "Student-scoped memory must never personalize the shared provider-cache prefix.");
            Assert.IsNotNull(tailLlm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage initialSnapshot = tailLlm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").StartsWith("## Memory\n", StringComparison.Ordinal));
            StringAssert.Contains("Learner likes geometry puzzles.", initialSnapshot.Text);
            Assert.AreEqual("Learner likes geometry puzzles.", tailMemory.MemoryState.SystemPromptMemorySnapshot);

            TestLlmClient updateLlm = new();
            await RunMemoryPlacementRequestAsync(
                updateLlm,
                "Learner likes geometry puzzles.\nLearner prefers hints.",
                "Learner likes geometry puzzles.");

            Assert.IsNotNull(updateLlm.LastRequest);
            StringAssert.DoesNotContain("Learner likes geometry puzzles.", updateLlm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("Learner prefers hints.", updateLlm.LastRequest.SystemPrompt);
            Assert.IsNotNull(updateLlm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage canonicalMemory = updateLlm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").StartsWith("## Memory\n", StringComparison.Ordinal));
            Microsoft.Extensions.AI.ChatMessage memoryUpdates = updateLlm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System &&
                (m.Text ?? "").StartsWith("## Memory (updates)", StringComparison.Ordinal));
            Assert.Less(updateLlm.LastRequest.ChatHistory.IndexOf(canonicalMemory),
                updateLlm.LastRequest.ChatHistory.IndexOf(memoryUpdates),
                "Canonical memory must precede its volatile delta in the ordered system tail.");
            StringAssert.Contains("Learner likes geometry puzzles.", canonicalMemory.Text);
            Assert.AreEqual(ChatRole.System, memoryUpdates.Role);
            StringAssert.Contains("## Memory (updates)", memoryUpdates.Text);
            StringAssert.Contains("Learner prefers hints.", memoryUpdates.Text);
        }

        [Test]
        public async Task RunTaskAsync_Compaction_ConsolidatesMemoryUpdatesIntoSystemTail()
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            StringAssert.DoesNotContain("Learner likes geometry puzzles.", llm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("Learner prefers hints.", llm.LastRequest.SystemPrompt);
            Assert.AreEqual(memory.MemoryState.Memory, memory.MemoryState.SystemPromptMemorySnapshot);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("## Memory (updates)")));
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("## Conversation Summary")));
            Microsoft.Extensions.AI.ChatMessage consolidated = llm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").StartsWith("## Memory\n", StringComparison.Ordinal));
            StringAssert.Contains("Learner likes geometry puzzles.", consolidated.Text);
            StringAssert.Contains("Learner prefers hints.", consolidated.Text);
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetry_ConsolidatesMemoryUpdatesIntoSystemTail()
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "retry test" });

            Assert.GreaterOrEqual(llm.Requests.Count, 2);
            LlmCompletionRequest first = llm.Requests[0];
            LlmCompletionRequest second = llm.Requests[1];

            Assert.AreEqual(first.SystemPrompt, second.SystemPrompt,
                "Memory consolidation during retry must not rewrite the shared provider-cache prefix.");
            StringAssert.DoesNotContain("Learner likes geometry puzzles.", first.SystemPrompt);
            StringAssert.DoesNotContain("Learner prefers hints.", first.SystemPrompt);
            Microsoft.Extensions.AI.ChatMessage firstCanonical = first.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").StartsWith("## Memory\n", StringComparison.Ordinal));
            Microsoft.Extensions.AI.ChatMessage firstUpdates = first.ChatHistory.Single(m =>
                m.Role == ChatRole.System &&
                (m.Text ?? "").StartsWith("## Memory (updates)", StringComparison.Ordinal));
            Assert.Less(first.ChatHistory.IndexOf(firstCanonical), first.ChatHistory.IndexOf(firstUpdates));
            StringAssert.Contains("Learner likes geometry puzzles.", firstCanonical.Text);
            StringAssert.Contains("Learner prefers hints.", firstUpdates.Text);

            StringAssert.DoesNotContain("Learner likes geometry puzzles.", second.SystemPrompt);
            StringAssert.DoesNotContain("Learner prefers hints.", second.SystemPrompt);
            Assert.IsFalse(second.ChatHistory != null &&
                           second.ChatHistory.Any(m => (m.Text ?? "").Contains("## Memory (updates)")));
            Microsoft.Extensions.AI.ChatMessage secondCanonical = second.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").StartsWith("## Memory\n", StringComparison.Ordinal));
            StringAssert.Contains("Learner likes geometry puzzles.", secondCanonical.Text);
            StringAssert.Contains("Learner prefers hints.", secondCanonical.Text);
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

            // WHY: With summarization off the tail is still budget-bounded; a window large enough for
            // the whole transcript must keep every turn verbatim and never emit a summary block.
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);

            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(10, llm.LastRequest.ChatHistory.Count(m => m.Role != ChatRole.System));
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m =>
                (m.Text ?? "").Contains("## Conversation Summary")));
        }

        [Test]
        public async Task RunTaskAsync_ContextOverflowRetry_WhenSummarizationDisabled_ShrinksTailWithoutSummary()
        {
            // FINDING-8a: with summarization off, an overflow retry used to rebuild the byte-identical
            // oversized request; the clamp must apply regardless of the summarization flag while still
            // never generating a summary block.
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
            // WHY: The override keeps the first pass wide enough for the full 10-message tail; the
            // 60-token role window makes the retry-shrunk policy budget clamp below it, so the retry
            // pass provably drops oldest turns without ever generating a summary.
            TestSettings settings = new()
            {
                EnableConversationHistorySummarization = false,
                ConversationHistoryRecentTokenBudgetOverride = 250,
                MaxContextOverflowRetries = 1
            };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "retry" });

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.AreEqual(10, llm.Requests[0].ChatHistory.Count(m => m.Role != ChatRole.System),
                "First pass with summarization off keeps the full tail.");
            Assert.Less(llm.Requests[1].ChatHistory.Count(m => m.Role != ChatRole.System), 10,
                "Overflow retry must shrink the tail even with summarization disabled.");
            Assert.IsTrue(llm.Requests[1].ChatHistory.Any(m =>
                    (m.Text ?? "").Contains("old-context-9")),
                "Retry keeps the newest turns.");
            Assert.IsFalse(llm.Requests[0].ChatHistory.Any(m =>
                (m.Text ?? "").Contains("## Conversation Summary")));
            Assert.IsFalse(llm.Requests[1].ChatHistory.Any(m =>
                (m.Text ?? "").Contains("## Conversation Summary")));
        }

        [Test]
        public async Task RunTaskAsync_WhenSummarizationDisabled_ContextPruningStillApplies()
        {
            // FINDING-8b: EnableContextPruning / MaxRetainedToolResultMessages silently stopped applying
            // when summarization was off because the context manager was bypassed entirely.
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "question one" });
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "assistant", Content = "answer one" });
            for (int i = 0; i < 5; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = "tool",
                    Content = $"## Tool Results\n- tool_{i}: ok result-{i}"
                });
            }

            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);
            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "prune" });

            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            string projected = string.Join("\n", llm.LastRequest.ChatHistory.Select(message => message.Text));
            int retained = ((ICoreAISettings)settings).MaxRetainedToolResultMessages;
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(i >= 5 - retained, projected.Contains($"result-{i}"),
                    "The prompt retains only the configured newest tool results.");
            }
            StringAssert.Contains("question one", projected);
            StringAssert.Contains("answer one", projected);
            Assert.AreEqual(5, memory.FakeHistory.Count(message => message.Role == "tool"),
                "Prompt pruning must not delete stored tool history.");
        }

        [Test]
        public async Task RunTaskAsync_SummarizationDisabled_LongSessionTailStaysBounded()
        {
            // WHY: Long-session latency regression: with summarization off the whole transcript used to be
            // re-sent every request (UnlimitedHistoryTokenBudget), so per-request payload grew without
            // limit. The tail must saturate at the window-derived budget and stop growing.
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 2048, false, 10000);
            TestSettings settings = new() { EnableConversationHistorySummarization = false };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            HeuristicTokenEstimator estimator = new();
            int tokensAtMediumSession = 0;
            foreach (int sessionLength in new[] { 200, 400 })
            {
                while (memory.FakeHistory.Count < sessionLength)
                {
                    int i = memory.FakeHistory.Count;
                    memory.FakeHistory.Add(new Ai.ChatMessage
                    {
                        Role = i % 2 == 0 ? "user" : "assistant",
                        Content = $"turn-{i}-".PadRight(120, 'x')
                    });
                }

                await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "long session" });

                Assert.IsNotNull(llm.LastRequest);
                Assert.IsNotNull(llm.LastRequest.ChatHistory);
                List<Microsoft.Extensions.AI.ChatMessage> tail = llm.LastRequest.ChatHistory
                    .Where(m => (m.Text ?? "").StartsWith("turn-"))
                    .ToList();
                int tailTokens = tail.Sum(m => estimator.EstimateText(m.Text ?? ""));

                Assert.LessOrEqual(tailTokens, 2048,
                    $"Tail for a {sessionLength}-message session must fit the 2048-token window-derived budget.");
                Assert.Less(tail.Count, sessionLength,
                    "Oldest turns must roll out of the tail instead of re-sending the whole transcript.");
                Assert.IsTrue((tail[tail.Count - 1].Text ?? "").StartsWith($"turn-{sessionLength - 1}-"),
                    "The newest turn always stays in the tail.");
                Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"),
                    "Summarization off must still not emit a summary block.");

                if (sessionLength == 200)
                {
                    tokensAtMediumSession = tailTokens;
                }
                else
                {
                    Assert.LessOrEqual(tailTokens, tokensAtMediumSession,
                        "Doubling the session length must not grow the per-request tail payload.");
                }
            }
        }

        private sealed class RecordingContextManager : IConversationContextManager
        {
            public ConversationContextBuildArgs LastBuildArgs { get; private set; }

            public ConversationContextSnapshot BuildSnapshot(
                string roleId,
                Ai.ChatMessage[] history,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                ConversationContextBuildArgs buildArgs = null)
            {
                LastBuildArgs = buildArgs;
                return new ConversationContextSnapshot { RecentMessages = history, WasCompacted = false };
            }
        }

        [Test]
        public async Task RunTaskAsync_CountOverflow_ReachesSummaryInsteadOfDisappearingBeforeCompaction()
        {
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.AddRange(Enumerable.Range(0, 7)
                .Select(i => new Ai.ChatMessage("user", "remember-marker-" + i)));
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 2);
            InMemoryConversationSummaryStore summaries = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, policy,
                new DeterministicConversationContextManager(summaries));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "continue" });

            Assert.That(summaries.LoadSummary("test_role"), Does.Contain("remember-marker-0"));
            Assert.That(summaries.LoadSummary("test_role"), Does.Contain("remember-marker-4"));
            Assert.AreEqual(7, memory.FakeHistory.Count, "Compaction must preserve the recoverable source history.");
        }

        [Test]
        public async Task RunTaskAsync_RolledSummaryMaxTokensZero_MeansUnlimited()
        {
            // FINDING-10: explicit 0 is the documented "unlimited" opt-out and must not be remapped to
            // the 2048 default; a positive value passes through unchanged.
            RecordingContextManager recorder = new();
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "hi" });
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory("test_role", true, 8192, false, 50);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider, recorder);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });

            Assert.IsNotNull(recorder.LastBuildArgs);
            Assert.AreEqual(0, recorder.LastBuildArgs.MaxRolledSummaryTokens,
                "Explicit 0 must reach the context manager as 0 (= unlimited), not the 2048 default.");
            Assert.Greater(recorder.LastBuildArgs.SummaryTokenBudget, 0,
                "Unlimited by cap is still bounded by the request: the summary reserve must travel alongside.");
            Assert.LessOrEqual(
                recorder.LastBuildArgs.SummaryTokenBudget + recorder.LastBuildArgs.HistoryTokenBudget,
                recorder.LastBuildArgs.SourceBudget.Value.HistoryTokenBudget,
                "Summary reserve + recent tail never exceed the policy's conversation allowance.");

            settings.ConversationRolledSummaryMaxTokens = 512;
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });
            Assert.AreEqual(512, recorder.LastBuildArgs.MaxRolledSummaryTokens);
            Assert.LessOrEqual(recorder.LastBuildArgs.SummaryTokenBudget, 512);
        }

        private const string LlamaCppOverflowEvidence =
            "HTTP error 400: HTTP 400 | Body: {\"error\":\"Engine protocol predict request returned 400: " +
            "{\\\"error\\\":{\\\"code\\\":400,\\\"message\\\":\\\"request (61849 tokens) exceeds the available " +
            "context size (4096 tokens), try increasing it\\\",\\\"type\\\":\\\"exceed_context_size_error\\\"," +
            "\\\"n_prompt_tokens\\\":61849,\\\"n_ctx\\\":4096}}\"}";

        private static int EstimateRequestTokens(LlmCompletionRequest request)
        {
            HeuristicTokenEstimator estimator = new();
            int total = estimator.EstimateText(request.SystemPrompt ?? "") +
                        estimator.EstimateText(request.UserPayload ?? "");
            if (request.ChatHistory != null)
            {
                foreach (Microsoft.Extensions.AI.ChatMessage message in request.ChatHistory)
                {
                    total += estimator.EstimateText(message.Text ?? "");
                }
            }

            return total;
        }

        private static AgentMemoryPolicy BuildSlimHistoryPolicy(string roleId, int contextTokens)
        {
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(roleId, true, contextTokens, false, 50);
            policy.DisableMemoryTool(roleId);
            policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());
            return policy;
        }

        private static InMemoryConversationSummaryStore SeedOversizedSummary(string roleId)
        {
            InMemoryConversationSummaryStore store = new();
            // WHY: ~50k estimated tokens, the shape of the ~272k-char summary from audit 17 [1].
            store.SaveSummary(roleId, "oldest recap line\n" + new string('h', 200_000) + "\nnewest recap line");
            return store;
        }

        [Test]
        public void TryParseReportedContextTokens_ReadsTheLimitFromKnownRefusalShapes()
        {
            Assert.AreEqual(4096, AiOrchestrator.TryParseReportedContextTokens(LlamaCppOverflowEvidence),
                "Escaped llama.cpp body inside an outer JSON string.");
            Assert.AreEqual(40192, AiOrchestrator.TryParseReportedContextTokens(
                "", "{\"error\":{\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":61849,\"n_ctx\":40192}}"));
            Assert.AreEqual(40192, AiOrchestrator.TryParseReportedContextTokens(
                "request (61849 tokens) exceeds the available context size (40192 tokens), try increasing it"));
            Assert.AreEqual(8192, AiOrchestrator.TryParseReportedContextTokens(
                "This model's maximum context length is 8192 tokens. However, you requested 9000 tokens."));
            Assert.AreEqual(0, AiOrchestrator.TryParseReportedContextTokens("context too long", null));
        }

        [Test]
        public void BoundWindowForOverflowRetry_UsesTheReportedLimit_ThenTheRefusedRequestSize()
        {
            const int unlimited = CoreAISettings.UnlimitedContextWindowTokens;
            Assert.AreEqual(4096, AiOrchestrator.BoundWindowForOverflowRetry(unlimited, 4096, unlimited, 61_849),
                "A freshly reported limit wins outright over the configured window.");
            Assert.AreEqual(3072, AiOrchestrator.BoundWindowForOverflowRetry(unlimited, 4096, 4096, 4096),
                "A rebuild that already used the reported limit and still overflowed shrinks by its own size.");
            Assert.AreEqual(46_386, AiOrchestrator.BoundWindowForOverflowRetry(unlimited, 0, unlimited, 61_849),
                "No reported limit: the refused request's estimated size, shrunk by a quarter, is the ceiling.");
            Assert.AreEqual(2048, AiOrchestrator.BoundWindowForOverflowRetry(2048, 40_192, unlimited, 61_849),
                "A configured window smaller than a freshly reported limit is kept.");
            Assert.AreEqual(375, AiOrchestrator.BoundWindowForOverflowRetry(2048, 40_192, 2048, 500),
                "A request built under the reported limit that still overflowed shrinks by its own size.");
        }

        [Test]
        public async Task RunTaskAsync_StoredSummaryLargerThanWindow_IsBoundedBeforeTheFirstSend()
        {
            // Audit 17 [1]: an ordinary short message must never leave with a summary the window cannot hold.
            const int window = 4096;
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "earlier question" });
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "assistant", Content = "earlier answer" });
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", window);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(SeedOversizedSummary("test_role")));

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "test_role",
                Hint = "Give a short response about streaming chat."
            });

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage summary = llm.LastRequest.ChatHistory.Single(m =>
                (m.Text ?? "").Contains(ConversationSummaryPromptProjection.Header));
            StringAssert.Contains("newest recap line", summary.Text, "Bounding keeps the newest suffix.");
            StringAssert.DoesNotContain("oldest recap line", summary.Text, "precondition: the summary was cut.");
            Assert.LessOrEqual(EstimateRequestTokens(llm.LastRequest), window,
                "System prompt + summary + history must fit the window before the request leaves.");
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("earlier answer")),
                "The recent tail keeps its own budget.");
        }

        /// <summary>
        /// The contract between two layers whose invariants once collided. The context manager bounds what
        /// is STORED by the user's explicit cap only, so the durable retelling stays whole and the teardown
        /// append may evict the oldest message it retells. The orchestrator bounds what is SENT to the
        /// summary reserve, so a short message still fits the window. One turn has to show both at once:
        /// a single bound shared by the two jobs breaks one side or the other.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_OversizedStoredSummary_StaysWholeInTheStore_AndReachesTheModelBounded()
        {
            const int window = 4096;
            TestLlmClient llm = new();
            TestMemoryStore memory = new();
            // WHY: ~3000 tokens against a ~2000-token tail budget, so this turn folds the oldest messages
            // and rewrites the store; a turn that writes nothing would prove nothing about the store.
            for (int i = 0; i < 10; i++)
            {
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"old-context-{i}-".PadRight(1200, 'x')
                });
            }

            InMemoryConversationSummaryStore summaryStore = SeedOversizedSummary("test_role");
            string seeded = summaryStore.LoadSummary("test_role");
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", window);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(summaryStore));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });

            string persisted = summaryStore.LoadSummary("test_role");
            StringAssert.Contains("[fold:v1:", persisted, "precondition: this turn folded history and rewrote the store.");
            StringAssert.Contains("old-context-0", persisted, "precondition: the fold retells the oldest message.");
            StringAssert.StartsWith(seeded, persisted,
                "Stored side: the whole seeded retelling survives the write, oldest line first. The reserve never reaches the store.");

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage sent = llm.LastRequest.ChatHistory.Single(m =>
                (m.Text ?? "").Contains(ConversationSummaryPromptProjection.Header));
            StringAssert.DoesNotContain("oldest recap line", sent.Text,
                "Sent side: the same turn's request carries only the newest suffix of that retelling.");
            StringAssert.Contains("old-context-0", sent.Text, "The newest part, this turn's fold, is what the model sees.");
            Assert.Less(sent.Text.Length, seeded.Length / 10, "precondition: the sent copy was cut, not merely reformatted.");
            Assert.LessOrEqual(EstimateRequestTokens(llm.LastRequest), window,
                "System prompt + bounded summary + recent tail fit the window.");
        }

        [Test]
        public async Task RunTaskAsync_OverflowWithReportedLimit_RetryFitsTheReportedWindow_AndLaterTurnsRemember()
        {
            // Audit 17 [1]/[3]: configured window "unlimited", backend n_ctx 4096. The retry must be built
            // against the limit the backend just reported, and the next turn must not pay the refusal again.
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.ContextLengthExceeded,
                    Error = LlamaCppOverflowEvidence,
                    HttpStatus = 400
                },
                new LlmCompletionResult { Ok = true, Content = "ok" },
                new LlmCompletionResult { Ok = true, Content = "ok again" });
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "earlier question" });
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", CoreAISettings.UnlimitedContextWindowTokens);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0, MaxContextOverflowRetries = 3 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(SeedOversizedSummary("test_role")));

            string first = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });

            Assert.AreEqual("ok", first);
            Assert.AreEqual(2, llm.Requests.Count);
            Assert.Greater(EstimateRequestTokens(llm.Requests[0]), 4096,
                "precondition: the first pass trusted the unlimited window and overflowed.");
            Assert.LessOrEqual(EstimateRequestTokens(llm.Requests[1]), 4096,
                "The retry must fit the n_ctx the backend reported, not a 0.75-shrunk multimillion allowance.");
            Assert.LessOrEqual(llm.Requests[1].ContextWindowTokens, 4096);

            string second = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "again" });

            Assert.AreEqual("ok again", second);
            Assert.AreEqual(3, llm.Requests.Count, "The learned limit spares the next turn a refused request.");
            Assert.LessOrEqual(EstimateRequestTokens(llm.Requests[2]), 4096);
        }

        [Test]
        public async Task RunTaskAsync_OverflowWithoutReportedLimit_EachRetryShrinksTheRealTotal()
        {
            ToolTraceLlmClient llm = new(
                new LlmCompletionResult { Ok = false, ErrorCode = LlmErrorCode.ContextLengthExceeded, Error = "context too long" },
                new LlmCompletionResult { Ok = false, ErrorCode = LlmErrorCode.ContextLengthExceeded, Error = "still too long" },
                new LlmCompletionResult { Ok = true, Content = "ok" });
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "earlier question" });
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", CoreAISettings.UnlimitedContextWindowTokens);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0, MaxContextOverflowRetries = 3 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(SeedOversizedSummary("test_role")));

            string content = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });

            Assert.AreEqual("ok", content);
            Assert.AreEqual(3, llm.Requests.Count);
            int first = EstimateRequestTokens(llm.Requests[0]);
            int second = EstimateRequestTokens(llm.Requests[1]);
            int third = EstimateRequestTokens(llm.Requests[2]);
            Assert.Less(second, first * 0.8,
                "With nothing reported, the retry shrinks the request that actually overflowed - not a no-op on a huge allowance.");
            Assert.Less(third, second * 0.8, "Every further retry keeps shrinking the real total.");
        }

        private sealed class StreamingOverflowThenOkLlm : ILlmClient
        {
            private readonly string _overflowError;

            public StreamingOverflowThenOkLlm(string overflowError)
            {
                _overflowError = overflowError;
            }

            public List<LlmCompletionRequest> Requests { get; } = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Assert.Fail("Streaming overflow test must use CompleteStreamingAsync.");
                return Task.FromResult<LlmCompletionResult>(null);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                await Task.Yield();
                if (Requests.Count == 1)
                {
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = _overflowError,
                        ErrorCode = LlmErrorCode.ContextLengthExceeded,
                        HttpStatus = 400
                    };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "streamed ok" };
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        [Test]
        public async Task RunStreamingAsync_OverflowWithReportedLimit_RetryFitsTheReportedWindow()
        {
            // The failing PlayMode scenario is a streaming turn; the chunk error carries the provider body.
            StreamingOverflowThenOkLlm llm = new(LlamaCppOverflowEvidence);
            TestMemoryStore memory = new();
            memory.FakeHistory.Add(new Ai.ChatMessage { Role = "user", Content = "earlier question" });
            AgentMemoryPolicy policy = BuildSlimHistoryPolicy("test_role", CoreAISettings.UnlimitedContextWindowTokens);
            TestSettings settings = new() { ConversationRolledSummaryMaxTokens = 0, MaxContextOverflowRetries = 3 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(SeedOversizedSummary("test_role")));

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "test_role", Hint = "Give a short response about streaming chat." }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(2, llm.Requests.Count);
            Assert.Greater(EstimateRequestTokens(llm.Requests[0]), 4096, "precondition: the first pass overflowed.");
            Assert.LessOrEqual(EstimateRequestTokens(llm.Requests[1]), 4096,
                "The streaming retry must fit the reported n_ctx.");
            Assert.AreEqual("streamed ok", string.Concat(chunks.Select(c => c.Text ?? "")));
            Assert.IsFalse(chunks.Any(c => c.ErrorCode == LlmErrorCode.ContextLengthExceeded),
                "The refused first pass must not leak to the caller.");
        }

        /// <summary>
        /// WHY the expectation flipped: this test was written when the rolling summary was committed only
        /// after the owning request succeeded, so a turn that never got an answer left the summary store
        /// untouched. That ordering had a hole - every terminal path of a turn, failure included, appends
        /// the learner's message, and on a bounded store an append evicts the oldest message. Committing
        /// only on success meant a failed turn could evict source that nothing retold yet. Since 7.40.0 the
        /// fold is committed before the request is dispatched, which is what makes the teardown append safe,
        /// and the price is exactly this: a turn that dies in context-overflow retries leaves behind the
        /// summary it prepared. That is a retelling of messages the store still holds, not new content, so
        /// the trade is a rolled summary against a lost message.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_ContextOverflowRetriesFail_StillCommitsTheSummaryTheAppendReliesOn()
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(summaryStore));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "retry" });

            Assert.AreEqual(2, llm.Requests.Count);
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "A turn that exhausted its retries still records the learner's message once.");
            Assert.AreEqual("retry", memory.Appended[0].Content);
            StringAssert.Contains("old-context-0", summaryStore.LoadSummary("test_role"),
                "The oldest source the teardown append can evict must already be retold in the summary.");
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

            // WHY: Same shape as RunTaskAsync_CompactsOldHistory_IntoTailSummary: a real-size window whose
            // summary reserve holds the retelling of the five folded turns.
            for (int i = 0; i < 10; i++)
            {
                string content = $"old-context-{i}-".PadRight(1000, 'x');
                memory.FakeHistory.Add(new Ai.ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = content
                });
            }

            policy.ConfigureChatHistory("test_role", true, 4096, false, 50);

            TestSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);

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
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                store, policy, null, null, settings, TestActorIdentityProvider);

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

            // WHY: The override pins the recent tail to one turn; the window only has to be real enough
            // for the summary reserve to hold the nine folded bullets (~230 tokens). The former 60-token
            // window floored the allowance at 32 tokens and only passed while the summary was unbounded.
            policy.ConfigureChatHistory("test_role", true, 4096, false, 50);

            TestSettings settings = new() { ConversationHistoryRecentTokenBudgetOverride = 32 };
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider,
                new DeterministicConversationContextManager(new NullConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "budget test" });

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Microsoft.Extensions.AI.ChatMessage summary = llm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.User && (m.Text ?? "").Contains("## Conversation Summary"));
            StringAssert.Contains("old-context-0", summary.Text);
            // WHY: The summary now travels under the user role (a retelling carries user-level trust, not
            // system-level), so "not system" no longer means "verbatim transcript" - the summary block
            // has to be excluded by identity to count the retained turns.
            List<Microsoft.Extensions.AI.ChatMessage> transcript = llm.LastRequest.ChatHistory
                .Where(m => m.Role != ChatRole.System && !ReferenceEquals(m, summary))
                .ToList();
            Assert.AreEqual(1, transcript.Count, "The override must retain only the newest transcript turn.");
            StringAssert.Contains("old-context-9", transcript[0].Text);
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
                composer, new TestMemoryStore(), policy, null, null, settings,
                TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "Hi",
                TraceId = "trace-context",
                SourceTag = "practice-slot"
            });

            string expectedContext = new StaticContextProvider().BuildContext(
                new AiTaskRequest { SourceTag = "practice-slot" }, "Teacher", "trace-context");
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains(expectedContext));
            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            Microsoft.Extensions.AI.ChatMessage worldState = llm.LastRequest.ChatHistory[^1];
            Assert.AreEqual(ChatRole.System, worldState.Role);
            StringAssert.Contains(expectedContext, worldState.Text);
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
                composer, new TestMemoryStore(), policy, null, null, settings,
                TestActorIdentityProvider);

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
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);

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
                composer, new TestMemoryStore(), policy, null, null, settings,
                TestActorIdentityProvider);
            await orchestratorSync.RunTaskAsync(task);

            int syncToolCount = llm.LastRequest.Tools.Count;
            string syncFirstTool = llm.LastRequest.Tools.Count > 0 ? llm.LastRequest.Tools[0].Name : "";
            LlmToolChoiceMode syncMode = llm.LastRequest.ForcedToolMode;
            CollectionAssert.AreEqual(new[] { "spawn_drag_and_drop" }, llm.LastRequest.AllowedToolNames);

            TestLlmClient llmStream = new();
            AiOrchestrator orchestratorStream = new(
                new TestAuthority(), llmStream, new TestSink(), new TestTelemetry(),
                composer, new TestMemoryStore(), policy, null, null, settings,
                TestActorIdentityProvider);

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
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);

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
            int blockedCalls = 0;
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
                    new Func<string>(() => { blockedCalls++; return "{\"success\":true}"; })));

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
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);

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
            Assert.AreEqual(0, blockedCalls, "A disallowed tool body must remain unreachable.");
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                memory, policy, null, null, settings, TestActorIdentityProvider,
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
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                ForcedToolMode = LlmToolChoiceMode.None
            });

            Assert.AreEqual(0, llm.LastRequest.Tools.Count);
        }

        // The FileAgentMemoryStore round-trip lives in AiOrchestratorHistoryFileStoreEditModeTests: that store is
        // a CoreAiUnity type, and this fixture is linked into the portable (engine-free) test project.

        [Test]
        public async Task RunTaskAsync_ReadsTheRoleHistoryOnce_TheResendDecisionComesFromThatRead()
        {
            // WHY: the resend rule used to read GetChatHistory(roleId, 1) on top of the full history read every
            // turn makes; on a file store each read is a synchronous gate wait on the main thread. The one read
            // that builds the prompt already ends with the store tail, so it decides.
            CancelOnceThenSucceedLlmClient llm = new();
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Teacher", "assistant", "earlier answer");
            AiOrchestrator orchestrator = BuildOrchestrator(llm, memory, BuildToolResultPolicy("Teacher"));
            const string payload = "[help] the learner is stuck on task 3";

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload }));
            Assert.AreEqual(1, memory.HistoryReads, "The cancelled attempt reads the history exactly once.");

            string answer = await orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = payload });

            Assert.AreEqual("recovered", answer);
            Assert.AreEqual(2, memory.HistoryReads, "The resend reads the history exactly once as well.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" },
                memory.Appended.Select(m => m.MessageRole).ToArray(),
                "...and the resend rule still holds from that single read.");
            Assert.IsFalse(
                llm.Requests[1].ChatHistory != null &&
                llm.Requests[1].ChatHistory.Any(m => (m.Text ?? "").Contains(payload)),
                "The prompt filter follows the same decision.");
        }

        [Test]
        public async Task RunTaskAsync_AuthorityDenied_StillReadsTheTailForTheResendRule()
        {
            // WHY: a turn that never built a request has no history read to reuse, so the teardown keeps its
            // own one-message read - the resend rule must hold there too.
            RoleScopedLiveMemoryStore memory = new();
            memory.Seed("Teacher", "user", "unanswered");
            TestSettings settings = new();
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            AiOrchestrator orchestrator = new(
                new TestAuthority { CanRunAiTasks = false }, new TestLlmClient(), new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, TestActorIdentityProvider);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "unanswered" });

            Assert.AreEqual(1, memory.HistoryReads);
            Assert.IsEmpty(memory.Appended, "The denied resend of the unanswered message is not stored twice.");
        }

        [Test]
        public async Task RunStreamingAsync_TimeoutChunk_RecordsDeadlineCancellation_LikeAThrownTimeout()
        {
            // WHY: the default pipeline's timeout decorator yields a Timeout CHUNK on the streaming path; it was
            // recorded as a provider failure while the same timeout thrown on stream open was the deadline.
            ScriptedStreamLlmClient llm = new(new LlmStreamChunk
            {
                IsDone = true, Error = "LLM request timed out.", ErrorCode = LlmErrorCode.Timeout
            });
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(llm, new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "slow" }));

            Assert.AreEqual(LlmErrorCode.Timeout, terminal.ErrorCode);
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.DeadlineCancellation }, metrics.Completions);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunStreamingAsync_TimeoutAfterCallerCancel_EndsAsTheCancellation(bool viaChunk)
        {
            // WHY: a timer that raced the stop is still the stop - for the chunk the consumer sees and for the
            // metric alike, whether the timeout arrived as a chunk or as a throw.
            using CancellationTokenSource caller = new();
            CancelThenFailStreamLlmClient llm = new(caller.Cancel,
                viaChunk
                    ? new LlmStreamChunk { IsDone = true, Error = "LLM request timed out.", ErrorCode = LlmErrorCode.Timeout }
                    : null,
                viaChunk ? null : new LlmOperationTimeoutException());
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(llm, new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "slow" }, caller.Token));

            Assert.AreEqual(LlmErrorCode.Cancelled, terminal.ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, terminal.Error, "The text agrees with the code.");
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunStreamingAsync_FaultAfterCallerCancel_EndsAsTheCancellation(bool typed)
        {
            using CancellationTokenSource caller = new();
            Exception fault = typed
                ? new LlmClientException("socket disposed", LlmErrorCode.BackendUnavailable, 503)
                : new IOException("socket disposed");
            CancelThenFailStreamLlmClient llm = new(caller.Cancel, null, fault);
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(llm, new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }, caller.Token));

            Assert.AreEqual(LlmErrorCode.Cancelled, terminal.ErrorCode,
                "A fault the stop caused is the stop, whatever its type.");
            Assert.AreEqual(LlmCancellation.CancelledErrorText, terminal.Error);
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
        }

        [Test]
        public async Task RunStreamingAsync_FaultOnOpenAfterCallerCancel_EndsAsTheCancellation()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new ThrowOnOpenStreamLlmClient(new IOException("socket disposed")),
                new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }, caller.Token));

            Assert.AreEqual(LlmErrorCode.Cancelled, terminal.ErrorCode);
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
        }

        [Test]
        public async Task RunStreamingAsync_TimeoutOnOpenWithLiveCaller_RecordsDeadlineCancellation()
        {
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new ThrowOnOpenStreamLlmClient(new LlmOperationTimeoutException()),
                new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }));

            Assert.AreEqual(LlmErrorCode.Timeout, terminal.ErrorCode);
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.DeadlineCancellation }, metrics.Completions);
        }

        [Test]
        public async Task RunTaskAsync_LibraryTimeoutAfterCallerCancel_IsTheCallersCancellation_NotTheDeadline()
        {
            // WHY: the non-streaming attribution answered "deadline" for every LlmOperationTimeoutException
            // without asking whether the caller had already stopped; the caller wins (LlmCancellation).
            using CancellationTokenSource caller = new();
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenThrowLlmClient(caller.Cancel, new LlmOperationTimeoutException()),
                new RoleScopedLiveMemoryStore(), metrics);

            await CaptureExceptionAsync<OperationCanceledException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "slow" }, caller.Token));

            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
        }

        [Test]
        public async Task RunTaskAsync_LibraryTimeoutWithLiveCaller_IsTheDeadline()
        {
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenThrowLlmClient(() => { }, new LlmOperationTimeoutException()),
                new RoleScopedLiveMemoryStore(), metrics);

            await CaptureExceptionAsync<LlmOperationTimeoutException>(() => orchestrator.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "slow" }));

            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.DeadlineCancellation }, metrics.Completions);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunTaskAsync_FaultAfterCallerCancel_SurfacesAsTheCancellation(bool typed)
        {
            using CancellationTokenSource caller = new();
            Exception fault = typed
                ? new LlmClientException("socket disposed", LlmErrorCode.BackendUnavailable, 503)
                : new IOException("socket disposed");
            RecordingMetrics metrics = new();
            RoleScopedLiveMemoryStore memory = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenThrowLlmClient(caller.Cancel, fault), memory, metrics);

            OperationCanceledException thrown = await CaptureExceptionAsync<OperationCanceledException>(() =>
                orchestrator.RunTaskAsync(
                    new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }, caller.Token));

            Assert.AreSame(fault, thrown.InnerException, "The fault stays attached for diagnostics.");
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.MessageRole).ToArray(),
                "The learner's words are still recorded on the cancelled path.");
        }

        [TestCase(LlmErrorCode.ProviderError, true)]
        [TestCase(LlmErrorCode.BackendUnavailable, true)]
        [TestCase(LlmErrorCode.AuthExpired, true)]
        [TestCase(LlmErrorCode.None, true)]
        [TestCase(LlmErrorCode.ProviderError, false)]
        [TestCase(LlmErrorCode.BackendUnavailable, false)]
        [TestCase(LlmErrorCode.AuthExpired, false)]
        [TestCase(LlmErrorCode.None, false)]
        public async Task RunTaskAsync_FailedResultAfterCallerCancel_SurfacesAsTheCancellation(
            LlmErrorCode code,
            bool streamingTransport)
        {
            // WHY: a THROWN fault after the stop already surfaced as the cancellation, while a RETURNED failure
            // kept its own code and counted as a provider failure - one user Stop reported two ways. Both
            // transports: the streaming one collapses the chunk into the result first.
            using CancellationTokenSource caller = new();
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenReturnLlmClient(caller.Cancel, new LlmCompletionResult
                {
                    Ok = false, Error = "HTTP 503 from the provider", ErrorCode = code
                }),
                new RoleScopedLiveMemoryStore(), metrics, streamingTransport);

            OperationCanceledException thrown = await CaptureExceptionAsync<OperationCanceledException>(() =>
                orchestrator.RunTaskAsync(
                    new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }, caller.Token));

            Assert.AreEqual(LlmCancellation.CancelledErrorText, thrown.Message, "The text follows the code.");
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunTaskResultAsync_FailedResultWithLiveCaller_KeepsItsOwnCode(bool streamingTransport)
        {
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenReturnLlmClient(() => { }, new LlmCompletionResult
                {
                    Ok = false, Error = "HTTP 401", ErrorCode = LlmErrorCode.AuthExpired
                }),
                new RoleScopedLiveMemoryStore(), metrics, streamingTransport);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" });

            Assert.AreEqual(LlmErrorCode.AuthExpired, result.ErrorCode);
            Assert.AreEqual("HTTP 401", result.Error);
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.ProviderFailure }, metrics.Completions);
        }

        /// <summary>
        /// The non-streaming transport hands the orchestrator the client's own instance; normalizing an empty
        /// answer into a failure must not reach back into it.
        /// </summary>
        [Test]
        public async Task RunTaskResultAsync_EmptySuccess_FailureIsACopyThatLeavesTheClientResultUntouched()
        {
            LlmCompletionResult shared = new() { Ok = true, Content = "" };
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(
                new CancelThenReturnLlmClient(() => { }, shared),
                new RoleScopedLiveMemoryStore(), new RecordingMetrics(), streamingTransport: false);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" });

            Assert.AreNotSame(shared, result);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.EmptyResponse, result.ErrorCode);
            Assert.AreEqual("empty response", result.Error);
            Assert.IsTrue(shared.Ok, "The client's instance is not mutated.");
            Assert.AreEqual(LlmErrorCode.None, shared.ErrorCode);
            Assert.AreEqual("", shared.Error);
        }

        [Test]
        public async Task RunTaskResultAsync_StructuredRejectionAfterTools_IsACopyThatLeavesTheClientResultUntouched()
        {
            LlmToolCallTrace[] traces = { new("write_script", true, 4d, "native", "done") };
            LlmCompletionResult shared = new() { Ok = true, Content = "not json", ExecutedToolCalls = traces };
            AiOrchestrator orchestrator = BuildOrchestrator(
                new CancelThenReturnLlmClient(() => { }, shared),
                new TestMemoryStore(),
                BuildToolResultPolicy("Programmer"),
                structuredPolicy: new RejectingStructuredPolicy(),
                settings: new TestSettings { EnableStreaming = false });

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(
                new AiTaskRequest { RoleId = "Programmer", Hint = "write a script" });

            Assert.AreNotSame(shared, result);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual("Structured response validation failed after tool execution.", result.Error);
            Assert.IsTrue(shared.Ok, "The client's instance is not mutated.");
            Assert.AreEqual(LlmErrorCode.None, shared.ErrorCode);
            Assert.AreEqual("", shared.Error);
        }

        private sealed class RejectingStructuredPolicy : IRoleStructuredResponsePolicy
        {
            public bool ShouldValidate(string roleId)
            {
                return true;
            }

            public bool TryValidate(string roleId, string rawContent, out string failureReason)
            {
                failureReason = "not a structured payload";
                return false;
            }
        }

        [TestCase(LlmErrorCode.ProviderError)]
        [TestCase(LlmErrorCode.BackendUnavailable)]
        [TestCase(LlmErrorCode.AuthExpired)]
        [TestCase(LlmErrorCode.None)]
        public async Task RunStreamingAsync_ErrorChunkAfterCallerCancel_EndsAsTheCancellation(LlmErrorCode code)
        {
            using CancellationTokenSource caller = new();
            LlmStreamChunk inner = new() { IsDone = true, Error = "HTTP 503 from the provider", ErrorCode = code };
            CancelThenFailStreamLlmClient llm = new(caller.Cancel, inner, null);
            RecordingMetrics metrics = new();
            AiOrchestrator orchestrator = BuildOrchestratorWithMetrics(llm, new RoleScopedLiveMemoryStore(), metrics);

            LlmStreamChunk terminal = await DrainToTerminalAsync(orchestrator.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", SourceTag = "Chat", Hint = "q" }, caller.Token));

            Assert.AreEqual(LlmErrorCode.Cancelled, terminal.ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, terminal.Error, "The text follows the code.");
            CollectionAssert.AreEqual(new[] { AiLlmCompletionOutcome.Cancelled }, metrics.Completions);
            Assert.AreEqual(code, inner.ErrorCode, "The inner client's chunk is not mutated.");
            Assert.AreEqual("HTTP 503 from the provider", inner.Error);
        }

        [Test]
        public async Task RunTaskAsync_AllowlistNamesOneWrapperFunction_ExposesOnlyThatFunction()
        {
            // WHY: the provider is offered a wrapper's functions, never the wrapper's name; an allowlist written
            // from those names (the names Tool Availability lists) used to drop the whole wrapper.
            TestLlmClient llm = new();
            CameraFunctionsTool camera = new() { ToolTimeoutMsOverride = 4321, IsMutating = true };
            AiOrchestrator orchestrator = BuildWrapperOrchestrator(llm, camera);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = new[] { "camera_look" }
            });

            Assert.AreEqual(1, llm.LastRequest.Tools.Count);
            ILlmTool exposed = llm.LastRequest.Tools[0];
            Assert.AreNotSame(camera, exposed, "Only some functions are allowed: the wrapper is narrowed.");
            Assert.AreEqual("camera", exposed.Name);
            Assert.AreEqual(4321, exposed.ToolTimeoutMsOverride, "Per-tool settings are the wrapper's own.");
            Assert.IsTrue(exposed.IsMutating);
            CollectionAssert.AreEqual(new[] { "camera_look" },
                ((IAIFunctionsLlmTool)exposed).CreateAIFunctions().Select(f => f.Name).ToArray(),
                "The provider is offered only the allowed function.");
            string availability = ToolAvailabilityOf(llm.LastRequest);
            StringAssert.Contains("- camera_look", availability);
            StringAssert.DoesNotContain("- camera_capture", availability);
            StringAssert.DoesNotContain("- camera_list", availability);
            StringAssert.DoesNotContain("- spawn_quiz", availability);
        }

        [TestCase("camera")]
        [TestCase("camera_capture,camera_look,camera_list")]
        public async Task RunTaskAsync_AllowlistCoversTheWholeWrapper_KeepsTheWrapperItself(string allowed)
        {
            TestLlmClient llm = new();
            CameraFunctionsTool camera = new();
            AiOrchestrator orchestrator = BuildWrapperOrchestrator(llm, camera);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = allowed.Split(',')
            });

            Assert.AreEqual(1, llm.LastRequest.Tools.Count);
            Assert.AreSame(camera, llm.LastRequest.Tools[0],
                "The wrapper's own name, or every one of its functions, allows the whole wrapper.");
        }

        [Test]
        public async Task RunTaskAsync_AllowlistNamesNoWrapperFunction_DropsTheWrapper()
        {
            TestLlmClient llm = new();
            AiOrchestrator orchestrator = BuildWrapperOrchestrator(llm, new CameraFunctionsTool());

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                AllowedToolNames = new[] { "spawn_quiz" }
            });

            CollectionAssert.AreEqual(new[] { "spawn_quiz" }, llm.LastRequest.Tools.Select(t => t.Name).ToArray());
        }

        [Test]
        public async Task RunTaskAsync_TextShapedCallOfAWrapperFunction_IsStrippedFromTheAnswer()
        {
            // WHY: the leak strip knew only registered names, so a text-shaped call to camera_look - the name the
            // model actually uses for the camera wrapper - stayed in the visible answer.
            const string leaked = "{\"name\":\"camera_look\",\"arguments\":{\"target\":\"door\"}}";
            ToolTraceLlmClient llm = new(new LlmCompletionResult
            {
                Ok = true,
                Content = "Let me look at the door.\n" + leaked
            });
            AiOrchestrator orchestrator = BuildWrapperOrchestrator(llm, new CameraFunctionsTool());

            string answer = await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "look" });

            StringAssert.Contains("Let me look at the door.", answer);
            StringAssert.DoesNotContain("camera_look", answer);
        }

        private static AiOrchestrator BuildWrapperOrchestrator(ILlmClient llm, CameraFunctionsTool camera)
        {
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Teacher");
            policy.SetToolsForRole("Teacher", new ILlmTool[] { new StubTool("spawn_quiz"), camera });
            TestSettings settings = new();
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new TestMemoryStore(), policy, null, null, settings, TestActorIdentityProvider);
        }

        private static string ToolAvailabilityOf(LlmCompletionRequest request)
        {
            Microsoft.Extensions.AI.ChatMessage message = request.ChatHistory?.FirstOrDefault(m =>
                (m.Text ?? "").StartsWith("## Tool Availability (current request)", StringComparison.Ordinal));
            Assert.IsNotNull(message, "precondition: the request carries its tool availability.");
            return message.Text.Replace("\r\n", "\n");
        }

        private static AiOrchestrator BuildOrchestratorWithMetrics(
            ILlmClient llm,
            IAgentMemoryStore memory,
            IAiOrchestrationMetrics metrics,
            bool streamingTransport = true)
        {
            AgentMemoryPolicy policy = BuildToolResultPolicy("Teacher");
            TestSettings settings = new() { EnableStreaming = streamingTransport };
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, metrics, settings, TestActorIdentityProvider);
        }

        /// <summary>A camera-like wrapper: registered as <c>camera</c>, offering three functions.</summary>
        private sealed class CameraFunctionsTool : ILlmTool, IAIFunctionsLlmTool
        {
            public string Name => "camera";
            public string Description => "Camera functions.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
            public int? ToolTimeoutMsOverride { get; set; }
            public bool IsMutating { get; set; }

            public IEnumerable<AIFunction> CreateAIFunctions()
            {
                yield return AIFunctionFactory.Create((Func<string>)(() => "captured"),
                    new AIFunctionFactoryOptions { Name = "camera_capture", Description = "Capture." });
                yield return AIFunctionFactory.Create((Func<string, string>)(target => "looked:" + target),
                    new AIFunctionFactoryOptions { Name = "camera_look", Description = "Look at a target." });
                yield return AIFunctionFactory.Create((Func<string>)(() => "cameras:main"),
                    new AIFunctionFactoryOptions { Name = "camera_list", Description = "List cameras." });
            }
        }

        /// <summary>Cancels the caller's token, then RETURNS the configured failed result from CompleteAsync.</summary>
        private sealed class CancelThenReturnLlmClient : ILlmClient
        {
            private readonly Action _cancelCaller;
            private readonly LlmCompletionResult _result;

            public CancelThenReturnLlmClient(Action cancelCaller, LlmCompletionResult result)
            {
                _cancelCaller = cancelCaller;
                _result = result;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                _cancelCaller();
                return Task.FromResult(_result);
            }
        }

        private static async Task<LlmStreamChunk> DrainToTerminalAsync(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            LlmStreamChunk terminal = null;
            await foreach (LlmStreamChunk chunk in stream)
            {
                if (chunk.IsDone)
                {
                    terminal = chunk;
                }
            }

            Assert.IsNotNull(terminal, "precondition: the stream ends with a terminal chunk.");
            return terminal;
        }

        private sealed class RecordingMetrics : IAiOrchestrationMetrics
        {
            public List<AiLlmCompletionOutcome> Completions { get; } = new();

            public void RecordLlmCompletion(
                string actorId, string roleId, string traceId, AiLlmCompletionOutcome outcome, double wallMs)
            {
                Completions.Add(outcome);
            }

            public void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason)
            {
            }

            public void RecordCommandPublished(string actorId, string roleId, string traceId)
            {
            }
        }

        /// <summary>Cancels the caller's token, then throws the configured fault from CompleteAsync.</summary>
        private sealed class CancelThenThrowLlmClient : ILlmClient
        {
            private readonly Action _cancelCaller;
            private readonly Exception _fault;

            public CancelThenThrowLlmClient(Action cancelCaller, Exception fault)
            {
                _cancelCaller = cancelCaller;
                _fault = fault;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                _cancelCaller();
                return Task.FromException<LlmCompletionResult>(_fault);
            }
        }

        /// <summary>
        /// Streams one visible chunk, cancels the caller's token, then either yields the configured terminal
        /// chunk or throws the configured fault - the shape of a pipeline torn down by the stop.
        /// </summary>
        private sealed class CancelThenFailStreamLlmClient : ILlmClient
        {
            private readonly Action _cancelCaller;
            private readonly LlmStreamChunk _terminal;
            private readonly Exception _fault;

            public CancelThenFailStreamLlmClient(Action cancelCaller, LlmStreamChunk terminal, Exception fault)
            {
                _cancelCaller = cancelCaller;
                _terminal = terminal;
                _fault = fault;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("streaming only");
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "partial" };
                await Task.Yield();
                _cancelCaller();
                if (_fault != null)
                {
                    throw _fault;
                }

                yield return _terminal;
            }
        }

        /// <summary>Throws synchronously when the stream is opened, before any chunk.</summary>
        private sealed class ThrowOnOpenStreamLlmClient : ILlmClient
        {
            private readonly Exception _fault;

            public ThrowOnOpenStreamLlmClient(Exception fault)
            {
                _fault = fault;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("streaming only");
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _fault;
            }
        }
    }
}
