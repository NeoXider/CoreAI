using System;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The in-world chat overlay on a real runtime panel in Play Mode, with no Hub, no model and no input device:
    /// a scripted keyboard opens it with T, Enter and the slash, a scripted model streams one answer into one
    /// feed line, and the status line names the running tool one tick after the tool event.
    /// </summary>
    public sealed class CoreAiChatOverlayPlayModeTests
    {
        private sealed class ScriptedKeyboard : IChatOverlayKeyboard
        {
            public readonly HashSet<KeyCode> Pressed = new();

            public bool IsModifierHeld => false;

            public bool WasPressedThisFrame(KeyCode key)
            {
                return Pressed.Contains(key);
            }
        }

        private sealed class ScriptedSender : IChatOverlayTurnSender, IChatOverlayTurn
        {
            public IChatOverlayTurnObserver Observer;
            public ChatOverlayTurnRequest Request;

            public bool IsAvailable => true;

            public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
            {
                Request = request;
                Observer = observer;
                return this;
            }

            public void StopAgent(string roleId)
            {
            }

            public void Cancel()
            {
            }
        }

        private sealed class ScriptedTools : IAgentToolEventSource
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
                ToolCallCompleted?.Invoke(new LlmToolCallCompleted(traceId, "Programmer", tool, "{}", "{}", 1d));
            }

            public void Fail(string traceId, string tool)
            {
                ToolCallFailed?.Invoke(new LlmToolCallFailed(traceId, "Programmer", tool, "{}", "error", 1d));
            }
        }

        private ScriptedKeyboard _keyboard;
        private ScriptedSender _sender;
        private ScriptedTools _tools;
        private ChatOverlayController _controller;
        private CoreAiChatOverlayView _view;
        private CoreAiChatOverlayEntryPoint _entryPoint;

        [SetUp]
        public void SetUp()
        {
            CoreAiChatOverlaySettings settings = new() { DontDestroyOnLoad = false };
            ChatOverlayOptions options = settings.ResolveOptions();
            _keyboard = new ScriptedKeyboard();
            _sender = new ScriptedSender();
            _tools = new ScriptedTools();
            _view = new CoreAiChatOverlayView(settings, options);
            ChatAgentSelector agents = new(BuiltInAgentRoleIds.Programmer);
            ChatCommandRegistry registry = new();
            _controller = new ChatOverlayController(options, _view, _sender, registry, agents,
                new UnityChatOverlayClock(), _tools);
            _entryPoint = new CoreAiChatOverlayEntryPoint(_controller, _view, _keyboard, settings, agents);
            _entryPoint.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _entryPoint?.Dispose();
            _controller?.Dispose();
        }

        private IEnumerator PressFor(KeyCode key)
        {
            _keyboard.Pressed.Add(key);
            _entryPoint.Tick();
            _keyboard.Pressed.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator OpenKeys_OpenTheInputOnTheRuntimePanel_WithoutAHub()
        {
            yield return null;
            Assert.IsNotNull(_entryPoint.Surface, "the overlay creates its own document");
            Assert.IsNotNull(_view.Root.panel, "the overlay is on a live panel");

            yield return PressFor(KeyCode.T);
            Assert.IsTrue(_controller.IsInputOpen);
            Assert.AreEqual(DisplayStyle.Flex, _view.Input.resolvedStyle.display);
            Assert.AreEqual("", _view.Input.value, "the chat key opens an empty input");

            _controller.CloseInput();
            _entryPoint.Tick();
            Assert.IsFalse(_controller.IsInputOpen);

            yield return PressFor(KeyCode.Return);
            Assert.IsTrue(_controller.IsInputOpen);
            _controller.CloseInput();
            _entryPoint.Tick();

            yield return PressFor(KeyCode.Slash);
            Assert.IsTrue(_controller.IsInputOpen);
            Assert.AreEqual("/", _view.Input.value);
        }

        [UnityTest]
        public IEnumerator ScriptedModel_StreamsIntoOneLine_AndTheStatusFollowsTheTool()
        {
            yield return null;
            _controller.Submit("put a red block in front of me");
            Assert.IsNotNull(_sender.Request);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, _sender.Request.RoleId);
            string trace = _sender.Request.TraceId;

            _tools.Start(trace, "execute_lua");
            _entryPoint.Tick();
            Assert.AreEqual("running execute_lua", _view.StatusLabel.text, "the tool shows on the first tick");
            yield return null;

            _tools.Complete(trace, "execute_lua");
            _sender.Observer.OnChunk(new LlmStreamChunk { Text = "Placed a red " });
            _entryPoint.Tick();
            yield return null;
            _sender.Observer.OnChunk(new LlmStreamChunk { Text = "block." });
            _entryPoint.Tick();
            yield return null;

            ChatFeedLine answer = null;
            int assistantLines = 0;
            foreach (ChatFeedLine line in _controller.Feed.Lines)
            {
                if (line.Kind == ChatFeedLineKind.Assistant)
                {
                    answer = line;
                    assistantLines++;
                }
            }

            Assert.AreEqual(1, assistantLines);
            Assert.AreEqual("Placed a red block.", _view.GetLineText(answer.Id));

            _sender.Observer.OnCompleted();
            _entryPoint.Tick();
            yield return null;
            Assert.AreEqual(DisplayStyle.None, _view.StatusLabel.resolvedStyle.display, "the status clears when done");
        }
    }
}
