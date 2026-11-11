using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.ChatOverlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.Npc
{
    /// <summary>
    /// The Unity layer of the NPC block: the installer (earlier registrations win, a scope without a model still
    /// speaks fallback lines), the chat-feed and bubble sinks behind it, the UI Toolkit bubble view (shown over an
    /// anchor in front of the camera, hidden otherwise), the runtime document, and the entry point hearing the chat.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiNpcUnityEditModeTests
    {
        private SynchronizationContext _previousContext;
        private readonly List<Object> _created = new();

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void Installer_PostsAnAddressedNpcLine_ToTheChatFeedAndTheBubbleBoard()
        {
            RecordingMessages messages = new();
            ReplyingOrchestrator orchestrator = new("Fresh bread today.");
            ContainerBuilder builder = new();
            builder.RegisterInstance<IChatOverlayMessages>(messages);
            builder.RegisterInstance<IAiOrchestrationService>(orchestrator);
            builder.RegisterCoreAiNpcServices(new CoreAiNpcSettings());

            using (IObjectResolver container = builder.Build())
            {
                INpcDirector director = container.Resolve<INpcDirector>();
                director.Add(Bob());
                director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, any bread?"));

                CollectionAssert.AreEqual(new[] { "Bob|Fresh bread today." }, messages.Lines);
                NpcSpeechBubbleBoard board = container.Resolve<NpcSpeechBubbleBoard>();
                Assert.IsTrue(board.TryGet("bob", out NpcSpeechBubble bubble));
                Assert.AreEqual("Fresh bread today.", bubble.Text);
                Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, orchestrator.Requests[0].RoleId);
            }
        }

        [Test]
        public void Installer_EarlierRegistrationsWin()
        {
            RecordingSink sink = new();
            AlwaysNobody arbiter = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<INpcSpeechSink>(sink);
            builder.RegisterInstance<INpcAddressArbiter>(arbiter);
            builder.RegisterInstance<IAiOrchestrationService>(new ReplyingOrchestrator("Hi."));
            builder.RegisterCoreAiNpcServices(new CoreAiNpcSettings());

            using (IObjectResolver container = builder.Build())
            {
                INpcDirector director = container.Resolve<INpcDirector>();
                director.Add(Bob());
                NpcHearResult heard = director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?"));
                director.Ask("bob", NpcHeardLine.Chat("p1", "Steve", "direct"));

                Assert.AreSame(sink, container.Resolve<INpcSpeechSink>());
                Assert.AreEqual(1, arbiter.Calls, "The replaced arbiter decided the heard line.");
                Assert.IsFalse(heard.IsTaken);
                CollectionAssert.AreEqual(new[] { "bob:Hi." }, sink.Completed, "The replaced sink got the line.");
            }
        }

        [Test]
        public void Installer_WithoutAModelPipeline_NpcsSpeakTheirFallbackLines()
        {
            RecordingSink sink = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<INpcSpeechSink>(sink);
            builder.RegisterCoreAiNpcServices(new CoreAiNpcSettings());

            using (IObjectResolver container = builder.Build())
            {
                INpcDirector director = container.Resolve<INpcDirector>();
                NpcAgentDefinition bob = Bob();
                bob.Persona.FallbackLines = new[] { "Not now, the wheat needs me." };
                director.Add(bob);
                director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?"));

                CollectionAssert.AreEqual(new[] { "bob:Not now, the wheat needs me." }, sink.Completed);
            }
        }

        [Test]
        public void Installer_Disabled_RegistersNothing()
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiNpcs(new CoreAiNpcSettings { Enabled = false });

            using (IObjectResolver container = builder.Build())
            {
                Assert.IsNull(container.ResolveOrDefault<INpcDirector>());
                Assert.IsNull(container.ResolveOrDefault<CoreAiNpcEntryPoint>());
            }
        }

        [Test]
        public void BubbleView_ShowsABubbleOverAnAnchorInFrontOfTheCamera_AndHidesItOtherwise()
        {
            ManualClock clock = new();
            NpcSpeechBubbleBoard board = new(null, clock);
            NpcSpeechAnchorRegistry anchors = new();
            NpcPosition anchor = new(0d, 2d, 10d);
            anchors.Register("bob", () => anchor);
            NpcSpeechBubbleView view = new(board, anchors, 40f);
            Camera camera = CreateCamera();
            NpcSpeech speech = new("bob", "Bob", "p1", "Steve", 1, "");

            board.SpeechStarted(speech);
            view.Render(camera);
            Assert.IsTrue(view.TryGetElement("bob", out VisualElement element));
            Assert.AreEqual(1, view.VisibleCount);
            Assert.AreEqual(DisplayStyle.Flex, element.style.display.value);
            Assert.IsTrue(element.ClassListContains(NpcSpeechBubbleView.ThinkingClass));
            Assert.AreEqual("Bob", element.Q<Label>(className: NpcSpeechBubbleView.NameClass).text);

            board.SpeechGrew(speech, "Fresh bread");
            view.Render(camera);
            Assert.IsFalse(element.ClassListContains(NpcSpeechBubbleView.ThinkingClass));
            Assert.AreEqual("Fresh bread", element.Q<Label>(className: NpcSpeechBubbleView.TextClass).text);

            anchor = new NpcPosition(0d, 2d, -10d);
            view.Render(camera);
            Assert.AreEqual(DisplayStyle.None, element.style.display.value, "Behind the camera: hidden.");

            anchor = new NpcPosition(0d, 2d, 100d);
            view.Render(camera);
            Assert.AreEqual(0, view.VisibleCount, "Beyond the distance limit: hidden.");

            anchor = new NpcPosition(0d, 2d, 10d);
            view.Render(null);
            Assert.AreEqual(0, view.VisibleCount, "No camera: hidden.");

            anchors.Unregister("bob");
            view.Render(camera);
            Assert.AreEqual(0, view.VisibleCount, "No anchor: hidden.");
        }

        [Test]
        public void BubbleView_MarksAFallbackLine_AndHidesAFinishedLineAfterItsHoldTime()
        {
            ManualClock clock = new() { Now = 5d };
            NpcSpeechBubbleBoard board = new(new NpcSpeechBubbleOptions { MinHoldSeconds = 1d, HoldSecondsPerCharacter = 0d }, clock);
            NpcSpeechAnchorRegistry anchors = new();
            anchors.Register("bob", () => new NpcPosition(0d, 0d, 5d));
            NpcSpeechBubbleView view = new(board, anchors);
            Camera camera = CreateCamera();
            NpcSpeech speech = new("bob", "Bob", "p1", "Steve", 1, "");

            board.SpeechStarted(speech);
            board.SpeechCompleted(speech, "Ask me later.", true);
            view.Render(camera);
            view.TryGetElement("bob", out VisualElement element);
            Assert.IsTrue(element.ClassListContains(NpcSpeechBubbleView.FallbackClass));
            Assert.AreEqual(1, view.VisibleCount);

            clock.Now = 6.5d;
            board.Tick();
            view.Render(camera);
            Assert.AreEqual(0, view.VisibleCount);
        }

        [Test]
        public void BubbleView_AttachesToAndDetachesFromItsHost()
        {
            NpcSpeechBubbleView view = new(new NpcSpeechBubbleBoard(), new NpcSpeechAnchorRegistry());
            VisualElement host = new();

            view.Attach(host);
            Assert.AreSame(host, view.Root.parent);
            view.Detach();
            Assert.IsNull(view.Root.parent);
        }

        [Test]
        public void Surface_GivesThePanelItMakes_TheDefaultRuntimeTheme()
        {
            NpcSpeechBubbleSurface surface = NpcSpeechBubbleSurface.Create(new CoreAiNpcSettings());
            _created.Add(GameObject.Find(NpcSpeechBubbleSurface.GameObjectName));
            ThemeStyleSheet theme = Resources.Load<ThemeStyleSheet>(CoreAiChatOverlaySettings.DefaultThemeResourcePath);

            Assert.IsNotNull(theme, "the runtime theme ships in Resources");
            Assert.AreSame(theme, surface.Panel.themeStyleSheet,
                "a panel with a theme logs no 'No Theme Style Sheet set' warning");
            surface.Dispose();
        }

        [Test]
        public void Surface_KeepsAGamesOwnPanelSettingsAsTheyAre_NegativeTwin()
        {
            // WHY a theme of the game's own: the Editor fills a theme-less panel with the project's first runtime
            // theme when a document uses it, so "still null" would test the Editor, not the surface.
            ThemeStyleSheet gameTheme = ScriptableObject.CreateInstance<ThemeStyleSheet>();
            PanelSettings own = ScriptableObject.CreateInstance<PanelSettings>();
            own.themeStyleSheet = gameTheme;
            _created.Add(own);
            _created.Add(gameTheme);
            NpcSpeechBubbleSurface surface = NpcSpeechBubbleSurface.Create(new CoreAiNpcSettings { PanelSettings = own });
            _created.Add(GameObject.Find(NpcSpeechBubbleSurface.GameObjectName));

            Assert.AreSame(own, surface.Panel, "the game's panel is used");
            Assert.AreSame(gameTheme, own.themeStyleSheet, "and the surface does not restyle it");
            surface.Dispose();
        }

        [Test]
        public void Surface_CreatesARuntimeDocument_AndDisposeDestroysIt()
        {
            NpcSpeechBubbleSurface surface = NpcSpeechBubbleSurface.Create(new CoreAiNpcSettings());
            GameObject created = GameObject.Find(NpcSpeechBubbleSurface.GameObjectName);
            _created.Add(created);

            Assert.IsNotNull(created);
            Assert.IsNotNull(created.GetComponent<UIDocument>());
            Assert.IsNotNull(created.GetComponent<UIDocument>().panelSettings);
            surface.Dispose();
            Assert.IsTrue(created == null, "Dispose destroys the document it created.");
        }

        [Test]
        public void EntryPoint_LetsNpcsHearThePlayersChatLine_AndTicksTheDirector()
        {
            RecordingSink sink = new();
            ReplyingOrchestrator orchestrator = new("Hello, Steve.");
            NpcDirector director = new(orchestrator, sink, new NpcDirectorOptions(), new ManualClock());
            director.Add(Bob());
            NpcSpeechBubbleBoard board = new();
            ChatOverlayController overlay = new(new ChatOverlayOptions(), new SilentView(), new IdleSender(),
                new ChatCommandRegistry(), new ChatAgentSelector(BuiltInAgentRoleIds.SmartChat), new ChatClock());
            CoreAiNpcSettings settings = new() { ShowBubbles = false, PlayerName = "Steve" };
            using CoreAiNpcEntryPoint entry = new(director, board, new NpcSpeechBubbleView(board, null), settings,
                overlay);

            entry.Start();
            overlay.Submit("Bob, good morning!");
            entry.Tick();

            Assert.IsNotNull(entry.Hearing);
            Assert.AreEqual("Steve: Bob, good morning!", orchestrator.Requests[0].Hint);
            CollectionAssert.AreEqual(new[] { "bob:Hello, Steve." }, sink.Completed);
            overlay.Dispose();
            director.Dispose();
        }

        private Camera CreateCamera()
        {
            GameObject gameObject = new("NpcBubbleTestCamera");
            _created.Add(gameObject);
            Camera camera = gameObject.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1000f;
            return camera;
        }

        private static NpcAgentDefinition Bob()
        {
            return new NpcAgentDefinition
            {
                Persona = new NpcPersona { Id = "bob", DisplayName = "Bob" },
                Budget = new NpcBudget { CooldownSeconds = 0d }
            };
        }

        private sealed class ManualClock : INpcClock
        {
            public double Now;

            public double NowSeconds => Now;
        }

        private sealed class RecordingMessages : IChatOverlayMessages
        {
            public readonly List<string> Lines = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add("|" + text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Lines.Add(speaker + "|" + text);
            }
        }

        private sealed class RecordingSink : INpcSpeechSink
        {
            public readonly List<string> Completed = new();

            public void SpeechStarted(NpcSpeech speech)
            {
            }

            public void SpeechGrew(NpcSpeech speech, string textSoFar)
            {
            }

            public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
            {
                Completed.Add(speech.NpcId + ":" + text);
            }

            public void SpeechCancelled(NpcSpeech speech)
            {
            }
        }

        private sealed class AlwaysNobody : INpcAddressArbiter
        {
            public int Calls;

            public NpcAddressDecision Decide(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> candidates,
                double nowSeconds, INpcLineOfSight lineOfSight)
            {
                Calls++;
                return NpcAddressDecision.Ignored(NpcIgnoreReason.NotAddressed);
            }
        }

        private sealed class ReplyingOrchestrator : IAiOrchestrationService
        {
            private readonly string _reply;

            public ReplyingOrchestrator(string reply)
            {
                _reply = reply;
            }

            public readonly List<AiTaskRequest> Requests = new();

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult(_reply);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                await Task.CompletedTask;
                yield return new LlmStreamChunk { Text = _reply };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class ChatClock : IChatOverlayClock
        {
            public double NowSeconds => 0d;
        }

        private sealed class SilentView : IChatOverlayView
        {
            public event Action<string> Submitted
            {
                add { }
                remove { }
            }

            public event Action CloseRequested
            {
                add { }
                remove { }
            }

            public void SetInputOpen(bool open, string prefill)
            {
            }

            public void AddLine(ChatFeedLine line)
            {
            }

            public void UpdateLine(ChatFeedLine line)
            {
            }

            public void RemoveLine(ChatFeedLine line)
            {
            }

            public void SetLineVisible(ChatFeedLine line, bool visible)
            {
            }

            public void SetStatus(AgentStatusKind kind, string text)
            {
            }
        }

        private sealed class IdleSender : IChatOverlayTurnSender, IChatOverlayTurn
        {
            public bool IsAvailable => true;

            public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
            {
                return this;
            }

            public void StopAgent(string roleId)
            {
            }

            public void Cancel()
            {
            }
        }
    }
}
