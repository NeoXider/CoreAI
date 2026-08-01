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
                memory, policy, null, null, settings);

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
                memory, policy, null, null, settings);

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
                memory, policy, null, null, settings);

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
                memory, policy, null, null, settings);

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
                memory, policy, null, null, settings);

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
            IAgentMemoryStore memory,
            AgentMemoryPolicy policy,
            IConversationContextManager contextManager = null)
        {
            TestSettings settings = new();
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, contextManager);
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
            List<Microsoft.Extensions.AI.ChatMessage> transcript = llm.LastRequest.ChatHistory
                .Where(m => m.Role != ChatRole.System)
                .ToList();
            Assert.AreEqual(15, transcript.Count,
                "History should be truncated to exactly MaxChatHistoryMessages");

            // Check that we got the *most recent* 15
            Assert.IsTrue(transcript[14].Text.Contains("Short msg 49"),
                "Last message should match the latest");
            Assert.IsTrue(transcript[0].Text.Contains("Short msg 35"),
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
            Microsoft.Extensions.AI.ChatMessage summary = llm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").Contains("## Conversation Summary"));
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
            Assert.AreEqual(ChatRole.System, summaryMessage.Role);
            StringAssert.Contains("## Conversation Summary", summaryMessage.Text);
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
                memory, policy, null, null, settings,
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
                memory, policy, null, null, settings,
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
                memory, policy, null, null, settings,
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
                memory, policy, null, null, settings,
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
                memory, policy, null, null, settings,
                new DeterministicConversationContextManager(new InMemoryConversationSummaryStore()));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "prune" });

            Assert.IsNotNull(llm.LastRequest.ChatHistory);
            int toolMessages = llm.LastRequest.ChatHistory.Count(m =>
                (m.Text ?? "").Contains("## Tool Results"));
            Assert.AreEqual(3, toolMessages,
                "Default MaxRetainedToolResultMessages (3) must prune stale tool results with summarization off.");
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("question one")),
                "Non-tool turns stay intact.");
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
                memory, policy, null, null, settings,
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
                memory, policy, null, null, settings, recorder);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });

            Assert.IsNotNull(recorder.LastBuildArgs);
            Assert.AreEqual(0, recorder.LastBuildArgs.MaxRolledSummaryTokens,
                "Explicit 0 must reach the context manager as 0 (= unlimited), not the 2048 default.");

            settings.ConversationRolledSummaryMaxTokens = 512;
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "test_role", Hint = "hi" });
            Assert.AreEqual(512, recorder.LastBuildArgs.MaxRolledSummaryTokens);
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
            Assert.IsFalse(llm.LastRequest.SystemPrompt.Contains("## Conversation Summary"));
            Microsoft.Extensions.AI.ChatMessage summary = llm.LastRequest.ChatHistory.Single(m =>
                m.Role == ChatRole.System && (m.Text ?? "").Contains("## Conversation Summary"));
            StringAssert.Contains("old-context-0", summary.Text);
            List<Microsoft.Extensions.AI.ChatMessage> transcript = llm.LastRequest.ChatHistory
                .Where(m => m.Role != ChatRole.System)
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
