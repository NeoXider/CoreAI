using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The status of a turn as the tracker keeps it: what the model is writing when it composes a tool call, and
    /// how much it has produced so far. A turn that writes a long script used to read "thinking" from start to end.
    /// </summary>
    [TestFixture]
    public sealed class AgentStatusTrackerEditModeTests
    {
        [Test]
        public void ToolComposing_SaysWhatIsBeingWritten_ThenTheToolRuns()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);

            status.ToolComposing("execute_lua", 512);

            Assert.AreEqual(AgentStatusKind.ComposingTool, status.Kind);
            Assert.AreEqual("execute_lua", status.ToolName);
            Assert.AreEqual(512, status.ComposingCharacters);
            Assert.AreEqual("writing a script", status.Format(0d));

            status.ToolStarted("execute_lua");
            Assert.AreEqual(AgentStatusKind.RunningTool, status.Kind);
            Assert.AreEqual(0, status.ComposingCharacters, "the size belongs to the composing state only");

            status.ToolCompleted("execute_lua");
            Assert.AreEqual(AgentStatusKind.Thinking, status.Kind);
        }

        [Test]
        public void ToolComposing_WithoutAConfiguredText_UsesTheFormat()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);

            status.ToolComposing("world_command", 300);
            Assert.AreEqual("preparing world_command", status.Format(0d));

            status.ToolComposing("", 600);
            Assert.AreEqual("preparing tool", status.Format(0d), "a provider may send the name late");
        }

        [Test]
        public void ToolComposing_DoesNotHideARunningToolOrARetry()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);
            status.ToolStarted("get_mod_logs");

            status.ToolComposing("execute_lua", 400);
            Assert.AreEqual(AgentStatusKind.RunningTool, status.Kind);

            status.ToolFailed("get_mod_logs");
            status.ToolComposing("execute_lua", 800);
            Assert.AreEqual(AgentStatusKind.Retrying, status.Kind);
            Assert.AreEqual(200, status.OutputTokens, "what arrived is counted whatever the line shows");
        }

        [Test]
        public void ToolComposing_WhileIdle_IsIgnored()
        {
            AgentStatusTracker status = new();

            status.ToolComposing("execute_lua", 400);
            status.AddStreamedCharacters(400);

            Assert.AreEqual(AgentStatusKind.Idle, status.Kind);
            Assert.AreEqual(0, status.OutputTokens);
        }

        [Test]
        public void OutputTokens_CountEverythingThatArrived_AndShowAfterTheElapsedTime()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);

            status.AddStreamedCharacters(400);
            status.ToolComposing("execute_lua", 4000);

            Assert.AreEqual(1100, status.OutputTokens);
            Assert.AreEqual("writing a script", status.Format(0.5d), "no elapsed time yet, so no count either");
            Assert.AreEqual("writing a script · 12s · ~1.1k tokens", status.Format(12d));
        }

        [Test]
        public void OutputTokens_KeepWhatAnEarlierResponseWrote()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);
            status.ToolComposing("execute_lua", 2000);
            status.ToolStarted("execute_lua");
            status.ToolCompleted("execute_lua");

            status.ToolComposing("execute_lua", 400);

            Assert.AreEqual(600, status.OutputTokens, "the next response counts from zero, on top of the first");
        }

        [Test]
        public void OutputTokens_StartAgainWithTheNextTurn_AndCanBeHidden()
        {
            ChatOverlayOptions options = new() { OutputTokensFormat = "" };
            AgentStatusTracker status = new(options);
            status.BeginTurn(0d);
            status.AddStreamedCharacters(4000);
            Assert.AreEqual("thinking · 5s", status.Format(5d));

            status.EndTurn();
            Assert.AreEqual(1000, status.OutputTokens, "kept for whoever reads it after the turn");

            status.BeginTurn(10d);
            Assert.AreEqual(0, status.OutputTokens);
        }

        [Test]
        public void Version_FollowsTheShownCount_NotEveryChunk()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);
            status.AddStreamedCharacters(400);
            int version = status.Version;

            status.AddStreamedCharacters(4);
            Assert.AreEqual(version, status.Version, "101 tokens still reads 100");

            status.AddStreamedCharacters(40);
            Assert.Greater(status.Version, version, "111 tokens reads 110");
        }

        [TestCase(0, "0")]
        [TestCase(4, "10")]
        [TestCase(352, "350")]
        [TestCase(355, "360")]
        [TestCase(996, "1.0k")]
        [TestCase(1234, "1.2k")]
        [TestCase(12345, "12.3k")]
        public void FormatTokens_RoundsAnEstimate(int tokens, string expected)
        {
            Assert.AreEqual(expected, AgentStatusTracker.FormatTokens(tokens));
        }

        [Test]
        public void BackgroundDetail_ShowsWhileIdle_AndATurnShowsItsOwnStatus()
        {
            AgentStatusTracker status = new();
            int version = status.Version;

            status.SetBackgroundDetail("builder-1 working");
            Assert.AreEqual("builder-1 working", status.Format(0d));
            Assert.IsFalse(status.IsActive, "background work is not a turn");
            Assert.Greater(status.Version, version);

            status.BeginTurn(1d);
            Assert.AreEqual("thinking", status.Format(1d));

            status.EndTurn();
            Assert.AreEqual("builder-1 working", status.Format(2d), "a turn does not clear it");

            status.SetBackgroundDetail("");
            Assert.AreEqual("", status.Format(3d));
        }

        [Test]
        public void Detail_ReplacesTheComposingText_AsItDoesThinking()
        {
            AgentStatusTracker status = new();
            status.BeginTurn(0d);
            status.SetDetail("builder: walls 2/5");

            status.ToolComposing("execute_lua", 400);

            Assert.AreEqual("builder: walls 2/5", status.Format(0d));
        }
    }
}
