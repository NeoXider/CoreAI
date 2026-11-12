using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Undo and the startup record of AI edits that change only state without a revision of its own: the Lighting
    /// day cycle, StarterPlayer settings, PointLight and GUI properties. Such an edit made by a gated call is an AI
    /// change (recorded, undoable, recorded for the next start); the same state changed every frame by a script
    /// outside the gate (a day cycle) neither records an entry nor refuses an undo nor writes a startup record.
    /// Engine-free, over the production session controller and gate.
    /// </summary>
    [TestFixture]
    public sealed class WorldUndoRevisionFreeStateEditModeTests
    {
        private const string MakeItNight = "game:GetService('Lighting').ClockTime = 0 return true";

        private const string BuildTower =
            "for i = 1, 3 do local p = Instance.new('Part') p.Name = 'Tower' .. i p.Size = Vector3.new(2, 2, 2) "
            + "p.Position = Vector3.new(0, i * 2, 0) p.Anchored = true p.Parent = workspace end return true";

        private static ActorContext Actor(string id)
        {
            return new LocalActorIdentityProvider(id).GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static RbxLighting Lighting(P3HeadlessSession session)
        {
            return (RbxLighting)session.Controller.CurrentRbxApi.Registry.WorldRoot.Parent.FindFirstChildOfClass("Lighting");
        }

        /// <summary>
        /// Writes a column of rock at cell column (<paramref name="x"/>, 0) outside the gate, as a world generator
        /// (<paramref name="generated"/>, marked with the Workspace attribute around the write) or as a player.
        /// </summary>
        private static void WriteTerrain(P3HeadlessSession session, int x, bool generated)
        {
            RbxInstance workspace = session.Controller.CurrentRbxApi.Registry.WorldRoot;
            RbxTerrain terrain = RbxTerrainBootstrap.Find(workspace);
            Assert.IsNotNull(terrain, "the headless world has a Terrain");
            Assert.IsTrue(TerrainMaterials.TryGetSlot("Rock", out byte rock));
            if (generated)
            {
                workspace.SetAttribute(RbxWorldRuntimeSessionController.TerrainGeneratingAttribute, true);
            }

            terrain.Store.FillBox(new TerrainCellBox(x, -4, 0, 1, 5, 1), rock, TerrainOccupancy.Full);
            if (generated)
            {
                workspace.SetAttribute(RbxWorldRuntimeSessionController.TerrainGeneratingAttribute, false);
            }
        }

        /// <summary>"Make it night" changes nothing but Lighting, and is one AI change an undo takes back.</summary>
        [Test]
        public async Task Undo_AnAiEditOfLightingOnly_IsRecorded_AndUndone()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");

            LuaTool.LuaResult night = await session.ExecuteAsAsync(builder, MakeItNight);

            Assert.IsTrue(night.Success, night.Error);
            Assert.AreEqual(0f, Lighting(session).ClockTime);
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.AreEqual(1, status.UndoCount, "a Lighting-only edit is an AI change");
            Assert.IsTrue(status.CanUndo, status.Reason);
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(RbxLighting.DefaultClockTime, Lighting(session).ClockTime, "the undo put the afternoon back");
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>A StarterPlayer-only edit is an AI change too.</summary>
        [Test]
        public async Task Undo_AnAiEditOfStarterPlayerOnly_IsUndone()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");

            LuaTool.LuaResult zoom = await session.ExecuteAsAsync(builder,
                "game:GetService('StarterPlayer').CameraMaxZoomDistance = 40 return true");

            Assert.IsTrue(zoom.Success, zoom.Error);
            Assert.AreEqual(1, session.Undo.GetStatus(builder.ActorId).UndoCount);
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success);
            RbxStarterPlayer starterPlayer = (RbxStarterPlayer)session.Controller.CurrentRbxApi.Registry.WorldRoot.Parent
                .FindFirstChildOfClass("StarterPlayer");
            Assert.AreEqual(RbxStarterPlayer.DefaultCameraMaxZoomDistance, starterPlayer.CameraMaxZoomDistance);
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// An edit of only a HUD label's text or a torch's brightness (setters without a revision) is an AI change an
        /// undo takes back; the HUD build before it is its own change.
        /// </summary>
        [Test]
        public async Task Undo_AnAiEditOfGuiOrPointLightOnly_IsUndone()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            RbxTextLabel label = null;
            RbxPointLight torch = null;
            await Gated(session, builder, registry =>
            {
                label = BuildHud(registry);
                torch = (RbxPointLight)registry.Create("PointLight", originTag: "console:hud");
                torch.Parent = registry.WorldRoot;
            });

            await Gated(session, builder, _ => label.TextState.SetText("Score: 99"));
            await Gated(session, builder, _ => torch.SetBrightness(5d));

            Assert.AreEqual(3, session.Undo.GetStatus(builder.ActorId).UndoCount);
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success, "the torch edit is undone");
            Assert.IsTrue((await session.Undo.UndoAsync(builder)).Success, "the label edit is undone");
            InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;
            Assert.IsTrue(live.TryGet(label.Id, out RbxInstance restoredLabel));
            Assert.AreEqual("Score: 0", ((RbxTextLabel)restoredLabel).TextState.Text);
            Assert.IsTrue(live.TryGet(torch.Id, out RbxInstance restoredTorch));
            Assert.AreEqual(1f, ((RbxPointLight)restoredTorch).Brightness);
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// Twin: a day-cycle script that drives ClockTime every frame, outside the gate, adds no entry, does not refuse
        /// the undo of the AI's build, and a read-only call meanwhile records nothing.
        /// </summary>
        [Test]
        public async Task Undo_ADayCycleTickingEveryFrame_NeitherRecordsNorRefuses()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);

            TickDayCycle(session, 120);
            LuaTool.LuaResult lookup = await session.ExecuteAsAsync(builder, "return workspace:FindFirstChild('Tower1') ~= nil");
            TickDayCycle(session, 120);

            Assert.IsTrue(lookup.Success, lookup.Error);
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.AreEqual(1, status.UndoCount, "neither the ticks nor the lookup are AI changes");
            Assert.IsTrue(status.CanUndo, status.Reason);
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// Land a world generator streams in after the AI's build (Terrain written while the Workspace marks the writes
        /// as the generator's, as a Block World does for every column) does not refuse the undo of the build: the land
        /// comes back from the seed. Before, every streamed column moved the Terrain's revision and the undo was
        /// refused as if the player had edited the world.
        /// </summary>
        [Test]
        public async Task Undo_LandStreamedInAfterTheAiBuild_DoesNotRefuseIt()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);

            WriteTerrain(session, 40, true);

            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.IsTrue(status.CanUndo, status.Reason);
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// Twin: a block the player places after the AI's build (a Terrain write nobody marks as generated) still refuses
        /// the undo, which would otherwise take that block away too.
        /// </summary>
        [Test]
        public async Task Undo_APlayersBlockAfterTheAiBuild_StillRefusesIt()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            WriteTerrain(session, 40, true);

            WriteTerrain(session, 41, false);

            Assert.IsFalse(session.Undo.GetStatus(builder.ActorId).CanUndo);
            RbxWorldUndoResult refused = await session.Undo.UndoAsync(builder);
            Assert.AreEqual(RbxWorldUndoResult.WorldChangedStatus, refused.Status, refused.Message);
            StringAssert.Contains("a player, a mod script or the Hub", refused.Message);
            Assert.IsNotNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower1"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>Another agent's gated Lighting edit after mine refuses my undo and names that agent.</summary>
        [Test]
        public async Task Undo_AnotherAgentsGatedLightingEdit_RefusesMyUndo_AndNamesThem()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext first = Actor("first-agent");
            ActorContext second = Actor("second-agent");
            Assert.IsTrue((await session.ExecuteAsAsync(first, BuildTower)).Success);
            Assert.IsTrue((await session.ExecuteAsAsync(second, MakeItNight)).Success);

            RbxWorldUndoResult refused = await session.Undo.UndoAsync(first);
            RbxWorldUndoResult allowed = await session.Undo.UndoAsync(second);

            Assert.AreEqual(RbxWorldUndoResult.WorldChangedStatus, refused.Status);
            StringAssert.Contains("Agent 'second-agent'", refused.Message);
            Assert.IsTrue(allowed.Success, allowed.Message);
            Assert.AreEqual(RbxLighting.DefaultClockTime, Lighting(session).ClockTime);
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// The StarterGui copy of a new HUD into the player's PlayerGui (the next frame, outside the gate) and a respawn
        /// that replaces it do not refuse the undo of the build: a package never holds a PlayerGui copy.
        /// </summary>
        [Test]
        public async Task Undo_PlayerGuiCopiesOfANewHud_DoNotRefuseItsUndo()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = Actor("builder");
            RbxTextLabel label = null;
            await Gated(session, builder, registry => label = BuildHud(registry));
            InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;
            RbxDataModel game = (RbxDataModel)live.WorldRoot.Parent;
            RbxPlayers players = (RbxPlayers)game.GetService("Players");
            players.CharacterAutoLoads = false;
            RbxPlayer player = players.EnsureActor(live, "local-player");
            RbxStarterGuiReplicator replicator = new(game);
            replicator.CopyInto(player);
            replicator.CopyInto(player);
            Assert.IsNotNull(player.FindFirstChildOfClass("PlayerGui").FindFirstChild("Hud"), "precondition: copied");

            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(status.CanUndo, status.Reason);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsFalse(session.Controller.CurrentRbxApi.Registry.TryGet(label.Id, out RbxInstance _));
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// The startup record follows a GUI-only AI edit (its setter raises no registry event), and a day cycle ticking
        /// outside the gate plus a read-only call write no record.
        /// </summary>
        [Test]
        public async Task StartupRecord_FollowsAGuiOnlyEdit_AndIgnoresADayCycle()
        {
            P3MemoryPackageStore store = new(P3Fixtures.CapturePayload("StartupWorld"));
            using P3HeadlessSession session = new(store);
            session.Controller.StartupRefreshInterval = TimeSpan.Zero;
            ActorContext builder = Actor("builder");
            await session.Controller.RestoreStartupSelectionAsync();
            RbxTextLabel label = null;
            // WHY a first call before the build: an actor's first execute_lua admits its Player, whose parenting
            // advances the Players service's revision, which the watch counts; that is not what this test measures.
            Assert.IsTrue((await session.ExecuteAsAsync(builder, "return 0")).Success);
            await Gated(session, builder, registry => label = BuildHud(registry));
            int afterBuild = store.SelectedPayloads.Count;

            TickDayCycle(session, 120);
            Assert.IsTrue((await session.ExecuteAsAsync(builder, "return 1")).Success);
            int afterTicks = store.SelectedPayloads.Count;
            await Gated(session, builder, _ => label.TextState.SetText("Score: 7"));

            Assert.AreEqual(afterBuild, afterTicks, "a day cycle outside the gate writes no startup record");
            Assert.AreEqual(afterTicks + 1, store.SelectedPayloads.Count, "the GUI-only edit is recorded");
            RbxWorldPackagePayload latest = store.SelectedPayloads[store.SelectedPayloads.Count - 1];
            Assert.IsTrue(latest.Gui[label.Id].TryGetValue("Text", out object text));
            Assert.AreEqual("Score: 7", text);
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// The mark: a revision-free edit moves only <see cref="RbxWorldStateMark.Values"/>; a mod's own GUI and a
        /// PlayerGui copy move nothing; a world-owned instance moves the revisions.
        /// </summary>
        [Test]
        public void StateMark_ValuesFollowRevisionFreeState_AndCopiesAndModGuiAreLeftOut()
        {
            InstanceRegistry registry = new(worldId: P3Fixtures.WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxTextLabel label = BuildHud(registry);
            RbxWorldStateMark start = RbxWorldStateMark.Of(registry, 0);

            ((RbxLighting)game.FindFirstChildOfClass("Lighting")).SetClockTime(3d);
            RbxWorldStateMark night = RbxWorldStateMark.Of(registry, 0);
            label.TextState.SetText("changed");
            RbxWorldStateMark relabelled = RbxWorldStateMark.Of(registry, 0);
            RbxScreenGui modHud = (RbxScreenGui)registry.Create("ScreenGui", "hud-mod");
            modHud.Parent = game.FindFirstChildOfClass("StarterGui");
            modHud.SetDisplayOrder(9);
            RbxPlayers players = (RbxPlayers)game.GetService("Players");
            players.CharacterAutoLoads = false;
            new RbxStarterGuiReplicator(game).CopyInto(players.EnsureActor(registry, "local-player"));
            RbxWorldStateMark withCopies = RbxWorldStateMark.Of(registry, 0);

            Assert.IsTrue(night.HasSameRevisionsAs(start), "a Lighting service write leaves the revisions alone");
            Assert.AreNotEqual(start.Values, night.Values);
            Assert.AreNotEqual(start, night, "equality still sees every package-held change");
            Assert.AreNotEqual(night.Values, relabelled.Values);
            Assert.AreEqual(relabelled, withCopies, "a mod's own GUI and the PlayerGui copies are not what a package holds");
            game.Destroy();
        }

        private static async Task Gated(P3HeadlessSession session, ActorContext actor, Action<InstanceRegistry> edit)
        {
            bool done = await session.Gate.ExecuteAsync("execute_lua", actor.ActorId, _ =>
            {
                edit(session.Controller.CurrentRbxApi.Registry);
                return Task.FromResult(true);
            }, CancellationToken.None);
            Assert.IsTrue(done);
        }

        /// <summary>What a day-cycle mod does on every frame: advances ClockTime by a little, outside the gate.</summary>
        private static void TickDayCycle(P3HeadlessSession session, int frames)
        {
            RbxLighting lighting = Lighting(session);
            for (int frame = 0; frame < frames; frame++)
            {
                lighting.SetClockTime(lighting.ClockTime + 0.01d);
            }
        }

        /// <summary>A world-owned ScreenGui with one TextLabel in StarterGui; returns the label.</summary>
        private static RbxTextLabel BuildHud(InstanceRegistry registry)
        {
            RbxInstance starterGui = registry.WorldRoot.Parent.FindFirstChildOfClass("StarterGui");
            RbxScreenGui hud = (RbxScreenGui)registry.Create("ScreenGui", originTag: "console:hud");
            hud.Name = "Hud";
            hud.Parent = starterGui;
            RbxTextLabel label = (RbxTextLabel)registry.Create("TextLabel", originTag: "console:hud");
            label.Name = "Score";
            label.TextState.SetText("Score: 0");
            label.Parent = hud;
            return label;
        }
    }
}
