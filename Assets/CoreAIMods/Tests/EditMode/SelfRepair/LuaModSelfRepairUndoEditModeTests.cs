using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// A repair belongs to the change it repairs: on the production world session, the write-behind gate and the real
    /// undo history, the AI's automatic repair joins the history entry of the request that wrote the failing source, so
    /// one <c>/undo</c> takes back the request with every repair of it and <c>/redo</c> puts back the repaired world.
    /// Twins: a repair with another change in between stays its own entry named as a repair; a repair the player asked
    /// for on a mod the AI did not write is its own entry; a closed window records an ordinary change. Undoing never
    /// starts a repair: a version an undo put back is reported and paused, not rewritten, and a repair that runs while
    /// the player undoes stops with one line.
    /// </summary>
    [TestFixture]
    public sealed class LuaModSelfRepairUndoEditModeTests
    {
        private const string Broken =
            "local top = Instance.new('Part') top.Name = 'TowerTop' top.Parent = workspace\n" +
            "hooks_every(0.05, function() local part = nil; return part.Size end)";

        private const string Fixed =
            "local top = Instance.new('Part') top.Name = 'TowerTop' top.Parent = workspace\n" +
            "hooks_every(0.05, function() local part = { Size = 1 }; return part.Size end)";

        private const string BuildFloor =
            "local p = Instance.new('Part') p.Name = 'Floor' p.Anchored = true p.Parent = workspace return true";

        private const string BuildWall =
            "local p = Instance.new('Part') p.Name = 'Wall' p.Anchored = true p.Parent = workspace return true";

        [Test]
        public async Task Window_JoinsTheChangeItRepairs_SoOneUndoTakesBackBoth_AndRedoPutsBackTheRepairedWorld()
        {
            using SessionHarness harness = new();
            byte[] empty = P3Fixtures.TimelessPackage(harness.Session.Controller.CaptureCurrent());
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);
            long requestSequence = harness.LastSequence;

            using (IRbxWorldRepairWindow window = harness.Session.Undo.BeginRepair(harness.Actor.ActorId, requestSequence, "floor"))
            {
                Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);
                Assert.AreEqual(RbxWorldRepairRecording.JoinedRepairedChange, window.Recording);
            }

            byte[] repaired = P3Fixtures.TimelessPackage(harness.Session.Controller.CaptureCurrent());
            Assert.AreEqual(1, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount, "one entry for both");

            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);

            Assert.IsTrue(undone.Success, undone.Message);
            StringAssert.EndsWith("was undone, together with its repair.", undone.Message);
            CollectionAssert.AreEqual(empty, P3Fixtures.TimelessPackage(harness.Session.Controller.CaptureCurrent()),
                "the request and its repair went in one step");
            RbxWorldUndoResult redone = await harness.Session.Undo.RedoAsync(harness.Actor);
            Assert.IsTrue(redone.Success, redone.Message);
            StringAssert.EndsWith("was put back, together with its repair.", redone.Message);
            CollectionAssert.AreEqual(repaired, P3Fixtures.TimelessPackage(harness.Session.Controller.CaptureCurrent()),
                "redo puts back the repaired world");
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Window_WithAnotherChangeInBetween_StaysItsOwnEntry_NamedAsARepair()
        {
            using SessionHarness harness = new();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);
            long requestSequence = harness.LastSequence;
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);

            using (IRbxWorldRepairWindow window = harness.Session.Undo.BeginRepair(harness.Actor.ActorId, requestSequence, "floor"))
            {
                Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor,
                    "local p = Instance.new('Part') p.Name = 'Patch' p.Parent = workspace return true")).Success);
                Assert.AreEqual(RbxWorldRepairRecording.OwnEntry, window.Recording);
            }

            Assert.AreEqual(3, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount);
            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            Assert.AreEqual("The AI's repair of mod 'floor' was undone.", undone.Message);
            Assert.IsNotNull(harness.Root.FindFirstChild("Wall"), "the change in between is still there");
            RbxWorldUndoResult next = await harness.Session.Undo.UndoAsync(harness.Actor);
            StringAssert.DoesNotContain("repair", next.Message);
            Assert.IsNull(harness.Root.FindFirstChild("Wall"));
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Window_AfterAWorldEditOutsideTheGate_StaysItsOwnEntry()
        {
            using SessionHarness harness = new();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);
            long requestSequence = harness.LastSequence;
            RbxInstance playerMade = harness.Session.Controller.CurrentRbxApi.Registry.Create("Folder");
            playerMade.Name = "PlayerMade";
            playerMade.Parent = harness.Root;

            using IRbxWorldRepairWindow window = harness.Session.Undo.BeginRepair(harness.Actor.ActorId, requestSequence, "floor");
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);

            Assert.AreEqual(RbxWorldRepairRecording.OwnEntry, window.Recording,
                "joining would make one undo also take back the player's edit");
            Assert.AreEqual(2, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount);
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Window_WithNoParent_IsItsOwnEntry_AndTwoChangesOfOneWindowShareIt()
        {
            using SessionHarness harness = new();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);

            using (IRbxWorldRepairWindow window = harness.Session.Undo.BeginRepair(harness.Actor.ActorId, 0L, "mine"))
            {
                Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);
                Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor,
                    "local p = Instance.new('Part') p.Name = 'Patch' p.Parent = workspace return true")).Success);
                Assert.AreEqual(RbxWorldRepairRecording.OwnEntry, window.Recording);
            }

            Assert.AreEqual(2, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount,
                "the request, and the player's repair as one entry of its own");
            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            Assert.AreEqual("The AI's repair of mod 'mine' was undone.", undone.Message);
            Assert.IsNull(harness.Root.FindFirstChild("Wall"));
            Assert.IsNull(harness.Root.FindFirstChild("Patch"));
            Assert.IsNotNull(harness.Root.FindFirstChild("Floor"));
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task ClosedWindow_AndAnotherActorsWindow_RecordAnOrdinaryChange()
        {
            using SessionHarness harness = new();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);
            long requestSequence = harness.LastSequence;
            IRbxWorldRepairWindow closed = harness.Session.Undo.BeginRepair(harness.Actor.ActorId, requestSequence, "floor");
            closed.Dispose();
            using IRbxWorldRepairWindow other = harness.Session.Undo.BeginRepair("someone-else", requestSequence, "floor");

            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);

            Assert.AreEqual(RbxWorldRepairRecording.None, closed.Recording);
            Assert.AreEqual(RbxWorldRepairRecording.None, other.Recording);
            Assert.AreEqual(2, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount);
            StringAssert.DoesNotContain("repair", (await harness.Session.Undo.UndoAsync(harness.Actor)).Message);
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task AutomaticRepair_JoinsTheRequest_OneUndoRemovesBoth_AndNoRepairFollows()
        {
            using SessionHarness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident("tower");
            await harness.RepairOnceAsync();
            harness.TickRuntime(5);
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            StringAssert.StartsWith("'tower' is fixed; the AI says:", harness.LastLine);
            StringAssert.EndsWith("/undo takes back your request together with this fix.", harness.LastLine);
            Assert.AreEqual(1, harness.Session.Undo.GetStatus(harness.Actor.ActorId).UndoCount,
                "the repair joined the request that loaded the mod");

            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsFalse(harness.Session.Controller.Runtime.IsLoaded(harness.Actor, "tower"),
                "one /undo took back the request that made the mod, repair included");
            harness.TickRuntime(20);
            harness.Clock += 200d;
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count, "undoing never starts a repair");

            RbxWorldUndoResult redone = await harness.Session.Undo.RedoAsync(harness.Actor);
            Assert.IsTrue(redone.Success, redone.Message);
            Assert.IsTrue(harness.Session.Controller.Runtime.TryGetModSource(harness.Actor, "tower", out string source));
            Assert.AreEqual(Fixed, source, "redo puts back the repaired version");
            harness.TickRuntime(20);
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count);
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task RepairOfItsOwn_UndoneAlone_PutsBackTheBrokenVersion_WhichIsPausedAndNotRepaired()
        {
            using SessionHarness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success,
                "another change of the same request sits between the mod and its repair");
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident("tower");
            await harness.RepairOnceAsync();
            harness.TickRuntime(5);
            harness.Clock += 6d;
            harness.Coordinator.Tick();
            StringAssert.EndsWith("/undo takes back this fix.", harness.LastLine);
            harness.Lines.Clear();

            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            Assert.AreEqual("The AI's repair of mod 'tower' was undone.", undone.Message);
            Assert.IsTrue(harness.Session.Controller.Runtime.TryGetModSource(harness.Actor, "tower", out string source));
            Assert.AreEqual(Broken, source);
            Assert.IsTrue(harness.Coordinator.Authorship.IsPutBackByPlayer("tower"));
            harness.FailUntilIncident("tower");
            harness.Coordinator.Tick();
            harness.TickRuntime(10);
            harness.Clock += 200d;
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count, "the version the player put back is not rewritten");
            Assert.AreEqual(1, harness.Lines.Count, harness.LinesText);
            StringAssert.Contains("It is the version your /undo or /redo put back, so the AI leaves it alone.", harness.Lines[0]);
            StringAssert.Contains("Its code is paused and its objects stay", harness.Lines[0]);
            StringAssert.EndsWith("/fixmod tower asks the AI to fix it.", harness.Lines[0]);
            Assert.IsTrue(harness.IsQuarantined("tower"), "the bleeding stops");
            Assert.IsNotNull(FindAnywhere(harness, "TowerTop"),
                "its objects stay");

            Assert.AreEqual("Queued 'tower' for repair.", harness.Coordinator.RequestRepair("tower"));
            harness.Coordinator.Tick();
            Assert.AreEqual(2, harness.Model.Requests.Count, "/fixmod still repairs it");
            await harness.Model.Pending;
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task UndoWhileARepairRuns_StopsIt_WithOneLine_AndNoFurtherAttempt()
        {
            using SessionHarness harness = new();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildFloor)).Success);
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.HoldTurns = true;
            harness.FailUntilIncident("tower");
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count);
            harness.Lines.Clear();

            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            Assert.IsTrue(undone.Success, undone.Message);
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Cancels, "the running turn is stopped");
            harness.Model.ReleaseHeld(LuaModRepairTurnEnd.Cancelled);
            harness.Coordinator.Tick();
            harness.Clock += 200d;
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count);
            Assert.AreEqual(1, harness.Lines.Count, harness.LinesText);
            StringAssert.StartsWith("Stopped repairing 'tower': your /undo or /redo took that change back", harness.Lines[0]);
            Assert.AreEqual(LuaModRepairPhase.Healthy, harness.Coordinator.Policy.PhaseOf("tower"));
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task UndoOfAnUnrelatedChange_LeavesAFixedModAlone_AndSaysNothing()
        {
            using SessionHarness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident("tower");
            await harness.RepairOnceAsync();
            harness.TickRuntime(5);
            harness.Clock += 6d;
            harness.Coordinator.Tick();
            Assert.IsTrue((await harness.Session.ExecuteAsAsync(harness.Actor, BuildWall)).Success);
            harness.Lines.Clear();

            RbxWorldUndoResult undone = await harness.Session.Undo.UndoAsync(harness.Actor);
            harness.TickRuntime(10);
            harness.Coordinator.Tick();

            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsEmpty(harness.Lines);
            Assert.IsFalse(harness.Coordinator.Authorship.IsPutBackByPlayer("tower"),
                "the undo restarted the mod with the same source; it did not put back another version");
            await harness.Session.Gate.WaitForPendingBackupAsync();
        }

        private static RbxInstance FindAnywhere(SessionHarness harness, string name)
        {
            foreach (RbxInstance instance in harness.Session.Controller.CurrentRbxApi.Registry.GetLiveInstances())
            {
                if (instance != null && instance.Name == name)
                {
                    return instance;
                }
            }

            return null;
        }

        /// <summary>The production world session with the real tool, the real undo history and a scripted model.</summary>
        private sealed class SessionHarness : IDisposable, IRbxWorldMutationObserver
        {
            public SessionHarness()
            {
                Session = new P3HeadlessSession(new P3MemoryPackageStore(), new P3MemorySourceStore());
                Session.Gate.AddObserver(this);
                IActorIdentityProvider identity = CoreServicesInstaller.DefaultLocalHostIdentityProvider;
                Actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
                Tool = new LuaModsLlmTool(Session.Controller.Runtime, new CoreAISettingsOptions(), NullLog.Instance,
                    P3Fixtures.SessionCapabilities, true, identity, BuiltInAgentRoleIds.Programmer, Session.Gate);
                Model = new LuaModSelfRepairLoopEditModeTests.ScriptedRepairModel(Tool);
                LuaModAuthorshipTracker authorship = new();
                Session.Gate.AddObserver(authorship);
                Coordinator = new LuaModSelfRepairCoordinator(Session.Controller.Runtime, Actor, null, authorship, Model,
                    null, null, null, () => Clock, null, null, new LuaModSelfRepairPorts
                    {
                        BackendCost = () => LuaModRepairBackendCost.Local,
                        Undo = Session.Undo,
                        Pause = (id, reason) => Session.Controller.CurrentConcreteRuntime.TryQuarantine(Actor, id, reason)
                    });
                Coordinator.PlayerLinePosted += (_, line) =>
                {
                    if (!line.StartsWith("AI is fixing", StringComparison.Ordinal))
                    {
                        Lines.Add(line);
                    }
                };
            }

            public P3HeadlessSession Session { get; }

            public ActorContext Actor { get; }

            public LuaModsLlmTool Tool { get; }

            public LuaModSelfRepairLoopEditModeTests.ScriptedRepairModel Model { get; }

            public LuaModSelfRepairCoordinator Coordinator { get; }

            public List<string> Lines { get; } = new();

            public string LastLine => Lines.Count == 0 ? "" : Lines[Lines.Count - 1];

            public string LinesText => string.Join(" | ", Lines);

            public double Clock { get; set; } = 1000d;

            public long LastSequence { get; private set; }

            public RbxInstance Root => Session.Controller.CurrentRbxApi.Registry.WorldRoot;

            public async Task LoadThroughToolAsync(string modId, string code)
            {
                string json = await Tool.ExecuteAsync("load", modId, code);
                StringAssert.Contains("\"success\":true", json);
            }

            public async Task RepairOnceAsync()
            {
                Coordinator.Tick();
                await Model.Pending;
                Coordinator.Tick();
            }

            public void TickRuntime(int frames)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    Clock += 0.1d;
                    Session.Controller.Runtime.Tick(Actor, 0.1d);
                }
            }

            public void FailUntilIncident(string modId)
            {
                for (int frame = 0; frame < 20 && Coordinator.Policy.PhaseOf(modId) == LuaModRepairPhase.Healthy; frame++)
                {
                    TickRuntime(1);
                }

                Assert.AreNotEqual(LuaModRepairPhase.Healthy, Coordinator.Policy.PhaseOf(modId),
                    "the failing mod opened an incident");
            }

            public bool IsQuarantined(string modId)
            {
                foreach (LuaModInfo info in Session.Controller.Runtime.ListMods(Actor))
                {
                    if (info != null && info.Id == modId)
                    {
                        return info.Quarantined;
                    }
                }

                return false;
            }

            public void OnMutationStarting(RbxWorldMutationInfo mutation)
            {
                LastSequence = mutation.Sequence;
            }

            public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
            {
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                Model.ReleaseHeld();
                Session.Dispose();
            }
        }
    }
}
