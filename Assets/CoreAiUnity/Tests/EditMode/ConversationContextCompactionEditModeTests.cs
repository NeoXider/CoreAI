using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Diagnostics;
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
            Assert.AreEqual(
                snap.Summary,
                ConversationFoldMarker.Strip(store.LastSavedSummary),
                "Persisted text must be the snapshot summary plus only the fold marker.");
            StringAssert.Contains("[fold:v1:", store.LastSavedSummary);
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
            Assert.AreEqual(
                ConversationFoldMarker.Strip(savedSummary),
                snap.Summary,
                "Below-trigger snapshot must expose the stored summary without the fold marker.");
            Assert.IsFalse(snap.WasCompacted);
            Assert.AreEqual(3, snap.RecentMessages.Length);
        }

        [Test]
        public void DeterministicManager_RepeatedAndGrowingHistory_FoldsEachPrefixOnce()
        {
            RecordingSummaryStore store = new();
            DeterministicConversationContextManager mgr = new(store, new FlatTokenEstimator(10));
            ConversationContextBuildArgs args = new()
            {
                HistoryTokenBudget = 25,
                CompactionTriggerRatio = 0.8f
            };

            mgr.BuildSnapshot(
                "r", MakeHistory(5), new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 }, args);
            mgr.BuildSnapshot(
                "r", MakeHistory(5), new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 }, args);
            mgr.BuildSnapshot(
                "r", MakeHistory(7), new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 }, args);

            string summary = store.LoadSummary("r");
            Assert.AreEqual(1, CountOccurrences(summary, "- user: msg0"));
            Assert.AreEqual(1, CountOccurrences(summary, "- assistant: msg1"));
            Assert.AreEqual(1, CountOccurrences(summary, "- user: msg2"));
            Assert.AreEqual(1, CountOccurrences(summary, "- assistant: msg3"));
            Assert.AreEqual(1, CountOccurrences(summary, "- user: msg4"));
            Assert.AreEqual(2, store.SaveSummaryCalls);
        }

        [Test]
        public void DeterministicManager_DeferredPersistence_SavesOnlyOnCommit()
        {
            RecordingSummaryStore store = new();
            DeterministicConversationContextManager mgr = new(store, new FlatTokenEstimator(10));
            ConversationContextSnapshot snapshot = mgr.BuildSnapshot(
                "r",
                MakeHistory(5),
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 25,
                    CompactionTriggerRatio = 0.8f,
                    DeferSummaryPersistence = true
                });

            Assert.AreEqual(0, store.SaveSummaryCalls);
            Assert.AreEqual("", store.LoadSummary("r"));
            snapshot.Commit();
            Assert.AreEqual(1, store.SaveSummaryCalls);
            Assert.AreEqual(snapshot.Summary, ConversationFoldMarker.Strip(store.LoadSummary("r")));
            StringAssert.Contains("[fold:v1:", store.LoadSummary("r"));
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        [Test]
        public async Task LlmAssisted_CancellationToken_Rethrows()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);
            CancellationTokenSource cts = new();
            cts.Cancel();

            // TaskCanceledException inherits from OperationCanceledException.
            // The async state machine may wrap the cancellation as either subtype,
            // so we use CatchAsync (accepts derived types) — matching production catch blocks.
            try
            {
                await mgr.BuildSnapshotAsync(
                    "r", MakeHistory(5),
                    new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                    new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                    "t", cts.Token).ConfigureAwait(false);
                Assert.Fail("expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
            }
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
            Assert.IsTrue(snap.Summary.StartsWith("…"), "Summary should evict oldest content when over MaxRolledSummaryTokens.");
            string persisted = store.LoadSummary("roleA");
            Assert.AreEqual(snap.Summary, ConversationFoldMarker.Strip(persisted),
                "Store should receive the same truncated summary as the snapshot, plus only the fold marker.");
            Assert.LessOrEqual(
                est.EstimateText(ConversationFoldMarker.Strip(persisted)),
                30,
                "Persisted summary prose should stay near the token cap.");
            StringAssert.Contains("[fold:v1:", persisted,
                "Limiter runs before stamping, so the marker must survive truncation.");
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

            Assert.IsTrue(snap.Summary.StartsWith("…"));
            Assert.Less(est.EstimateText(snap.Summary), est.EstimateText(new string('q', 800)));
            Assert.AreEqual("tail-only", snap.RecentMessages[^1].Content);
        }

        [Test]
        public void FindFoldStart_WhitespaceMessage_NeverMatchesStoredBullets()
        {
            // FINDING-4a: "- user: " (blank content) is a substring of every user bullet; the old
            // IndexOf match treated everything before a blank message as already folded.
            string summary = ConversationBulletSummary.Format(
                "",
                new[]
                {
                    new ChatMessage { Role = "user", Content = "hello" },
                    new ChatMessage { Role = "assistant", Content = "sure" }
                },
                2);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "hello" },
                new() { Role = "assistant", Content = "sure" },
                new() { Role = "user", Content = "unfolded question" },
                new() { Role = "user", Content = "   " }
            };

            Assert.AreEqual(2, ConversationBulletSummary.FindFoldStart(summary, history, 4),
                "Blank message must not match; fold resumes at the first unfolded non-empty message.");
        }

        [Test]
        public void FindFoldStart_PrefixMessage_DoesNotMatchInsideStoredBullet()
        {
            // FINDING-4b: message "hel" formats to "- user: hel", a substring of the stored bullet
            // "- user: hello world"; only a whole-final-line watermark match may count.
            string summary = ConversationBulletSummary.Format(
                "",
                new[] { new ChatMessage { Role = "user", Content = "hello world" } },
                1);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "hello world" },
                new() { Role = "user", Content = "hel" },
                new() { Role = "user", Content = "tail" }
            };

            Assert.AreEqual(1, ConversationBulletSummary.FindFoldStart(summary, history, 3),
                "Prefix message must not be treated as folded via substring subsumption.");
        }

        [Test]
        public void FindFoldStart_DuplicateMessage_MatchesOnlyFinalWatermarkLine()
        {
            // FINDING-4c: a repeated "ok" matched the OLD folded "ok" bullet mid-summary; backward
            // search then skipped folding everything between the real fold point and the duplicate.
            string summary = ConversationBulletSummary.Format(
                "",
                new[]
                {
                    new ChatMessage { Role = "user", Content = "ok" },
                    new ChatMessage { Role = "assistant", Content = "watermark reply" }
                },
                2);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "ok" },
                new() { Role = "assistant", Content = "watermark reply" },
                new() { Role = "user", Content = "ok" },
                new() { Role = "user", Content = "newest" }
            };

            Assert.AreEqual(2, ConversationBulletSummary.FindFoldStart(summary, history, 4),
                "Duplicate message must only match the summary's final watermark line.");
        }

        [Test]
        public void FindFoldStart_WatermarkAsFinalLine_BackwardCompatibleWithPersistedSummaries()
        {
            // WHY-shaped guard: summaries persisted by the previous code already end with the last
            // folded message's bullet; the stricter matcher must still recognize them fully folded.
            ChatMessage[] history =
            {
                new() { Role = "user", Content = "first" },
                new() { Role = "assistant", Content = "second" },
                new() { Role = "user", Content = "third" }
            };
            string summary = ConversationBulletSummary.Format("", history, 3);

            Assert.AreEqual(3, ConversationBulletSummary.FindFoldStart(summary, history, 3));
        }

        /// <summary>
        /// The old design stamped the last non-empty bullet as watermark; the marker now covers the whole
        /// folded prefix by content hash, including a whitespace-only message just before the tail.
        /// </summary>
        [Test]
        public async Task LlmAssisted_PersistedMarker_CoversWhitespaceTailMessage()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "alpha" },
                new() { Role = "assistant", Content = "beta" },
                new() { Role = "user", Content = "gamma" },
                new() { Role = "assistant", Content = "   " },
                new() { Role = "user", Content = "tail-1" },
                new() { Role = "assistant", Content = "tail-2" }
            };

            await mgr.BuildSnapshotAsync(
                "r", history,
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs { HistoryTokenBudget = 25, UseLlmContextCompaction = true },
                "t", CancellationToken.None).ConfigureAwait(false);

            string persisted = store.LoadSummary("r");
            StringAssert.Contains("[fold:v1:", persisted, "Persisted summary must end with a fold marker.");
            Assert.AreEqual(4, ConversationBulletSummary.FindFoldStart(persisted, history, 4),
                "Persisted marker must mark the whole folded prefix (incl. the whitespace tail) as folded.");
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

        // --- Fold-marker redesign tests (F14-F17, marker stripping, limiter, migration) ---

        private static AgentMemoryPolicy.RoleMemoryConfig DefaultRoleConfig()
        {
            return new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 };
        }

        private static ConversationContextBuildArgs LlmArgs(int budget = 25)
        {
            return new ConversationContextBuildArgs { HistoryTokenBudget = budget, UseLlmContextCompaction = true };
        }

        /// <summary>
        /// F14: wave-2 code could persist a summary whose FINAL line is a blank bullet ("- user: ").
        /// The final-line matcher never matches it; the substring fallback must recognize the prefix as
        /// folded so the upgrade costs zero extra LLM calls.
        /// </summary>
        [Test]
        public async Task F14_LegacyBlankBulletFinalLine_NoResummarizeOnUpgrade()
        {
            InMemoryConversationSummaryStore store = new();
            store.SaveSummary(
                "r",
                "Previous conversation summary:\n- user: alpha\n- assistant: beta\n- user: ");
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "alpha" },
                new() { Role = "assistant", Content = "beta" },
                new() { Role = "user", Content = "tail-1" },
                new() { Role = "assistant", Content = "tail-2" }
            };

            ConversationContextSnapshot snap = await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.CompleteCallCount,
                "Legacy blank-bullet watermark must migrate via substring fallback without a re-summarize.");
            Assert.IsTrue(snap.WasCompacted);
        }

        /// <summary>
        /// F15: the watermark message can be pruned/trimmed out of history before fold detection. The marker
        /// stores hashes of the last 8 folded messages, so any survivor still anchors the fold; only genuinely
        /// new messages are re-summarized, never the whole prefix, and it cannot recur.
        /// </summary>
        [Test]
        public async Task F15_WatermarkMessagePruned_FoldsOnlyNewMessages()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] firstHistory =
            {
                new() { Role = "user", Content = "m0" },
                new() { Role = "assistant", Content = "m1" },
                new() { Role = "user", Content = "m2" },
                new() { Role = "assistant", Content = "m3" },
                new() { Role = "user", Content = "m4" }
            };

            await mgr.BuildSnapshotAsync(
                "r", firstHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, llm.CompleteCallCount, "First fold covers m0..m2.");

            ChatMessage[] prunedHistory =
            {
                new() { Role = "user", Content = "m0" },
                new() { Role = "assistant", Content = "m1" },
                new() { Role = "assistant", Content = "m3" },
                new() { Role = "user", Content = "m4" },
                new() { Role = "user", Content = "m5" },
                new() { Role = "assistant", Content = "m6" }
            };

            await mgr.BuildSnapshotAsync(
                "r", prunedHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(2, llm.CompleteCallCount);
            string payload = llm.LastRequest.UserPayload;
            int foldSection = payload.IndexOf("## Dialogue lines to fold", StringComparison.Ordinal);
            string foldLines = payload.Substring(foldSection);
            StringAssert.DoesNotContain("- user: m0", foldLines,
                "Already-folded m0 must not be re-summarized when the m2 watermark was pruned away.");
            StringAssert.DoesNotContain("- assistant: m1", foldLines,
                "Already-folded m1 must not be re-summarized when the m2 watermark was pruned away.");
            StringAssert.Contains("- assistant: m3", foldLines, "New message m3 must be folded.");
        }

        /// <summary>
        /// F16: an all-whitespace foldable prefix previously produced no watermark at all, so every
        /// BuildSnapshotAsync re-summarized forever. Hashing whitespace like any other content makes the
        /// fold converge after a single LLM call.
        /// </summary>
        [Test]
        public async Task F16_AllWhitespacePrefix_ConvergesAfterSingleFold()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "   " },
                new() { Role = "assistant", Content = " " },
                new() { Role = "user", Content = "\t" },
                new() { Role = "assistant", Content = "tail-1" },
                new() { Role = "user", Content = "tail-2" }
            };

            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            int callsAfterFirst = llm.CompleteCallCount;

            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(callsAfterFirst, llm.CompleteCallCount,
                "A whitespace-only fold must advance the persisted marker so later builds never re-summarize.");
            Assert.LessOrEqual(callsAfterFirst, 1);
        }

        /// <summary>
        /// F17: a live message repeating folded text verbatim ("ok") must not pull the fold point forward
        /// past unique unfolded messages; the marker consumes each hash at its oldest occurrence.
        /// </summary>
        [Test]
        public async Task F17_LiveDuplicateOfFoldedText_DoesNotLoseInterveningMessages()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] firstHistory =
            {
                new() { Role = "user", Content = "ok" },
                new() { Role = "assistant", Content = "watermark reply" },
                new() { Role = "user", Content = "tail-a" },
                new() { Role = "assistant", Content = "tail-b" }
            };

            await mgr.BuildSnapshotAsync(
                "r", firstHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, llm.CompleteCallCount, "First fold covers ok + watermark reply.");

            ChatMessage[] grownHistory =
            {
                new() { Role = "user", Content = "ok" },
                new() { Role = "assistant", Content = "watermark reply" },
                new() { Role = "user", Content = "unique-between" },
                new() { Role = "user", Content = "ok" },
                new() { Role = "assistant", Content = "tail-1" },
                new() { Role = "user", Content = "tail-2" }
            };

            await mgr.BuildSnapshotAsync(
                "r", grownHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(2, llm.CompleteCallCount);
            string payload = llm.LastRequest.UserPayload;
            int foldSection = payload.IndexOf("## Dialogue lines to fold", StringComparison.Ordinal);
            string foldLines = payload.Substring(foldSection);
            StringAssert.Contains("- user: unique-between", foldLines,
                "The message between the real fold point and the live duplicate must be folded, not lost.");
            StringAssert.DoesNotContain("- assistant: watermark reply", foldLines,
                "Already-folded messages must not be re-summarized.");
        }

        /// <summary>
        /// A pruned watermark followed by a live verbatim duplicate must not move the fold point past
        /// intervening messages that have never been summarized.
        /// </summary>
        [Test]
        public async Task FoldMarker_PrunedWatermarkWithLaterDuplicate_FoldsInterveningMessages()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] firstHistory =
            {
                new() { Role = "user", Content = "m0" },
                new() { Role = "assistant", Content = "m1" },
                new() { Role = "user", Content = "watermark" },
                new() { Role = "assistant", Content = "tail-a" },
                new() { Role = "user", Content = "tail-b" }
            };

            await mgr.BuildSnapshotAsync(
                "r", firstHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            ChatMessage[] prunedHistory =
            {
                new() { Role = "user", Content = "m0" },
                new() { Role = "assistant", Content = "m1" },
                new() { Role = "assistant", Content = "never summarized" },
                new() { Role = "user", Content = "watermark" },
                new() { Role = "assistant", Content = "tail-1" },
                new() { Role = "user", Content = "tail-2" }
            };

            await mgr.BuildSnapshotAsync(
                "r", prunedHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(2, llm.CompleteCallCount);
            string foldLines = llm.LastRequest.UserPayload.Substring(
                llm.LastRequest.UserPayload.IndexOf("## Dialogue lines to fold", StringComparison.Ordinal));
            StringAssert.Contains("- assistant: never summarized", foldLines);
            StringAssert.Contains("- user: watermark", foldLines,
                "The later duplicate is live content and must be folded rather than skipped.");
        }

        /// <summary>
        /// Convergence must not depend on ChatMessage struct equality (which includes Timestamp):
        /// repeated identical replies with REAL distinct timestamps land in the folded prefix, and the
        /// fold point must advance past them instead of re-summarizing a growing region every turn.
        /// </summary>
        [Test]
        public async Task FoldMarker_DuplicateFoldedMessagesWithRealTimestamps_Converge()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] history =
            {
                new() { Role = "user", Content = "do it", Timestamp = 1000 },
                new() { Role = "assistant", Content = "ok", Timestamp = 1001 },
                new() { Role = "user", Content = "again", Timestamp = 1002 },
                new() { Role = "assistant", Content = "ok", Timestamp = 1003 },
                new() { Role = "user", Content = "tail-a", Timestamp = 1004 },
                new() { Role = "assistant", Content = "tail-b", Timestamp = 1005 }
            };

            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            int callsAfterFirst = llm.CompleteCallCount;

            ConversationContextSnapshot second = await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(callsAfterFirst, llm.CompleteCallCount,
                "An unchanged history must not trigger another compaction call - the fold point " +
                "must advance past duplicate folded messages even with distinct timestamps.");
            Assert.IsTrue(second.WasCompacted);
        }

        /// <summary>
        /// Marker parsing accepts only the persisted lowercase-hex grammar and leaves invalid marker-like
        /// final lines untouched as summary prose.
        /// </summary>
        [Test]
        public void FoldMarker_NonHexOrInvalidShape_IsRejectedAndPreserved()
        {
            string nonHex = "summary\n[fold:v1:not-a-hash]";
            Assert.IsFalse(ConversationFoldMarker.TryParse(nonHex, out _));
            Assert.AreEqual(nonHex, ConversationFoldMarker.Strip(nonHex));
            Assert.IsFalse(ConversationFoldMarker.TryParse("[fold:v1:ABCDEF012345]", out _));
            Assert.IsFalse(ConversationFoldMarker.TryParse("[fold:v1:0123456789ab,]", out _));
            Assert.IsFalse(ConversationFoldMarker.TryParse(
                "[fold:v1:000000000000,111111111111,222222222222,333333333333,444444444444," +
                "555555555555,666666666666,777777777777,888888888888]", out _));
        }

        /// <summary>
        /// A strict marker-shaped line in the middle of summary prose is quoted content, not persistence
        /// metadata, and survives stripping unchanged.
        /// </summary>
        [Test]
        public void FoldMarker_StrictMidTextMarkerLine_SurvivesStrip()
        {
            string summary = "before\n[fold:v1:0123456789ab]\nafter";
            Assert.AreEqual(summary, ConversationFoldMarker.Strip(summary));
            Assert.IsFalse(ConversationFoldMarker.TryParse(summary, out _));
        }

        /// <summary>
        /// A trailing marker echoed by the compaction LLM is removed before the clean snapshot is produced
        /// and before the authentic marker is stamped.
        /// </summary>
        [Test]
        public async Task LlmAssisted_TrailingEchoedMarker_StrippedBeforeStamping()
        {
            const string echoedMarker = "[fold:v1:0123456789ab]";
            InMemoryConversationSummaryStore store = new();
            LongResultLlmClient llm = new("clean prose\n" + echoedMarker);
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ConversationContextSnapshot snapshot = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(5), DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("clean prose", snapshot.Summary);
            StringAssert.DoesNotContain(echoedMarker, store.LoadSummary("r"));
            Assert.IsTrue(ConversationFoldMarker.TryParse(store.LoadSummary("r"), out _));
        }

        /// <summary>
        /// Serialized inspector diagnostics expose and estimate only clean summary prose, never the
        /// persistence marker.
        /// </summary>
        [Test]
        public void AgentSessionInspector_StripsMarkerFromDisplayAndTokenEstimate()
        {
            const string roleId = BuiltInAgentRoleIds.PlainChat;
            InMemoryConversationSummaryStore store = new();
            CoreAISettingsOptions settings = new();
            AgentMemoryPolicy policy = new();
            store.SaveSummary(roleId, "clean prose\n[fold:v1:0123456789ab]");

            AgentSessionSnapshot marked = AgentSessionInspector.InspectSerializedInputs(
                roleId, settings, memoryPolicy: policy, summaryStore: store,
                tokenEstimator: new HeuristicTokenEstimator());
            store.SaveSummary(roleId, "clean prose");
            AgentSessionSnapshot clean = AgentSessionInspector.InspectSerializedInputs(
                roleId, settings, memoryPolicy: policy, summaryStore: store,
                tokenEstimator: new HeuristicTokenEstimator());

            Assert.AreEqual("clean prose", marked.ConversationSummary);
            Assert.AreEqual(clean.Budget.EstimatedSystemTokens, marked.Budget.EstimatedSystemTokens);
            Assert.IsFalse(marked.EstimatedRequestChatHistory.Any(
                message => message.Content.Contains("[fold:v1:", StringComparison.Ordinal)));
        }

        /// <summary>
        /// Marker stripping: snapshot.Summary and the LLM payload's prior-summary section must contain
        /// clean prose only; the marker lives exclusively in the persisted text as its final line.
        /// </summary>
        [Test]
        public async Task FoldMarker_StrippedFromSnapshotAndLlmPayload_PresentOnlyInPersistedText()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ConversationContextSnapshot first = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(5), DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            StringAssert.DoesNotContain("[fold:v1:", first.Summary);
            StringAssert.Contains("[fold:v1:", store.LoadSummary("r"));
            Assert.IsTrue(store.LoadSummary("r").TrimEnd().EndsWith("]", StringComparison.Ordinal),
                "Marker must be the final line of the persisted summary.");

            ConversationContextSnapshot second = await mgr.BuildSnapshotAsync(
                "r", MakeHistory(7), DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            StringAssert.DoesNotContain("[fold:v1:", second.Summary);
            StringAssert.DoesNotContain("[fold:v1:", llm.LastRequest.UserPayload,
                "The prior-summary section handed to the LLM must be stripped of the marker.");
        }

        /// <summary>
        /// Limiter interaction: MaxRolledSummaryTokens truncates the prose BEFORE the marker is stamped,
        /// so an aggressive cap can never delete or corrupt the marker and fold detection still works.
        /// </summary>
        [Test]
        public void FoldMarker_SurvivesAggressiveSummaryTokenCap()
        {
            InMemoryConversationSummaryStore store = new();
            HeuristicTokenEstimator est = new();
            DeterministicConversationContextManager mgr = new(store, est);
            ChatMessage[] history = new ChatMessage[10];
            for (int i = 0; i < 10; i++)
            {
                history[i] = new ChatMessage { Role = "user", Content = $"m{i}-" + new string('z', 48) };
            }

            ConversationContextBuildArgs buildArgs = new()
            {
                HistoryTokenBudget = 28,
                MaxRolledSummaryTokens = 4
            };

            mgr.BuildSnapshot("r", history, DefaultRoleConfig(), buildArgs);

            string persisted = store.LoadSummary("r");
            Assert.IsTrue(ConversationFoldMarker.TryParse(persisted, out _),
                "Marker must parse intact from the persisted summary even under a tiny token cap.");
            Assert.Greater(ConversationBulletSummary.FindFoldStart(persisted, history, 8), 0,
                "Fold detection must still work after aggressive prose truncation.");
        }

        /// <summary>
        /// Migration: a wave-3 summary (watermark bullet as final line, no marker) is recognized without
        /// any re-summarize, and the first save writes a marker that takes over from then on.
        /// </summary>
        [Test]
        public async Task Migration_Wave3FinalLineSummary_RecognizedThenMarkerTakesOver()
        {
            ChatMessage[] foldedPrefix =
            {
                new() { Role = "user", Content = "alpha" },
                new() { Role = "assistant", Content = "beta" },
                new() { Role = "user", Content = "gamma" }
            };
            string wave3Summary = ConversationBulletSummary.Format("", foldedPrefix, 3);
            Assert.IsFalse(ConversationFoldMarker.TryParse(wave3Summary, out _),
                "Sanity: legacy summary has no marker.");

            InMemoryConversationSummaryStore store = new();
            store.SaveSummary("r", wave3Summary);
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            ChatMessage[] history =
            {
                foldedPrefix[0],
                foldedPrefix[1],
                foldedPrefix[2],
                new() { Role = "assistant", Content = "delta" },
                new() { Role = "user", Content = "tail-1" },
                new() { Role = "assistant", Content = "tail-2" }
            };

            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.CompleteCallCount);
            string foldLines = llm.LastRequest.UserPayload.Substring(
                llm.LastRequest.UserPayload.IndexOf("## Dialogue lines to fold", StringComparison.Ordinal));
            StringAssert.DoesNotContain("- user: alpha", foldLines,
                "Wave-3 final-line watermark must be honored; only 'delta' is new.");
            StringAssert.Contains("- assistant: delta", foldLines);
            Assert.IsTrue(ConversationFoldMarker.TryParse(store.LoadSummary("r"), out _),
                "First save after migration must stamp the structured marker.");
        }

        /// <summary>
        /// Degradation: when every marker hash has vanished from history, the fold restarts from 0 exactly
        /// once — the freshly stamped marker matches on the next build, so the re-fold cannot recur.
        /// </summary>
        [Test]
        public async Task Degradation_AllMarkerHashesGone_RefoldsOnceThenConverges()
        {
            InMemoryConversationSummaryStore store = new();
            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, new FlatTokenEstimator(10), llm);

            await mgr.BuildSnapshotAsync(
                "r", MakeHistory(5), DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, llm.CompleteCallCount);

            ChatMessage[] disjointHistory =
            {
                new() { Role = "user", Content = "n0" },
                new() { Role = "assistant", Content = "n1" },
                new() { Role = "user", Content = "n2" },
                new() { Role = "assistant", Content = "n3" },
                new() { Role = "user", Content = "n4" }
            };

            await mgr.BuildSnapshotAsync(
                "r", disjointHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, llm.CompleteCallCount, "Disjoint history triggers one graceful fold-from-0.");

            await mgr.BuildSnapshotAsync(
                "r", disjointHistory, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, llm.CompleteCallCount,
                "The new marker written by the degraded fold must prevent any recurrence.");
        }

        /// <summary>
        /// Both managers stamp the same marker format, so a role can move between deterministic and
        /// LLM-assisted compaction without re-summarizing.
        /// </summary>
        [Test]
        public async Task DeterministicMarker_ReadableByLlmAssistedManager_NoRefold()
        {
            InMemoryConversationSummaryStore store = new();
            FlatTokenEstimator est = new(10);
            DeterministicConversationContextManager det = new(store, est);
            ChatMessage[] history = MakeHistory(5);

            det.BuildSnapshot(
                "r", history, DefaultRoleConfig(),
                new ConversationContextBuildArgs { HistoryTokenBudget = 25, CompactionTriggerRatio = 0.8f });
            Assert.IsTrue(ConversationFoldMarker.TryParse(store.LoadSummary("r"), out _),
                "Deterministic manager must stamp the marker too.");

            RecordingLlmClient llm = new();
            LlmAssistedConversationContextManager mgr = new(store, est, llm);
            await mgr.BuildSnapshotAsync(
                "r", history, DefaultRoleConfig(), LlmArgs(),
                "t", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.CompleteCallCount,
                "The deterministic marker must be honored by the LLM-assisted manager.");
        }
    }
}
