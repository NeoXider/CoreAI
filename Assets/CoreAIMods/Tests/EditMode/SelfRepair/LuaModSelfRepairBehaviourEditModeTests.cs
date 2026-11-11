using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using CoreAI.Composition;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// What the player gets from the repair loop beyond "it repairs": a paid backend is asked first and nothing is sent
    /// until <c>/fixmod</c>; a fix is described in the model's own words, and a fix that deleted the feature is not
    /// called fixed; each further attempt is told what the one before did; repair turns stay out of the chat history,
    /// which keeps one system-event note; a mod the loop leaves alone while it fails all the time is paused, one that
    /// fails now and then keeps running; offline says one sentence. Each with its twin.
    /// </summary>
    [TestFixture]
    public sealed class LuaModSelfRepairBehaviourEditModeTests
    {
        private const string Broken = "hooks_every(0.05, function() local part = nil; return part.Size end)";
        private const string Fixed = "hooks_every(0.05, function() local part = { Size = 1 }; return part.Size end)";

        [Test]
        public async Task PaidBackend_AsksFirstInOneLine_AndSendsNothingUntilFixmod()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            harness.Cost = LuaModRepairBackendCost.Paid;
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();
            for (int frame = 0; frame < 10; frame++)
            {
                harness.Coordinator.Tick();
                harness.TickRuntime(2);
                harness.Clock += 60d;
            }

            Assert.AreEqual(LuaModRuntimeRepairMode.AskFirst, harness.Coordinator.EffectiveMode);
            Assert.IsEmpty(harness.Model.Requests, "no token is spent without the player asking");
            Assert.AreEqual(1, harness.Lines.Count);
            StringAssert.StartsWith("Mod 'tower' is failing: attempt to index a nil value", harness.Lines[0]);
            StringAssert.Contains("Your AI backend is paid, so nothing is sent to it until you ask.", harness.Lines[0]);
            StringAssert.EndsWith("/fixmod tower repairs it.", harness.Lines[0]);

            harness.Coordinator.RequestRepair("tower");
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count, "/fixmod sends it");
            await harness.Model.Pending;
        }

        [Test]
        public async Task UnknownBackend_AsksFirstToo_AndAnExplicitAutomaticModeRepairsOnAPaidOne()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness unknown = new(null, new LuaModSelfRepairPorts());
            await unknown.LoadThroughToolAsync("tower", Broken);
            unknown.FailUntilIncident();
            unknown.Coordinator.Tick();
            using LuaModSelfRepairLoopEditModeTests.Harness chosen =
                new(new LuaModRuntimeRepairOptions { Mode = LuaModRuntimeRepairMode.Automatic });
            chosen.Cost = LuaModRepairBackendCost.Paid;
            await chosen.LoadThroughToolAsync("tower", Broken);
            chosen.Model.Fix = _ => Fixed;
            chosen.FailUntilIncident();
            chosen.Coordinator.Tick();

            Assert.IsEmpty(unknown.Model.Requests);
            StringAssert.Contains("Your AI backend may be paid", unknown.Lines[0]);
            Assert.AreEqual(1, chosen.Model.Requests.Count, "the host's explicit choice wins over the backend rule");
            await chosen.Model.Pending;
        }

        [Test]
        public async Task FixThatDeletesTheHandler_IsNotCalledFixed()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => "local unused = 1";
            harness.Model.Answer = _ => "Changed: removed the timer that failed.";
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual(LuaModRepairPhase.Fixed, harness.Coordinator.Policy.PhaseOf("tower"), "the errors did stop");
            Assert.AreEqual("'tower' runs without errors now, but the fix removed 1 of its 1 event handler; the AI says: " +
                            "Removed the timer that failed. Check that it still does what you asked. " +
                            "/undo takes back the AI's change.", harness.LastLine);
        }

        [Test]
        public async Task FixWithoutASummary_SaysTheAiDidNotSay_AndAKeptHandlerIsCalledFixed()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.Model.Answer = _ => "";
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual("'tower' is fixed; the AI did not say what it changed (1 line -> 1 line). " +
                            "/undo takes back the AI's change.", harness.LastLine);
        }

        [Test]
        public async Task SecondAttempt_IsToldWhatTheFirstChangedAndThatItStillFails()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = request => request.Attempt == 1 ? "-- first try\n" + Broken : Fixed;
            harness.Model.Answer = request => request.Attempt == 1 ? "Changed: added a comment." : "Changed: built the part.";
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.TickRuntime(2);
            harness.Clock += 11d;
            harness.Coordinator.Tick();

            Assert.AreEqual(2, harness.Model.Requests.Count);
            string second = harness.Model.Requests[1].Text;
            StringAssert.Contains("Runtime repair, attempt 2 of 3", second);
            StringAssert.Contains("previous_attempt: you reloaded it (you said: \"Added a comment.\")", second);
            StringAssert.Contains("but it still fails with the same error, now at tower:2.", second);
            StringAssert.Contains("-- first try", second, "the model sees the fix that failed");
            StringAssert.DoesNotContain("previous_attempt", harness.Model.Requests[0].Text);
            await harness.Model.Pending;
        }

        [Test]
        public async Task AttemptThatDidNotReload_IsToldSoNextTime()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = request => request.Attempt == 1 ? null : Fixed;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.Clock += 11d;
            harness.Coordinator.Tick();

            Assert.AreEqual(2, harness.Model.Requests.Count);
            StringAssert.Contains("previous_attempt: you did not reload the mod", harness.Model.Requests[1].Text);
            await harness.Model.Pending;
        }

        [Test]
        public async Task RepairTurns_StayOutOfTheHistory_AndOneSystemEventNoteRecordsAFix()
        {
            List<string> notes = new();
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new(null, Ports(notes));
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual(AiTaskHistoryMode.None, harness.Model.Requests[0].HistoryMode,
                "the repair report is not stored as a line the player typed");
            Assert.AreEqual(1, notes.Count);
            StringAssert.StartsWith(LuaModSelfRepairCoordinator.HistoryNotePrefix + " mod 'tower' failed", notes[0]);
            StringAssert.Contains("outcome: fixed", notes[0]);
            StringAssert.Contains("Its last change: The handler now builds the part", notes[0]);
            Assert.LessOrEqual(notes[0].Length, LuaModSelfRepairCoordinator.MaximumHistoryNoteCharacters);
        }

        [Test]
        public async Task ThreeFailedAttempts_LeaveOneNote_AndAnIncidentThatChangedNothingLeavesNone()
        {
            List<string> notes = new();
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new(null, Ports(notes));
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = request => "-- try " + request.Attempt + "\n" + Broken;
            harness.FailUntilIncident();
            await RunThreeAttemptsAsync(harness);
            List<string> quiet = new();
            using LuaModSelfRepairLoopEditModeTests.Harness nothing = new(null, Ports(quiet));
            await nothing.LoadThroughToolAsync("tower", Broken);
            nothing.FailUntilIncident();
            await RunThreeAttemptsAsync(nothing);

            Assert.AreEqual(3, harness.Model.Requests.Count);
            Assert.AreEqual(1, notes.Count, "not three copies of an error report the player never typed");
            StringAssert.Contains("outcome: gave up after 3 tries", notes[0]);
            Assert.AreEqual(3, nothing.Model.Requests.Count);
            Assert.IsEmpty(quiet, "the model changed nothing, so there is nothing for it to remember");
        }

        [Test]
        public async Task GaveUpMod_ThatFailsEveryFrame_IsPaused_AndAReloadRunsItAgain()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = PausingHarness();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = request => "-- try " + request.Attempt + "\n" + Broken;
            harness.FailUntilIncident();
            await RunThreeAttemptsAsync(harness);
            harness.TickRuntime(3);
            harness.Coordinator.Tick();

            StringAssert.StartsWith("Could not fix 'tower' after 3 tries. Its code is paused and its objects stay", harness.LastLine);
            StringAssert.Contains("/fixmod tower tries again", harness.LastLine);
            Assert.IsTrue(IsQuarantined(harness, "tower"));
            Assert.IsTrue(harness.Runtime.IsLoaded(harness.Actor, "tower"), "paused, not unloaded");
            int errors = Errors(harness);
            harness.TickRuntime(10);
            Assert.AreEqual(errors, Errors(harness), "a paused mod does not fail any more");

            await harness.Tool.ExecuteAsync("reload", "tower", Fixed);
            Assert.IsFalse(IsQuarantined(harness, "tower"), "a reload brings it back");
        }

        [Test]
        public async Task HandWrittenMod_ThatFailsEveryFrame_IsReportedAndPaused_InOneLine()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = PausingHarness();
            harness.Runtime.LoadMod(harness.Actor, "mine", Broken, LuaCapabilities.All, false);
            harness.FailUntilIncident("mine");
            harness.Coordinator.Tick();
            harness.TickRuntime(10);
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Lines.Count, string.Join(" | ", harness.Lines));
            StringAssert.StartsWith("Mod 'mine' is failing: attempt to index a nil value", harness.Lines[0]);
            StringAssert.EndsWith("The AI did not write it, so the AI leaves it alone. Its code is paused and its objects " +
                                  "stay; saving it in the Hub (Mods) runs it again. /fixmod mine asks the AI to fix it.",
                harness.Lines[0]);
            Assert.IsTrue(IsQuarantined(harness, "mine"));
            await Task.CompletedTask;
        }

        [Test]
        public async Task HandWrittenMod_ThatFailsNowAndThen_IsReported_AndKeepsRunning()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = PausingHarness();
            harness.Runtime.LoadMod(harness.Actor, "clicks",
                "local n = 0\nhooks_every(0.05, function() n = n + 1 end)\n" +
                "hooks_every(2, function() local part = nil; return part.Size end)", LuaCapabilities.All, false);
            for (int frame = 0; frame < 400 && harness.Coordinator.Policy.PhaseOf("clicks") == LuaModRepairPhase.Healthy; frame++)
            {
                harness.TickRuntime(1);
            }

            harness.Coordinator.Tick();
            harness.TickRuntime(60);
            harness.Coordinator.Tick();

            Assert.AreEqual(LuaModRepairTrigger.RepeatedSite, harness.Coordinator.Policy.Get("clicks").Trigger);
            Assert.AreEqual(1, harness.Lines.Count, string.Join(" | ", harness.Lines));
            StringAssert.DoesNotContain("paused", harness.Lines[0]);
            Assert.IsFalse(IsQuarantined(harness, "clicks"), "the rest of it works, so it keeps running");
            await Task.CompletedTask;
        }

        [Test]
        public async Task Offline_PausesAModThatFailsEveryFrame_WithoutASecondChatLine()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = PausingHarness();
            harness.Model.IsModelAvailable = false;
            await harness.LoadThroughToolAsync("a", Broken);
            await harness.LoadThroughToolAsync("b", Broken);
            harness.FailUntilIncident("a");
            harness.FailUntilIncident("b");
            harness.Coordinator.Tick();
            harness.TickRuntime(5);
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Lines.Count, string.Join(" | ", harness.Lines));
            StringAssert.Contains("one that keeps failing is paused and its objects stay", harness.Lines[0]);
            Assert.IsTrue(IsQuarantined(harness, "a"));
            Assert.IsTrue(IsQuarantined(harness, "b"), "the second one is paused too, silently");
        }

        private static LuaModSelfRepairPorts Ports(List<string> notes)
        {
            return new LuaModSelfRepairPorts
            {
                BackendCost = () => LuaModRepairBackendCost.Local,
                HistoryNote = notes.Add
            };
        }

        private static LuaModSelfRepairLoopEditModeTests.Harness PausingHarness()
        {
            LuaModSelfRepairPorts ports = new() { BackendCost = () => LuaModRepairBackendCost.Local };
            LuaModSelfRepairLoopEditModeTests.Harness harness = new(null, ports);
            ActorContext actor = harness.Actor;
            LuaCsModRuntime runtime = harness.Stack.Runtime;
            ports.Pause = (id, reason) => runtime.TryQuarantine(actor, id, reason);
            return harness;
        }

        private static async Task RunThreeAttemptsAsync(LuaModSelfRepairLoopEditModeTests.Harness harness)
        {
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
        }

        private static bool IsQuarantined(LuaModSelfRepairLoopEditModeTests.Harness harness, string id)
        {
            foreach (LuaModInfo info in harness.Runtime.ListMods(harness.Actor))
            {
                if (info != null && info.Id == id)
                {
                    return info.Quarantined;
                }
            }

            return false;
        }

        private static int Errors(LuaModSelfRepairLoopEditModeTests.Harness harness)
        {
            return harness.Logs.Query(new LuaLogQuery { ModId = "tower", MaxCount = 1000 }).Count;
        }
    }
}
