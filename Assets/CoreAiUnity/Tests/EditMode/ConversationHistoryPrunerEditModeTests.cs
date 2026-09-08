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

        /// <summary>
        /// Дефект: прежняя норма считала перекрытием совпадение одного имени инструмента. Два вызова
        /// одного инструмента с РАЗНЫМИ результатами — это два разных вызова (аргументов в durable-блоке
        /// нет, доказать «тот же вызов» нечем), и ребёнок терял первый из них.
        /// </summary>
        [Test]
        public void Prune_KeepsOlderToolResult_WhenSameToolReturnedADifferentResult()
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

            Assert.AreSame(history, pruned,
                "Same tool name with a different recorded result is a different call; nothing is superseded.");
        }

        /// <summary>
        /// Перекрытие, которое можно доказать по данным: тот же инструмент и дословно та же записанная
        /// выдача в более новом блоке. Старшая копия не несёт ничего нового и уходит.
        /// </summary>
        [Test]
        public void Prune_DropsOlderToolResult_WhenNewerBlockRepeatsTheSameToolAndResult()
        {
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok {\"success\":true}"),
                Msg("assistant", "noted"),
                Msg("tool", "## Tool Results\n- call_skill_tool: ok moved"),
                Msg("user", "continue"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok {\"success\":true}"),
                Msg("assistant", "done")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(6, pruned.Length);
            Assert.AreEqual("opening", pruned[0].Content);
            Assert.AreEqual("noted", pruned[1].Content);
            Assert.AreEqual("## Tool Results\n- call_skill_tool: ok moved", pruned[2].Content);
            Assert.AreEqual("continue", pruned[3].Content);
            Assert.AreEqual("## Tool Results\n- spawn_quiz: ok {\"success\":true}", pruned[4].Content);
            Assert.AreEqual("done", pruned[5].Content);
        }

        /// <summary>
        /// Страж на скиллы. У учителя RedoSchool каждый скилл идёт через один внешний инструмент
        /// <c>call_skill_tool</c>, и по старой норме «следующий слайд» выбрасывал из промпта вывод
        /// код-станции — тот самый, про который ребёнок сейчас спрашивает «а почему вывелось 5?».
        /// Разные внутренние вызовы одной обёртки должны выживать все.
        /// </summary>
        [Test]
        public void Prune_SkillRouter_NextSlideDoesNotDiscardCodeStationOutput()
        {
            const string runCode =
                "## Tool Results\n- call_skill_tool: ok {\"success\":true,\"tool\":\"run_code\",\"stdout\":\"5\\n\"}";
            const string slide3 =
                "## Tool Results\n- call_skill_tool: ok {\"success\":true,\"tool\":\"next_slide\",\"slide\":3}";
            const string slide4 =
                "## Tool Results\n- call_skill_tool: ok {\"success\":true,\"tool\":\"next_slide\",\"slide\":4}";
            ChatMessage[] history =
            {
                Msg("user", "запусти мой код"),
                Msg("tool", runCode),
                Msg("assistant", "Вывод: 5"),
                Msg("user", "дальше"),
                Msg("tool", slide3),
                Msg("assistant", "Слайд 3"),
                Msg("user", "дальше"),
                Msg("tool", slide4),
                Msg("assistant", "Слайд 4"),
                Msg("user", "а почему вывелось 5?")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreSame(history, pruned,
                "Three different skill calls behind one router name are three different calls; none is superseded.");
            Assert.IsTrue(System.Array.Exists(pruned, m => m.Content == runCode),
                "The code-station output the learner is asking about must still be in the prompt.");
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
        public void DeterministicManager_NoCompaction_PrunesWholeEmittedHistoryWithoutTouchingSummary()
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

        [Test]
        public void Prune_StripsThinkBlocks_FromOlderAssistantTurns_KeepsNewestIntact()
        {
            ChatMessage[] history =
            {
                Msg("user", "u0"),
                Msg("assistant", "<think>old reasoning</think>Answer one"),
                Msg("user", "u1"),
                Msg("assistant", "<think>fresh reasoning</think>Answer two")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(4, pruned.Length);
            Assert.AreEqual("u0", pruned[0].Content);
            Assert.AreEqual("Answer one", pruned[1].Content);
            Assert.AreEqual("u1", pruned[2].Content);
            Assert.AreEqual("<think>fresh reasoning</think>Answer two", pruned[3].Content,
                "The newest assistant turn keeps its reasoning intact.");
        }

        [Test]
        public void Prune_DropsOlderAssistantMessage_WhenItIsOnlyReasoning()
        {
            ChatMessage[] history =
            {
                Msg("user", "u0"),
                Msg("assistant", "<think>just thinking, no answer</think>"),
                Msg("user", "u1"),
                Msg("assistant", "final answer")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(3, pruned.Length);
            Assert.AreEqual("u0", pruned[0].Content);
            Assert.AreEqual("u1", pruned[1].Content);
            Assert.AreEqual("final answer", pruned[2].Content);
        }

        [Test]
        public void Prune_StripsOrphanCloseTag_InOlderAssistant()
        {
            ChatMessage[] history =
            {
                Msg("user", "u0"),
                Msg("assistant", "hidden chain of thought</think>Visible answer"),
                Msg("user", "u1"),
                Msg("assistant", "latest")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(4, pruned.Length);
            Assert.AreEqual("Visible answer", pruned[1].Content);
        }

        [Test]
        public void Prune_KeepsThinkBlock_WhenOnlyAssistantIsTheNewest()
        {
            ChatMessage[] history =
            {
                Msg("user", "u0"),
                Msg("assistant", "<think>reasoning</think>answer")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreSame(history, pruned);
        }

        /// <summary>
        /// Hardening: a Full-policy tool block indents the tool output under "  Detail:", and that output may
        /// itself contain markdown/YAML lines like "  - read_file: ok". Those nested lines must NOT be parsed
        /// as tool names — otherwise a later block whose Detail merely mentions an older tool would wrongly
        /// supersede and drop it. Here the newer block ran 'list_dir' but its Detail text contains
        /// "- spawn_quiz: ok"; the older real 'spawn_quiz' result must survive.
        /// </summary>
        [Test]
        public void Prune_IgnoresNestedDetailLines_DoesNotFalselySupersede()
        {
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok created quiz"),
                Msg("assistant", "noted"),
                Msg("tool",
                    "## Tool Results\n- list_dir: ok\n  Detail:\n  Files found:\n  - spawn_quiz: ok\n  - other: FAILED"),
                Msg("user", "continue")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            // The older spawn_quiz block is NOT superseded (the newer block's only real tool is list_dir),
            // so nothing is removed and the original array is returned unchanged.
            Assert.AreSame(history, pruned,
                "Nested Detail lines must not be treated as tool names; the older result must survive.");
        }

        /// <summary>
        /// Под политикой Full выдача инструмента лежит в отступном блоке Detail и входит в идентичность
        /// записи: та же выдача в новом блоке — старший блок избыточен.
        /// </summary>
        [Test]
        public void Prune_StillSupersedes_RealTopLevelEntry_WithIdenticalFullDetail()
        {
            const string block = "## Tool Results\n- spawn_quiz: ok\n  Detail:\n  {\"success\":true}";
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", block),
                Msg("assistant", "noted"),
                Msg("tool", block),
                Msg("user", "continue")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreEqual(4, pruned.Length);
            Assert.AreEqual("opening", pruned[0].Content);
            Assert.AreEqual("noted", pruned[1].Content);
            Assert.AreEqual(block, pruned[2].Content);
            Assert.AreEqual("continue", pruned[3].Content);
        }

        /// <summary>
        /// Под Full две выдачи одного инструмента различаются только внутри Detail — и этого достаточно,
        /// чтобы считать их разными вызовами: старшая выдача остаётся.
        /// </summary>
        [Test]
        public void Prune_KeepsOlderFullPolicyBlock_WhenOnlyTheDetailDiffers()
        {
            ChatMessage[] history =
            {
                Msg("user", "opening"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok\n  Detail:\n  old detail"),
                Msg("assistant", "noted"),
                Msg("tool", "## Tool Results\n- spawn_quiz: ok\n  Detail:\n  fresh detail"),
                Msg("user", "continue")
            };

            ChatMessage[] pruned = ConversationHistoryPruner.Prune(history, 10);

            Assert.AreSame(history, pruned);
        }

        private static ChatMessage Msg(string role, string content)
        {
            return new ChatMessage { Role = role, Content = content };
        }
    }
}
