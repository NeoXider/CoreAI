using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// The repair loop's two wordings: the chat names a failing mod by its header name in one short line (it stopped
    /// working and the AI is fixing it, it was paused and everything else keeps going, it could not be fixed this time),
    /// and the error, the mod id and the console and Hub hints go to the mod log only. The negative twin: the detailed
    /// line is still in the log and in <see cref="LuaModSelfRepairCoordinator.PlayerLinePosted"/>, never in the chat.
    /// </summary>
    [TestFixture]
    public sealed class LuaModSelfRepairChatWordingEditModeTests
    {
        private const string Header = "--[[@coreai\nid: tower\nname: Castle 2 - Tower\n]]\n";
        private const string Broken = "hooks_every(0.05, function() local part = nil; return part.Size end)";
        private const string Fixed = "hooks_every(0.05, function() local part = { Size = 1 }; return part.Size end)";

        [Test]
        public async Task AFailingMod_TheChatNamesItByItsHeaderName_AndSaysTheAiIsFixingIt()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Header + Broken);
            harness.FailUntilIncident();

            CollectionAssert.AreEqual(new[] { "Something in this world stopped working (Tower). The AI is fixing it." },
                harness.Chat.Posted);
            StringAssert.StartsWith("Mod 'tower' is failing: attempt to index a nil value", harness.Lines[0],
                "the detailed line still goes to the listeners");
        }

        [Test]
        public async Task TheRepairsAnnouncement_AndTheFixedLine_NameTheModToo_TheLogKeepsItsId()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Header + Broken);
            harness.Model.Fix = _ => Header + Fixed;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.TickRuntime(5);
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual(LuaModRepairPhase.Fixed, harness.Coordinator.Policy.PhaseOf("tower"));
            CollectionAssert.Contains(harness.Chat.Posted, "AI is fixing 'Tower' (try 1 of 3). /stop cancels.");
            StringAssert.StartsWith("'Tower' is fixed; the AI says:", harness.Chat.Posted[harness.Chat.Posted.Count - 1]);
            StringAssert.StartsWith("'tower' is fixed; the AI says:", harness.LastLine, "the log keeps the id");
        }

        [Test]
        public async Task NegativeTwin_TheErrorAndTheConsoleAndHubHints_StayInTheModLog_NotInTheChat()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            harness.Model.IsModelAvailable = false;
            await harness.LoadThroughToolAsync("tower", Header + Broken);
            harness.FailUntilIncident();
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Chat.Posted.Count, string.Join(" | ", harness.Chat.Posted));
            string chat = harness.Chat.Posted[0];
            StringAssert.StartsWith("Something in this world stopped working (Tower)", chat);
            StringAssert.EndsWith("Everything else keeps going.", chat);
            foreach (string developerText in new[] { "attempt to index", "'tower'", "offline", "/console", "Hub" })
            {
                StringAssert.DoesNotContain(developerText, chat, "the chat line carries no developer text");
            }

            IReadOnlyList<LuaLogEntry> repair = harness.Logs.Query(new LuaLogQuery { ModId = "tower" })
                .FindAllAsList(entry => entry.ScriptName == LuaModSelfRepairCoordinator.RepairScriptName);
            Assert.AreEqual(1, repair.Count);
            StringAssert.StartsWith("Mod 'tower' is failing: attempt to index a nil value", repair[0].Message);
            StringAssert.Contains("The AI is offline", repair[0].Message);
            StringAssert.Contains("The mod console (/console) lists them; fix them in the Hub (Mods).", repair[0].Message);
        }

        [Test]
        public async Task AModTheAiCouldNotFix_IsPaused_AndTheChatSaysSoInOneShortLine()
        {
            LuaModSelfRepairPorts ports = new() { BackendCost = () => LuaModRepairBackendCost.Local };
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new(null, ports);
            ActorContext actor = harness.Actor;
            LuaCsModRuntime runtime = harness.Stack.Runtime;
            ports.Pause = (id, reason) => runtime.TryQuarantine(actor, id, reason);
            await harness.LoadThroughToolAsync("tower", Header + Broken);
            harness.Model.Fix = request => Header + "-- try " + request.Attempt + "\n" + Broken;
            harness.FailUntilIncident();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                harness.Coordinator.Tick();
                await harness.Model.Pending;
                harness.Coordinator.Tick();
                harness.TickRuntime(2);
                harness.Clock += 31d;
            }

            harness.TickRuntime(2);
            harness.Coordinator.Tick();
            harness.TickRuntime(3);
            harness.Coordinator.Tick();

            StringAssert.StartsWith("Could not fix 'tower' after 3 tries. Its code is paused", harness.LastLine,
                "the log keeps the detailed line");
            Assert.AreEqual("Tower is paused; it could not be fixed this time.",
                harness.Chat.Posted[harness.Chat.Posted.Count - 1]);
            CollectionAssert.Contains(harness.Chat.Posted, "The AI is still fixing Tower.");
            foreach (string line in harness.Chat.Posted)
            {
                StringAssert.DoesNotContain("attempt to index", line, "no error text in the chat");
                StringAssert.DoesNotContain("Hub (Mods)", line, "no Hub hint in the chat");
            }
        }

        [Test]
        public void PlayerNameOf_LeavesOutASeriesPrefix_AndFallsBackToTheId()
        {
            Assert.AreEqual("Falling blocks", LuaModSelfRepairCoordinator.PlayerNameOf("Block World 9 - Falling blocks", "x"));
            Assert.AreEqual("Tower", LuaModSelfRepairCoordinator.PlayerNameOf("Tower", "tower"));
            Assert.AreEqual("Sky - Night", LuaModSelfRepairCoordinator.PlayerNameOf("Mods - Sky - Night", "x"),
                "only the first prefix is left out");
            Assert.AreEqual("tower", LuaModSelfRepairCoordinator.PlayerNameOf("  ", "tower"), "no name: the id");
            Assert.AreEqual("Tower -", LuaModSelfRepairCoordinator.PlayerNameOf("Tower -", "tower"),
                "a dash with nothing after it is part of the name");
        }
    }
}
