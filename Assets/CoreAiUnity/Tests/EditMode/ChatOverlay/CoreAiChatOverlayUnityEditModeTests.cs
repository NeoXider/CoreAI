using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using CoreAI.Chat;
using CoreAI.Composition;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
using UnityEngine.InputSystem;
#endif

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The Unity layer of the in-world chat: the installer lets earlier registrations win, the key polling opens
    /// and closes the input (and lets keys through while closed), the UI Toolkit view's key handling and tree,
    /// the runtime document, and the default sender over <see cref="CoreAiChatService"/>.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatOverlayUnityEditModeTests
    {
        private sealed class FakeKeyboard : IChatOverlayKeyboard
        {
            public readonly HashSet<KeyCode> Pressed = new();

            public bool IsModifierHeld { get; set; }

            public bool WasPressedThisFrame(KeyCode key)
            {
                return Pressed.Contains(key);
            }
        }

        private sealed class FixedGate : IChatOverlayOpenGate
        {
            public bool Allowed { get; set; } = true;

            public bool IsOpenAllowed()
            {
                return Allowed;
            }
        }

        private sealed class RecordingObserver : IChatOverlayTurnObserver
        {
            public readonly List<string> Texts = new();
            public readonly TaskCompletionSource<bool> Ended = new();
            public ChatOverlayTurnFailure? Failure;

            public void OnChunk(LlmStreamChunk chunk)
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    Texts.Add(chunk.Text);
                }
            }

            public void OnCompleted()
            {
                Ended.TrySetResult(true);
            }

            public void OnFailed(ChatOverlayTurnFailure failure)
            {
                Failure = failure;
                Ended.TrySetResult(false);
            }
        }

        private sealed class ScriptedOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            public readonly List<AiTaskRequest> Requests = new();

            public bool WaitForCancellation { get; set; }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult("buffered answer");
            }

            public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "buffered answer" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                if (WaitForCancellation)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                yield return new LlmStreamChunk { Text = "Hel" };
                yield return new LlmStreamChunk { Text = "lo" };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private static CoreAiChatService ServiceOver(ScriptedOrchestrator orchestrator, bool streaming)
        {
            CoreAISettingsOptions settings = new() { EnableStreaming = streaming, LlmRequestTimeoutSeconds = 0f };
            return new CoreAiChatService(orchestrator, null, settings, null, null, null, null,
                CoreServicesInstaller.DefaultLocalHostIdentityProvider, null);
        }

        private static async Task<bool> WaitFor(Task<bool> ended)
        {
            Task finished = await Task.WhenAny(ended, Task.Delay(10000));
            Assert.AreSame(ended, finished, "the turn did not end within 10 s");
            return await ended;
        }

        private static (CoreAiChatOverlayEntryPoint EntryPoint, ChatOverlayController Controller,
            CoreAiChatOverlayView View) CreateEntryPoint(FakeKeyboard keyboard, IChatOverlayOpenGate gate,
                CoreAiChatOverlaySettings settings = null)
        {
            CoreAiChatOverlaySettings resolved = settings ?? new CoreAiChatOverlaySettings();
            resolved.HostElement = new VisualElement();
            ChatOverlayOptions options = resolved.ResolveOptions();
            CoreAiChatOverlayView view = new(resolved, options);
            ChatAgentSelector agents = new(BuiltInAgentRoleIds.SmartChat);
            ChatOverlayController controller = new(options, view, new ScriptedTurnSender(), new ChatCommandRegistry(),
                agents, new ManualChatClock());
            CoreAiChatOverlayEntryPoint entryPoint =
                new(controller, view, keyboard, resolved, agents, null, gate);
            entryPoint.Start();
            return (entryPoint, controller, view);
        }

        [Test]
        public void Installer_EarlierRegistrationsWin_AndTheControllerServesEveryPort()
        {
            FakeChatOverlayView view = new();
            ScriptedTurnSender sender = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<IChatOverlayView>(view);
            builder.RegisterInstance<IChatOverlayTurnSender>(sender);
            builder.RegisterCoreAiChatOverlayServices(new CoreAiChatOverlaySettings { RoleId = "Programmer" });

            using (IObjectResolver container = builder.Build())
            {
                ChatOverlayController controller = container.Resolve<ChatOverlayController>();
                Assert.AreSame(controller, container.Resolve<IChatOverlayInputState>());
                Assert.AreSame(controller, container.Resolve<IChatOverlayMessages>());
                Assert.AreSame(controller, container.Resolve<IChatTurnControl>());
                Assert.AreEqual("Programmer", container.Resolve<IChatAgentSelector>().ActiveRoleId);

                container.Resolve<IChatOverlayMessages>().PostMessage("Guard", "Halt!");
                controller.Submit("hello");

                Assert.AreEqual("Halt!", view.Added[0].Text, "the replaced view renders the feed");
                Assert.AreEqual(1, sender.Requests.Count, "the replaced sender receives the turn");
                Assert.AreEqual("Programmer", sender.Requests[0].RoleId);
            }
        }

        [Test]
        public void Installer_Disabled_RegistersNothing()
        {
            ContainerBuilder builder = new();

            builder.RegisterCoreAiChatOverlay(new CoreAiChatOverlaySettings { Enabled = false });

            Assert.IsFalse(builder.Exists(typeof(ChatOverlayController)));
            Assert.IsFalse(builder.Exists(typeof(IChatCommandRegistry), true));
            Assert.IsFalse(builder.Exists(typeof(CoreAiChatOverlayEntryPoint)));
        }

        [Test]
        public void Installer_WithoutAnLlmPipeline_GivesAnUnavailableSender()
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<IChatOverlayView>(new FakeChatOverlayView());
            builder.RegisterCoreAiChatOverlayServices();

            using (IObjectResolver container = builder.Build())
            {
                IChatOverlayTurnSender sender = container.Resolve<IChatOverlayTurnSender>();
                Assert.IsInstanceOf<CoreAiChatServiceTurnSender>(sender);
                Assert.IsFalse(sender.IsAvailable);
            }
        }

        [Test]
        public void Settings_SerializedFieldsApplyOverTheCodeOptions_ButEmptyOnesKeepThem()
        {
            CoreAiChatOverlaySettings settings = new() { FadeDelaySeconds = 4f };
            settings.Options.RoleId = BuiltInAgentRoleIds.Builder;
            settings.Options.FadeDelaySeconds = 99d;
            settings.Options.UnknownCommandFormat = "No such command.";

            ChatOverlayOptions resolved = settings.ResolveOptions();

            Assert.AreEqual(BuiltInAgentRoleIds.Builder, resolved.RoleId, "an empty serialized role keeps the code one");
            Assert.AreEqual(4d, resolved.FadeDelaySeconds);
            Assert.AreEqual("No such command.", resolved.UnknownCommandFormat);
            Assert.AreNotSame(settings.Options, resolved);
            settings.RoleId = BuiltInAgentRoleIds.Programmer;
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, settings.ResolveOptions().RoleId);
        }

        [Test]
        public void KnownRoles_AddPolicyRoles_AndHideInternalOnes()
        {
            AgentMemoryPolicy policy = new();
            policy.ConfigureRole("Mayor", useMemoryTool: false);

            IReadOnlyList<string> roles = CoreAiChatOverlayInstaller.KnownRoles(policy);

            CollectionAssert.Contains(roles, BuiltInAgentRoleIds.Programmer);
            CollectionAssert.Contains(roles, "Mayor");
            CollectionAssert.DoesNotContain(roles, BuiltInAgentRoleIds.ContextCompactionAux);
        }

        [Test]
        public void Keys_OpenChatAndCommand_FromTheConfiguredKeys()
        {
            FakeKeyboard keyboard = new();
            (CoreAiChatOverlayEntryPoint entryPoint, ChatOverlayController controller, CoreAiChatOverlayView view) =
                CreateEntryPoint(keyboard, null);

            keyboard.Pressed.Add(KeyCode.T);
            entryPoint.PollKeys(1);
            Assert.IsTrue(controller.IsInputOpen);
            Assert.AreEqual("", view.Input.value);

            keyboard.Pressed.Clear();
            keyboard.Pressed.Add(KeyCode.Escape);
            entryPoint.PollKeys(2);
            Assert.IsFalse(controller.IsInputVisible, "Escape closes the input");
            controller.Tick();
            Assert.IsFalse(controller.IsInputOpen);

            keyboard.Pressed.Clear();
            keyboard.Pressed.Add(KeyCode.Slash);
            entryPoint.PollKeys(3);
            Assert.IsTrue(controller.IsInputOpen);
            Assert.AreEqual("/", view.Input.value, "the slash key pre-fills the prefix");

            entryPoint.Dispose();
            controller.Dispose();
        }

        [Test]
        public void Keys_EnterOpensTheInput_ButNotInTheFrameItClosed()
        {
            FakeKeyboard keyboard = new();
            (CoreAiChatOverlayEntryPoint entryPoint, ChatOverlayController controller, _) =
                CreateEntryPoint(keyboard, null);
            keyboard.Pressed.Add(KeyCode.Return);
            entryPoint.PollKeys(1);
            Assert.IsTrue(controller.IsInputOpen);

            controller.Submit("");
            entryPoint.PollKeys(2);

            Assert.IsFalse(controller.IsInputVisible, "the Enter that closed the input does not reopen it");
            controller.Tick();
            entryPoint.PollKeys(3);
            Assert.IsTrue(controller.IsInputVisible, "a later Enter opens it again");

            entryPoint.Dispose();
            controller.Dispose();
        }

        [Test]
        public void Keys_WhileClosed_WithoutAnOpenKey_PassThrough()
        {
            FakeKeyboard keyboard = new();
            (CoreAiChatOverlayEntryPoint entryPoint, ChatOverlayController controller, CoreAiChatOverlayView view) =
                CreateEntryPoint(keyboard, null);
            keyboard.Pressed.Add(KeyCode.W);
            keyboard.Pressed.Add(KeyCode.Space);
            keyboard.Pressed.Add(KeyCode.Escape);

            entryPoint.PollKeys(1);

            Assert.IsFalse(controller.IsInputOpen, "game keys do not open or block anything");
            Assert.IsFalse(view.HandleInputKey(KeyCode.W, 'w', 1), "a closed input consumes no key");

            entryPoint.Dispose();
            controller.Dispose();
        }

        [Test]
        public void Keys_IgnoredWithModifiers_OrWhenTheGateRefuses()
        {
            FakeKeyboard keyboard = new();
            FixedGate gate = new() { Allowed = false };
            (CoreAiChatOverlayEntryPoint entryPoint, ChatOverlayController controller, _) =
                CreateEntryPoint(keyboard, gate);
            keyboard.Pressed.Add(KeyCode.T);

            entryPoint.PollKeys(1);
            Assert.IsFalse(controller.IsInputOpen, "another text field owns the keyboard");

            gate.Allowed = true;
            keyboard.IsModifierHeld = true;
            entryPoint.PollKeys(2);
            Assert.IsFalse(controller.IsInputOpen, "Ctrl+T is not chat");

            keyboard.IsModifierHeld = false;
            entryPoint.PollKeys(3);
            Assert.IsTrue(controller.IsInputOpen);

            entryPoint.Dispose();
            controller.Dispose();
        }

        [Test]
        public void Keys_WhileOpen_TypingDoesNotReopenOrClose()
        {
            FakeKeyboard keyboard = new();
            (CoreAiChatOverlayEntryPoint entryPoint, ChatOverlayController controller, CoreAiChatOverlayView view) =
                CreateEntryPoint(keyboard, null);
            controller.OpenInput("tower");
            keyboard.Pressed.Add(KeyCode.T);
            keyboard.Pressed.Add(KeyCode.Slash);

            entryPoint.PollKeys(5);

            Assert.IsTrue(controller.IsInputVisible);
            Assert.AreEqual("tower", view.Input.value, "an open key typed into the field changes nothing");

            entryPoint.Dispose();
            controller.Dispose();
        }

        [Test]
        public void View_SwallowsTheOpeningKeystroke_OnlyInItsFrame()
        {
            CoreAiChatOverlayView view = new();
            view.SetInputOpen(true, "");
            view.SuppressOpeningKey(KeyCode.T, 10);

            Assert.IsTrue(view.HandleInputKey(KeyCode.T, '\0', 10));
            Assert.IsTrue(view.HandleInputKey(KeyCode.None, 't', 11));
            Assert.IsFalse(view.HandleInputKey(KeyCode.None, 't', 12), "a later t is typing");
        }

        [Test]
        public void View_SwallowsTheOpeningKeystroke_OnAnotherKeyboardLayout()
        {
            const char cyrillicOnTheTKey = 'е';
            CoreAiChatOverlayView view = new();
            view.SetInputOpen(true, "");
            view.SuppressOpeningKey(KeyCode.T, 10);

            Assert.IsTrue(view.HandleInputKey(KeyCode.T, '\0', 10));
            Assert.IsTrue(view.HandleInputKey(KeyCode.None, cyrillicOnTheTKey, 11),
                "the opening key's own character, as a Russian layout types it");
            Assert.IsFalse(view.HandleInputKey(KeyCode.None, cyrillicOnTheTKey, 11), "the next one is typing");

            view.SuppressOpeningKey(KeyCode.Slash, 20);
            Assert.IsTrue(view.HandleInputKey(KeyCode.None, '.', 20), "the slash key types a full stop there");
            Assert.IsFalse(view.HandleInputKey(KeyCode.None, 'a', 20), "typing after it reaches the field");
        }

        [Test]
        public void View_OpenedByAKeyWithoutACharacter_SwallowsNoTyping()
        {
            CoreAiChatOverlayView view = new();
            view.SetInputOpen(true, "");
            view.SuppressOpeningKey(KeyCode.F1, 10);

            Assert.IsFalse(view.HandleInputKey(KeyCode.None, 'a', 10));
        }

        [Test]
        public void View_DefaultStyle_ShowsAWhiteCaretOnTheDarkField()
        {
            CoreAiChatOverlayView view = new();

            Assert.AreEqual(Color.white, view.Input.textSelection.cursorColor);
            Assert.Greater(view.Input.textSelection.selectionColor.a, 0f);
        }

        [Test]
        public void View_EnterSubmitsTheText_EscapeRequestsClose()
        {
            CoreAiChatOverlayView view = new();
            List<string> submitted = new();
            int closes = 0;
            view.Submitted += submitted.Add;
            view.CloseRequested += () => closes++;
            view.SetInputOpen(true, "/agent Programmer");

            Assert.IsTrue(view.HandleInputKey(KeyCode.Return, '\0', 1));
            Assert.IsTrue(view.HandleInputKey(KeyCode.None, '\n', 1), "the character twin of the same Enter");
            Assert.IsTrue(view.HandleInputKey(KeyCode.Escape, '\0', 2));
            Assert.IsFalse(view.HandleInputKey(KeyCode.A, 'a', 2), "ordinary typing goes to the field");

            CollectionAssert.AreEqual(new[] { "/agent Programmer" }, submitted, "one Enter submits once");
            Assert.AreEqual(1, closes);
        }

        [Test]
        public void View_InputOpenAndClose_TogglesTheFieldAndThePointer()
        {
            CoreAiChatOverlayView view = new();
            Assert.AreEqual(DisplayStyle.None, view.Input.style.display.value);
            Assert.AreEqual(PickingMode.Ignore, view.Feed.pickingMode, "a closed overlay never takes the pointer");

            view.SetInputOpen(true, "/");
            Assert.AreEqual(DisplayStyle.Flex, view.Input.style.display.value);
            Assert.AreEqual("/", view.Input.value);
            Assert.AreEqual(PickingMode.Position, view.Feed.pickingMode, "the wheel scrolls while open");
            Assert.IsTrue(view.Root.ClassListContains(CoreAiChatOverlayView.OpenClassName));

            view.SetInputOpen(false, "");
            Assert.AreEqual(DisplayStyle.None, view.Input.style.display.value);
            Assert.AreEqual("", view.Input.value);
            Assert.AreEqual(PickingMode.Ignore, view.Feed.pickingMode);
        }

        [Test]
        public void View_RendersLines_FadesThem_AndShowsTheStatus()
        {
            CoreAiChatOverlayView view = new();
            ChatFeed feed = new();
            feed.LineAdded += view.AddLine;
            feed.LineChanged += view.UpdateLine;
            feed.LineRemoved += view.RemoveLine;

            ChatFeedLine line = feed.BeginStreaming(ChatFeedLineKind.Assistant, "AI", 0d);
            feed.Append(line, "Hello", 0d);
            Assert.AreEqual("Hello", view.GetLineText(line.Id));
            Assert.IsTrue(view.IsLineShown(line.Id));

            view.SetLineVisible(line, false);
            Assert.IsFalse(view.IsLineShown(line.Id));

            view.SetStatus(AgentStatusKind.RunningTool, "running execute_lua");
            Assert.AreEqual(DisplayStyle.Flex, view.StatusLabel.style.display.value);
            Assert.AreEqual("running execute_lua", view.StatusLabel.text);
            view.SetStatus(AgentStatusKind.Idle, "");
            Assert.AreEqual(DisplayStyle.None, view.StatusLabel.style.display.value);

            feed.Clear();
            Assert.AreEqual(0, view.LineCount);
        }

        [Test]
        public void View_AttachDetach_MovesTheTreeWithoutStackingIt()
        {
            CoreAiChatOverlayView view = new();
            VisualElement first = new();
            VisualElement second = new();

            view.Attach(first);
            view.Attach(first);
            Assert.AreEqual(1, first.childCount);

            view.Attach(second);
            Assert.AreEqual(0, first.childCount);
            Assert.AreSame(second, view.Root.parent);

            view.Detach();
            Assert.IsNull(view.Root.parent);
            Assert.IsNull(view.Host);
        }

        [Test]
        public void Surface_CreatesADocumentOnARuntimePanel_AndDisposeRemovesIt()
        {
            CoreAiChatOverlaySurface surface = CoreAiChatOverlaySurface.Create(new CoreAiChatOverlaySettings
            {
                SortingOrder = 77
            });
            try
            {
                Assert.IsNotNull(surface.Document);
                Assert.IsTrue(surface.OwnsPanelSettings);
                Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, surface.Document.panelSettings.scaleMode);
                Assert.AreEqual(77f, surface.Document.panelSettings.sortingOrder);
                Assert.IsNotNull(surface.Root);
            }
            finally
            {
                GameObject gameObject = surface.Document.gameObject;
                surface.Dispose();
                Assert.IsTrue(gameObject == null, "the document GameObject is destroyed");
                Assert.IsNull(surface.Document);
            }
        }

        [Test]
        public void Hotkeys_CharacterFor_CoversTheDefaultKeys()
        {
            Assert.AreEqual('t', ChatOverlayHotkeys.CharacterFor(KeyCode.T));
            Assert.AreEqual('/', ChatOverlayHotkeys.CharacterFor(KeyCode.Slash));
            Assert.AreEqual('\n', ChatOverlayHotkeys.CharacterFor(KeyCode.Return));
            Assert.AreEqual('1', ChatOverlayHotkeys.CharacterFor(KeyCode.Alpha1));
            Assert.AreEqual('\0', ChatOverlayHotkeys.CharacterFor(KeyCode.F5));
        }

#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
        [Test]
        public void Hotkeys_MapLegacyKeysToInputSystemKeys()
        {
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.T, out Key t));
            Assert.AreEqual(Key.T, t);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.Return, out Key enter));
            Assert.AreEqual(Key.Enter, enter);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.KeypadEnter, out Key numpadEnter));
            Assert.AreEqual(Key.NumpadEnter, numpadEnter);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.Slash, out Key slash));
            Assert.AreEqual(Key.Slash, slash);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.Alpha0, out Key zero));
            Assert.AreEqual(Key.Digit0, zero);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.Alpha7, out Key seven));
            Assert.AreEqual(Key.Digit7, seven);
            Assert.IsTrue(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.BackQuote, out Key backquote));
            Assert.AreEqual(Key.Backquote, backquote);
            Assert.IsFalse(ChatOverlayHotkeys.TryMapToInputSystemKey(KeyCode.None, out _));
        }
#endif

        [Test]
        public void Sender_BuildsTheRequestLikeTheChatPanel()
        {
            CoreAiChatServiceTurnSender sender = new(ServiceOver(new ScriptedOrchestrator(), true));

            AiTaskRequest request = sender.BuildTaskRequest(new ChatOverlayTurnRequest
            {
                Text = "build a tower",
                RoleId = "Programmer",
                TraceId = "trace-1"
            });

            Assert.AreEqual("Programmer", request.RoleId);
            Assert.AreEqual("build a tower", request.Hint);
            Assert.AreEqual("Chat", request.SourceTag);
            Assert.AreEqual("trace-1", request.TraceId);
            Assert.IsTrue(request.ActorContext.HasValue);
        }

        [Test]
        public async Task Sender_StreamsChunksToTheObserver_WithTheTraceId()
        {
            ScriptedOrchestrator orchestrator = new();
            CoreAiChatServiceTurnSender sender = new(ServiceOver(orchestrator, true), enableCameraTool: false);
            RecordingObserver observer = new();

            sender.Send(new ChatOverlayTurnRequest { Text = "hi", RoleId = "SmartChat", TraceId = "abc" }, observer);

            Assert.IsTrue(await WaitFor(observer.Ended.Task));
            CollectionAssert.AreEqual(new[] { "Hel", "lo" }, observer.Texts);
            Assert.AreEqual("abc", orchestrator.Requests[0].TraceId);
            Assert.AreEqual("hi", orchestrator.Requests[0].Hint);
        }

        [Test]
        public async Task Sender_Buffered_DeliversTheWholeAnswer()
        {
            ScriptedOrchestrator orchestrator = new();
            CoreAiChatServiceTurnSender sender = new(ServiceOver(orchestrator, true), enableCameraTool: false);
            RecordingObserver observer = new();

            sender.Send(new ChatOverlayTurnRequest { Text = "hi", RoleId = "SmartChat", StreamingOverride = false },
                observer);

            Assert.IsTrue(await WaitFor(observer.Ended.Task));
            CollectionAssert.AreEqual(new[] { "buffered answer" }, observer.Texts);
        }

        [Test]
        public async Task Sender_Cancel_EndsTheTurnAsCancelled()
        {
            ScriptedOrchestrator orchestrator = new() { WaitForCancellation = true };
            CoreAiChatServiceTurnSender sender = new(ServiceOver(orchestrator, true), enableCameraTool: false);
            RecordingObserver observer = new();
            IChatOverlayTurn turn = sender.Send(new ChatOverlayTurnRequest { Text = "hi", RoleId = "SmartChat" },
                observer);

            turn.Cancel();

            Assert.IsFalse(await WaitFor(observer.Ended.Task));
            Assert.AreEqual(ChatOverlayTurnFailureKind.Cancelled, observer.Failure.Value.Kind);
            CollectionAssert.IsEmpty(observer.Texts);
        }

        [Test]
        public void Sender_WithoutService_FailsAtOnce()
        {
            CoreAiChatServiceTurnSender sender = new(null);
            RecordingObserver observer = new();

            sender.Send(new ChatOverlayTurnRequest { Text = "hi" }, observer);

            Assert.IsFalse(sender.IsAvailable);
            Assert.IsTrue(observer.Ended.Task.IsCompleted);
            Assert.AreEqual(ChatOverlayTurnFailureKind.Error, observer.Failure.Value.Kind);
        }
    }
}
