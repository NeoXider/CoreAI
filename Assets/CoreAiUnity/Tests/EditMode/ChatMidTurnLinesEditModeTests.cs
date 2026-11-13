using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.ChatOverlay;
using CoreAI.Tests.EditMode.ChatOverlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
#endif

namespace CoreAI.Tests.EditMode
{
#if COREAI_LLM
    /// <summary>
    /// A line the player sends while a turn runs (<see cref="LlmTurnInbox"/>) in the three tool loops: the MEAI
    /// non-streaming loop, its threadless WebGL twin and the streaming loop of <see cref="MeaiLlmClient"/>. The line
    /// reaches the next model request right after the tool results; a line that arrives during the final answer makes
    /// exactly one more round.
    /// </summary>
    [TestFixture]
    public sealed class ChatMidTurnLinesToolLoopEditModeTests
    {
        private const string Line = "and paint it blue";

        private SynchronizationContext _previousContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NonStreaming_ALineSentDuringAToolCall_ReachesTheNextRequestAfterTheToolResult(bool webGlLoop)
        {
            LlmTurnInbox inbox = new();
            ScriptedClient inner = new((iteration, _) => iteration == 1
                ? ToolCall("paint", "call_1")
                : Text("Done, it is blue."));
            SmartToolCallingChatClient client = NewClient(inner, inbox);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { PostingFunction("paint", inbox) }
            };

            MEAI.ChatResponse response = await Run(client, options, webGlLoop);

            Assert.AreEqual(2, inner.Requests.Count, "the line rode the existing next step; no extra round");
            List<MEAI.ChatMessage> next = inner.Requests[1];
            Assert.AreEqual(MEAI.ChatRole.User, next[next.Count - 1].Role);
            Assert.AreEqual(Line, next[next.Count - 1].Text, "the line is the newest message the model reads");
            Assert.AreEqual(MEAI.ChatRole.Tool, next[next.Count - 2].Role, "right after what the tool returned");
            Assert.IsFalse(inner.Requests[0].Any(message => message.Text == Line),
                "the request already running never changed");
            CollectionAssert.AreEqual(new[] { Line }, inbox.Delivered);
            Assert.AreEqual(0, inbox.PendingCount);
            StringAssert.Contains("Done, it is blue.", response.Text);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NonStreaming_ALineSentDuringTheFinalAnswer_RunsExactlyOneMoreRound(bool webGlLoop)
        {
            LlmTurnInbox inbox = new();
            ScriptedClient inner = new((iteration, _) =>
            {
                if (iteration == 1)
                {
                    inbox.TryPost(Line);
                    return Text("The house is built.");
                }

                return Text("Painted it blue too.");
            });
            SmartToolCallingChatClient client = NewClient(inner, inbox);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { PostingFunction("paint", null) }
            };

            MEAI.ChatResponse response = await Run(client, options, webGlLoop);

            Assert.AreEqual(2, inner.Requests.Count, "exactly one more round answers the line");
            List<MEAI.ChatMessage> second = inner.Requests[1];
            Assert.AreEqual(Line, second[second.Count - 1].Text);
            Assert.AreEqual(MEAI.ChatRole.Assistant, second[second.Count - 2].Role);
            Assert.AreEqual("The house is built.", second[second.Count - 2].Text,
                "the first answer stays in the history before the line");
            string text = LastAssistantText(response);
            StringAssert.StartsWith("The house is built.", text, "the turn's text keeps the first answer");
            StringAssert.EndsWith("Painted it blue too.", text);
        }

        [Test]
        public async Task Streaming_ALineSentDuringAToolCall_ReachesTheNextRequestAfterTheToolResult()
        {
            LlmTurnInbox inbox = new();
            StreamingClient inner = new(
                new[] { TextUpdate("Building."), CallUpdate("paint", "call_1") },
                new[] { TextUpdate("Done, it is blue.") });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new CoreAISettingsOptions(),
                supportsNativeToolCalling: true);

            List<LlmStreamChunk> chunks = await Collect(client, StreamingRequest(inbox, new PostingTool("paint", inbox)));

            Assert.AreEqual(2, inner.Requests.Count);
            List<MEAI.ChatMessage> next = inner.Requests[1];
            Assert.AreEqual(MEAI.ChatRole.User, next[next.Count - 1].Role);
            Assert.AreEqual(Line, next[next.Count - 1].Text);
            Assert.AreEqual(MEAI.ChatRole.Tool, next[next.Count - 2].Role, "right after what the tool returned");
            Assert.IsTrue(string.IsNullOrEmpty(chunks.Last().Error), chunks.Last().Error);
            string text = string.Concat(chunks.Select(chunk => chunk.Text));
            StringAssert.Contains("Building.", text, "the stream that was running when the line came was not cut");
            StringAssert.Contains("Done, it is blue.", text);
        }

        [Test]
        public async Task Streaming_ALineSentDuringTheFinalAnswer_RunsExactlyOneMoreRound()
        {
            LlmTurnInbox inbox = new();
            StreamingClient inner = new(
                new[] { TextUpdate("The house "), TextUpdate("is built.") },
                new[] { TextUpdate("Painted it blue too.") })
            {
                OnRequest = iteration =>
                {
                    if (iteration == 1)
                    {
                        inbox.TryPost(Line);
                    }
                }
            };
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new CoreAISettingsOptions(),
                supportsNativeToolCalling: true);

            List<LlmStreamChunk> chunks = await Collect(client, StreamingRequest(inbox, new PostingTool("paint", null)));

            Assert.AreEqual(2, inner.Requests.Count, "exactly one more round answers the line");
            List<MEAI.ChatMessage> second = inner.Requests[1];
            Assert.AreEqual(Line, second[second.Count - 1].Text);
            Assert.AreEqual("The house is built.", second[second.Count - 2].Text);
            Assert.IsTrue(chunks.Last().IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(chunks.Last().Error), chunks.Last().Error);
            Assert.IsTrue(chunks.Any(chunk => chunk.StartsNewMessage && chunk.Text == "Painted it blue too."),
                "the second answer streams as a new message of the same turn");
        }

        private static SmartToolCallingChatClient NewClient(MEAI.IChatClient inner, LlmTurnInbox inbox)
        {
            return new SmartToolCallingChatClient(inner, NullLog.Instance, new CoreAISettingsOptions(), true,
                new List<ILlmTool>(), "Programmer", 3) { Inbox = inbox };
        }

        private static async Task<MEAI.ChatResponse> Run(SmartToolCallingChatClient client, MEAI.ChatOptions options,
            bool webGlLoop)
        {
            List<MEAI.ChatMessage> messages = new() { new MEAI.ChatMessage(MEAI.ChatRole.User, "build a house") };
            if (!webGlLoop)
            {
                return await client.GetResponseAsync(messages, options);
            }

            MethodInfo loop = typeof(SmartToolCallingChatClient).GetMethod("GetWebGlResponseAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(loop, "the threadless WebGL loop is compiled in the editor too");
            return await (Task<MEAI.ChatResponse>)loop.Invoke(client,
                new object[] { messages, options, CancellationToken.None });
        }

        private static string LastAssistantText(MEAI.ChatResponse response)
        {
            return response.Messages.Last(message => message.Role == MEAI.ChatRole.Assistant).Text;
        }

        private static async Task<List<LlmStreamChunk>> Collect(MeaiLlmClient client, LlmCompletionRequest request)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        private static LlmCompletionRequest StreamingRequest(LlmTurnInbox inbox, ILlmTool tool)
        {
            return new LlmCompletionRequest
            {
                AgentRoleId = "Programmer",
                UserPayload = "build a house",
                Tools = new List<ILlmTool> { tool },
                Inbox = inbox
            };
        }

        private static MEAI.ChatResponse ToolCall(string name, string callId)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, name, new Dictionary<string, object>()) }));
        }

        private static MEAI.ChatResponse Text(string text)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));
        }

        private static MEAI.ChatResponseUpdate TextUpdate(string text)
        {
            return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, text);
        }

        private static MEAI.ChatResponseUpdate CallUpdate(string name, string callId)
        {
            MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, string.Empty);
            update.Contents.Add(new MEAI.FunctionCallContent(callId, name, new Dictionary<string, object>()));
            return update;
        }

        /// <summary>A tool that, while it runs, has the player send <see cref="Line"/> (when given an inbox).</summary>
        private static MEAI.AIFunction PostingFunction(string name, LlmTurnInbox inbox)
        {
            return MEAI.AIFunctionFactory.Create((Func<string>)(() =>
            {
                inbox?.TryPost(Line);
                return "{\"Success\":true}";
            }), new MEAI.AIFunctionFactoryOptions { Name = name, Description = "test tool" });
        }

        private sealed class PostingTool : ILlmTool, IAIFunctionLlmTool
        {
            private readonly LlmTurnInbox _inbox;

            public PostingTool(string name, LlmTurnInbox inbox)
            {
                Name = name;
                _inbox = inbox;
            }

            public string Name { get; }
            public string Description => "test tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return PostingFunction(Name, _inbox);
            }
        }

        /// <summary>Answers each non-streaming request from a script and records what every request carried.</summary>
        private sealed class ScriptedClient : MEAI.IChatClient
        {
            private readonly Func<int, List<MEAI.ChatMessage>, MEAI.ChatResponse> _script;

            public ScriptedClient(Func<int, List<MEAI.ChatMessage>, MEAI.ChatResponse> script)
            {
                _script = script;
            }

            public List<List<MEAI.ChatMessage>> Requests { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                List<MEAI.ChatMessage> snapshot = chatMessages.ToList();
                Requests.Add(snapshot);
                if (Requests.Count > 3)
                {
                    throw new InvalidOperationException("The loop asked the model more often than the test expects.");
                }

                return Task.FromResult(_script(Requests.Count, snapshot));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Streams one scripted update list per request; running out of scripts fails the turn.</summary>
        private sealed class StreamingClient : MEAI.IChatClient
        {
            private readonly Queue<MEAI.ChatResponseUpdate[]> _scripts;

            public StreamingClient(params MEAI.ChatResponseUpdate[][] scripts)
            {
                _scripts = new Queue<MEAI.ChatResponseUpdate[]>(scripts);
            }

            public Action<int> OnRequest { get; set; }

            public List<List<MEAI.ChatMessage>> Requests { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Requests.Add(chatMessages.ToList());
                if (_scripts.Count == 0)
                {
                    throw new InvalidOperationException("The loop asked the model more often than the test expects.");
                }

                MEAI.ChatResponseUpdate[] script = _scripts.Dequeue();
                for (int i = 0; i < script.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return script[i];
                    if (i == 0)
                    {
                        // WHY after the first update: the player writes while this answer is streaming.
                        OnRequest?.Invoke(Requests.Count);
                    }

                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
#endif

    /// <summary>
    /// The orchestrator's side of <see cref="LlmTurnInbox"/>: delivered lines enter the role's history right after the
    /// turn's own message, in order; a stopped turn keeps the lines it never delivered in the history and starts no
    /// new turn.
    /// </summary>
    [TestFixture]
    public sealed class ChatMidTurnLinesHistoryEditModeTests
    {
        [Test]
        public async Task DeliveredLines_AreRecordedAfterTheTurnsMessage_InTheOrderTheModelSawThem()
        {
            LlmTurnInbox inbox = new();
            inbox.TryPost("first line");
            inbox.TryPost("second line");
            InboxLlmClient llm = new(request =>
            {
                request.Inbox.TakeForDelivery();
                request.Inbox.TryPost("too late for this turn");
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "Both done." });
            });
            RecordingStore memory = new();

            await Orchestrator(llm, memory).RunTaskAsync(ChatTask(inbox));

            CollectionAssert.AreEqual(
                new[]
                {
                    "user:build a house", "user:first line", "user:second line", "assistant:Both done."
                },
                History(memory));
            Assert.IsTrue(inbox.IsClosed, "the turn is over for its inbox");
            CollectionAssert.AreEqual(new[] { "too late for this turn" }, inbox.TakeUndelivered(),
                "after an answer a line the model never saw is left for the chat to send as a turn of its own");
        }

        [Test]
        public async Task ALineLeftAtCancel_IsInTheHistoryAfterTheTurnEnds_AndNoNewTurnStarts()
        {
            LlmTurnInbox inbox = new();
            using CancellationTokenSource stop = new();
            InboxLlmClient llm = new(request =>
            {
                inbox.TryPost("and a tower");
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            });
            RecordingStore memory = new();

            try
            {
                await Orchestrator(llm, memory).RunTaskAsync(ChatTask(inbox), stop.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(1, llm.Calls, "the stop does not start another turn for the waiting line");
            CollectionAssert.AreEqual(new[] { "user:build a house", "user:and a tower" }, History(memory),
                "the player's line is kept for the next turn the player starts");
            Assert.IsTrue(inbox.IsClosed);
            Assert.IsFalse(inbox.TryPost("after the stop"), "a later line is a turn of its own");
            Assert.AreEqual(0, inbox.PendingCount);
        }

        private static AiTaskRequest ChatTask(LlmTurnInbox inbox)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "build a house",
                SourceTag = "Chat",
                Inbox = inbox
            };
        }

        private static string[] History(RecordingStore memory)
        {
            return memory.Appended
                .Where(message => message.Role == "user" || message.Role == "assistant")
                .Select(message => message.Role + ":" + message.Content).ToArray();
        }

        private static AiOrchestrator Orchestrator(ILlmClient llm, IAgentMemoryStore memory)
        {
            AgentMemoryPolicy policy = new();
            AiOrchestratorHistoryEditModeTests.TestSettings settings = new() { EnableStreaming = false };
            return new AiOrchestrator(
                new AiOrchestratorHistoryEditModeTests.TestAuthority(), llm,
                new AiOrchestratorHistoryEditModeTests.TestSink(), new AiOrchestratorHistoryEditModeTests.TestTelemetry(),
                new AiPromptComposer(new AiOrchestratorHistoryEditModeTests.NullSys(),
                    new AiOrchestratorHistoryEditModeTests.NullUsr(), null, null, policy, settings),
                memory, policy, null, null, settings, new LocalActorIdentityProvider("mid-turn-lines-test"));
        }

        private sealed class RecordingStore : IAgentMemoryStore
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
                Appended.Add((role, content));
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
            }
        }

        private sealed class InboxLlmClient : ILlmClient
        {
            private readonly Func<LlmCompletionRequest, Task<LlmCompletionResult>> _answer;

            public InboxLlmClient(Func<LlmCompletionRequest, Task<LlmCompletionResult>> answer)
            {
                _answer = answer;
            }

            public int Calls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return _answer(request);
            }
        }
    }

    /// <summary>
    /// Both chats accept a line while a turn runs: the overlay (T) shows it at once and posts it to the turn, the Hub
    /// chat (F2) keeps Send enabled next to a Stop button of its own.
    /// </summary>
    [TestFixture]
    public sealed class ChatMidTurnLinesChatsEditModeTests
    {
        [Test]
        public void Overlay_AcceptsASendWhileBusy_AndShowsItAtOnce()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a house");
            harness.Sender.Stream("Building");

            harness.Controller.Submit("and paint it blue");

            List<ChatFeedLine> player = harness.Controller.Feed.Lines
                .Where(line => line.Kind == ChatFeedLineKind.Player).ToList();
            Assert.AreEqual(2, player.Count, "the line is in the feed at once, as the player's");
            Assert.AreEqual("and paint it blue", player[1].Text);
            Assert.IsFalse(harness.Controller.Feed.Lines.Any(line => line.Kind == ChatFeedLineKind.Error),
                "no busy refusal");
            Assert.AreEqual(1, harness.Sender.Requests.Count, "the running turn is neither cancelled nor restarted");
            Assert.AreEqual(0, harness.Sender.LastTurn.CancelCalls);
            Assert.AreEqual(1, harness.Sender.Requests[0].Inbox.PendingCount, "the turn takes it at its next step");
        }

        [Test]
        public void Overlay_ALineTheTurnNeverDelivered_StartsOneTurnAfterTheAnswer()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a house");
            harness.Controller.Submit("and paint it blue");

            harness.Sender.Stream("Built.");
            harness.Sender.Complete();
            harness.Controller.Tick();

            Assert.AreEqual(2, harness.Sender.Requests.Count);
            Assert.AreEqual("and paint it blue", harness.Sender.Requests[1].Text);
            Assert.AreEqual(2, harness.Controller.Feed.Lines.Count(line => line.Kind == ChatFeedLineKind.Player),
                "the line is not shown a second time");
        }

        [Test]
        public void Overlay_StopWithALineWaiting_StartsNoTurn()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a house");
            harness.Controller.Submit("and paint it blue");
            LlmTurnInbox inbox = harness.Sender.Requests[0].Inbox;

            Assert.IsTrue(harness.Controller.StopActiveTurn());
            harness.Controller.Tick();

            Assert.AreEqual(1, harness.Sender.Requests.Count, "the dialog does not go on by itself");
            Assert.IsFalse(harness.Controller.IsTurnActive);
            Assert.IsTrue(inbox.IsClosed);
            Assert.AreEqual(1, inbox.PendingCount, "the line stays for the orchestrator to keep in the history");
        }

        [Test]
        public void Panel_WhileBusy_KeepsSendEnabled_AndShowsStop()
        {
            using PanelHost host = new();
            Button stop = host.Stop;
            Assert.IsNotNull(stop, "a custom tree without a stop button gets one beside the send button");
            Assert.AreEqual(host.Send.parent, stop.parent);
            Assert.AreEqual(DisplayStyle.None, stop.style.display.value, "idle: nothing to stop");

            host.SetField("_isSending", true);
            host.Invoke("UpdateSendButtonVisualState");

            Assert.IsTrue(host.Send.enabledSelf, "Send stays a send control while the turn runs");
            Assert.AreEqual(">", host.Send.text);
            Assert.IsFalse(host.Send.ClassListContains("coreai-chat-send-button-stop"));
            Assert.AreEqual(DisplayStyle.Flex, stop.style.display.value, "Stop is available");
            Assert.IsTrue(stop.enabledSelf);
            Assert.AreEqual("X", stop.text);

            host.SetField("_isStopping", true);
            host.Invoke("UpdateSendButtonVisualState");
            Assert.IsFalse(stop.enabledSelf, "a stop already under way takes no second click");
            Assert.IsFalse(host.Send.enabledSelf);

            host.SetField("_isStopping", false);
            host.SetField("_isSending", false);
            host.Invoke("UpdateSendButtonVisualState");
            Assert.AreEqual(DisplayStyle.None, stop.style.display.value);
        }

        [Test]
        public async Task Panel_ASendWhileBusy_GoesToTheRunningTurn_AndALineItNeverSawGetsItsOwnTurn()
        {
            using PanelHost host = new();
            GatedOrchestrator orchestrator = new();
            host.Panel.ChatService = new CoreAiChatService(orchestrator,
                settings: new CoreAISettingsOptions { EnableStreaming = true });
            Task<string> turn = host.Panel.SubmitMessageFromExternalAsync("build a house",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;
            Assert.IsTrue(host.Panel.IsRequestInProgress, "precondition: the turn runs");

            host.Input.value = "and paint it blue";
            host.Invoke("TrySendInput", true);

            Assert.AreEqual(1, orchestrator.Requests.Count, "the running turn is not interrupted");
            Assert.AreEqual("", host.Input.value, "the line left the input");
            Assert.AreEqual(1, orchestrator.Requests[0].Inbox.PendingCount, "the line waits in the turn's inbox");
            Assert.IsTrue(host.Panel.IsRequestInProgress);

            orchestrator.Release();
            await turn;
            for (int i = 0; i < 2000 && (orchestrator.Requests.Count < 2 || host.Panel.IsBusy); i++)
            {
                await Task.Yield();
            }

            Assert.AreEqual(2, orchestrator.Requests.Count,
                "the fake never delivered the line, so it runs as a turn of its own after the answer");
            Assert.AreEqual("and paint it blue", orchestrator.Requests[1].Hint);
            Assert.IsFalse(host.Panel.IsBusy);
        }

        /// <summary>A panel bound to a hand-made tree on a live UI panel: input, send button, message scroll.</summary>
        private sealed class PanelHost : IDisposable
        {
            private readonly GameObject _go;
            private readonly GameObject _documentHost;
            private readonly PanelSettings _settings;

            public PanelHost()
            {
                _settings = ScriptableObject.CreateInstance<PanelSettings>();
                _documentHost = new GameObject("ChatMidTurnLines_PanelHost");
                _documentHost.SetActive(false);
                UIDocument document = _documentHost.AddComponent<UIDocument>();
                document.panelSettings = _settings;
                _documentHost.SetActive(true);

                VisualElement row = new();
                document.rootVisualElement.Add(row);
                ScrollView scroll = new();
                document.rootVisualElement.Add(scroll);
                Input = new TextField();
                Send = new Button { name = "coreai-chat-send" };
                row.Add(Input);
                row.Add(Send);

                _go = new GameObject("ChatMidTurnLines_Panel");
                Panel = _go.AddComponent<CoreAiChatPanel>();
                Panel.SetActorIdentityProvider(new LocalActorIdentityProvider("mid-turn-panel-test"));
                SetField("_lifecycleActive", true);
                SetField("MessageScroll", scroll);
                SetField("InputField", Input);
                SetField("SendButton", Send);
                SetField("StopButton", Invoke("ResolveOrCreateStopButton"));
            }

            public CoreAiChatPanel Panel { get; }
            public TextField Input { get; }
            public Button Send { get; }
            public Button Stop => (Button)typeof(CoreAiChatPanel)
                .GetField("StopButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Panel);

            public void SetField(string name, object value)
            {
                typeof(CoreAiChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(Panel, value);
            }

            public object Invoke(string method, params object[] args)
            {
                return typeof(CoreAiChatPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Panel, args);
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_go);
                UnityEngine.Object.DestroyImmediate(_documentHost);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        /// <summary>Holds the first streamed turn open until released; later turns answer at once.</summary>
        private sealed class GatedOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public List<AiTaskRequest> Requests { get; } = new();

            public Task Started => _started.Task;

            public void Release()
            {
                _release.TrySetResult(true);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                return Task.FromResult("ok");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                Requests.Add(request);
                yield return new LlmStreamChunk { Text = "Building" };
                if (Requests.Count == 1)
                {
                    _started.TrySetResult(true);
                    await _release.Task;
                }

                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
