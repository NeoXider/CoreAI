using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The change journal of a world kept between starts (<c>RbxKeptWorldJournal</c>): a player's Terrain write in land
    /// the last record holds is on disk in the frame it was made, replaying the record and its journal gives the cells of
    /// a full capture taken at the end, a write made while a record encodes lands in that record or in the journal after
    /// it, land the record lacks and an undone world wait for the full record, a torn journal replays what can be read,
    /// and the journal is bounded.
    /// </summary>
    [TestFixture]
    public sealed class KeptWorldJournalEditModeTests
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
        /// One block in land the record holds: 29 bytes appended to the record's journal in the frame of the write
        /// (8 more for the file's header), no second record, and the world reads "saved" at once.
        /// </summary>
        [Test]
        public async Task Journal_ABlockInRecordedLand_IsInTheJournalInItsOwnFrame_In29Bytes()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            int guardCalls = world.Guard.Count;

            world.PlaceBlock(5, 4, 5);
            world.Pump();

            string[] journals = world.Journals();
            Assert.AreEqual(1, journals.Length, string.Join(", ", journals));
            Assert.AreEqual(RbxKeptWorldJournal.PathOf(world.StartupDirectory, world.NewestSequence()), journals[0]);
            Assert.AreEqual(RbxKeptWorldJournal.HeaderBytes + 29, new FileInfo(journals[0]).Length);
            Assert.AreEqual(1, world.Entries().Length, "no full record for a journaled block");
            Assert.AreEqual("saved", world.StateAttribute());
            Assert.AreEqual(guardCalls, world.Guard.Count, "a journaled block never raises the leave guard");
        }

        /// <summary>
        /// Replay equivalence: two thousand seeded writes (single cells and small boxes, placed and broken) over a
        /// recorded slab, then the record's package and its journal replay to exactly the cells of a capture taken at the
        /// end.
        /// </summary>
        [Test]
        public async Task Journal_TheRecordAndItsJournal_ReplayToTheCellsOfAFullCaptureTakenAtTheEnd()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            byte[] materials = { TerrainMaterials.Air, KeptWorlds.Brick(), KeptWorlds.Material("Sand"), KeptWorlds.Material("Rock") };
            System.Random random = new(1337);
            for (int write = 0; write < 2000; write++)
            {
                int x = random.Next(0, 46);
                int y = random.Next(0, 14);
                int z = random.Next(0, 46);
                byte material = materials[random.Next(materials.Length)];
                byte occupancy = material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full;
                if (random.Next(4) == 0)
                {
                    world.Terrain.FillBox(new TerrainCellBox(x, y, z, 2, 2, 2), material, occupancy);
                }
                else
                {
                    world.Terrain.SetCell(x, y, z, material, occupancy);
                }

                if (write % 7 == 0)
                {
                    world.Pump();
                }
            }

            world.Pump();
            Assert.AreEqual(1, world.Entries().Length, "every write was journaled: no record was needed");
            TerrainSnapshot live = session.Controller.CaptureCurrent().Terrain;

            RbxKeptWorldJournalReplay replay = RbxKeptWorldJournal.Replay(
                world.StartupDirectory, world.NewestSequence(), world.NewestEntry().Terrain);

            Assert.AreEqual("", replay.Problem);
            Assert.Greater(replay.Applied, 1000);
            KeptWorlds.AssertSameCells(live, replay.Terrain, Land, "record + journal against the live world");
        }

        /// <summary>
        /// A write made while a record of the world is still encoding is in that record or in the journal after it: the
        /// new record's journal holds exactly the writes made after its capture, the old journal is gone, and the new
        /// record and its journal replay to the live world.
        /// </summary>
        [Test]
        public async Task Journal_AWriteWhileARecordEncodes_IsInThatRecordOrInTheJournalAfterIt()
        {
            ManualFrames frames = new();
            KeptWorld world = await RecordedSlab(frames);
            using P3HeadlessSession session = world.Session;
            int firstSequence = world.NewestSequence();
            world.PlaceBlock(1, 4, 1);
            world.PlaceBlock(2, 4, 1);
            world.Pump();

            world.Now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            world.Pump();
            Task record = session.Controller.StartupRefreshForTests;
            Assert.IsFalse(record.IsCompleted, "precondition: the record is spread over frames");
            world.PlaceBlock(3, 4, 1);
            world.Terrain.SetCell(4, 4, 1, KeptWorlds.Material("Sand"), TerrainOccupancy.Full);
            world.Pump();
            frames.AdvanceUntil(() => record.IsCompleted, "the record");
            await KeptWorlds.Within(record);
            world.Pump();

            int secondSequence = world.NewestSequence();
            Assert.Greater(secondSequence, firstSequence);
            string[] journals = world.Journals();
            Assert.AreEqual(1, journals.Length, string.Join(", ", journals));
            Assert.AreEqual(RbxKeptWorldJournal.PathOf(world.StartupDirectory, secondSequence), journals[0],
                "the old journal went with its record");
            Assert.AreEqual(RbxKeptWorldJournal.HeaderBytes + 2 * 29, new FileInfo(journals[0]).Length,
                "only the two writes made after the capture");
            RbxKeptWorldJournalReplay replay = RbxKeptWorldJournal.Replay(
                world.StartupDirectory, secondSequence, world.NewestEntry().Terrain);
            Assert.AreEqual(2, replay.Applied);
            KeptWorlds.AssertSameCells(session.Controller.CaptureCurrent().Terrain, replay.Terrain, Land,
                "the new record + its journal against the live world");
        }

        /// <summary>
        /// Negative twin: a write in land the record does not hold (a world generator could fill it again at the next
        /// start) is not journaled and waits for the full record; once that record holds the land, writes there are
        /// journaled.
        /// </summary>
        [Test]
        public async Task Journal_AWriteInLandTheRecordLacks_WaitsForTheFullRecord_ThenIsJournaled()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;

            world.PlaceBlock(100, 2, 100);
            world.Pump();

            Assert.AreEqual("pending", world.StateAttribute());
            CollectionAssert.IsEmpty(world.Journals(), "nothing journaled outside the recorded land");
            await world.RecordAfterQuietTime();
            Assert.AreEqual("saved", world.StateAttribute());

            world.Terrain.SetCell(101, 2, 100, KeptWorlds.Brick(), TerrainOccupancy.Full);
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute(), "the land is recorded now, so the write is journaled");
            Assert.AreEqual(1, world.Journals().Length);
        }

        /// <summary>
        /// A journal whose tail was torn (a write cut short) or whose record was damaged replays the records before the
        /// damage, says where it stopped, and is cut back to them, so the next replay reads it whole.
        /// </summary>
        [Test]
        public async Task Journal_ATornOrDamagedJournal_ReplaysWhatCanBeRead_AndIsCutBackToIt()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            world.PlaceBlock(1, 4, 1);
            world.PlaceBlock(2, 4, 1);
            world.PlaceBlock(3, 4, 1);
            world.Pump();
            string journal = world.Journals()[0];
            long whole = new FileInfo(journal).Length;
            using (FileStream stream = new(journal, FileMode.Append, FileAccess.Write))
            {
                stream.Write(new byte[] { 40, 0, 0, 0, 1, 2, 3 }, 0, 7);
            }

            RbxKeptWorldJournalReplay torn = RbxKeptWorldJournal.Replay(
                world.StartupDirectory, world.NewestSequence(), world.NewestEntry().Terrain);

            Assert.AreEqual(3, torn.Applied);
            StringAssert.Contains("read up to byte " + whole, torn.Problem);
            Assert.AreEqual(whole, new FileInfo(journal).Length, "cut back to the readable records");
            Assert.AreEqual("", RbxKeptWorldJournal.Replay(
                world.StartupDirectory, world.NewestSequence(), world.NewestEntry().Terrain).Problem);

            byte[] bytes = File.ReadAllBytes(journal);
            bytes[RbxKeptWorldJournal.HeaderBytes + 29 + 12] ^= 0xFF;
            File.WriteAllBytes(journal, bytes);
            RbxKeptWorldJournalReplay damaged = RbxKeptWorldJournal.Replay(
                world.StartupDirectory, world.NewestSequence(), world.NewestEntry().Terrain);

            Assert.AreEqual(1, damaged.Applied, "the record before the damage");
            StringAssert.Contains("checksum", damaged.Problem);
            Assert.AreEqual(RbxKeptWorldJournal.HeaderBytes + 29, new FileInfo(journal).Length);
        }

        /// <summary>
        /// The journal is bounded: once it holds <see cref="RbxKeptWorldJournal.MaximumBytes"/>, a further write waits for
        /// the full record (the world reads "pending"), and that record starts an empty journal again.
        /// </summary>
        [Test]
        public async Task Journal_PastItsBound_LeavesWritesToTheFullRecord_WhichEmptiesIt()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            byte brick = KeptWorlds.Brick();
            byte sand = KeptWorlds.Material("Sand");
            long fits = (RbxKeptWorldJournal.MaximumBytes - RbxKeptWorldJournal.HeaderBytes) / 29;
            for (long write = 0; write < fits; write++)
            {
                int cell = (int)(write % 2048);
                world.Terrain.SetCell(cell % 46, 4 + (cell / 46) % 10, cell / 460, write % 4096 < 2048 ? brick : sand,
                    TerrainOccupancy.Full);
                if (write % 64 == 0)
                {
                    world.Pump();
                }
            }

            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute(), "every write so far fits");
            world.Terrain.SetCell(45, 13, 45, KeptWorlds.Material("Rock"), TerrainOccupancy.Full);
            world.Pump();

            Assert.AreEqual("pending", world.StateAttribute(), "the full journal leaves the write to the record");
            Assert.LessOrEqual(new FileInfo(world.Journals()[0]).Length, RbxKeptWorldJournal.MaximumBytes);
            await world.RecordAfterQuietTime();
            Assert.AreEqual("saved", world.StateAttribute());
            CollectionAssert.IsEmpty(world.Journals(), "the record holds everything; no write came after its capture");
        }

        /// <summary>
        /// An undo replaces the world, so the journal of the replaced world stops: a block placed after the undo waits for
        /// the record the undo calls for, which starts the journal again.
        /// </summary>
        [Test]
        public async Task Journal_AnUndo_StopsTheJournalUntilTheRecordOfTheUndoneWorld()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            LuaTool.LuaResult built = await KeptWorlds.Within(session.ExecuteAsAsync(KeptWorlds.Actor(),
                "local f = Instance.new('Folder') f.Name = 'AiTower' f.Parent = workspace return true"));
            Assert.IsTrue(built.Success, built.Error);
            await KeptWorlds.Within(session.Gate.WaitForPendingBackupAsync());
            RbxWorldUndoResult undone = await KeptWorlds.Within(
                session.Undo.UndoAsync(KeptWorlds.Actor(), CancellationToken.None).AsTask());
            Assert.IsTrue(undone.Success, undone.Message);
            world.Pump();

            world.PlaceBlock(6, 4, 6);
            world.Pump();
            Assert.AreEqual("pending", world.StateAttribute());
            CollectionAssert.IsEmpty(world.Journals(), "the journal of the replaced world does not take the block");

            await world.RecordAfterQuietTime();
            Assert.AreEqual("saved", world.StateAttribute());
            world.PlaceBlock(7, 4, 6);
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute(), "journaled again after the record");
            Assert.AreEqual(1, world.Journals().Length);
        }

        /// <summary>A cleared startup selection (the Hub's "Start with the default world next time") deletes the journals.</summary>
        [Test]
        public async Task Journal_AClearedSelection_DeletesTheJournals()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            world.PlaceBlock(1, 4, 1);
            world.Pump();
            Assert.AreEqual(1, world.Journals().Length, "precondition");

            RbxWorldPackageWriteResult cleared = await KeptWorlds.Within(session.Controller.ClearStartupSelectionAsync());

            Assert.IsTrue(cleared.Success, cleared.Error);
            CollectionAssert.IsEmpty(world.Journals());
            world.PlaceBlock(2, 4, 1);
            world.Pump();
            CollectionAssert.IsEmpty(world.Journals(), "nothing is journaled for a world that is no longer kept");
        }

        /// <summary>
        /// What the journal costs, measured here (engine-free, this machine): one block placed and appended in its own
        /// frame, the journal of five thousand such blocks, and replaying it at a start.
        /// </summary>
        [Test]
        public async Task Journal_Cost_OfABlockAndOfReplayingFiveThousand()
        {
            KeptWorld world = await RecordedSlab();
            using P3HeadlessSession session = world.Session;
            byte brick = KeptWorlds.Brick();
            byte sand = KeptWorlds.Material("Sand");
            const int Blocks = 5000;
            Stopwatch pumps = Stopwatch.StartNew();
            for (int frame = 0; frame < Blocks; frame++)
            {
                world.Pump();
            }

            double pumpUs = pumps.Elapsed.TotalMilliseconds * 1000d / Blocks;
            Stopwatch watch = Stopwatch.StartNew();
            for (int block = 0; block < Blocks; block++)
            {
                int cell = block % 2000;
                world.Terrain.SetCell(cell % 46, 4 + (cell / 46) % 10, cell / 460, (block / 2000) % 2 == 0 ? brick : sand,
                    TerrainOccupancy.Full);
                world.Pump();
            }

            double perBlockUs = watch.Elapsed.TotalMilliseconds * 1000d / Blocks;
            long journalBytes = new FileInfo(world.Journals()[0]).Length;

            // The journal alone, over a bare store: the write's event, the record, and one open, append and close.
            TerrainChunkStore bare = new();
            bare.RestoreSnapshot(world.NewestEntry().Terrain);
            string directory = Path.Combine(_worlds.NewDirectory(), "bare");
            Directory.CreateDirectory(directory);
            RbxKeptWorldJournal journal = new();
            journal.Begin(directory, 1, bare.CaptureSnapshot(), 0L);
            bare.Changed += changes => journal.TryRecord(changes, bare);
            Stopwatch alone = Stopwatch.StartNew();
            for (int block = 0; block < Blocks; block++)
            {
                int cell = block % 2000;
                bare.SetCell(cell % 46, 4 + (cell / 46) % 10, cell / 460, (block / 2000) % 2 == 0 ? brick : sand,
                    TerrainOccupancy.Full);
                journal.Flush();
            }

            double journalUs = alone.Elapsed.TotalMilliseconds * 1000d / Blocks;
            RbxWorldPackagePayload entry = world.NewestEntry();
            Stopwatch replayWatch = Stopwatch.StartNew();
            RbxKeptWorldJournalReplay replay = RbxKeptWorldJournal.Replay(
                world.StartupDirectory, world.NewestSequence(), entry.Terrain);
            double replayMs = replayWatch.Elapsed.TotalMilliseconds;

            TestContext.WriteLine("a frame of the headless world without a block: " + pumpUs.ToString("0.0")
                                  + " us; with a block placed, journaled and appended: " + perBlockUs.ToString("0.0")
                                  + " us; the journal alone (record + open/append/close of the file): "
                                  + journalUs.ToString("0.0") + " us a block; journal of " + Blocks + " blocks: "
                                  + journalBytes + " bytes; replaying it at a start: " + replayMs.ToString("0.0") + " ms");
            Assert.AreEqual(RbxKeptWorldJournal.HeaderBytes + Blocks * 29L, journalBytes);
            Assert.AreEqual(RbxKeptWorldJournal.HeaderBytes + Blocks * 29L, new FileInfo(RbxKeptWorldJournal.PathOf(directory, 1)).Length);
            Assert.AreEqual(Blocks, replay.Applied);
            Assert.Less(replayMs, 10000d, "a start replays five thousand blocks in well under ten seconds");
        }

        // ==================== Helpers ====================

        /// <summary>A kept world whose first record holds a 48 x 4 x 48 slab of rock (nine chunks), so writes there journal.</summary>
        private async Task<KeptWorld> RecordedSlab(ManualFrames frames = null)
        {
            KeptWorld world = _worlds.New(frames: frames != null ? frames.WaitAsync : (Func<CancellationToken, UniTask>)null);
            world.Terrain.FillBox(Slab, KeptWorlds.Material("Rock"), TerrainOccupancy.Full);
            world.Pump();
            world.Now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            world.Pump();
            Task record = world.Session.Controller.StartupRefreshForTests;
            frames?.AdvanceUntil(() => record.IsCompleted, "the first record");
            await KeptWorlds.Within(record);
            world.Pump();
            Assert.AreEqual(1, world.Entries().Length, "precondition: the slab is recorded");
            Assert.AreEqual("saved", world.StateAttribute(), "precondition");
            return world;
        }

        /// <summary>Frames a test hands out one at a time: every wait completes on the next <see cref="Advance"/>.</summary>
        private sealed class ManualFrames
        {
            private UniTaskCompletionSource _next = new();

            public UniTask WaitAsync(CancellationToken cancellationToken)
            {
                return _next.Task;
            }

            public void AdvanceUntil(Func<bool> done, string what)
            {
                for (int frame = 0; frame < 100000 && !done(); frame++)
                {
                    UniTaskCompletionSource current = _next;
                    _next = new UniTaskCompletionSource();
                    current.TrySetResult();
                }

                Assert.IsTrue(done(), what + " finishes as frames come");
            }
        }
    }
}
