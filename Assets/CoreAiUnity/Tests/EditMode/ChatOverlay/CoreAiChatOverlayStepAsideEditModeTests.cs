using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The overlay stepping aside for a chat drawn elsewhere (a Lua mod) through <see cref="IChatOverlaySurfaceSwitch"/>:
    /// while claimed its view is detached, no key opens it, an open input closes and its built-in command names are
    /// free for the claimant; releasing the claim brings all of it back. The controller keeps ticking.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatOverlayStepAsideEditModeTests
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

        private FakeKeyboard _keyboard;
        private ChatCommandRegistry _registry;
        private ChatOverlaySurfaceSwitch _switch;
        private ChatOverlayController _controller;
        private CoreAiChatOverlayView _view;
        private VisualElement _host;
        private CoreAiChatOverlayEntryPoint _entryPoint;

        private void Create(bool claimFirst = false)
        {
            CoreAiChatOverlaySettings settings = new();
            _host = new VisualElement();
            settings.HostElement = _host;
            ChatOverlayOptions options = settings.ResolveOptions();
            _view = new CoreAiChatOverlayView(settings, options);
            _keyboard = new FakeKeyboard();
            _registry = new ChatCommandRegistry();
            _switch = new ChatOverlaySurfaceSwitch();
            ChatAgentSelector agents = new(BuiltInAgentRoleIds.SmartChat);
            _controller = new ChatOverlayController(options, _view, new ScriptedTurnSender(), _registry, agents,
                new ManualChatClock());
            if (claimFirst)
            {
                _switch.Claim("lua chat");
            }

            _entryPoint = new CoreAiChatOverlayEntryPoint(_controller, _view, _keyboard, settings, agents, null, null,
                null, _switch);
            _entryPoint.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _entryPoint?.Dispose();
            _controller?.Dispose();
        }

        [Test]
        public void AClaim_HidesTheOverlay_FreesTheBuiltInNames_AndAReleaseBringsThemBack()
        {
            Create();
            Assert.AreSame(_host, _view.Root.parent);
            Assert.IsTrue(_registry.TryResolve("help", out _));

            using (_switch.Claim("lua chat"))
            {
                Assert.IsTrue(_entryPoint.IsSteppedAside);
                Assert.IsNull(_view.Root.parent, "the view is detached at once");
                Assert.IsFalse(_registry.TryResolve("help", out _), "the claimant may register /help itself");
                Assert.IsFalse(_registry.TryResolve("stop", out _));

                _keyboard.Pressed.Add(KeyCode.T);
                _entryPoint.Tick();
                Assert.IsFalse(_controller.IsInputOpen, "no key opens a stepped-aside overlay");
                _keyboard.Pressed.Clear();
            }

            Assert.IsFalse(_entryPoint.IsSteppedAside);
            Assert.AreSame(_host, _view.Root.parent);
            Assert.IsTrue(_registry.TryResolve("help", out _));
        }

        [Test]
        public void AClaim_ClosesAnOpenInput()
        {
            Create();
            _controller.OpenInput("half a line");

            _switch.Claim("lua chat");

            Assert.IsFalse(_controller.IsInputVisible);
        }

        [Test]
        public void Negative_ClaimedBeforeStart_TheOverlayNeverShows_UntilReleased()
        {
            Create(claimFirst: true);

            Assert.IsTrue(_entryPoint.IsSteppedAside);
            Assert.IsNull(_view.Root.parent);
            Assert.IsFalse(_registry.TryResolve("help", out _));
        }

        [Test]
        public void SteppedAside_TheControllerStillTicks_SoPostedLinesAreKept()
        {
            Create();
            _switch.Claim("lua chat");

            _controller.PostSystemMessage("Autopilot off.");
            _entryPoint.Tick();

            Assert.AreEqual("Autopilot off.", _controller.Feed.Lines[_controller.Feed.Lines.Count - 1].Text);
        }
    }
}
