using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.ChatOverlay;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.TextChatLua
{
    /// <summary>
    /// <c>TextChatCommand</c> the Roblox way through the real mod runtime and the in-world chat's command registry:
    /// a mod registers <c>/time</c>, the chat runs it and <c>Triggered</c> gets the local player's TextSource and the
    /// whole line; the command goes away with the mod; a command the game registered first wins.
    /// </summary>
    [TestFixture]
    public sealed class TextChatCommandLuaEditModeTests
    {
        private const string TimeCommand = @"
local TextChatService = game:GetService('TextChatService')
local command = Instance.new('TextChatCommand')
command.Name = 'TimeCommand'
command.PrimaryAlias = '/time'
command.SecondaryAlias = '/t'
command.Triggered:Connect(function(source, text)
  store_set('text', text)
  store_set('user', tostring(source and source.UserId))
  store_set('class', source and source.ClassName or 'nil')
end)
command.Parent = TextChatService";

        private SynchronizationContext _savedContext;
        private UxmlServiceTestWorld _world;
        private ChatCommandRegistry _commands;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = UxmlServiceTestWorld.WithFake();
            _world.Roblox.Players.EnsureActor(_world.Roblox.Registry, "local-player");
            _world.Roblox.LocalPlayerActorId = "local-player";
            _commands = new ChatCommandRegistry();
            _world.Roblox.AttachChatCommands(new ChatCommandRegistrySink(_commands));
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private ChatCommandResult Send(string line)
        {
            Assert.AreEqual(ChatInputKind.Command,
                ChatCommandParser.Classify(line, ChatCommandParser.DefaultPrefix, out ChatCommandInvocation invocation,
                    out string _));
            Assert.IsTrue(_commands.TryResolve(invocation.Name, out IChatCommand command), invocation.Name);
            return command.Execute(new ChatCommandContext(invocation, _commands, null, ChatCommandParser.DefaultPrefix));
        }

        [Test]
        public void AModsTextChatCommand_IsAChatCommand_AndTriggeredGetsTheSenderAndTheLine()
        {
            _world.Load("clock", TimeCommand);

            ChatCommandResult result = Send("/time day");
            _world.Frame();

            Assert.AreEqual(ChatCommandOutcome.Handled, result.Outcome, "the line does not reach the model");
            Assert.AreEqual("/time day", _world.Get("clock", "text"), "unfilteredText is the whole line");
            Assert.IsTrue(_world.Roblox.Players.TryGetByActorId("local-player", out RbxPlayer player));
            Assert.AreEqual(player.UserId.ToString(), _world.Get("clock", "user"));
            Assert.AreEqual("TextSource", _world.Get("clock", "class"));

            Send("/t");
            _world.Frame();
            Assert.AreEqual("/t", _world.Get("clock", "text"), "the secondary alias triggers it too");
        }

        [Test]
        public void Negative_UnloadingTheMod_RemovesItsCommand()
        {
            _world.Load("clock", TimeCommand);
            Assert.IsTrue(_commands.TryResolve("time", out IChatCommand _));

            Assert.IsTrue(_world.Unload("clock"));
            _world.Frame();

            Assert.IsFalse(_commands.TryResolve("time", out IChatCommand _), "a mod's commands go with the mod");
            Assert.IsFalse(_commands.TryResolve("t", out IChatCommand _));
        }

        [Test]
        public void Negative_ACommandTheGameRegisteredFirst_Wins()
        {
            _commands.Register(new ChatCommand("time", "The game's own clock.", _ => ChatCommandResult.Handled("game")));
            _world.Load("clock", TimeCommand);

            ChatCommandResult result = Send("/time");
            _world.Frame();

            Assert.AreEqual("game", result.Reply, "the game's command answered");
            Assert.AreEqual("", _world.Get("clock", "text") ?? "", "the mod's Triggered never fired");
            Assert.IsFalse(_commands.TryResolve("t", out IChatCommand _),
                "a command whose primary alias is taken is not added at all");
        }
    }
}
