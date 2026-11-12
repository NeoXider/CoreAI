using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.ChatOverlay;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.TextChatLua
{
    /// <summary>
    /// The bundled chat mod (<c>coreai_chat.lua</c>) through the real mod runtime, the GUI presenter over a fake host,
    /// the world's <c>TextChatService</c> over the overlay's command registry, hooks, feed and surface switch, and
    /// <c>AIService:GetAssistant()</c> over <see cref="ChatAgentTurns"/> with a scripted sender: the owner's parity
    /// list (opening key held for the host to swallow, Enter on an empty line, streaming, the status line,
    /// <c>/stop</c>, commands, villagers' and system lines, the pointer left free while closed).
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatModLuaEditModeTests
    {
        private const string ModId = "coreai_chat";
        private const int KeyT = 116;
        private const int KeyReturn = 13;
        private const int KeySlash = 47;
        private const int KeyEscape = 27;

        private SynchronizationContext _savedContext;
        private ChatModWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private ChatModWorld Load(bool allowAssistant = true, bool attachAi = true, bool withOverlay = false)
        {
            _world = new ChatModWorld(allowAssistant, attachAi, withOverlay);
            _world.Stack.Runtime.LoadMod(ModId, ChatSource());
            _world.Frames(2);
            return _world;
        }

        private static string ChatSource()
        {
            string path = Path.Combine(UnityEngine.Application.dataPath, "CoreAIMods", "Runtime", "Resources",
                "CoreAIMods", "coreai_chat.lua");
            return File.ReadAllText(path);
        }

        [Test]
        public void Loading_TakesTheChatFromTheOverlay_AndUnloadingGivesItBack()
        {
            Load();
            Assert.IsFalse(_world.SurfaceSwitch.IsDefaultSurfaceShown, "the built-in chat stepped aside");
            Assert.IsNotNull(_world.Gui("CoreAIChat"));

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod(ModId));
            _world.Frames(2);

            Assert.IsTrue(_world.SurfaceSwitch.IsDefaultSurfaceShown, "turning the mod off brings the built-in chat back");
        }

        [Test]
        public void Negative_WithoutTheAssistantSetting_TheModStaysIdle_AndTheOverlayKeepsTheChat()
        {
            Load(allowAssistant: false);

            Assert.IsTrue(_world.SurfaceSwitch.IsDefaultSurfaceShown);
            Assert.IsNull(_world.Gui("CoreAIChat"));
        }

        [Test]
        public void Negative_InAWorldWithoutTheAiSurface_TheModStaysIdle_WithoutAnError()
        {
            Load(attachAi: false);

            Assert.IsTrue(_world.Stack.Runtime.IsLoaded(ModId));
            Assert.IsTrue(_world.SurfaceSwitch.IsDefaultSurfaceShown);
            Assert.IsNull(_world.Gui("CoreAIChat"));
        }

        [Test]
        public void T_OpensAnEmptyInput_Focused_WithTheOpeningKeyHeldForTheHostToSwallow()
        {
            Load();

            _world.Press(KeyT);

            RbxTextBox box = _world.Input();
            Assert.IsTrue(box.Visible);
            Assert.IsTrue(box.IsFocused);
            Assert.AreEqual("", box.TextState.Text);
            CollectionAssert.Contains(_world.Field(box).LastFocusHeldKeys, KeyT,
                "the host swallows the keystroke that opened the chat, whatever the layout made of it");
        }

        [Test]
        public void Slash_OpensTheInputWithTheSlashTyped()
        {
            Load();

            _world.Press(KeySlash);

            Assert.IsTrue(_world.Input().IsFocused);
            Assert.AreEqual("/", _world.Input().TextState.Text);
        }

        [Test]
        public void EnterWithText_ShowsThePlayersLine_SendsItToTheAgent_AndCloses()
        {
            Load();

            _world.Say("build a tower");

            Assert.AreEqual(1, _world.Sender.Requests.Count);
            Assert.AreEqual("build a tower", _world.Sender.Requests[0].Text);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, _world.Sender.Requests[0].RoleId);
            CollectionAssert.Contains(_world.FeedTexts(), "You: build a tower");
            Assert.IsFalse(_world.Input().Visible);
            CollectionAssert.AreEqual(new[] { "build a tower" }, _world.Router.Offered, "villagers heard it first");
        }

        [Test]
        public void Negative_EnterOnAnEmptyLine_OnlyCloses()
        {
            Load();

            _world.Say("   ");

            Assert.AreEqual(0, _world.Sender.Requests.Count);
            Assert.AreEqual(0, _world.FeedTexts().Count);
            Assert.IsFalse(_world.Input().Visible);
        }

        [Test]
        public void Negative_EscapeCloses_WithoutSending()
        {
            Load();
            _world.Press(KeyT);
            _world.Type("never mind");

            _world.Press(KeyEscape);

            Assert.IsFalse(_world.Input().Visible);
            Assert.AreEqual(0, _world.Sender.Requests.Count);
        }

        [Test]
        public void TheEnterThatSends_DoesNotOpenTheChatAgain()
        {
            Load();
            _world.Press(KeyT);
            _world.Type("hello");

            _world.Field(_world.Input()).SubmitByPlayer();
            _world.Keys.PressKey(KeyReturn);
            _world.Frames(2);

            Assert.IsFalse(_world.Input().Visible, "the closing Enter is processed input");
            Assert.AreEqual(1, _world.Sender.Requests.Count);
        }

        [Test]
        public void TheAnswer_StreamsIntoOneLine()
        {
            Load();
            _world.Say("hi");

            _world.Sender.Stream("Hel");
            _world.Frames(1);
            _world.Sender.Stream("lo there");
            _world.Frames(1);

            List<string> lines = _world.FeedTexts();
            Assert.AreEqual("AI: Hello there", lines[lines.Count - 1]);
            Assert.AreEqual(1, lines.Count(line => line.StartsWith("AI:", StringComparison.Ordinal)));
        }

        [Test]
        public void TheStatusLine_FollowsTheAgent_AndClearsWhenTheTurnEnds()
        {
            Load();
            _world.Say("build a tower");
            _world.Frames(1);
            StringAssert.StartsWith("thinking", _world.StatusText());

            _world.Tools.Start(_world.Turns.Current.TraceId, "execute_lua");
            _world.Frames(2);
            StringAssert.StartsWith("running execute_lua", _world.StatusText());

            _world.Tools.Complete(_world.Turns.Current.TraceId, "execute_lua");
            _world.Frames(1);
            _world.Sender.Observer.OnChunk(new LlmStreamChunk
            {
                GenerationInProgress = true, ComposingToolName = "execute_lua", ComposingToolCharacters = 600
            });
            _world.Frames(2);
            StringAssert.StartsWith("writing a script", _world.StatusText());

            _world.Sender.Stream("Built.");
            _world.Sender.Complete();
            _world.Frames(2);
            Assert.AreEqual("", _world.StatusText());
            Assert.IsFalse(((RbxGuiObject)_world.Gui("Status")).Visible);
        }

        [Test]
        public void TheStatusLine_AddsTheElapsedTime_AndTheTokens_FromTheFirstSecond()
        {
            Load();
            _world.Say("build a tower");
            _world.Sender.Observer.OnChunk(new LlmStreamChunk { ReasoningText = new string('r', 400) });
            _world.Frames(1);
            Assert.AreEqual("thinking", _world.StatusText(), "nothing is added before the first second");

            _world.Clock.ProcessTimeSeconds += 3.2d;
            _world.Frames(1);

            Assert.AreEqual("thinking \u00b7 3s \u00b7 ~100 tokens", _world.StatusText());
        }

        [Test]
        public void Lines_FadeAfterTenSeconds_AndComeBackWhileTheInputIsOpen()
        {
            Load();
            _world.HostFeed.Add(ChatFeedLineKind.System, "", "Autopilot off.", 0d);
            _world.Frames(2);
            RbxTextLabel line = _world.FeedLabels().Last();
            Assert.AreEqual(0f, line.TextState.TextTransparency, 0.001f);

            _world.Clock.ProcessTimeSeconds += 11d;
            _world.Frames(1);
            Assert.AreEqual(1f, line.TextState.TextTransparency, 0.001f, "faded out while closed");

            _world.Press(KeyT);
            Assert.AreEqual(0f, line.TextState.TextTransparency, 0.001f, "every line shows while the input is open");
        }

        [Test]
        public void Stop_CancelsTheRunningTurn_AndSaysSo()
        {
            Load();
            _world.Say("build a castle");

            _world.Say("/stop");

            Assert.AreEqual(1, _world.Sender.LastTurn.CancelCalls);
            CollectionAssert.AreEqual(new[] { BuiltInAgentRoleIds.Programmer }, _world.Sender.StoppedRoles);
            CollectionAssert.Contains(_world.FeedTexts(), "Stopped.");
            Assert.IsFalse(_world.Turns.IsBusy);
        }

        [Test]
        public void Negative_ASecondLineWhileTheAgentAnswers_IsRefusedLocally()
        {
            Load();
            _world.Say("one");

            _world.Say("two");

            Assert.AreEqual(1, _world.Sender.Requests.Count);
            CollectionAssert.Contains(_world.FeedTexts(), "The AI is still answering. Type /stop to cancel it.");
        }

        [Test]
        public void Help_ListsTheChatsOwnCommands_AndTheGamesCommands()
        {
            Load();
            _world.Commands.Register(new ChatCommand("undo", "Take back the AI's last change.",
                _ => ChatCommandResult.Handled("Undone.")));

            _world.Say("/help");

            string help = _world.FeedTexts().Last();
            StringAssert.StartsWith("Commands:", help);
            StringAssert.Contains("/undo - Take back the AI's last change.", help);
            StringAssert.Contains("/agent [role] - Show the agent that answers", help);
            StringAssert.Contains("/stop - Stop the agent's current answer.", help);
            Assert.AreEqual(0, _world.Sender.Requests.Count);
        }

        [Test]
        public void AGameCommand_RunsThroughTheChat_AndItsReplyIsShown()
        {
            Load();
            int runs = 0;
            _world.Commands.Register(new ChatCommand("undo", "Take back the AI's last change.", _ =>
            {
                runs++;
                return ChatCommandResult.Handled("Undone.");
            }));

            _world.Say("/undo");

            Assert.AreEqual(1, runs);
            CollectionAssert.Contains(_world.FeedTexts(), "Undone.");
            Assert.AreEqual(0, _world.Sender.Requests.Count, "a command never reaches the model");
        }

        [Test]
        public void Negative_AnUnknownCommand_AnswersLocally_AndSendsNothing()
        {
            Load();

            _world.Say("/teleport home");

            CollectionAssert.Contains(_world.FeedTexts(), "Unknown command /teleport. Type /help for the list.");
            Assert.AreEqual(0, _world.Sender.Requests.Count);
            Assert.AreEqual(0, _world.Router.Offered.Count);
        }

        [Test]
        public void ADoubledSlash_IsSentAsText()
        {
            Load();

            _world.Say("//help me");

            Assert.AreEqual("/help me", _world.Sender.Requests[0].Text);
        }

        [Test]
        public void Agent_SwitchesTheRoleThatAnswers()
        {
            Load();

            _world.Say("/agent smartchat");
            _world.Say("hello");

            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, _world.Agents.ActiveRoleId);
            CollectionAssert.Contains(_world.FeedTexts(), "Agent: " + BuiltInAgentRoleIds.SmartChat + ".");
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, _world.Sender.Requests[0].RoleId);
        }

        [Test]
        public void ALineAVillagerTakes_NeverReachesTheAgent()
        {
            Load();
            _world.Router.Take = text => text.StartsWith("Bob", StringComparison.Ordinal);

            _world.Say("Bob, where is the well?");
            _world.HostFeed.Add(ChatFeedLineKind.System, "Bob", "Behind the church.", 0d);
            _world.Frames(2);

            Assert.AreEqual(0, _world.Sender.Requests.Count);
            List<string> lines = _world.FeedTexts();
            int asked = lines.IndexOf("You: Bob, where is the well?");
            int answered = lines.IndexOf("Bob: Behind the church.");
            Assert.GreaterOrEqual(asked, 0);
            Assert.Greater(answered, asked, "the villager's line lands in the same feed, after the question");
        }

        [Test]
        public void SystemLines_FromOtherCode_LandInTheFeed()
        {
            Load();

            _world.HostFeed.Add(ChatFeedLineKind.System, "", "Autopilot off.", 0d);
            _world.HostFeed.Add(ChatFeedLineKind.Error, "", "Nothing to undo.", 0d);
            _world.Frames(2);

            List<string> lines = _world.FeedTexts();
            CollectionAssert.Contains(lines, "Autopilot off.");
            CollectionAssert.Contains(lines, "Nothing to undo.");
        }

        [Test]
        public void ALongLine_GrowsToItsWrappedText_AndTheNextLineStartsBelowIt()
        {
            Load();
            _world.HostFeed.Add(ChatFeedLineKind.System, "", new string('x', 40) + " " + string.Join(" ",
                Enumerable.Repeat("word", 40)), 0d);
            _world.HostFeed.Add(ChatFeedLineKind.System, "", "short", 0d);
            _world.Frames(2);

            List<RbxTextLabel> labels = _world.FeedLabels();
            RbxTextLabel tall = labels[labels.Count - 2];
            RbxTextLabel shortLine = labels[labels.Count - 1];
            Assert.Greater(tall.AbsoluteSize.Y, shortLine.AbsoluteSize.Y * 2f, "the wrapped line grows to its text");
            Assert.GreaterOrEqual(shortLine.AbsolutePosition.Y, tall.AbsolutePosition.Y + tall.AbsoluteSize.Y,
                "lines never overlap");
            Assert.LessOrEqual(tall.TextState.TextBounds.X, tall.AbsoluteSize.X);
        }

        [Test]
        public void AToolOnlyTurn_Says_Done()
        {
            Load();
            _world.Say("place a block");

            _world.Sender.Stream("Tool call completed: execute_lua.");
            _world.Sender.Complete();
            _world.Frames(2);

            CollectionAssert.Contains(_world.FeedTexts(), "AI: Done.");
        }

        [Test]
        public void AFailedTurn_ShowsTheReadableSentence()
        {
            Load();
            _world.Say("hi");

            _world.Sender.Fail(ChatOverlayTurnFailureKind.Timeout, "The assistant took too long to answer.");
            _world.Frames(2);

            CollectionAssert.Contains(_world.FeedTexts(), "The assistant took too long to answer.");
        }

        [Test]
        public void WhileClosed_TheFeedTakesNoPointer_SoTheCameraCanCaptureItAgain()
        {
            Load();
            RbxGuiObject feed = (RbxGuiObject)_world.Gui("Feed");
            Assert.AreEqual("Ignore", _world.Field(feed).Attributes["picking-mode"]);

            _world.Press(KeyT);
            Assert.AreEqual("Position", _world.Field(feed).Attributes["picking-mode"], "twin: open, it scrolls");

            _world.Press(KeyEscape);
            Assert.AreEqual("Ignore", _world.Field(feed).Attributes["picking-mode"]);
        }

        [Test]
        public void AHostTurn_ShowsItsStatusAndAnswerInTheLuaChat_AndHoldsTheSlot()
        {
            Load(withOverlay: true);

            Assert.IsTrue(_world.Overlay.TryStartHostTurn(new ChatOverlayHostTurnRequest
            {
                Text = "notices from the team", Announcement = "builder-1 finished.", ShowAnswer = true
            }));
            _world.Frames(2);
            StringAssert.StartsWith("thinking", _world.StatusText(), "a turn the host runs shows in the Lua chat");
            CollectionAssert.Contains(_world.FeedTexts(), "builder-1 finished.");

            _world.Say("hello");
            Assert.AreEqual(1, _world.Sender.Requests.Count, "the player's line waits for the host turn");
            CollectionAssert.Contains(_world.FeedTexts(), "The AI is still answering. Type /stop to cancel it.");

            _world.Sender.Stream("The walls are up.");
            _world.Sender.Complete();
            _world.Frames(2);

            CollectionAssert.Contains(_world.FeedTexts(), "AI: The walls are up.", "a wake's answer lands in the feed");
            Assert.AreEqual("", _world.StatusText());
            Assert.IsFalse(_world.Turns.IsBusy);
        }

        [Test]
        public void ThePlayersTurn_KeepsAHostTurnWaiting()
        {
            Load(withOverlay: true);
            _world.Say("build a tower");

            Assert.IsTrue(_world.Overlay.IsTurnActive);
            Assert.IsFalse(_world.Overlay.TryStartHostTurn(new ChatOverlayHostTurnRequest { Text = "repair" }));
            Assert.AreEqual(_world.Turns.Current.TraceId, _world.Overlay.ActiveTraceId);
        }

        [Test]
        public void Stop_StopsAHostTurn()
        {
            Load(withOverlay: true);
            _world.Overlay.TryStartHostTurn(new ChatOverlayHostTurnRequest { Text = "repair the mod" });
            _world.Frames(1);

            _world.Say("/stop");

            Assert.IsFalse(_world.Overlay.IsTurnActive);
            Assert.AreEqual(1, _world.Sender.LastTurn.CancelCalls);
        }

        [Test]
        public void BackgroundWork_ShowsOnTheStatusLineWhileNoTurnRuns()
        {
            Load(withOverlay: true);

            _world.Overlay.Status.SetBackgroundDetail("builder-1 working (2 steps)");
            _world.Frames(2);

            Assert.AreEqual("builder-1 working (2 steps)", _world.StatusText());
            Assert.IsTrue(((RbxGuiObject)_world.Gui("Status")).Visible);

            _world.Overlay.Status.SetBackgroundDetail("");
            _world.Frames(2);
            Assert.AreEqual("", _world.StatusText());
        }

        [Test]
        public void TheRunningTurnOfAnUnloadedChat_IsStopped()
        {
            Load();
            _world.Say("build");

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod(ModId));
            _world.Frames(2);

            Assert.IsFalse(_world.Turns.IsBusy, "a reloaded chat can send at once");
            Assert.AreEqual(1, _world.Sender.LastTurn.CancelCalls);
        }

        private sealed class ChatModWorld : IDisposable
        {
            public ChatModWorld(bool allowAssistant, bool attachAi, bool withOverlay)
            {
                Keys = new InMemoryInputSource { FocusState = new InputFocusState() };
                Clock = new ManualClock();
                Roblox = new LuaCsRbxApiBindings(inputSource: Keys, connections: new ModConnectionRegistry(),
                    clockSource: Clock);
                Roblox.Players.EnsureActor(Roblox.Registry, "local-player");
                Roblox.LocalPlayerActorId = "local-player";
                Commands = new ChatCommandRegistry();
                Roblox.AttachChatCommands(new ChatCommandRegistrySink(Commands));
                SurfaceSwitch = new ChatOverlaySurfaceSwitch();
                Router = new FakeRouter();
                Sender = new FakeSender();
                Tools = new FakeTools();
                Agents = new ChatAgentSelector(BuiltInAgentRoleIds.Programmer,
                    () => new[] { BuiltInAgentRoleIds.Programmer, BuiltInAgentRoleIds.SmartChat });
                if (withOverlay)
                {
                    Overlay = new ChatOverlayController(new ChatOverlayOptions(), new NoView(), Sender, Commands, Agents,
                        new OverlayClock(Clock), Tools);
                    HostFeed = Overlay.Feed;
                    Host = new ChatOverlayWorldChatHost(Commands, Overlay, HostFeed, SurfaceSwitch, Overlay);
                    Turns = new ChatAgentTurns(Sender, Agents, null, Tools, null, null,
                        () => Clock.ProcessTimeSeconds, Overlay);
                }
                else
                {
                    HostFeed = new ChatFeed();
                    Host = new ChatOverlayWorldChatHost(Commands, Router, HostFeed, SurfaceSwitch);
                    Turns = new ChatAgentTurns(Sender, Agents, null, Tools);
                }

                Roblox.AttachChatHost(Host);
                Action<LuaCsApiRegistry, LuaCapabilities> ai = CoreAiLuaAiBindings.Attach(Roblox,
                    new CoreAiLuaAiOptions { AllowPlayerAssistantFromLua = allowAssistant }, null, null, null, null,
                    null, null, null, null, Turns);
                Store = new UxmlServiceTestWorld.MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    ModStore = Store,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = Roblox,
                    AdditionalGameplayBindings = attachAi ? ai : null
                });
                Backend = new FakeRbxUiBackend();
                Surface = new RuntimeUiLuaSurface(Backend);
                UiWorld = Surface.AttachTo(Roblox);
            }

            public InMemoryInputSource Keys { get; }

            public ManualClock Clock { get; }

            public LuaCsRbxApiBindings Roblox { get; }

            public ChatCommandRegistry Commands { get; }

            public ChatOverlaySurfaceSwitch SurfaceSwitch { get; }

            public ChatFeed HostFeed { get; }

            public FakeRouter Router { get; }

            public ChatOverlayWorldChatHost Host { get; }

            public FakeSender Sender { get; }

            public FakeTools Tools { get; }

            public ChatAgentSelector Agents { get; }

            public ChatAgentTurns Turns { get; }

            public ChatOverlayController Overlay { get; }

            public UxmlServiceTestWorld.MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public FakeRbxUiBackend Backend { get; }

            public RuntimeUiLuaSurface Surface { get; }

            public RbxUiWorld UiWorld { get; }

            public void Frames(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    Overlay?.Tick();
                    Roblox.Scheduler.Advance(1d / 60d);
                }
            }

            /// <summary>A key goes down for one frame and comes back up.</summary>
            public void Press(int keyCode)
            {
                Keys.PressKey(keyCode);
                Frames(1);
                Keys.ReleaseKey(keyCode);
                Frames(1);
            }

            public void Type(string text)
            {
                Field(Input()).ChangeValueByPlayer(RbxUiValue.FromText(text));
                Frames(1);
            }

            /// <summary>Opens the chat with T, types <paramref name="text"/> and presses Enter in the field.</summary>
            public void Say(string text)
            {
                Press(KeyT);
                Type(text);
                Field(Input()).SubmitByPlayer();
                Frames(2);
            }

            public RbxInstance Gui(string name)
            {
                foreach (RbxInstance instance in Roblox.Registry.GetLiveInstances())
                {
                    if (instance.Name == name && !instance.IsDestroyed && instance.FindFirstAncestorOfClass("PlayerGui") != null)
                    {
                        return instance;
                    }
                }

                return null;
            }

            public RbxTextBox Input()
            {
                return (RbxTextBox)Gui("Input");
            }

            public FakeRbxUiBackend.FakeElement Field(RbxGuiObject gui)
            {
                return UiWorld.GuiPresenter.GetElementPort(gui) as FakeRbxUiBackend.FakeElement;
            }

            public string StatusText()
            {
                return ((RbxTextLabel)Gui("Status")).TextState.Text;
            }

            public List<RbxTextLabel> FeedLabels()
            {
                RbxInstance feed = Gui("Feed");
                return feed == null
                    ? new List<RbxTextLabel>()
                    : feed.GetChildren().OfType<RbxTextLabel>().OrderBy(label => label.LayoutOrder).ToList();
            }

            public List<string> FeedTexts()
            {
                RbxInstance feed = Gui("Feed");
                return feed == null
                    ? new List<string>()
                    : feed.GetChildren().OfType<RbxTextLabel>().OrderBy(label => label.LayoutOrder)
                        .Select(label => label.TextState.Text).ToList();
            }

            public void Dispose()
            {
                Turns.Dispose();
                Overlay?.Dispose();
                Host.Dispose();
                Surface.Dispose();
                Roblox.Dispose();
            }
        }

        private sealed class OverlayClock : IChatOverlayClock
        {
            private readonly ManualClock _clock;

            public OverlayClock(ManualClock clock)
            {
                _clock = clock;
            }

            public double NowSeconds => _clock.ProcessTimeSeconds;
        }

        private sealed class NoView : IChatOverlayView
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

        private sealed class ManualClock : IRbxClockSource
        {
            public double ProcessTimeSeconds { get; set; } = 100d;

            public double GameTimeSeconds => ProcessTimeSeconds;

            public long UnixTimeSeconds => 1700000000L;

            public double UnixTimeSecondsFractional => UnixTimeSeconds;
        }

        private sealed class FakeRouter : IChatOverlayPlayerLineRouter
        {
            public readonly List<string> Offered = new();

            public Func<string, bool> Take { get; set; } = _ => false;

            public bool OfferPlayerLine(string text)
            {
                Offered.Add(text);
                return Take(text);
            }
        }

        private sealed class FakeTurn : IChatOverlayTurn
        {
            public int CancelCalls { get; private set; }

            public void Cancel()
            {
                CancelCalls++;
            }
        }

        private sealed class FakeSender : IChatOverlayTurnSender
        {
            public readonly List<ChatOverlayTurnRequest> Requests = new();
            public readonly List<string> StoppedRoles = new();

            public bool IsAvailable { get; set; } = true;

            public IChatOverlayTurnObserver Observer { get; private set; }

            public FakeTurn LastTurn { get; private set; }

            public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
            {
                Requests.Add(request);
                Observer = observer;
                LastTurn = new FakeTurn();
                return LastTurn;
            }

            public void StopAgent(string roleId)
            {
                StoppedRoles.Add(roleId);
            }

            public void Stream(string text)
            {
                Observer.OnChunk(new LlmStreamChunk { Text = text });
            }

            public void Complete()
            {
                Observer.OnCompleted();
            }

            public void Fail(ChatOverlayTurnFailureKind kind, string message)
            {
                Observer.OnFailed(new ChatOverlayTurnFailure(kind, message));
            }
        }

        private sealed class FakeTools : IAgentToolEventSource
        {
            public event Action<LlmToolCallStarted> ToolCallStarted;

            public event Action<LlmToolCallCompleted> ToolCallCompleted;

            public event Action<LlmToolCallFailed> ToolCallFailed;

            public void Start(string traceId, string tool)
            {
                ToolCallStarted?.Invoke(new LlmToolCallStarted(traceId, "Programmer", tool, "{}"));
            }

            public void Complete(string traceId, string tool)
            {
                ToolCallCompleted?.Invoke(new LlmToolCallCompleted(traceId, "Programmer", tool, "{}", "{}", 5d));
            }

            public void Fail(string traceId, string tool)
            {
                ToolCallFailed?.Invoke(new LlmToolCallFailed(traceId, "Programmer", tool, "{}", "error", 5d));
            }
        }
    }
}
