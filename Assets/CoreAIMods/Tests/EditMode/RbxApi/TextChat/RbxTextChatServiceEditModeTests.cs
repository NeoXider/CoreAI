using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.TextChat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.TextChat
{
    /// <summary>
    /// Engine-free <c>TextChatService</c> commands over a fake chat command list: a <c>TextChatCommand</c> under
    /// the service is a chat command while it is enabled, named and inside, a command the chat already answers
    /// keeps its alias, and a destroyed holder lets the waiting command take it. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxTextChatServiceEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxTextChatService _service;
        private FakeSink _sink;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            RbxDataModel game = DataModelBootstrap.CreateGame(_registry);
            _service = (RbxTextChatService)game.FindFirstChildOfClass("TextChatService");
            Assert.IsNotNull(_service, "the bootstrap creates TextChatService");
            _sink = new FakeSink();
            _log = new List<string>();
            _service.AttachCommandSink(_sink, () => null, _log.Add);
        }

        private RbxTextChatCommand Command(string primary, string secondary = "", RbxInstance parent = null)
        {
            RbxTextChatCommand command = (RbxTextChatCommand)_registry.Create("TextChatCommand");
            command.PrimaryAlias = primary;
            command.SecondaryAlias = secondary;
            command.Parent = parent ?? _service;
            return command;
        }

        [Test]
        public void ACommandUnderTheService_IsAChatCommand_WithoutItsSlash()
        {
            Command("/time", "/t");

            Assert.IsTrue(_sink.Has("time"));
            Assert.AreEqual("t", _sink.SecondaryOf("time"));
            Assert.AreEqual(1, _service.RegisteredCommandCount);
        }

        [Test]
        public void ACommandInTheTextChatCommandsFolder_IsAChatCommand_AndMovingItOutRemovesIt()
        {
            RbxTextChatCommand command = Command("/time", parent: _service.TextChatCommands);

            Assert.IsTrue(_sink.Has("time"), "the Roblox place for commands");

            command.Parent = _registry.WorldRoot;
            Assert.IsFalse(_sink.Has("time"), "twin: moved out of the folder");
        }

        [Test]
        public void Negative_ACommandOutsideTheService_OrWithoutAnAlias_IsNoChatCommand()
        {
            Command("/time", parent: _registry.WorldRoot);
            Command("   ");

            Assert.AreEqual(0, _sink.Count);
        }

        [Test]
        public void DisablingMovingOrDestroying_RemovesTheCommand_AndEnablingAddsItBack()
        {
            RbxTextChatCommand command = Command("/time");

            command.Enabled = false;
            Assert.IsFalse(_sink.Has("time"), "disabled");
            command.Enabled = true;
            Assert.IsTrue(_sink.Has("time"), "twin: enabled again");

            command.Parent = _registry.WorldRoot;
            Assert.IsFalse(_sink.Has("time"), "moved out");
            command.Parent = _service;
            Assert.IsTrue(_sink.Has("time"));

            command.PrimaryAlias = "/clock";
            Assert.IsFalse(_sink.Has("time"));
            Assert.IsTrue(_sink.Has("clock"), "a renamed alias moves the registration");

            command.Destroy();
            Assert.AreEqual(0, _sink.Count, "destroyed, as when its mod unloads");
        }

        [Test]
        public void ACommandInAFolderUnderTheService_Counts()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _service;
            Command("/time", parent: folder);

            Assert.IsTrue(_sink.Has("time"));
        }

        [Test]
        public void TheGamesOwnCommand_WinsTheAlias_AndTheNewcomerIsReportedOnce()
        {
            _sink.Taken.Add("time");
            RbxTextChatCommand command = Command("/time");
            command.Enabled = false;
            command.Enabled = true;

            Assert.IsFalse(_sink.Has("time"), "a command registered first is kept");
            Assert.AreEqual(1, _log.FindAll(line => line.Contains("already a chat command")).Count);
        }

        [Test]
        public void TwoScriptCommands_OnOneAlias_FirstWins_AndTheSecondTakesItWhenTheFirstGoes()
        {
            RbxTextChatCommand first = Command("/time");
            RbxTextChatCommand second = Command("/time");
            Assert.AreEqual(1, _sink.Count);

            first.Destroy();

            Assert.IsTrue(_sink.Has("time"), "the waiting command takes the free alias");
            second.Destroy();
            Assert.AreEqual(0, _sink.Count, "and it was the second command's");
        }

        [Test]
        public void Negative_DetachingTheChat_RemovesEveryCommand_AndNothingIsAddedUntilAttached()
        {
            Command("/time");
            _service.DetachCommandSink();
            Assert.AreEqual(0, _sink.Count);

            Command("/weather");
            Assert.AreEqual(0, _sink.Count, "no chat attached");

            _service.AttachCommandSink(_sink, () => null, _log.Add);
            Assert.IsTrue(_sink.Has("time"));
            Assert.IsTrue(_sink.Has("weather"), "twin: attaching registers what is already there");
        }

        private sealed class FakeSink : IRbxChatCommandSink
        {
            private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> Taken { get; } = new(StringComparer.OrdinalIgnoreCase);

            public int Count => _byName.Count;

            public bool Has(string name)
            {
                return _byName.ContainsKey(name);
            }

            public string SecondaryOf(string name)
            {
                return _byName[name].Secondary;
            }

            public bool IsTaken(string alias)
            {
                return Taken.Contains(alias) || _byName.ContainsKey(alias);
            }

            public IDisposable Add(string name, string secondaryAlias, Action<string> trigger)
            {
                Entry entry = new(name, secondaryAlias, trigger);
                _byName.Add(name, entry);
                return new Handle(() => _byName.Remove(name));
            }

            private sealed class Entry
            {
                public Entry(string name, string secondary, Action<string> trigger)
                {
                    Name = name;
                    Secondary = secondary;
                    Trigger = trigger;
                }

                public string Name { get; }

                public string Secondary { get; }

                public Action<string> Trigger { get; }
            }

            private sealed class Handle : IDisposable
            {
                private Action _dispose;

                public Handle(Action dispose)
                {
                    _dispose = dispose;
                }

                public void Dispose()
                {
                    Action dispose = _dispose;
                    _dispose = null;
                    dispose?.Invoke();
                }
            }
        }
    }
}
