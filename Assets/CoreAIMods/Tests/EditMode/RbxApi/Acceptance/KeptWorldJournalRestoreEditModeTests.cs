using System;
using System.Collections;
using System.IO;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// A kept world at its next start: the startup restore replays the change journal onto the last record before the
    /// world opens, a torn journal opens with what can be read and says the rest is lost, a confirmed load drops the
    /// journal, and a saved world that cannot be opened is kept until the player decides. These read world packages
    /// from disk, which waits for player-loop frames, so they run as Unity tests.
    /// </summary>
    [TestFixture]
    public sealed class KeptWorldJournalRestoreEditModeTests
    {
        private static readonly TerrainCellBox Slab = new(0, 0, 0, 48, 4, 48);
        private static readonly TerrainCellBox Land = new(0, 0, 0, 48, 16, 48);
        private readonly KeptWorlds _worlds = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            _worlds.Dispose();
        }

        /// <summary>
        /// Blocks journaled after the last record and then a reload with no further record: the world reopens with them,
        /// "saved", and the next block goes on into the same journal.
        /// </summary>
        [UnityTest]
        public IEnumerator Restore_AJournaledBlock_ReopensWithoutAFullRecord_AndTheJournalGoesOn()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = _worlds.NewDirectory();
                TerrainSnapshot before;
                KeptWorld world = await RecordedSlab(root);
                using (P3HeadlessSession first = world.Session)
                {
                    world.PlaceBlock(5, 4, 5);
                    world.Terrain.SetCell(6, 2, 5, TerrainMaterials.Air, 0);
                    world.Pump();
                    Assert.AreEqual(1, world.Journals().Length, "precondition: the blocks are journaled");
                    before = first.Controller.CaptureCurrent().Terrain;
                }

                KeptWorld reopened = _worlds.New(root: root, adopt: false);
                using P3HeadlessSession second = reopened.Session;
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();
                second.Controller.AdoptDefaultWorldIfKept();
                reopened.Pump();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                KeptWorlds.AssertSameCells(before, second.Controller.CaptureCurrent().Terrain, Land,
                    "the reopened world against the world before the reload");
                Assert.AreEqual("saved", reopened.StateAttribute());
                CollectionAssert.IsEmpty(second.Diagnostics, string.Join(" | ", second.Diagnostics));
                long length = new FileInfo(reopened.Journals()[0]).Length;
                reopened.PlaceBlock(7, 4, 5);
                reopened.Pump();
                Assert.AreEqual(length + 29, new FileInfo(reopened.Journals()[0]).Length, "the journal goes on");
            });
        }

        /// <summary>
        /// A journal torn by a reload in the middle of a write: the world opens with the writes before the tear, the
        /// state says the rest could not be read back, and the diagnostics say where it stopped.
        /// </summary>
        [UnityTest]
        public IEnumerator Restore_ATornJournal_OpensWithWhatCanBeRead_AndSaysTheRestIsLost()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = _worlds.NewDirectory();
                string journal;
                KeptWorld world = await RecordedSlab(root);
                using (world.Session)
                {
                    world.PlaceBlock(5, 4, 5);
                    world.Pump();
                    journal = world.Journals()[0];
                }

                using (FileStream stream = new(journal, FileMode.Append, FileAccess.Write))
                {
                    stream.Write(new byte[] { 200, 0, 0, 0, 9, 9 }, 0, 6);
                }

                KeptWorld reopened = _worlds.New(root: root, adopt: false);
                using P3HeadlessSession second = reopened.Session;
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();
                reopened.Pump();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.AreEqual(KeptWorlds.Brick(), reopened.Terrain.GetMaterial(5, 4, 5), "the write before the tear");
                Assert.AreEqual("failed", reopened.StateAttribute());
                StringAssert.Contains("could not all be read back", (string)reopened.NoteAttribute());
                Assert.AreEqual(1, second.Diagnostics.Count, string.Join(" | ", second.Diagnostics));
                StringAssert.Contains("read up to byte", second.Diagnostics[0]);
            });
        }

        /// <summary>A confirmed load replaces the world: its journal is dropped, and the loaded world journals on its own record.</summary>
        [UnityTest]
        public IEnumerator Restore_AConfirmedLoad_DropsTheJournal_AndTheLoadedWorldJournalsOnItsOwn()
        {
            return UniTask.ToCoroutine(async () =>
            {
                KeptWorld world = await RecordedSlab(_worlds.NewDirectory());
                using P3HeadlessSession session = world.Session;
                Assert.IsTrue((await session.Controller.SaveManualAsync(KeptWorlds.Actor(), "slab")).Success);
                world.PlaceBlock(5, 4, 5);
                world.Pump();
                Assert.AreEqual(1, world.Journals().Length, "precondition");

                RbxWorldLoadRequest request = await session.Controller.RequestManualLoadAsync(KeptWorlds.Actor(), "slab");
                RbxWorldLoadResult loaded = await session.Controller.ConfirmManualLoadAsync(request.RequestId, true);
                world.Pump();

                Assert.IsTrue(loaded.Success, loaded.Error);
                CollectionAssert.IsEmpty(world.Journals(), "the journal of the replaced world is dropped");
                world.PlaceBlock(8, 4, 8);
                world.Pump();
                string[] journals = world.Journals();
                Assert.AreEqual(1, journals.Length);
                Assert.AreEqual(RbxKeptWorldJournal.PathOf(world.StartupDirectory, world.NewestSequence()), journals[0]);
            });
        }

        /// <summary>
        /// Path 6: a saved world that cannot be opened at the next start is kept on disk while the player builds (the
        /// first block used to record the default world over it and prune it), the state says why, and /save is the
        /// player's choice to keep the new world instead.
        /// </summary>
        [UnityTest]
        public IEnumerator Restore_AFallenBackRestore_KeepsTheUnopenableSave_UntilThePlayerSaves()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = _worlds.NewDirectory();
                string entry;
                KeptWorld first = _worlds.New(root: root);
                using (first.Session)
                {
                    StringAssert.StartsWith("Saved ", await first.Session.Controller.SaveKeptWorldNowAsync());
                    entry = first.Entries()[0];
                }

                File.WriteAllBytes(entry, System.Text.Encoding.UTF8.GetBytes("not a world package"));
                KeptWorld world = _worlds.New(root: root, adopt: false);
                using P3HeadlessSession second = world.Session;
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();
                Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, restored.Outcome);
                second.Controller.AdoptDefaultWorldIfKept();
                world.Pump();

                Assert.AreEqual("failed", world.StateAttribute());
                StringAssert.Contains("could not be opened", (string)world.NoteAttribute());
                world.PlaceBlock(4, 2, 4);
                world.Pump();
                world.Now += RbxWorldRuntimeSessionController.KeptWorldRefreshInterval + TimeSpan.FromSeconds(1d);
                world.Pump();
                await second.Controller.StartupRefreshForTests;
                Assert.IsTrue(File.Exists(entry), "the unopenable save survives the player's blocks");
                CollectionAssert.IsEmpty(world.Guard, "nothing would be lost by leaving: this start is not saved anyway");

                string saved = await second.Controller.SaveKeptWorldNowAsync();
                world.Pump();

                StringAssert.Contains("It replaces the saved world that could not be opened.", saved);
                Assert.IsFalse(File.Exists(entry), "the player chose to replace it");
                Assert.AreEqual("saved", world.StateAttribute());
                TerrainChunkStore cells = new();
                cells.RestoreSnapshot(world.NewestEntry().Terrain);
                Assert.AreEqual(KeptWorlds.Brick(), cells.GetMaterial(4, 2, 4), "the new world is the one saved");
            });
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
