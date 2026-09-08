using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Судьба хода учителя, когда панель выключили посреди ответа (Esc, потеря фокуса).
    /// <para>
    /// Дефект, от лица ребёнка: панель погасла на полуслове — и ответ учителя пропал не только с
    /// экрана, но и из истории. Следующий ход модель начинала так, будто ничего не отвечала, а токены
    /// уже были сожжены. Причина была в <c>OnDisable</c>: поколение хода сдвигалось БЕЗУСЛОВНО, хотя
    /// <see cref="CoreAiChatPanel.CancelsActiveRequestOnDisable"/> обещал хосту «ход, доигранный до
    /// конца». Следующий чанк видел себя устаревшим, выходил из перечисления, сервис досрочно
    /// закрывал итератор оркестратора, а тот в <c>finally</c> записывал в историю одну реплику ученика:
    /// реплика ассистента пишется позже, в точке публикации, до которой поток не доходил.
    /// </para>
    /// <para>
    /// История здесь настоящая: боевой <see cref="AiOrchestrator"/> поверх боевого
    /// <see cref="CoreAiChatService"/>, только модель и хранилище подменены. Двойник оркестратора
    /// не доказал бы ничего — дефект жил ровно на стыке трёх слоёв.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelDisableKeepsTurnEditModeTests
    {
        private const string RoleId = "Teacher";
        private const string FirstPart = "Сравни: 2 ";
        private const string SecondPart = "< 3 — верно.";
        private const string FullReply = FirstPart + SecondPart;

        /// <summary>
        /// Хост попросил не обрывать ход (<c>CancelsActiveRequestOnDisable = false</c>): панель
        /// выключилась посреди стрима, а ход дошёл до конца — ответ учителя в истории оркестратора,
        /// в кэше ленты и у обработчика ответа. Пузырь выключенного дерева при этом не трогается.
        /// </summary>
        [Test]
        public async Task PanelKeepingTurnAlive_DisabledMidStream_ReplyReachesHistory()
        {
            using PanelCtx<KeepTurnAlivePanel> ctx = NewPanel<KeepTurnAlivePanel>();
            ScrollView oldScroll = AttachScroll(ctx.Panel);
            GatedStreamingLlmClient llm = new();
            RecordingMemoryStore memory = new();
            ctx.Panel.ChatService = NewChatService(llm, memory);

            Task<string> turn = ctx.Panel.SubmitMessageFromExternalAsync(
                "что больше?",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await WaitFor(llm.SecondChunkRequested, "модель отдала первый чанк");
            int generationBeforeDisable = ctx.Panel.CurrentTurnGeneration;

            ctx.Panel.SimulateDisable();

            Assert.AreEqual(generationBeforeDisable, ctx.Panel.CurrentTurnGeneration,
                "Ход, который никто не прерывал, обязан остаться ТЕКУЩИМ: сдвиг поколения и есть причина потери ответа.");
            Assert.IsTrue(ctx.Panel.IsBusy,
                "Пока учитель договаривает, панель занята — новая реплика его не перебьёт.");
            Assert.IsFalse(RequestCancelled(ctx.Panel), "Запрос без отмены: хост попросил доиграть ход.");

            llm.Release();
            string reply = await WaitFor(turn, "ход после выключения панели");

            Assert.AreEqual(FullReply, reply, "Ход обязан вернуть полный ответ, а не оборваться на выключении.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" }, memory.Appended.Select(m => m.Role).ToArray(),
                "Ребёнок вернулся — модель обязана помнить, что отвечала: реплика ассистента в истории.");
            Assert.AreEqual(FullReply, memory.Appended[1].Content);
            CollectionAssert.AreEqual(new[] { FullReply }, ctx.Panel.ReceivedResponses,
                "Хост получает ответ через OnResponseReceived ровно один раз и целиком.");
            Assert.IsFalse(ctx.Panel.IsBusy, "Доигранный ход сам снимает busy в своём finally.");

            foreach (Label label in AiLabels(oldScroll))
            {
                Assert.IsFalse(label.text.Contains(SecondPart),
                    "Дерево выключенной панели отпущено: хвост ответа в мёртвый пузырь не дописывается.");
                Assert.IsFalse(label.ClassListContains(CoreAiChatPanel.StreamingActiveUssClassName),
                    "Пузырь выключенного дерева не должен выглядеть живым.");
            }
        }

        /// <summary>
        /// Панель включили обратно, пока учитель ещё говорит: продолжение открывает пузырь в НОВОМ дереве
        /// с начала сегмента, а не хвостом без начала — ребёнок читает фразу целиком.
        /// </summary>
        [Test]
        public async Task PanelKeepingTurnAlive_ReboundMidStream_ContinuesInNewTreeFromSegmentStart()
        {
            using PanelCtx<KeepTurnAlivePanel> ctx = NewPanel<KeepTurnAlivePanel>();
            AttachScroll(ctx.Panel);
            GatedStreamingLlmClient llm = new();
            RecordingMemoryStore memory = new();
            ctx.Panel.ChatService = NewChatService(llm, memory);

            Task<string> turn = ctx.Panel.SubmitMessageFromExternalAsync(
                "что больше?",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await WaitFor(llm.SecondChunkRequested, "модель отдала первый чанк");

            ctx.Panel.SimulateDisable();
            // Повторное включение: новое дерево привязано, жизненный цикл снова активен. Ход при этом
            // тот же — поколение не двигалось.
            ScrollView newScroll = AttachScroll(ctx.Panel);
            SetField(ctx.Panel, "_lifecycleActive", true);

            llm.Release();
            string reply = await WaitFor(turn, "ход после повторного включения панели");

            Assert.AreEqual(FullReply, reply);
            List<Label> labels = AiLabels(newScroll);
            Assert.AreEqual(1, labels.Count, "Сегмент ответа продолжается в одном пузыре нового дерева.");
            Assert.AreEqual(FullReply, labels[0].text,
                "Пузырь в новом дереве начинается с начала сегмента, а не с хвоста после выключения.");
            Assert.AreEqual(FullReply, memory.Appended.Single(m => m.Role == "assistant").Content);
        }

        /// <summary>
        /// Пакетное умолчание (<c>true</c>) не менялось: выключение панели — конец хода. Заодно этот тест
        /// закрепляет сам механизм потери из описания дефекта: устаревший ход выходит из перечисления,
        /// итератор оркестратора закрывается досрочно, и в истории остаётся ТОЛЬКО реплика ученика.
        /// </summary>
        [Test]
        public async Task DefaultPanel_DisabledMidStream_TurnIsAbandonedAndOnlyUserTurnIsRecorded()
        {
            using PanelCtx<DefaultDisablePanel> ctx = NewPanel<DefaultDisablePanel>();
            AttachScroll(ctx.Panel);
            GatedStreamingLlmClient llm = new();
            RecordingMemoryStore memory = new();
            ctx.Panel.ChatService = NewChatService(llm, memory);

            Task<string> turn = ctx.Panel.SubmitMessageFromExternalAsync(
                "что больше?",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await WaitFor(llm.SecondChunkRequested, "модель отдала первый чанк");
            int generationBeforeDisable = ctx.Panel.CurrentTurnGeneration;

            ctx.Panel.SimulateDisable();

            Assert.AreEqual(generationBeforeDisable + 1, ctx.Panel.CurrentTurnGeneration,
                "По умолчанию выключение делает ход устаревшим.");
            Assert.IsFalse(ctx.Panel.IsBusy, "По умолчанию busy снимается прямо в OnDisable.");
            Assert.IsTrue(RequestCancelled(ctx.Panel), "По умолчанию запрос отменяется.");

            llm.Release();
            string reply = await WaitFor(turn, "брошенный ход");

            Assert.IsNull(reply, "Брошенный ход не возвращает ответа.");
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "Брошенный ход оставляет в истории только реплику ученика — так и терялся ответ при false.");
            CollectionAssert.IsEmpty(ctx.Panel.ReceivedResponses);
        }

        // ---------- panels ----------

        /// <summary>
        /// Хост, у которого история живёт вне панели (в RedoSchool — брифинг-чат): выключение панели
        /// значит лишь «человек вышел из фокуса».
        /// </summary>
        private sealed class KeepTurnAlivePanel : CoreAiChatPanel, IDisableSimulatingPanel
        {
            public List<string> ReceivedResponses { get; } = new();

            protected override bool CancelsActiveRequestOnDisable => false;

            protected override bool AutoFocusInputFieldEnabled => false;

            /// <summary>EditMode не зовёт lifecycle-колбэки MonoBehaviour, поэтому OnDisable вызывается напрямую.</summary>
            public void SimulateDisable()
            {
                OnDisable();
            }

            protected override void OnResponseReceived(string fullResponse)
            {
                ReceivedResponses.Add(fullResponse);
            }
        }

        /// <summary>Панель с пакетным умолчанием <c>CancelsActiveRequestOnDisable = true</c>.</summary>
        private sealed class DefaultDisablePanel : CoreAiChatPanel, IDisableSimulatingPanel
        {
            public List<string> ReceivedResponses { get; } = new();

            protected override bool AutoFocusInputFieldEnabled => false;

            public void SimulateDisable()
            {
                OnDisable();
            }

            protected override void OnResponseReceived(string fullResponse)
            {
                ReceivedResponses.Add(fullResponse);
            }
        }

        private interface IDisableSimulatingPanel
        {
            List<string> ReceivedResponses { get; }

            void SimulateDisable();
        }

        // ---------- helpers ----------

        private const float TurnTimeoutSeconds = 8f;

        private static async Task<T> WaitFor<T>(Task<T> task, string operation)
        {
            await WaitFor((Task)task, operation);
            return task.Result;
        }

        private static async Task WaitFor(Task task, string operation)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(System.TimeSpan.FromSeconds(TurnTimeoutSeconds)));
            Assert.AreSame(task, finished, $"{operation}: не дождались за {TurnTimeoutSeconds:F0} с.");
            await task;
        }

        private static ScrollView AttachScroll(CoreAiChatPanel panel)
        {
            // Настоящий (без панели) ScrollView: пузыри создаются и доступны для проверки, а планировщик
            // безопасно принимает задания без живой панели.
            ScrollView scroll = new();
            SetField(panel, "MessageScroll", scroll);
            SetField(panel, "ChatContainer", scroll);
            return scroll;
        }

        private static List<Label> AiLabels(VisualElement scroll)
        {
            return scroll.Query<Label>().Class("coreai-ai-message").ToList();
        }

        private static bool RequestCancelled(CoreAiChatPanel panel)
        {
            CancellationTokenSource cts = GetField<CancellationTokenSource>(panel, "_activeRequestCts");
            return cts == null || cts.IsCancellationRequested;
        }

        private static CoreAiChatService NewChatService(ILlmClient llm, IAgentMemoryStore memory)
        {
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(RoleId, true, 8192, false, 10);
            policy.DisableMemoryTool(RoleId);
            policy.SetToolsForRole(RoleId, System.Array.Empty<ILlmTool>());
            StubSettings settings = new();
            AiOrchestrator orchestrator = new(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings,
                new LocalActorIdentityProvider("disable-keeps-turn-test"));
            return new CoreAiChatService(orchestrator, policy, settings, memory);
        }

        private readonly struct PanelCtx<TPanel> : System.IDisposable
            where TPanel : CoreAiChatPanel, IDisableSimulatingPanel
        {
            public readonly GameObject Go;
            public readonly TPanel Panel;

            public PanelCtx(GameObject go, TPanel panel)
            {
                Go = go;
                Panel = panel;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Go);
            }
        }

        private static PanelCtx<TPanel> NewPanel<TPanel>()
            where TPanel : CoreAiChatPanel, IDisableSimulatingPanel
        {
            GameObject go = new("CoreAiChatPanel_DisableKeepsTurn_Test");
            TPanel panel = go.AddComponent<TPanel>();
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = RoleId, EnableStreaming = true });
            panel.SetActorIdentityProvider(new LocalActorIdentityProvider("disable-keeps-turn-test"));
            // WHY: EditMode не вызывает lifecycle-колбэки MonoBehaviour, поэтому «панель включена»
            // моделируется явно, не ослабляя боевой страж жизненного цикла.
            SetField(panel, "_lifecycleActive", true);
            return new PanelCtx<TPanel>(go, panel);
        }

        private static void SetField(CoreAiChatPanel panel, string fieldName, object value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
        }

        private static T GetField<T>(CoreAiChatPanel panel, string fieldName)
        {
            return (T)typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        // ---------- fakes ----------

        /// <summary>
        /// Модель отдаёт первый кусок ответа и замирает, пока тест не отпустит её: ровно в этот момент
        /// панель выключают.
        /// </summary>
        private sealed class GatedStreamingLlmClient : ILlmClient
        {
            private readonly TaskCompletionSource<bool> _secondChunkRequested =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Выполняется, когда конвейер запросил ВТОРОЙ чанк — первый уже прошёл через панель.</summary>
            public Task SecondChunkRequested => _secondChunkRequested.Task;

            public void Release()
            {
                _release.TrySetResult(true);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = FullReply });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = FirstPart };
                _secondChunkRequested.TrySetResult(true);
                // WHY: отмену намеренно не слушаем — так поведение брошенного и доигранного хода
                // различает только панель, а не послушная модель.
                await _release.Task;
                yield return new LlmStreamChunk { Text = SecondPart, IsDone = true };
            }
        }

        private sealed class RecordingMemoryStore : IAgentMemoryStore
        {
            private readonly List<Ai.ChatMessage> _history = new();

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
                _history.Clear();
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Clear();
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                Appended.Add((role, content));
                _history.Add(new Ai.ChatMessage(role, content));
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return _history.ToArray();
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
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

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => string.Empty;
            public float Temperature => 0.3f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 1;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 1;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}
