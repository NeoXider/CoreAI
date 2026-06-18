using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class ConversationHistoryPrunerEditModeTests
    {
        [Test]
        public void Prune_KeepsMostRecentToolResults_AndAllUserAssistantTurns()
        {
            ChatMessage[] history =
            {
                Msg("user", "u0"),
                Msg("tool", "## Tool Results\nold-0"),
                Msg("assistant", "a1"),
                Msg("tool", "## Tool Results\nold-1"),
                Msg("user", "u2"),
                Msg("tool", "## Tool Results\nnew-2"),
                Msg("assistant", "a3")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 2);

            Assert.AreEqual(6, pruned.Length);
            Assert.AreEqual("u0", pruned[0].Content);
            Assert.AreEqual("a1", pruned[1].Content);
            Assert.AreEqual("## Tool Results\nold-1", pruned[2].Content);
            Assert.AreEqual("u2", pruned[3].Content);
            Assert.AreEqual("## Tool Results\nnew-2", pruned[4].Content);
            Assert.AreEqual("a3", pruned[5].Content);
        }

        [Test]
        public void Prune_CollapsesExactConsecutiveDuplicates_UsingTrimmedContent()
        {
            ChatMessage[] history =
            {
                Msg("user", "same"),
                Msg("user", " same \n"),
                Msg("assistant", "same"),
                Msg("user", "same")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 3);

            Assert.AreEqual(3, pruned.Length);
            Assert.AreEqual("user", pruned[0].Role);
            Assert.AreEqual("same", pruned[0].Content);
            Assert.AreEqual("assistant", pruned[1].Role);
            Assert.AreEqual("user", pruned[2].Role);
        }

        [Test]
        public void Prune_DropsOlderToolResult_WhenNewerResultForSameToolExists()
        {
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok old"),
                Msg("assistant", "noted"),
                Msg("tool", "## Tool Results\n- call_skill_tool: ok moved"),
                Msg("user", "continue"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok fresh"),
                Msg("assistant", "done")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(6, pruned.Length);
            Assert.AreEqual("opening", pruned[0].Content);
            Assert.AreEqual("noted", pruned[1].Content);
            Assert.AreEqual("## Tool Results\n- call_skill_tool: ok moved", pruned[2].Content);
            Assert.AreEqual("continue", pruned[3].Content);
            Assert.AreEqual("## Tool Results\n- spawn_quiz: ok fresh", pruned[4].Content);
            Assert.AreEqual("done", pruned[5].Content);
        }

        [Test]
        public void Prune_KeepsOlderMixedToolBlock_WhenOnlyPartiallySuperseded()
        {
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok old\n- call_skill_tool: ok moved"),
                Msg("assistant", "noted"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok fresh"),
                Msg("user", "continue")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreSame(history, pruned);
        }

        [Test]
        public void Prune_WhenNothingRemoved_ReturnsOriginalArrayReference()
        {
            ChatMessage[] history =
            {
                Msg("user", "a"),
                Msg("assistant", "b"),
                Msg("tool", "## Tool Results\nlatest")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 1);

            Assert.AreSame(history, pruned);
        }

        [Test]
        public void DeterministicManager_PrunesOldToolResultsBeforePartition()
        {
            InMemoryConversationSummaryStore store = new();
            DeterministicConversationContextManager manager =
                new(store, new HeuristicTokenEstimator());

            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\nstale"),
                Msg("assistant", "noted"),
                Msg("tool", "## Tool Results\nfresh"),
                Msg("user", "continue")
            };

            ConversationContextSnapshot snapshot = manager.BuildSnapshot(
                "role-prune",
                history,
                new AgentMemoryPolicy.RoleMemoryConfig { ContextTokens = 8192 },
                new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = 4096,
                    EnableContextPruning = true,
                    MaxRetainedToolResultMessages = 1
                });

            Assert.IsFalse(snapshot.WasCompacted);
            Assert.AreEqual(4, snapshot.RecentMessages.Length);
            Assert.AreEqual("opening", snapshot.RecentMessages[0].Content);
            Assert.AreEqual("noted", snapshot.RecentMessages[1].Content);
            Assert.AreEqual("## Tool Results\nfresh", snapshot.RecentMessages[2].Content);
            Assert.AreEqual("continue", snapshot.RecentMessages[3].Content);
            Assert.AreEqual("", store.LoadSummary("role-prune"),
                "Pruning alone must not create or mutate a rolled summary.");
        }

        private static ChatMessage Msg(string role, string content)
        {
            return new ChatMessage { Role = role, Content = content };
        }
    }
}
