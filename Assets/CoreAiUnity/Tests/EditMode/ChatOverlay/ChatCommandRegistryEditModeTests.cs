using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The command registry packages and games add to at runtime: lookup by name and alias, conflicts,
    /// replacement and removal through the returned handle.
    /// </summary>
    [TestFixture]
    public sealed class ChatCommandRegistryEditModeTests
    {
        private static ChatCommand Make(string name, params string[] aliases)
        {
            return new ChatCommand(name, "Test command " + name, _ => ChatCommandResult.Handled(name), "", aliases);
        }

        [Test]
        public void Register_ResolvesByNameAndAlias_IgnoringCaseAndPrefix()
        {
            ChatCommandRegistry registry = new();
            ChatCommand fly = Make("fly", "f");
            registry.Register(fly);

            Assert.IsTrue(registry.TryResolve("FLY", out IChatCommand byName));
            Assert.IsTrue(registry.TryResolve("f", out IChatCommand byAlias));
            Assert.IsTrue(registry.TryResolve("/fly", out IChatCommand byPrefixed));
            Assert.AreSame(fly, byName);
            Assert.AreSame(fly, byAlias);
            Assert.AreSame(fly, byPrefixed);
            Assert.IsFalse(registry.TryResolve("flyy", out _));
        }

        [Test]
        public void Register_TakenName_Throws()
        {
            ChatCommandRegistry registry = new();
            registry.Register(Make("undo"));

            Assert.Throws<InvalidOperationException>(() => registry.Register(Make("Undo")));
        }

        [Test]
        public void Register_TakenAlias_Throws()
        {
            ChatCommandRegistry registry = new();
            registry.Register(Make("fly", "f"));

            Assert.Throws<InvalidOperationException>(() => registry.Register(Make("freeze", "F")));
        }

        [Test]
        public void Register_ReplaceExisting_ReplacesTheOwnerOfEveryKey()
        {
            ChatCommandRegistry registry = new();
            registry.Register(Make("fly", "f"));
            ChatCommand replacement = Make("fly");

            registry.Register(replacement, replaceExisting: true);

            Assert.IsTrue(registry.TryResolve("fly", out IChatCommand resolved));
            Assert.AreSame(replacement, resolved);
            Assert.IsFalse(registry.TryResolve("f", out _), "the replaced command's alias goes with it");
            Assert.AreEqual(1, registry.Commands.Count);
        }

        [Test]
        public void DisposeHandle_RemovesThatRegistration()
        {
            ChatCommandRegistry registry = new();
            IDisposable handle = registry.Register(Make("time"));

            handle.Dispose();
            handle.Dispose();

            Assert.IsFalse(registry.TryResolve("time", out _));
        }

        [Test]
        public void DisposeHandle_OfReplacedCommand_LeavesTheReplacement()
        {
            ChatCommandRegistry registry = new();
            IDisposable original = registry.Register(Make("time"));
            ChatCommand replacement = Make("time");
            registry.Register(replacement, replaceExisting: true);

            original.Dispose();

            Assert.IsTrue(registry.TryResolve("time", out IChatCommand resolved));
            Assert.AreSame(replacement, resolved);
        }

        [Test]
        public void Unregister_ByAlias_RemovesAllKeys_UnknownReturnsFalse()
        {
            ChatCommandRegistry registry = new();
            registry.Register(Make("autopilot", "ap"));

            Assert.IsTrue(registry.Unregister("ap"));
            Assert.IsFalse(registry.TryResolve("autopilot", out _));
            Assert.IsFalse(registry.Unregister("autopilot"));
        }

        [Test]
        public void Commands_AreSortedByName_AndChangedIsRaised()
        {
            ChatCommandRegistry registry = new();
            int changes = 0;
            registry.Changed += () => changes++;
            registry.Register(Make("zoom"));
            registry.Register(Make("agent"));
            registry.Register(Make("Model"));

            List<string> names = new();
            foreach (IChatCommand command in registry.Commands)
            {
                names.Add(command.Name);
            }

            CollectionAssert.AreEqual(new[] { "agent", "Model", "zoom" }, names);
            Assert.AreEqual(3, changes);
        }

        [Test]
        public void Register_NameWithSpace_IsRejected()
        {
            ChatCommandRegistry registry = new();

            Assert.Throws<ArgumentException>(() => registry.Register(Make("two words")));
            Assert.IsFalse(ChatCommandRegistry.IsValidName(""));
            Assert.IsTrue(ChatCommandRegistry.IsValidName("?"));
        }
    }
}
