using CoreAI.Ai;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The built-in commands /help, /agent, /model and /stop, run through the chat as the player types them,
    /// and the rule that a game's own command keeps its name.
    /// </summary>
    [TestFixture]
    public sealed class ChatOverlayBuiltInCommandsEditModeTests
    {
        [Test]
        public void Help_ListsEveryRegisteredCommand()
        {
            using ChatOverlayHarness harness = new();
            harness.Registry.Register(new ChatCommand("fly", "Toggle flight.", _ => ChatCommandResult.Handled()));

            harness.Controller.Submit("/help");

            ChatFeedLine reply = harness.LastLine;
            Assert.AreEqual(ChatFeedLineKind.Command, reply.Kind);
            StringAssert.Contains("/agent [role]", reply.Text);
            StringAssert.Contains("/model", reply.Text);
            StringAssert.Contains("/stop", reply.Text);
            StringAssert.Contains("/fly - Toggle flight.", reply.Text);
            Assert.AreEqual(0, harness.Sender.Requests.Count);
        }

        [Test]
        public void Help_ForOneCommand_DescribesItWithAliases()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/? /help");

            StringAssert.Contains("/help [command]", harness.LastLine.Text);
            StringAssert.Contains("Aliases: /?", harness.LastLine.Text);
        }

        [Test]
        public void Help_ForUnknownCommand_Fails()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/help nope");

            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
        }

        [Test]
        public void Agent_SwitchesTheRoleOfTheNextMessage()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/agent programmer");
            harness.Controller.Submit("put a red block in front of me");

            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, harness.Agents.ActiveRoleId);
            Assert.AreEqual("Agent: Programmer.", harness.Controller.Feed.Lines[0].Text);
            Assert.AreEqual(1, harness.Sender.Requests.Count, "the command itself sends nothing");
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, harness.Sender.Requests[0].RoleId);
        }

        [Test]
        public void Agent_UnknownRole_FailsAndKeepsTheRole()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/agent Wizard");

            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, harness.Agents.ActiveRoleId);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            StringAssert.Contains("Programmer", harness.LastLine.Text, "the reply lists what is available");
        }

        [Test]
        public void Agent_WithoutArgument_ShowsTheCurrentRole()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/agent");

            StringAssert.StartsWith("Agent: SmartChat.", harness.LastLine.Text);
        }

        [Test]
        public void Model_AssignsAProfileByIdOrName_ToTheActiveRole()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/model fast");
            Assert.AreEqual("fast", harness.Models.Assigned[BuiltInAgentRoleIds.SmartChat]);

            harness.Controller.Submit("/agent Programmer");
            harness.Controller.Submit("/model Cloud smart");
            Assert.AreEqual("smart", harness.Models.Assigned[BuiltInAgentRoleIds.Programmer]);
            Assert.AreEqual("Model for Programmer: Cloud smart.", harness.LastLine.Text);
            Assert.AreEqual(0, harness.Sender.Requests.Count);
        }

        [Test]
        public void Model_Auto_ClearsTheAssignment()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("/model fast");

            harness.Controller.Submit("/model auto");

            Assert.AreEqual("", harness.Models.Assigned[BuiltInAgentRoleIds.SmartChat]);
            Assert.AreEqual("Model for SmartChat: automatic.", harness.LastLine.Text);
        }

        [Test]
        public void Model_UnknownProfile_FailsAndAssignsNothing()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/model gpt-9");

            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            CollectionAssert.IsEmpty(harness.Models.Assigned);
        }

        [Test]
        public void Model_WithoutRouting_SaysItIsUnavailable()
        {
            using ChatOverlayHarness harness = new();
            harness.Models.IsAvailable = false;

            harness.Controller.Submit("/model fast");

            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            StringAssert.Contains("not available", harness.LastLine.Text);
        }

        [Test]
        public void Model_WithoutArgument_ShowsTheCurrentProfileAndTheChoices()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/model");

            StringAssert.StartsWith("Model for SmartChat: automatic.", harness.LastLine.Text);
            StringAssert.Contains("fast (Fast local, Ready)", harness.LastLine.Text);
        }

        [Test]
        public void Stop_WhileAnswering_StopsTheTurn()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a castle");

            harness.Controller.Submit("/stop");

            Assert.IsFalse(harness.Controller.IsTurnActive);
            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls);
            Assert.AreEqual(harness.Options.StoppedText, harness.LastLine.Text);
            Assert.AreEqual(1, harness.Sender.Requests.Count);
        }

        [Test]
        public void Stop_WhenIdle_SaysNothingToStop()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/stop");

            Assert.AreEqual("Nothing to stop.", harness.LastLine.Text);
            CollectionAssert.IsEmpty(harness.Sender.StoppedRoles);
        }

        [Test]
        public void RegisterAll_KeepsACommandTheGameRegisteredFirst()
        {
            ChatCommandRegistry registry = new();
            ChatCommand gameHelp = new("help", "The game's help.", _ => ChatCommandResult.Handled("game help"));
            registry.Register(gameHelp);
            ChatAgentSelector agents = new(BuiltInAgentRoleIds.SmartChat);

            using (ChatOverlayBuiltInCommands.RegisterAll(registry, agents, null, null))
            {
                Assert.IsTrue(registry.TryResolve("help", out IChatCommand help));
                Assert.AreSame(gameHelp, help);
                Assert.IsTrue(registry.TryResolve("agent", out _));
                Assert.IsFalse(registry.TryResolve("model", out _), "no routing seam, no /model");
                Assert.IsFalse(registry.TryResolve("stop", out _), "no turn control, no /stop");
            }

            Assert.IsFalse(registry.TryResolve("agent", out _), "disposing removes the built-ins");
            Assert.IsTrue(registry.TryResolve("help", out _), "and leaves the game's command");
        }

        [Test]
        public void Game_CanRemoveABuiltIn_WhichThenAnswersAsUnknown()
        {
            using ChatOverlayHarness harness = new();

            harness.Registry.Unregister("model");
            harness.Controller.Submit("/model fast");

            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            StringAssert.StartsWith("Unknown command /model", harness.LastLine.Text);
            CollectionAssert.IsEmpty(harness.Models.Assigned);
        }

        [Test]
        public void LaterPackage_RegistersAndRemovesItsOwnCommand()
        {
            using ChatOverlayHarness harness = new();
            bool flying = false;
            System.IDisposable fly = harness.Registry.Register(new ChatCommand("fly", "Toggle flight.", _ =>
            {
                flying = !flying;
                return ChatCommandResult.Handled(flying ? "Flying." : "Walking.");
            }));

            harness.Controller.Submit("/fly");
            Assert.IsTrue(flying);
            Assert.AreEqual("Flying.", harness.LastLine.Text);

            fly.Dispose();
            harness.Controller.Submit("/fly");
            Assert.IsTrue(flying, "a removed command no longer runs");
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
        }

        [Test]
        public void AsyncCommand_PostsItsLateReplyThroughTheContext()
        {
            using ChatOverlayHarness harness = new();
            IChatOverlayMessages captured = null;
            harness.Registry.Register(new ChatCommand("undo", "Undo the last change.", ctx =>
            {
                captured = ctx.Messages;
                return ChatCommandResult.Handled("Undoing...");
            }));

            harness.Controller.Submit("/undo");
            captured.PostSystemMessage("Restored the world from before the last AI change.");

            Assert.AreEqual("Restored the world from before the last AI change.", harness.LastLine.Text);
        }
    }
}
