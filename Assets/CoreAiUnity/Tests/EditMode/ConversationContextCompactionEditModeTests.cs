using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class ConversationContextCompactionEditModeTests
    {
        private sealed class FlatTokenEstimator : ITokenEstimator
        {
            private readonly int _perMessage;

            public FlatTokenEstimator(int perMessage)
            {
                _perMessage = Math.Max(1, perMessage);
            }

            public int EstimateText(string text) => _perMessage;
        }

        private sealed class RecordingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }
            public int CompleteCallCount { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "rolled_up_summary" });
            }
        }

        [Test]
        public void ConversationContextManagerFactories_DisableOrNullLlm_UsesDeterministic()
        {
            var store = new InMemoryConversationSummaryStore();
            ITokenEstimator est = new HeuristicTokenEstimator();
            IConversationContextManager a = ConversationContextManagerFactories.Create(false, store, est, null, null);
            IConversationContextManager b = ConversationContextManagerFactories.Create(true, store, est, null, null);
            Assert.IsInstanceOf<DeterministicConversationContextManager>(a);
            Assert.IsInstanceOf<DeterministicConversationContextManager>(b);
        }

        [Test]
        public void ConversationContextManagerFactories_EnableWithLlm_UsesSelectingWrapper()
        {
            var store = new InMemoryConversationSummaryStore();
            ITokenEstimator est = new HeuristicTokenEstimator();
            var llm = new RecordingLlmClient();
            IConversationContextManager m = ConversationContextManagerFactories.Create(true, store, est, llm, null);
            Assert.IsInstanceOf<SelectingConversationContextManager>(m);
        }

        [Test]
        public async Task SelectingManager_BuildSnapshotAsync_SkipsLlm_WhenArgsDisableCompaction()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            ITokenEstimator est = new FlatTokenEstimator(10);
            var mgr = new SelectingConversationContextManager(store, est, llm, LlmContextCompactionOptions.Default());

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "a" },
                new() { Role = "assistant", Content = "b" },
                new() { Role = "user", Content = "c" },
                new() { Role = "assistant", Content = "d" },
                new() { Role = "user", Content = "e" }
            };

            var roleConfig = new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 };
            var buildArgs = new ConversationContextBuildArgs
            {
                HistoryTokenBudget = 25,
                UseLlmContextCompaction = false
            };

            await mgr.BuildSnapshotAsync(
                    "r",
                    history,
                    roleConfig,
                    buildArgs,
                    "t",
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(0, llm.CompleteCallCount);
        }

        [Test]
        public async Task LlmAssisted_BuildSnapshotAsync_InvokesCompactionLlm_WhenPrefixEvicted()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            ITokenEstimator est = new FlatTokenEstimator(10);
            var mgr = new LlmAssistedConversationContextManager(store, est, llm, LlmContextCompactionOptions.Default());

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "a" },
                new() { Role = "assistant", Content = "b" },
                new() { Role = "user", Content = "c" },
                new() { Role = "assistant", Content = "d" },
                new() { Role = "user", Content = "e" }
            };

            var roleConfig = new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 };
            var buildArgs = new ConversationContextBuildArgs
            {
                HistoryTokenBudget = 25,
                UseLlmContextCompaction = true
            };

            ConversationContextSnapshot snap = await mgr.BuildSnapshotAsync(
                    "role1",
                    history,
                    roleConfig,
                    buildArgs,
                    "trace123",
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(1, llm.CompleteCallCount);
            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual(BuiltInAgentRoleIds.ContextCompactionAux, llm.LastRequest.AgentRoleId);
            Assert.AreEqual("trace123:compact", llm.LastRequest.TraceId);
            Assert.AreEqual(LlmToolChoiceMode.None, llm.LastRequest.ForcedToolMode);
            Assert.IsTrue(snap.WasCompacted);
            Assert.AreEqual("rolled_up_summary", snap.Summary);
            Assert.AreEqual(2, snap.RecentMessages.Length);
            Assert.IsNull(llm.LastRequest.ChatHistory, "Compaction must not replay tail as ChatHistory on auxiliary request.");
            Assert.AreEqual(
                LlmContextCompactionOptions.DefaultSystemPrompt,
                llm.LastRequest.SystemPrompt,
                "Main-role system prompt must not be used for compaction.");
            StringAssert.Contains("## Dialogue lines to fold into the rolling summary", llm.LastRequest.UserPayload);
            StringAssert.Contains("## Prior rolling summary", llm.LastRequest.UserPayload);
        }

        /// <summary>
        /// Guards that compaction never receives the orchestrator&apos;s main role system prose (Teacher contract, etc.).
        /// </summary>
        [Test]
        public async Task Compaction_Request_NeverUsesMainAgentSystem_CompactorPromptOnly()
        {
            const string forbiddenOrchestratorSystemSubstring =
                "Teacher agent REDOSCHOOL_ORCHESTRATOR_EXCLUSIVE_SYSTEM_MARKER_XQ9_NO_COMPACT";

            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            ITokenEstimator est = new FlatTokenEstimator(10);
            var mgr = new LlmAssistedConversationContextManager(store, est, llm);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = $"Hi — only transcript text {Environment.NewLine}(not system)" },
                new() { Role = "assistant", Content = "ok" },
                new() { Role = "user", Content = "next" },
                new() { Role = "assistant", Content = "tail" },
                new() { Role = "user", Content = "last" }
            };

            await mgr.BuildSnapshotAsync(
                    "roleX",
                    history,
                    new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                    new ConversationContextBuildArgs
                        { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                    "trace-no-leak",
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsNull(llm.LastRequest.ChatHistory);
            Assert.AreEqual(
                LlmContextCompactionOptions.DefaultSystemPrompt,
                llm.LastRequest.SystemPrompt);

            StringAssert.DoesNotContain(
                forbiddenOrchestratorSystemSubstring,
                llm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain(
                forbiddenOrchestratorSystemSubstring,
                llm.LastRequest.UserPayload);

            StringAssert.DoesNotContain("## Tool Contract", llm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("## Tool Contract", llm.LastRequest.UserPayload);
            StringAssert.StartsWith("trace-no-leak:compact", llm.LastRequest.TraceId);
        }

        [Test]
        public async Task Compaction_Request_CustomOptionSystem_OverridesTemplate()
        {
            const string compactOnly = "You only summarize transcripts. MAIN_ROLE_FORBIDDEN";
            var options = new LlmContextCompactionOptions { SystemPrompt = compactOnly };

            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            var mgr = new LlmAssistedConversationContextManager(
                store, new FlatTokenEstimator(10), llm, options);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "a" }, new() { Role = "assistant", Content = "b" },
                new() { Role = "user", Content = "c" }, new() { Role = "assistant", Content = "d" },
                new() { Role = "user", Content = "e" }
            };

            await mgr.BuildSnapshotAsync(
                    "r",
                    history,
                    new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                    new ConversationContextBuildArgs
                        { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                    "t",
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(compactOnly, llm.LastRequest.SystemPrompt);
        }

        // --- Edge-case tests added by audit gap remediation ---

        private sealed class ThrowingLlmClient : ILlmClient
        {
            private readonly Exception _ex;

            public ThrowingLlmClient(Exception ex) => _ex = ex;

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _ex;
            }
        }

        private sealed class WhitespaceResultLlmClient : ILlmClient
        {
            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "   \n  " });
            }
        }

        private static ChatMessage[] MakeHistory(int count) =>
            Enumerable.Range(0, count).Select(i => new ChatMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"msg{i}"
            }).ToArray();

        [Test]
        public void LlmAssisted_CancellationToken_Rethrows()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            var mgr = new LlmAssistedConversationContextManager(store, new FlatTokenEstimator(10), llm);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // TaskCanceledException inherits from OperationCanceledException.
            // The async state machine may wrap the cancellation as either subtype,
            // so we use CatchAsync (accepts derived types) — matching production catch blocks.
            Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await mgr.BuildSnapshotAsync(
                    "r", MakeHistory(5),
                    new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                    new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                    "t", cts.Token).ConfigureAwait(false);
            });
        }

        [Test]
        public async Task LlmAssisted_LlmFailure_FallsBackToBulletSummary()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new ThrowingLlmClient(new InvalidOperationException("LLM down"));
            var mgr = new LlmAssistedConversationContextManager(store, new FlatTokenEstimator(10), llm);

            ConversationContextSnapshot snap = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(6),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(snap.WasCompacted);
            Assert.IsNotNull(snap.Summary);
            Assert.IsNotEmpty(snap.Summary, "Bullet fallback should produce non-empty summary.");
        }

        [Test]
        public async Task LlmAssisted_EmptyLlmResult_FallsBackToBulletSummary()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new WhitespaceResultLlmClient();
            var mgr = new LlmAssistedConversationContextManager(store, new FlatTokenEstimator(10), llm);

            ConversationContextSnapshot snap = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(6),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(snap.WasCompacted);
            Assert.IsNotNull(snap.Summary);
            Assert.IsNotEmpty(snap.Summary, "Whitespace LLM result should fall back to bullet summary.");
        }

        [Test]
        public async Task LlmAssisted_LongSummary_TruncatedToMaxSummaryChars()
        {
            // Default MaxSummaryChars is 4000; generate content exceeding that.
            string longContent = new string('A', 6000);
            var store = new InMemoryConversationSummaryStore();
            var llm = new LongResultLlmClient(longContent);
            var mgr = new LlmAssistedConversationContextManager(store, new FlatTokenEstimator(10), llm);

            ConversationContextSnapshot snap = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(6),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(snap.WasCompacted);
            Assert.LessOrEqual(snap.Summary.Length, 4001, "Summary should be truncated to MaxSummaryChars (4000).");
            Assert.IsTrue(snap.Summary.EndsWith("…"), "Truncated summary should end with ellipsis.");
        }

        private sealed class LongResultLlmClient : ILlmClient
        {
            private readonly string _content;

            public LongResultLlmClient(string content) => _content = content;

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _content });
            }
        }

        [Test]
        public async Task LlmAssisted_LongPerMessageContent_TruncatedInPayload()
        {
            var store = new InMemoryConversationSummaryStore();
            var llm = new RecordingLlmClient();
            var mgr = new LlmAssistedConversationContextManager(store, new FlatTokenEstimator(10), llm);

            // Create history with one very long message
            ChatMessage[] history =
            {
                new() { Role = "user", Content = new string('X', 5000) },
                new() { Role = "assistant", Content = "short" },
                new() { Role = "user", Content = "latest" }
            };

            await mgr.BuildSnapshotAsync(
                "r", history,
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs { HistoryTokenBudget = 15, UseLlmContextCompaction = true },
                "t", CancellationToken.None).ConfigureAwait(false);

            // The payload should have truncated the 5000-char message to ~2000 chars
            Assert.IsNotNull(llm.LastRequest);
            string payload = llm.LastRequest.UserPayload;
            Assert.IsFalse(payload.Contains(new string('X', 3000)),
                "Per-message content over 2000 chars should be truncated in the compaction payload.");
        }
    }
}
