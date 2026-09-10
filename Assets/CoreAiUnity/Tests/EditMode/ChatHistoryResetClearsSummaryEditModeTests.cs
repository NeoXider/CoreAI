using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Дефект: <c>IAgentMemoryStore.ClearChatHistory</c> чистил только историю, а свёрнутый summary жил в
    /// отдельном сторе и переживал сброс. Оба менеджера контекста отдают сохранённый summary в КАЖДЫЙ ход,
    /// ещё до всякой компакции, — поэтому после «сбросить историю» на старте новой миссии ученик получал
    /// учителя, который в каждой реплике держит в голове прошлый урок. Здесь «сбросить историю» означает
    /// «сбросить историю»: и хвост, и всё, что из него свёрнуто.
    /// </summary>
    [TestFixture]
    public sealed class ChatHistoryResetClearsSummaryEditModeTests
    {
        private const string Role = "Teacher";

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public string UserId { get; set; } = "";

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("", UserId, "", "");
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class TestLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "Hello" });
            }
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
            public bool EnableStreaming => false;
            public bool EnableConversationHistorySummarization => true;
        }

        private static (ScopedAgentMemoryStoreDecorator memory, IConversationSummaryStore summaries) MakeScopedStores(
            IAgentMemoryScopeProvider provider)
        {
            IConversationSummaryStore summaries =
                new ScopedConversationSummaryStoreDecorator(new InMemoryConversationSummaryStore(), provider);
            ScopedAgentMemoryStoreDecorator memory =
                new(new InMemoryAgentMemoryStore(), provider, summaries);
            return (memory, summaries);
        }

        [Test]
        public void ClearChatHistory_DropsTheSummaryOfTheErasedTurns_OnlyForThatScope()
        {
            MutableScopeProvider provider = new();
            (ScopedAgentMemoryStoreDecorator memory, IConversationSummaryStore summaries) =
                MakeScopedStores(provider);

            provider.UserId = "student-a";
            memory.AppendChatMessage(Role, "user", "lesson-a turn", false);
            summaries.SaveSummary(Role, "lesson-a summary");

            provider.UserId = "student-b";
            memory.AppendChatMessage(Role, "user", "lesson-b turn", false);
            summaries.SaveSummary(Role, "lesson-b summary");

            provider.UserId = "student-a";
            memory.ClearChatHistory(Role);

            Assert.IsEmpty(memory.GetChatHistory(Role));
            Assert.AreEqual("", summaries.LoadSummary(Role),
                "The summary was folded from the erased turns; it must go with them.");

            provider.UserId = "student-b";
            Assert.AreEqual(1, memory.GetChatHistory(Role).Length);
            Assert.AreEqual("lesson-b summary", summaries.LoadSummary(Role),
                "Another learner's summary under the same role must be untouched.");
        }

        [Test]
        public void Clear_LongTermMemory_LeavesHistoryAndSummaryAlone()
        {
            (ScopedAgentMemoryStoreDecorator memory, IConversationSummaryStore summaries) =
                MakeScopedStores(new DefaultAgentMemoryScopeProvider());
            memory.Save(Role, new AgentMemoryState { Memory = "learner profile" });
            memory.AppendChatMessage(Role, "user", "turn", false);
            summaries.SaveSummary(Role, "summary");

            memory.Clear(Role);

            // WHY: Clear() is the long-term MEMORY reset (learner profile); the conversation and its
            // summary are a different lifetime and must survive it - the mirror of ClearChatHistory.
            Assert.IsFalse(memory.TryLoad(Role, out _));
            Assert.AreEqual(1, memory.GetChatHistory(Role).Length);
            Assert.AreEqual("summary", summaries.LoadSummary(Role));
        }

        /// <summary>
        /// Сквозной сценарий RedoSchool: прошлый урок свернулся в summary; новая миссия зовёт
        /// <c>memoryStore.ClearChatHistory</c> и ничего не знает про стор summary. В промпт первого хода
        /// нового урока не должно попасть ни строки прошлого пересказа.
        /// </summary>
        [Test]
        public async Task RunTaskAsync_AfterClearChatHistory_NextTurnCarriesNoLineOfThePreviousLesson()
        {
            (ScopedAgentMemoryStoreDecorator memory, IConversationSummaryStore summaries) =
                MakeScopedStores(new DefaultAgentMemoryScopeProvider());
            for (int i = 0; i < 10; i++)
            {
                memory.AppendChatMessage(
                    Role,
                    i % 2 == 0 ? "user" : "assistant",
                    $"old-lesson-{i}-".PadRight(1000, 'x'),
                    true);
            }

            AgentMemoryPolicy policy = new();
            // WHY: A 4096-token role window over ten 1000-char turns forces compaction on the first run
            // while its summary reserve still holds the retelling of the folded turns - the same setup
            // AiOrchestratorHistoryEditModeTests uses to produce a summary.
            policy.ConfigureChatHistory(Role, true, 4096, false, 50);
            TestSettings settings = new();
            TestLlmClient llm = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                // WHY: The anonymous local actor with an empty scope resolves to the bare role key, the
                // same key the host uses OUTSIDE a turn - so seeding, the turn and the reset all hit one file.
                new LocalActorIdentityProvider(),
                new DeterministicConversationContextManager(summaries));

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = Role, Hint = "продолжаем" });

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m =>
                    (m.Text ?? "").Contains(ConversationSummaryPromptProjection.Header) &&
                    (m.Text ?? "").Contains("old-lesson-0")),
                "Precondition: the first lesson was folded into a summary.");
            Assert.IsNotEmpty(summaries.LoadSummary(Role), "Precondition: the summary was persisted.");

            memory.ClearChatHistory(Role);
            memory.AppendChatMessage(Role, "user", "new-lesson-hello", true);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = Role, Hint = "новый урок" });

            Assert.IsNotNull(llm.LastRequest?.ChatHistory);
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m =>
                    (m.Text ?? "").Contains(ConversationSummaryPromptProjection.Header)),
                "After a history reset the next turn must carry no summary block at all.");
            Assert.IsFalse(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("old-lesson")),
                "Not a single line of the previous lesson may reach the model.");
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(m => (m.Text ?? "").Contains("new-lesson-hello")));
            Assert.AreEqual("", summaries.LoadSummary(Role));
        }
    }
}
