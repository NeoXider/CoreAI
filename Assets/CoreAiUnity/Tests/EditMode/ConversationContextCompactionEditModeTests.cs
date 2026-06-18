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

            public int EstimateText(string text)
            {
                return _perMessage;
            }
        }

        private sealed class RecordingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }
            public int CompleteCallCount { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "rolled_up_summary" });
            }
        }

        private sealed class RecordingSummaryStore : IConversationSummaryStore
        {
            private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);

            public int SaveSummaryCalls { get; private set; }
            public string LastSavedSummary { get; private set; }

            public void Seed(string roleId, string summary)
            {
                _summaries[roleId] = summary;
            }

            public string LoadSummary(string roleId)
            {
                return _summaries.TryGetValue(roleId, out string summary) ? summary : "";
            }

            public void SaveSummary(string roleId, string summary)
            {
                SaveSummaryCalls++;
                LastSavedSummary = summary;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    _summaries.Remove(roleId);
                    return;
                }

                _summaries[roleId] = summary;
            }

            public void ClearSummary(string roleId)
            {
                _summaries.Remove(roleId);
            }
        }

        [Test]
        public void ConversationContextManagerFactories_DisableOrNullLlm_UsesDeterministic()
        {
            InMemoryConversationSummaryStore store = new();
            ITokenEstimator est = new HeuristicTokenEstimator();
            IConversationContextManager a = ConversationContextManagerFactories.Create(false, store, est, null, null);
            IConversationContextManager b = ConversationContextManagerFactories.Create(true, store, est, null, null);
            Assert.IsInstanceOf<DeterministicConversationContextManager>(a);
            Assert.IsInstanceOf<DeterministicConversationContextManager>(b);
        }

        [Test]
        public void ConversationContextManagerFactories_EnableWithLlm_UsesSelectingWrapper()
        {
            InMemoryConversationSummaryStore store = new();
            ITokenEstimator est = new HeuristicTokenEstimator();
            RecordingLlmClient llm = new();
            IConversationContextManager m = ConversationContextManagerFactories.Create(true, store, est, llm, null);
            Assert.IsInstanceOf<SelectingConversationContextManager>(m);
        }

        [Test]
        public async Task SelectingManager_BuildSnapshotAsync_SkipsLlm_WhenArgsDisableCompaction()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            ITokenEstimator est = new FlatTokenEstimator(10);
            SelectingConversationContextManager mgr = new(store, est, llm, LlmContextCompactionOptions.Default());

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "a" },
                new() { Role = "assistant", Content = "b" },
                new() { Role = "user", Content = "c" },
                new() { Role = "assistant", Content = "d" },
                new() { Role = "user", Content = "e" }
            };

            AgentMemoryPolicy.RoleMemoryConfig roleConfig = new() { ContextTokens = 8192 };
            ConversationContextBuildArgs buildArgs = new()
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
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            ITokenEstimator est = new FlatTokenEstimator(10);
            LlmAssistedConversationContextManager mgr = new(store, est, llm, LlmContextCompactionOptions.Default());

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "a" },
                new() { Role = "assistant", Content = "b" },
                new() { Role = "user", Content = "c" },
                new() { Role = "assistant", Content = "d" },
                new() { Role = "user", Content = "e" }
            };

            AgentMemoryPolicy.RoleMemoryConfig roleConfig = new() { ContextTokens = 8192 };
            ConversationContextBuildArgs buildArgs = new()
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
            Assert.IsNull(llm.LastRequest.ChatHistory,
                "Compaction must not replay tail as ChatHistory on auxiliary request.");
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

            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            ITokenEstimator est = new FlatTokenEstimator(10);
            LlmAssistedConversationContextManager mgr = new(store, est, llm);

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
            LlmContextCompactionOptions options = new() { SystemPrompt = compactOnly };

            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(
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

            public ThrowingLlmClient(Exception ex)
            {
                _ex = ex;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _ex;
            }
        }

        private sealed class WhitespaceResultLlmClient : ILlmClient
        {
            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "   \n  " });
            }
        }

        private static ChatMessage[] MakeHistory(int count)
        {
            return Enumerable.Range(0, count).Select(i => new ChatMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"msg{i}"
            }).ToArray();
        }

        [Test]
        public void DeterministicManager_BelowCompactionTrigger_KeepsAllHistoryAndDoesNotSave()
        {
            RecordingSummaryStore store = new();
            store.Seed("r", "existing summary");
            DeterministicConversationContextManager mgr = new(store, new FlatTokenEstimator(10));
            ChatMessage[] history = MakeHistory(7);

            ConversationContextSnapshot snap = mgr.BuildSnapshot(
                "r",
                history,
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 100,
                    CompactionTriggerRatio = 0.8f
                });

            Assert.IsFalse(snap.WasCompacted);
            Assert.AreEqual("existing summary", snap.Summary);
            Assert.AreEqual(0, store.SaveSummaryCalls, "Below-trigger builds must not churn the summary store.");
            Assert.AreEqual(history.Length, snap.RecentMessages.Length);
            for (int i = 0; i < history.Length; i++)
            {
                Assert.AreEqual(history[i].Content, snap.RecentMessages[i].Content);
            }
        }

        [Test]
        public void DeterministicManager_AboveCompactionTrigger_SummarizesOldestAndSaves()
        {
            RecordingSummaryStore store = new();
            DeterministicConversationContextManager mgr = new(store, new FlatTokenEstimator(10));
            ChatMessage[] history = MakeHistory(5);

            ConversationContextSnapshot snap = mgr.BuildSnapshot(
                "r",
                history,
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 25,
                    CompactionTriggerRatio = 0.8f
                });

            Assert.IsTrue(snap.WasCompacted);
            Assert.AreEqual(1, store.SaveSummaryCalls);
            Assert.AreEqual(store.LastSavedSummary, snap.Summary);
            StringAssert.Contains("msg0", snap.Summary);
            StringAssert.Contains("msg2", snap.Summary);
            Assert.AreEqual(2, snap.RecentMessages.Length);
            Assert.AreEqual("msg3", snap.RecentMessages[0].Content);
            Assert.AreEqual("msg4", snap.RecentMessages[1].Content);
        }

        [Test]
        public void CompactionTriggerRatio_InvalidValue_UsesDefaultThreshold()
        {
            ConversationContextBuildArgs buildArgs = new()
            {
                CompactionTriggerRatio = 0f
            };

            Assert.AreEqual(
                CoreAISettings.DefaultConversationCompactionTriggerRatio,
                ConversationContextBudgetTokens.ResolveCompactionTriggerRatio(buildArgs));
        }

        [Test]
        public void DeterministicManager_BelowTriggerAfterPriorCompaction_DoesNotRewriteSummary()
        {
            RecordingSummaryStore store = new();
            DeterministicConversationContextManager mgr = new(store, new FlatTokenEstimator(10));

            mgr.BuildSnapshot(
                "r",
                MakeHistory(5),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 25,
                    CompactionTriggerRatio = 0.8f
                });
            string savedSummary = store.LoadSummary("r");

            ConversationContextSnapshot snap = mgr.BuildSnapshot(
                "r",
                MakeHistory(3),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 100,
                    CompactionTriggerRatio = 0.8f
                });

            Assert.AreEqual(1, store.SaveSummaryCalls);
            Assert.AreEqual(savedSummary, snap.Summary);
            Assert.IsFalse(snap.WasCompacted);
            Assert.AreEqual(3, snap.RecentMessages.Length);
        }

        [Test]
        public void LlmAssisted_CancellationToken_Rethrows()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);
            CancellationTokenSource cts = new();
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
            InMemoryConversationSummaryStore store = new();
            ThrowingLlmClient llm = new(new InvalidOperationException("LLM down"));
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

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
            InMemoryConversationSummaryStore store = new();
            WhitespaceResultLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

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
            string longContent = new('A', 6000);
            InMemoryConversationSummaryStore store = new();
            LongResultLlmClient llm = new(longContent);
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

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

            public LongResultLlmClient(string content)
            {
                _content = content;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _content });
            }
        }

        [Test]
        public void DeterministicManager_MaxRolledSummaryTokens_TruncatesBeforeSave()
        {
            InMemoryConversationSummaryStore store = new();
            HeuristicTokenEstimator est = new();
            DeterministicConversationContextManager mgr = new(store, est);
            ChatMessage[] history = new ChatMessage[10];
            for (int i = 0; i < 10; i++)
            {
                history[i] = new ChatMessage
                {
                    Role = "user",
                    Content = $"m{i}-" + new string('z', 48)
                };
            }

            AgentMemoryPolicy.RoleMemoryConfig roleConfig = new() { ContextTokens = 8192 };
            ConversationContextBuildArgs buildArgs = new()
            {
                HistoryTokenBudget = 28,
                MaxRolledSummaryTokens = 18
            };

            ConversationContextSnapshot snap = mgr.BuildSnapshot("roleA", history, roleConfig, buildArgs);

            Assert.IsTrue(snap.WasCompacted);
            Assert.IsTrue(snap.Summary.EndsWith("…"), "Summary should be truncated when over MaxRolledSummaryTokens.");
            string persisted = store.LoadSummary("roleA");
            Assert.AreEqual(snap.Summary, persisted,
                "Store should receive the same truncated summary as the snapshot.");
            Assert.LessOrEqual(est.EstimateText(persisted), 30, "Persisted summary should stay near the token cap.");
        }

        [Test]
        public void DeterministicManager_MaxRolledSummaryTokens_TruncatesStoredOnlySnapshot()
        {
            InMemoryConversationSummaryStore store = new();
            store.SaveSummary("roleB", new string('q', 800));
            HeuristicTokenEstimator est = new();
            DeterministicConversationContextManager mgr = new(store, est);
            ChatMessage[] history = new[]
            {
                new ChatMessage { Role = "user", Content = "tail-only" }
            };

            AgentMemoryPolicy.RoleMemoryConfig roleConfig = new() { ContextTokens = 8192 };
            ConversationContextBuildArgs buildArgs = new()
            {
                HistoryTokenBudget = 500,
                MaxRolledSummaryTokens = 25
            };

            ConversationContextSnapshot snap = mgr.BuildSnapshot("roleB", history, roleConfig, buildArgs);

            Assert.IsTrue(snap.Summary.EndsWith("…"));
            Assert.Less(est.EstimateText(snap.Summary), est.EstimateText(new string('q', 800)));
            Assert.AreEqual("tail-only", snap.RecentMessages[^1].Content);
        }

        [Test]
        public async Task LlmAssisted_LongPerMessageContent_TruncatedInPayload()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

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
