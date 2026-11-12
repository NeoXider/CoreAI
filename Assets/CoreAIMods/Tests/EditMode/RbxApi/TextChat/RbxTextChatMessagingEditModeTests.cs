using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.TextChat;
using CoreAI.Tests.EditMode.RbxApi.Gui;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.TextChat
{
    /// <summary>
    /// The messaging half of <c>TextChatService</c> (roadmap P2), engine-free over a fake host: <c>SendAsync</c> fires
    /// <c>SendingMessage</c>, runs a command instead of sending, delivers a line before the host's listeners hear it
    /// and marks a taken one; system and host lines arrive in <c>RBXSystem</c>; a configuration switched off claims the
    /// host's chat while its writer runs (DEV-142); the host's commands are listed; without a host a world
    /// <c>TextChatCommand</c> still answers. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxTextChatMessagingEditModeTests
    {
        private RbxGuiTestWorld _world;
        private RbxTextChatService _service;
        private FakeHost _host;
        private RbxTextSource _source;

        [SetUp]
        public void SetUp()
        {
            _world = new RbxGuiTestWorld();
            _service = (RbxTextChatService)_world.Game.FindFirstChildOfClass("TextChatService");
            _source = (RbxTextSource)_world.Registry.Create("TextSource", isRuntimeInfrastructure: true);
            _source.UserId = 7;
            _host = new FakeHost();
            _service.AttachChatHost(_host, () => _source);
        }

        [TearDown]
        public void TearDown()
        {
            _service.DetachChatHost();
            _world.Dispose();
        }

        private static RbxTextChatMessage Message(object[] fired)
        {
            return (RbxTextChatMessage)fired[0];
        }

        [Test]
        public void TheDefaultChannels_AreDetached_AndNamedAsInRoblox()
        {
            RbxInstance folder = _service.TextChannels;

            Assert.AreEqual("TextChannels", folder.Name);
            Assert.IsNull(folder.Parent, "detached: never saved, never replicated");
            Assert.AreSame(_service.GeneralChannel, folder.FindFirstChild("RBXGeneral"));
            Assert.AreSame(_service.SystemChannel, folder.FindFirstChild("RBXSystem"));
            Assert.IsTrue(_service.GeneralChannel.IsA("TextChannel"));
            Assert.IsFalse(_world.Registry.TryGetRecord(_service.GeneralChannel.Id, out _));
        }

        [Test]
        public void SendAsync_FiresSendingMessage_ThenDeliversWithTheLocalTextSource()
        {
            List<object[]> sending = _world.Listen(_service.SendingMessage);
            List<object[]> received = _world.Listen(_service.MessageReceived);
            List<object[]> onChannel = _world.Listen(_service.GeneralChannel.MessageReceived);

            RbxTextChatMessage sent = _service.SendAsync(_service.GeneralChannel, "hello", "");
            _world.Frame();

            Assert.AreEqual(1, sending.Count);
            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(1, onChannel.Count, "the channel's own MessageReceived fires too");
            RbxTextChatMessage message = Message(received[0]);
            Assert.AreSame(sent, message);
            Assert.AreEqual("hello", message.Text);
            Assert.AreSame(_source, message.TextSource);
            Assert.AreSame(_service.GeneralChannel, message.TextChannel);
            Assert.AreEqual(RbxTextChatMessageStatus.Success, message.Status);
            Assert.AreEqual("", message.Metadata);
            CollectionAssert.AreEqual(new[] { "hello" }, _host.Offered, "a delivered line reaches the listeners");
        }

        [Test]
        public void SendAsync_ACommand_RunsInsteadOfSending_AndItsReplyIsASystemMessage()
        {
            _host.Run = text => text.StartsWith("/undo", StringComparison.Ordinal)
                ? RbxChatSendResult.Command("Undone.")
                : RbxChatSendResult.Deliver();
            List<object[]> received = _world.Listen(_service.MessageReceived);

            RbxTextChatMessage sent = _service.SendAsync(_service.GeneralChannel, "/undo", "");
            _world.Frame();

            Assert.AreEqual(RbxTextChatMetadata.Command, sent.Metadata);
            Assert.AreEqual(1, received.Count, "the command line itself is not delivered");
            RbxTextChatMessage reply = Message(received[0]);
            Assert.AreEqual("Undone.", reply.Text);
            Assert.AreEqual(RbxTextChatMetadata.Command, reply.Metadata);
            Assert.IsNull(reply.TextSource);
            Assert.AreSame(_service.SystemChannel, reply.TextChannel);
            Assert.AreEqual(0, _host.Offered.Count, "a command is never offered to the listeners");
        }

        [Test]
        public void Negative_AFailedCommand_RepliesAsAnError()
        {
            _host.Run = _ => RbxChatSendResult.Command("Nothing to undo.", true);
            List<object[]> received = _world.Listen(_service.MessageReceived);

            _service.SendAsync(_service.GeneralChannel, "/undo", "");
            _world.Frame();

            Assert.AreEqual(RbxTextChatMetadata.Error, Message(received[0]).Metadata);
        }

        [Test]
        public void SendAsync_ALineAListenerTakes_IsShownBeforeTheListenersAnswer_AndMarkedTaken()
        {
            _host.Offer = text =>
            {
                _host.Post(new RbxChatHostLine("Bob", "Hello, traveller.", RbxTextChatMetadata.System));
                return true;
            };
            List<object[]> received = _world.Listen(_service.MessageReceived);

            RbxTextChatMessage sent = _service.SendAsync(_service.GeneralChannel, "Bob, hi", "");
            _world.Frame();

            Assert.AreEqual(RbxTextChatMetadata.Taken, sent.Metadata);
            Assert.AreEqual(2, received.Count);
            Assert.AreEqual("Bob, hi", Message(received[0]).Text, "the player's line comes first");
            Assert.AreEqual("Hello, traveller.", Message(received[1]).Text);
            Assert.AreEqual("Bob", Message(received[1]).PrefixText);
        }

        [Test]
        public void SendAsync_TheHostMayDeliverOtherText()
        {
            _host.Run = _ => RbxChatSendResult.Deliver("/help me");
            List<object[]> received = _world.Listen(_service.MessageReceived);

            RbxTextChatMessage sent = _service.SendAsync(_service.GeneralChannel, "//help me", "");
            _world.Frame();

            Assert.AreEqual("/help me", sent.Text);
            Assert.AreEqual("/help me", Message(received[0]).Text);
        }

        [Test]
        public void HostLines_AndDisplaySystemMessage_ArriveInRbxSystem()
        {
            List<object[]> received = _world.Listen(_service.MessageReceived);
            List<object[]> onSystem = _world.Listen(_service.SystemChannel.MessageReceived);

            _host.Post(new RbxChatHostLine("", "Autopilot off.", ""));
            _service.DisplaySystemMessage(_service.SystemChannel, "Welcome.", "Game.Welcome", "");
            _world.Frame();

            Assert.AreEqual(2, received.Count);
            Assert.AreEqual(2, onSystem.Count);
            Assert.AreEqual(RbxTextChatMetadata.System, Message(received[0]).Metadata, "a host line without a kind");
            Assert.AreEqual("Game.Welcome", Message(received[1]).Metadata);
            Assert.AreEqual(0, _host.Offered.Count, "system lines are never offered to the listeners");
        }

        [Test]
        public void AConfigurationSwitchedOff_ClaimsTheHostsChat_AndOnAgainReleasesIt()
        {
            _service.ChatInputBarConfiguration.SetEnabled(false, () => true);
            Assert.IsTrue(_service.IsChatClaimed);
            Assert.AreEqual(1, _host.Claims);

            _service.ChatWindowConfiguration.SetEnabled(false, () => true);
            Assert.AreEqual(1, _host.Claims, "one claim for the world");

            _service.ChatInputBarConfiguration.SetEnabled(true, null);
            Assert.IsTrue(_service.IsChatClaimed, "the window is still off");
            _service.ChatWindowConfiguration.SetEnabled(true, null);
            Assert.IsFalse(_service.IsChatClaimed);
            Assert.AreEqual(1, _host.Releases);
        }

        [Test]
        public void Negative_WhenTheWriterStops_TheSwitchComesBackOn()
        {
            bool alive = true;
            _service.ChatInputBarConfiguration.SetEnabled(false, () => alive);
            _service.CheckConfigurationWriters();
            Assert.IsFalse(_service.ChatInputBarConfiguration.Enabled, "twin: the writer still runs");

            alive = false;
            _service.CheckConfigurationWriters();

            Assert.IsTrue(_service.ChatInputBarConfiguration.Enabled);
            Assert.IsFalse(_service.IsChatClaimed);
            Assert.AreEqual(1, _host.Releases);
        }

        [Test]
        public void Negative_WithoutAHost_ASwitchClaimsNothing_UntilAHostAttaches()
        {
            _service.DetachChatHost();
            _service.ChatInputBarConfiguration.SetEnabled(false, null);
            Assert.IsFalse(_service.IsChatClaimed);

            FakeHost later = new();
            _service.AttachChatHost(later, () => _source);

            Assert.IsTrue(_service.IsChatClaimed, "a later host gets the world's claim");
            Assert.AreEqual(1, later.Claims);
        }

        [Test]
        public void TextChatCommands_ListsTheHostsCommands_AndFollowsChanges()
        {
            _host.Commands.Add(new RbxChatHostCommand("undo", new[] { "u" }, "", "Take back the AI's last change."));

            IReadOnlyList<RbxInstance> listed = _service.TextChatCommands.GetChildren();
            Assert.AreEqual(1, listed.Count);
            RbxTextChatCommand undo = (RbxTextChatCommand)listed[0];
            Assert.AreEqual("/undo", undo.PrimaryAlias);
            Assert.AreEqual("/u", undo.SecondaryAlias);
            Assert.AreEqual("Take back the AI's last change.", undo.GetAttribute("Description"));

            _host.Commands.Add(new RbxChatHostCommand("fly", Array.Empty<string>(), "", "Toggle flight."));
            Assert.AreEqual(1, _service.TextChatCommands.GetChildren().Count, "twin: cached until the host says");
            _host.RaiseCommandsChanged();
            Assert.AreEqual(2, _service.TextChatCommands.GetChildren().Count);
        }

        [Test]
        public void WithoutAHost_SendAsync_StillTriggersAWorldTextChatCommand()
        {
            _service.DetachChatHost();
            RbxTextChatCommand command = (RbxTextChatCommand)_world.Registry.Create("TextChatCommand");
            command.PrimaryAlias = "/time";
            command.Parent = _service;
            List<object[]> triggered = _world.Listen(command.Triggered);
            List<object[]> received = _world.Listen(_service.MessageReceived);

            RbxTextChatMessage sent = _service.SendAsync(_service.GeneralChannel, "/TIME now", "");
            _service.SendAsync(_service.GeneralChannel, "/timer", "");
            _world.Frame();

            Assert.AreEqual(RbxTextChatMetadata.Command, sent.Metadata);
            Assert.AreEqual(1, triggered.Count, "twin: a near miss is an ordinary line");
            Assert.AreEqual("/TIME now", triggered[0][1]);
            Assert.AreEqual(1, received.Count);
            Assert.AreEqual("/timer", Message(received[0]).Text);
        }

        private sealed class FakeHost : IRbxChatHost
        {
            public readonly List<string> Offered = new();
            public readonly List<RbxChatHostCommand> Commands = new();

            public Func<string, RbxChatSendResult> Run { get; set; } = _ => RbxChatSendResult.Deliver();

            public Func<string, bool> Offer { get; set; } = _ => false;

            public int Claims { get; private set; }

            public int Releases { get; private set; }

            public event Action HostCommandsChanged;

            public event Action<RbxChatHostLine> LinePosted;

            public RbxChatSendResult RunCommand(string text)
            {
                return Run(text);
            }

            public bool OfferPlayerLine(string text)
            {
                Offered.Add(text);
                return Offer(text);
            }

            public IReadOnlyList<RbxChatHostCommand> GetHostCommands()
            {
                return Commands.ToArray();
            }

            public IDisposable ClaimChat(string owner)
            {
                Claims++;
                return new Release(this);
            }

            public void Post(RbxChatHostLine line)
            {
                LinePosted?.Invoke(line);
            }

            public void RaiseCommandsChanged()
            {
                HostCommandsChanged?.Invoke();
            }

            private sealed class Release : IDisposable
            {
                private FakeHost _owner;

                public Release(FakeHost owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    if (_owner != null)
                    {
                        _owner.Releases++;
                        _owner = null;
                    }
                }
            }
        }
    }
}
