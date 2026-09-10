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
    /// What happens to the teacher's turn when the panel is disabled mid-answer (Esc, focus loss).
    /// <para>
    /// The defect, from the child's side: the panel went dark mid-sentence and the teacher's answer disappeared not
    /// only from the screen but from the history as well. The model started the next turn as if it had answered
    /// nothing, while the tokens had already been burned. The cause was in <c>OnDisable</c>: the turn generation was
    /// bumped UNCONDITIONALLY, even though <see cref="CoreAiChatPanel.CancelsActiveRequestOnDisable"/> promised the
    /// host "a turn played to the end". The next chunk saw itself as stale, left the enumeration, the service closed
    /// the orchestrator's iterator early, and in its <c>finally</c> the orchestrator wrote a single learner message
    /// into the history: the assistant message is written later, at the publication point the stream never reached.
    /// </para>
    /// <para>
    /// The history here is the real one: the production <see cref="AiOrchestrator"/> on top of the production
    /// <see cref="CoreAiChatService"/>, with only the model and the store replaced. An orchestrator double would
    /// have proved nothing: the defect lived exactly on the seam between the three layers.
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
        /// The host asked for the turn not to be cut (<c>CancelsActiveRequestOnDisable = false</c>): the panel was
        /// disabled mid-stream and the turn ran to the end, so the teacher's answer is in the orchestrator history,
        /// in the feed cache and at the response handler. The bubble of the disabled tree is left alone.
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
            await WaitFor(llm.SecondChunkRequested, "the model produced the first chunk");
            int generationBeforeDisable = ctx.Panel.CurrentTurnGeneration;

            ctx.Panel.SimulateDisable();

            Assert.AreEqual(generationBeforeDisable, ctx.Panel.CurrentTurnGeneration,
                "A turn nobody interrupted must stay the CURRENT one: bumping the generation is what loses the answer.");
            Assert.IsTrue(ctx.Panel.IsBusy,
                "While the teacher is still speaking the panel is busy, so a new message cannot cut in.");
            Assert.IsFalse(RequestCancelled(ctx.Panel), "No cancellation on the request: the host asked to play the turn out.");

            llm.Release();
            string reply = await WaitFor(turn, "the turn after the panel was disabled");

            Assert.AreEqual(FullReply, reply, "The turn must return the full answer instead of breaking off on disable.");
            CollectionAssert.AreEqual(new[] { "user", "assistant" }, memory.Appended.Select(m => m.Role).ToArray(),
                "The child came back, so the model must remember that it answered: the assistant message is in the history.");
            Assert.AreEqual(FullReply, memory.Appended[1].Content);
            CollectionAssert.AreEqual(new[] { FullReply }, ctx.Panel.ReceivedResponses,
                "The host receives the answer through OnResponseReceived exactly once and in full.");
            Assert.IsFalse(ctx.Panel.IsBusy, "A turn played to the end clears busy itself, in its own finally.");

            foreach (Label label in AiLabels(oldScroll))
            {
                Assert.IsFalse(label.text.Contains(SecondPart),
                    "The tree of the disabled panel is let go: the tail of the answer is not appended to a dead bubble.");
                Assert.IsFalse(label.ClassListContains(CoreAiChatPanel.StreamingActiveUssClassName),
                    "A bubble in a disabled tree must not look alive.");
            }
        }

        /// <summary>
        /// The panel is enabled again while the teacher is still speaking: the continuation opens a bubble in the NEW
        /// tree from the start of the segment, not as a tail without a head, so the child reads the whole phrase.
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
            await WaitFor(llm.SecondChunkRequested, "the model produced the first chunk");

            ctx.Panel.SimulateDisable();
            // Re-enabling: a new tree is attached and the lifecycle is active again. The turn is still the same one,
            // because the generation never moved.
            ScrollView newScroll = AttachScroll(ctx.Panel);
            SetField(ctx.Panel, "_lifecycleActive", true);

            llm.Release();
            string reply = await WaitFor(turn, "the turn after the panel was enabled again");

            Assert.AreEqual(FullReply, reply);
            List<Label> labels = AiLabels(newScroll);
            Assert.AreEqual(1, labels.Count, "The answer segment continues inside one bubble of the new tree.");
            Assert.AreEqual(FullReply, labels[0].text,
                "The bubble in the new tree starts at the start of the segment, not at the tail left after the disable.");
            Assert.AreEqual(FullReply, memory.Appended.Single(m => m.Role == "assistant").Content);
        }

        /// <summary>
        /// The package default (<c>true</c>) has not changed: disabling the panel ends the turn. This test also pins
        /// the loss mechanism from the defect description itself: a stale turn leaves the enumeration, the
        /// orchestrator's iterator closes early, and ONLY the learner message is left in the history.
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
            await WaitFor(llm.SecondChunkRequested, "the model produced the first chunk");
            int generationBeforeDisable = ctx.Panel.CurrentTurnGeneration;

            ctx.Panel.SimulateDisable();

            Assert.AreEqual(generationBeforeDisable + 1, ctx.Panel.CurrentTurnGeneration,
                "By default disabling makes the turn stale.");
            Assert.IsFalse(ctx.Panel.IsBusy, "By default busy is cleared right inside OnDisable.");
            Assert.IsTrue(RequestCancelled(ctx.Panel), "By default the request is cancelled.");

            llm.Release();
            string reply = await WaitFor(turn, "the abandoned turn");

            Assert.IsNull(reply, "An abandoned turn returns no answer.");
            CollectionAssert.AreEqual(new[] { "user" }, memory.Appended.Select(m => m.Role).ToArray(),
                "An abandoned turn leaves only the learner message in the history, which is exactly how the answer was lost with false.");
            CollectionAssert.IsEmpty(ctx.Panel.ReceivedResponses);
        }

        // ---------- panels ----------

        /// <summary>
        /// A host whose history lives outside the panel (in RedoSchool that is the briefing chat): disabling the
        /// panel means no more than "the person moved their focus elsewhere".
        /// </summary>
        private sealed class KeepTurnAlivePanel : CoreAiChatPanel, IDisableSimulatingPanel
        {
            public List<string> ReceivedResponses { get; } = new();

            protected override bool CancelsActiveRequestOnDisable => false;

            protected override bool AutoFocusInputFieldEnabled => false;

            /// <summary>EditMode does not fire MonoBehaviour lifecycle callbacks, so OnDisable is called directly.</summary>
            public void SimulateDisable()
            {
                OnDisable();
            }

            protected override void OnResponseReceived(string fullResponse)
            {
                ReceivedResponses.Add(fullResponse);
            }
        }

        /// <summary>A panel with the package default <c>CancelsActiveRequestOnDisable = true</c>.</summary>
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
            Assert.AreSame(task, finished, $"{operation}: did not arrive within {TurnTimeoutSeconds:F0} s.");
            await task;
        }

        private static ScrollView AttachScroll(CoreAiChatPanel panel)
        {
            // A real ScrollView (with no panel): bubbles are created and available for inspection, and the scheduler
            // safely accepts work without a live panel.
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
            // WHY: EditMode does not fire MonoBehaviour lifecycle callbacks, so "the panel is enabled" is modelled
            // explicitly, without weakening the production lifecycle guard.
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
        /// The model produces the first piece of the answer and freezes until the test releases it: that is exactly
        /// the moment the panel is disabled.
        /// </summary>
        private sealed class GatedStreamingLlmClient : ILlmClient
        {
            private readonly TaskCompletionSource<bool> _secondChunkRequested =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes when the pipeline asked for the SECOND chunk, so the first has already gone through the panel.</summary>
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
                // WHY: cancellation is deliberately ignored here, so that the difference between an abandoned and a
                // played-out turn comes from the panel alone, not from an obedient model.
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
