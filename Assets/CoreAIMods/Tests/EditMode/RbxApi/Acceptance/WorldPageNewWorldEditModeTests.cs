using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The Hub World page's actions on the production session controller: New world replaces a kept world now with an
    /// empty one (no Terrain, no builds, a new seed) that the enabled mods start on, drops the kept record and journal and
    /// reopens on the next start, and changes nothing without the player's confirmation; Save then Load brings the saved
    /// world back. These read world packages from disk, which waits for player-loop frames, so they run as Unity tests.
    /// </summary>
    [TestFixture]
    public sealed class WorldPageNewWorldEditModeTests
    {
        private static readonly TerrainCellBox Slab = new(0, 0, 0, 48, 4, 48);
        private readonly KeptWorlds _worlds = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            _worlds.Dispose();
        }

        /// <summary>
        /// A kept world with a record, a journal, a build and a seed of its own: after New world the live world holds none
        /// of them but the new seed, its active mod ran on it and its disabled mod did not, the old record and journal are
        /// gone, the outgoing world is a safety autosave, and the next start opens the new world.
        /// </summary>
        [UnityTest]
        public IEnumerator NewWorld_ReplacesAKeptWorldNow_DropsItsRecordAndJournal_AndReopensOnTheNextStart()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = _worlds.NewDirectory();
                KeptWorld world = await RecordedSlab(root);
                int oldSequence;
                using (P3HeadlessSession session = world.Session)
                {
                    SaveMod(session, "seed_reader", true,
                        "workspace:SetAttribute('SeedSeen', workspace:GetAttribute('" +
                        RbxWorldRuntimeSessionController.NewWorldSeedAttribute + "'))");
                    SaveMod(session, "switched_off", false, "workspace:SetAttribute('OffRan', true)");
                    world.PlaceBlock(5, 4, 5);
                    world.Pump();
                    oldSequence = world.NewestSequence();
                    Assert.AreEqual(1, world.Journals().Length, "precondition: a journaled block");
                    AddFolder(session, "PlayerBuild");
                    world.Workspace.SetAttribute("BlockWorldSeed", 5d);
                    session.Controller.ConfigureNewWorldSeedForTests(() => 4242);

                    RbxWorldLoadResult result = await session.Controller.OpenNewWorldAsync(true);
                    await KeptWorlds.Within(session.Gate.WaitForPendingBackupAsync());
                    world.Pump();

                    Assert.IsTrue(result.Success, result.Error);
                    Assert.IsTrue(result.StartupSelectionPersisted, result.StartupSelectionError);
                    Assert.AreEqual(1, result.ActiveModsStarted, "only the enabled mod starts");
                    RbxInstance workspace = world.Workspace;
                    Assert.IsNull(workspace.FindFirstChild("PlayerBuild"), "the build is gone");
                    Assert.IsNull(workspace.GetAttribute("BlockWorldSeed"), "no state of the old world");
                    Assert.AreEqual(4242d, workspace.GetAttribute(RbxWorldRuntimeSessionController.NewWorldSeedAttribute));
                    Assert.AreEqual(4242d, workspace.GetAttribute("SeedSeen"), "the enabled mod ran on the new world");
                    Assert.IsNull(workspace.GetAttribute("OffRan"), "a disabled mod stays off");
                    Assert.IsNotNull(RbxTerrainBootstrap.Find(workspace), "the new world has its Terrain for the generators");
                    Assert.AreEqual(TerrainMaterials.Air, world.Terrain.GetMaterial(5, 2, 5), "no Terrain cells");
                    Assert.AreEqual(0, world.Terrain.ChunkCount);

                    Assert.IsFalse(File.Exists(RbxKeptWorldJournal.PathOf(world.StartupDirectory, oldSequence)),
                        "the old world's journal is dropped");
                    foreach (string entry in world.Entries())
                    {
                        Assert.AreNotEqual(oldSequence, int.Parse(Path.GetFileNameWithoutExtension(entry),
                            System.Globalization.CultureInfo.InvariantCulture), "the old world's record is gone");
                    }

                    Assert.Greater(world.NewestSequence(), oldSequence);
                    RbxWorldPackagePayload recorded = world.NewestEntry();
                    Assert.IsTrue(recorded.Terrain == null || recorded.Terrain.ChunkCount == 0, "the record holds no cells");
                    Assert.IsTrue(HasAutoSave(world.Store, RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger),
                        "the outgoing world is kept as a safety autosave");
                    Assert.AreEqual("saved", world.StateAttribute());
                }

                KeptWorld reopened = _worlds.New(root: root, adopt: false);
                using P3HeadlessSession second = reopened.Session;
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();
                reopened.Pump();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.IsNull(reopened.Workspace.FindFirstChild("PlayerBuild"));
                Assert.AreEqual(4242d, reopened.Workspace.GetAttribute(RbxWorldRuntimeSessionController.NewWorldSeedAttribute));
                Assert.AreEqual(TerrainMaterials.Air, reopened.Terrain.GetMaterial(5, 2, 5), "the old slab did not come back");
            });
        }

        /// <summary>
        /// Negative twin: without the player's confirmation New world changes nothing: the same live world, its build,
        /// its cells, its record and its journal, and no autosave.
        /// </summary>
        [UnityTest]
        public IEnumerator NewWorld_WithoutConfirmation_ChangesNothing()
        {
            return UniTask.ToCoroutine(async () =>
            {
                KeptWorld world = await RecordedSlab(_worlds.NewDirectory());
                using P3HeadlessSession session = world.Session;
                world.PlaceBlock(5, 4, 5);
                world.Pump();
                AddFolder(session, "PlayerBuild");
                InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;
                string[] entries = world.Entries();
                string[] journals = world.Journals();
                long journalLength = new FileInfo(journals[0]).Length;

                RbxWorldLoadResult result = await session.Controller.OpenNewWorldAsync(false);
                world.Pump();

                Assert.IsFalse(result.Success);
                StringAssert.Contains("confirmation is required", result.Error);
                Assert.AreSame(live, session.Controller.CurrentRbxApi.Registry, "the world was not replaced");
                Assert.IsNotNull(world.Workspace.FindFirstChild("PlayerBuild"));
                Assert.AreEqual(KeptWorlds.Brick(), world.Terrain.GetMaterial(5, 4, 5));
                CollectionAssert.AreEqual(entries, world.Entries());
                CollectionAssert.AreEqual(journals, world.Journals());
                Assert.AreEqual(journalLength, new FileInfo(journals[0]).Length);
                CollectionAssert.IsEmpty(world.Store.ListAutoSaves(), "nothing was backed up, nothing ran");
            });
        }

        /// <summary>
        /// Save then Load: the live world saved to a slot is listed for the World page, and loading it after the player's
        /// confirmation brings back the world as it was saved, without what was built after the save.
        /// </summary>
        [UnityTest]
        public IEnumerator SaveThenLoad_RestoresTheSavedWorld()
        {
            return UniTask.ToCoroutine(async () =>
            {
                KeptWorld world = _worlds.New(root: _worlds.NewDirectory());
                using P3HeadlessSession session = world.Session;
                AddFolder(session, "SavedBuild");
                world.PlaceBlock(1, 1, 1);
                world.Pump();

                RbxWorldPackageWriteResult saved = await session.Controller.SaveManualAsync(KeptWorlds.Actor(), "castle");
                Assert.IsTrue(saved.Success, saved.Error);
                CollectionAssert.AreEqual(new[] { "castle" }, session.Controller.ListManualSaves());
                AddFolder(session, "BuiltAfterTheSave");
                world.PlaceBlock(3, 3, 3);
                world.Pump();

                RbxWorldLoadRequest request = await session.Controller.RequestManualLoadAsync(KeptWorlds.Actor(), "castle");
                RbxWorldLoadResult loaded = await session.Controller.ConfirmManualLoadAsync(request.RequestId, true);
                world.Pump();

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.IsNotNull(world.Workspace.FindFirstChild("SavedBuild"), "the saved world is back");
                Assert.AreEqual(KeptWorlds.Brick(), world.Terrain.GetMaterial(1, 1, 1), "with its block");
                Assert.IsNull(world.Workspace.FindFirstChild("BuiltAfterTheSave"), "a later build is not in the save");
                Assert.AreEqual(TerrainMaterials.Air, world.Terrain.GetMaterial(3, 3, 3), "a later block is not in the save");
            });
        }

        /// <summary>
        /// A world saved before its first block holds no Terrain cells, and its package no Terrain; loaded, it still has
        /// its Workspace's Terrain, as the default world does, so a mod that builds land can start on it.
        /// </summary>
        [UnityTest]
        public IEnumerator Load_AWorldSavedBeforeItsFirstBlock_HasItsTerrain()
        {
            return UniTask.ToCoroutine(async () =>
            {
                KeptWorld world = _worlds.New(root: _worlds.NewDirectory());
                using P3HeadlessSession session = world.Session;
                Assert.IsTrue((await session.Controller.SaveManualAsync(KeptWorlds.Actor(), "empty")).Success);

                RbxWorldLoadRequest request = await session.Controller.RequestManualLoadAsync(KeptWorlds.Actor(), "empty");
                RbxWorldLoadResult loaded = await session.Controller.ConfirmManualLoadAsync(request.RequestId, true);
                world.Pump();

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.IsNotNull(RbxTerrainBootstrap.Find(world.Workspace), "workspace.Terrain exists");
                world.PlaceBlock(2, 2, 2);
                Assert.AreEqual(KeptWorlds.Brick(), world.Terrain.GetMaterial(2, 2, 2));
            });
        }

        /// <summary>The empty world keeps the services and the Camera and drops everything else, with the seed on the Workspace.</summary>
        [Test]
        public void BuildNewWorld_KeepsTheServicesAndTheCamera_DropsTheRest_AndSeedsTheWorkspace()
        {
            KeptWorld world = _worlds.New();
            using P3HeadlessSession session = world.Session;
            AddFolder(session, "PlayerBuild");
            RbxInstance lighting = session.Controller.CurrentRbxApi.Game.FindFirstChildOfClass("Lighting");
            lighting.SetAttribute("Mood", "night");
            RbxWorldPackagePayload current = session.Controller.CaptureCurrent();

            RbxWorldPackagePayload built = RbxWorldRuntimeSessionController.BuildNewWorld(
                current, session.Controller.CurrentRbxApi.Registry.Catalog, 77);

            List<string> classes = new();
            foreach (InstanceSnapshot node in built.Tree.Instances)
            {
                classes.Add(node.ClassName);
                if (node.ClassName == "Workspace")
                {
                    Assert.AreEqual(1, node.Attributes.Count);
                    Assert.AreEqual(RbxWorldRuntimeSessionController.NewWorldSeedAttribute, node.Attributes[0].Name);
                    Assert.AreEqual(77d, node.Attributes[0].NumberValue);
                }
                else
                {
                    CollectionAssert.IsEmpty(node.Attributes, node.ClassName + " keeps no attribute");
                }
            }

            CollectionAssert.Contains(classes, "Workspace");
            CollectionAssert.Contains(classes, "Lighting");
            CollectionAssert.Contains(classes, "Camera");
            CollectionAssert.DoesNotContain(classes, "Folder");
            Assert.AreEqual(RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion, built.FormatVersion);
            Assert.IsNull(built.Terrain);
            Assert.AreEqual(0, built.Parts.Count);
            Assert.AreSame(current.Mods, built.Mods, "the live world's mods");
        }

        private static void SaveMod(P3HeadlessSession session, string id, bool active, string source)
        {
            session.SourceStore.Save(id, source, new LuaModManifest
            {
                Id = id,
                Name = id,
                OwnerActorId = KeptWorlds.Actor().ActorId,
                Capabilities = (LuaCapabilities.Read | LuaCapabilities.WorldEdit).ToString(),
                Active = active
            });
        }

        private static void AddFolder(P3HeadlessSession session, string name)
        {
            InstanceRegistry registry = session.Controller.CurrentRbxApi.Registry;
            RbxInstance folder = registry.Create("Folder");
            folder.Name = name;
            folder.Parent = KeptWorlds.Workspace(session);
        }

        private static bool HasAutoSave(FileRbxWorldPackageStore store, string trigger)
        {
            foreach (RbxAutoSaveInfo autoSave in store.ListAutoSaves())
            {
                if (string.Equals(autoSave.Trigger, trigger, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A kept world in <paramref name="root"/> whose first record holds a slab of rock, so writes there journal.</summary>
        private async UniTask<KeptWorld> RecordedSlab(string root)
        {
            KeptWorld world = _worlds.New(root: root);
            world.Terrain.FillBox(Slab, KeptWorlds.Material("Rock"), TerrainOccupancy.Full);
            world.Pump();
            world.Now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            world.Pump();
            await world.Session.Controller.StartupRefreshForTests;
            world.Pump();
            Assert.AreEqual(1, world.Entries().Length, "precondition: the slab is recorded");
            return world;
        }
    }
}
