using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The save state of a world kept between starts: the Workspace attributes <c>CoreAIWorldSaveState</c> and
    /// <c>CoreAIWorldSaveNote</c> say "pending" while a change waits, "saving" while it is written or the browser still
    /// stores it, "saved" once it is safe, and "failed" with the reason when a record was not written; the page's leave
    /// guard follows the unsaved states, the chat says a failure once, and no package ever holds the state.
    /// </summary>
    [TestFixture]
    public sealed class KeptWorldSaveStateEditModeTests
    {
        private readonly KeptWorlds _worlds = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            _worlds.Dispose();
        }

        /// <summary>
        /// A player's block in a kept world that has no record yet (nothing for a journal to extend): "pending" with the
        /// leave guard up until the quiet time passed, then "saved" with the guard down, and the record holds neither
        /// attribute.
        /// </summary>
        [Test]
        public async Task KeptWorld_ABlockIsPendingUntilTheQuietTime_ThenSaved_AndNoRecordHoldsTheState()
        {
            KeptWorld world = _worlds.New();
            using P3HeadlessSession session = world.Session;
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute(), "a kept world with no change is saved");
            Assert.AreEqual("", world.NoteAttribute());
            CollectionAssert.IsEmpty(world.Guard, "nothing unsaved, no leave guard");

            world.PlaceBlock(4, 2, 4);
            world.Pump();

            Assert.AreEqual("pending", world.StateAttribute(), "the block waits for the quiet time");
            Assert.AreEqual(RbxKeptWorldSaveState.Pending, session.Controller.KeptWorldSaveState);
            CollectionAssert.AreEqual(new[] { true }, world.Guard, "the page asks before a reload loses the block");

            await world.RecordAfterQuietTime();

            Assert.AreEqual("saved", world.StateAttribute());
            CollectionAssert.AreEqual(new[] { true, false }, world.Guard, "the guard goes once the block is stored");
            RbxWorldPackagePayload recorded = world.NewestEntry();
            foreach (InstanceSnapshot node in recorded.Tree.Instances)
            {
                foreach (AttributeSnapshot attribute in node.Attributes)
                {
                    Assert.AreNotEqual(RbxWorldRuntimeSessionController.WorldSaveStateAttribute, attribute.Name);
                    Assert.AreNotEqual(RbxWorldRuntimeSessionController.WorldSaveNoteAttribute, attribute.Name);
                }
            }
        }

        /// <summary>Negative twin: a world that is not kept publishes no save state and never guards the page.</summary>
        [Test]
        public void NotKept_PublishesNoSaveState_AndNeverGuardsThePage()
        {
            FileRbxWorldPackageStore store = new(_worlds.NewDirectory(), persistenceSyncAsync: _ => UniTask.FromResult(true));
            using P3HeadlessSession session = new(store);
            List<bool> guard = new();
            session.Controller.ConfigureSaveStateProbesForTests(() => true, () => 0, guard.Add);

            session.Controller.PumpFrame(KeptWorlds.Host(), 0.016f);
            RbxInstance workspace = KeptWorlds.Workspace(session);
            RbxTerrainBootstrap.Find(workspace).Store.SetCell(1, 1, 1, KeptWorlds.Brick(), TerrainOccupancy.Full);
            session.Controller.PumpFrame(KeptWorlds.Host(), 0.016f);

            Assert.IsNull(workspace.GetAttribute(RbxWorldRuntimeSessionController.WorldSaveStateAttribute));
            Assert.IsNull(workspace.GetAttribute(RbxWorldRuntimeSessionController.WorldSaveNoteAttribute));
            Assert.AreEqual(RbxKeptWorldSaveState.NotKept, session.Controller.KeptWorldSaveState);
            CollectionAssert.IsEmpty(guard);
        }

        /// <summary>
        /// A record that is not durably written is "failed" with its reason, the chat says it once, a second failure
        /// with the same reason says nothing more, and the first record that is written again says so.
        /// </summary>
        [Test]
        public async Task KeptWorld_AFailedRecord_IsFailedWithItsReason_AndTheChatSaysItOnceAndTheRecovery()
        {
            bool durable = false;
            KeptWorld world = _worlds.New(_ => UniTask.FromResult(durable));
            using P3HeadlessSession session = world.Session;
            RecordingMessages chat = new();
            using RbxKeptWorldSaveNotices notices = new(session.Controller, chat);

            world.PlaceBlock(4, 2, 4);
            world.Pump();
            await world.RecordAfterQuietTime();

            Assert.AreEqual("failed", world.StateAttribute());
            StringAssert.Contains("durable persistence was not confirmed", (string)world.NoteAttribute());
            Assert.AreEqual(1, chat.Lines.Count, string.Join(" | ", chat.Lines));
            StringAssert.StartsWith("The world is not saved: ", chat.Lines[0]);

            world.PlaceBlock(5, 2, 4);
            world.Pump();
            await world.RecordAfterQuietTime();
            Assert.AreEqual(1, chat.Lines.Count, "the same reason is said once");

            durable = true;
            world.PlaceBlock(6, 2, 4);
            world.Pump();
            Assert.AreEqual("failed", world.StateAttribute(), "still not saved while the change waits");
            await world.RecordAfterQuietTime();

            Assert.AreEqual("saved", world.StateAttribute());
            Assert.AreEqual("", world.NoteAttribute());
            Assert.AreEqual(2, chat.Lines.Count);
            Assert.AreEqual("The world is saved again.", chat.Lines[1]);
        }

        /// <summary>
        /// On a page whose storage is still writing, a written record is "saving" until the storage is idle; another
        /// page of the game sharing the storage is a warning in the note whatever the state.
        /// </summary>
        [Test]
        public async Task KeptWorld_StorageStillWriting_IsSaving_AndAnotherPageIsAWarning()
        {
            KeptWorld world = _worlds.New();
            using P3HeadlessSession session = world.Session;
            world.FlushIdle = false;
            world.PlaceBlock(4, 2, 4);
            world.Pump();
            await world.RecordAfterQuietTime();

            Assert.AreEqual("saving", world.StateAttribute(), "written, but the browser has not stored it yet");
            Assert.IsTrue(world.Guard[world.Guard.Count - 1]);

            world.FlushIdle = true;
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute());
            Assert.IsFalse(world.Guard[world.Guard.Count - 1]);

            world.OtherPages = 1;
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute());
            StringAssert.Contains("Another page of this game is open", (string)world.NoteAttribute());
        }

        /// <summary>
        /// A script that writes the save state attribute through a gated call has changed nothing a package holds: the
        /// world is not marked for a record, and the next frame puts the state back. The twin: another Workspace
        /// attribute is a change the world waits to record.
        /// </summary>
        [Test]
        public async Task KeptWorld_AScriptWritingTheSaveState_IsNoChangeOfTheWorld_AnotherAttributeIs()
        {
            KeptWorld world = _worlds.New();
            using P3HeadlessSession session = world.Session;
            // WHY a first call and its record: the headless world's local player joins during the first gated call,
            // which is a change of the world of its own.
            await RunGated(session, "return true");
            world.Pump();
            await world.RecordAfterQuietTime();
            Assert.AreEqual("saved", world.StateAttribute(), "precondition");

            await RunGated(session,
                "workspace:SetAttribute('" + RbxWorldRuntimeSessionController.WorldSaveStateAttribute + "', 'x') return true");
            world.Pump();
            Assert.AreEqual("saved", world.StateAttribute(), "not pending, and the script's value is put back");

            await RunGated(session, "workspace:SetAttribute('BuiltByTheAi', 'x') return true");
            world.Pump();
            Assert.AreEqual("pending", world.StateAttribute(), "another attribute waits for the record");
        }

        private static async Task RunGated(P3HeadlessSession session, string code)
        {
            LuaTool.LuaResult result = await KeptWorlds.Within(session.ExecuteAsAsync(KeptWorlds.Actor(), code));
            Assert.IsTrue(result.Success, result.Error);
            await KeptWorlds.Within(session.Gate.WaitForPendingBackupAsync());
        }

        internal sealed class RecordingMessages : IChatOverlayMessages
        {
            public List<string> Lines { get; } = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add(text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Lines.Add(text);
            }
        }
    }

    /// <summary>A kept world over a file store, with its clock and page probes, for the kept-world fixtures.</summary>
    internal sealed class KeptWorld
    {
        public P3HeadlessSession Session;
        public FileRbxWorldPackageStore Store;
        public DateTime Now = KeptWorlds.StartUtc;
        public bool FlushIdle = true;
        public int OtherPages;
        public readonly List<bool> Guard = new();

        public RbxInstance Workspace => KeptWorlds.Workspace(Session);

        public TerrainChunkStore Terrain => RbxTerrainBootstrap.Find(Workspace).Store;

        public string StartupDirectory => Store.StartupDirectoryForTests;

        public void Pump()
        {
            Session.Controller.PumpFrame(KeptWorlds.Host(), 0.016f);
        }

        public void PlaceBlock(int x, int y, int z)
        {
            Terrain.SetCell(x, y, z, KeptWorlds.Brick(), TerrainOccupancy.Full);
        }

        public object StateAttribute()
        {
            return Workspace.GetAttribute(RbxWorldRuntimeSessionController.WorldSaveStateAttribute);
        }

        public object NoteAttribute()
        {
            return Workspace.GetAttribute(RbxWorldRuntimeSessionController.WorldSaveNoteAttribute);
        }

        /// <summary>Lets the quiet time pass, runs the frame that starts the record, waits for it and runs one more frame.</summary>
        public async Task RecordAfterQuietTime()
        {
            Now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            Pump();
            await KeptWorlds.Within(Session.Controller.StartupRefreshForTests);
            Pump();
        }

        public string[] Entries()
        {
            string[] entries = Directory.Exists(StartupDirectory)
                ? Directory.GetFiles(StartupDirectory, "*" + RbxWorldPackageNames.Extension)
                : Array.Empty<string>();
            Array.Sort(entries, StringComparer.Ordinal);
            return entries;
        }

        public string[] Journals()
        {
            string[] journals = Directory.Exists(StartupDirectory)
                ? Directory.GetFiles(StartupDirectory, "*" + RbxKeptWorldJournal.Extension)
                : Array.Empty<string>();
            Array.Sort(journals, StringComparer.Ordinal);
            return journals;
        }

        public RbxWorldPackagePayload NewestEntry()
        {
            string[] entries = Entries();
            Assert.Greater(entries.Length, 0, "a startup entry was written");
            return RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(entries[entries.Length - 1]));
        }

        public int NewestSequence()
        {
            string[] entries = Entries();
            Assert.Greater(entries.Length, 0, "a startup entry was written");
            return int.Parse(Path.GetFileNameWithoutExtension(entries[entries.Length - 1]),
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Makes kept worlds in temporary directories and deletes them; shared by the kept-world fixtures.</summary>
    internal sealed class KeptWorlds : IDisposable
    {
        public static readonly DateTime StartUtc = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(30d);
        private readonly List<string> _directories = new();

        /// <summary>
        /// A kept world over a file store in a new directory (or <paramref name="root"/>), adopted as the startup world
        /// unless <paramref name="adopt"/> is false, after its first frame.
        /// </summary>
        public KeptWorld New(
            Func<CancellationToken, UniTask<bool>> persistence = null,
            string root = null,
            bool adopt = true,
            Func<CancellationToken, UniTask> frames = null)
        {
            KeptWorld world = new();
            world.Store = new FileRbxWorldPackageStore(
                root ?? NewDirectory(),
                persistenceSyncAsync: persistence ?? (_ => UniTask.FromResult(true)),
                waitBeforeDeferredEncodeAsync: frames,
                encodeFrameBudget: frames != null ? TimeSpan.Zero : (TimeSpan?)null);
            world.Session = new P3HeadlessSession(world.Store);
            world.Session.Controller.ConfigurePendingLoadClockForTests(() => world.Now, TimeSpan.FromMinutes(5d));
            world.Session.Controller.ConfigureSaveStateProbesForTests(
                () => world.FlushIdle, () => world.OtherPages, world.Guard.Add);
            world.Session.Controller.KeepWorldBetweenStarts(null);
            if (adopt)
            {
                world.Session.Controller.AdoptDefaultWorldIfKept();
            }

            // WHY a first frame: the kept world follows its Terrain from the frame pump on.
            world.Pump();
            return world;
        }

        public string NewDirectory()
        {
            string path = P3Fixtures.NewTemporaryDirectory();
            _directories.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (string directory in _directories)
            {
                P3Fixtures.DeleteDirectory(directory);
            }

            _directories.Clear();
        }

        public static RbxInstance Workspace(P3HeadlessSession session)
        {
            return session.Controller.CurrentRbxApi.Game.FindFirstChildOfClass("Workspace");
        }

        /// <summary>
        /// The host that drives the frames, as the engine's frame driver does: the mod runtime's Tick needs
        /// unrestricted host authority.
        /// </summary>
        public static ActorContext Host()
        {
            return CoreAI.Composition.CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        public static ActorContext Actor()
        {
            return new LocalActorIdentityProvider("kept-world-player").GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        public static byte Brick()
        {
            return Material("Brick");
        }

        public static byte Material(string name)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(name, out byte slot), name);
            return slot;
        }

        /// <summary>Asserts that <paramref name="actual"/> holds the cells of <paramref name="expected"/> in <paramref name="region"/>.</summary>
        public static void AssertSameCells(TerrainSnapshot expected, TerrainSnapshot actual, TerrainCellBox region, string what)
        {
            int count = (int)region.CellCount;
            byte[] expectedMaterials = new byte[count];
            byte[] expectedOccupancy = new byte[count];
            byte[] actualMaterials = new byte[count];
            byte[] actualOccupancy = new byte[count];
            TerrainChunkStore expectedCells = new();
            TerrainChunkStore actualCells = new();
            if (expected != null)
            {
                expectedCells.RestoreSnapshot(expected);
            }

            if (actual != null)
            {
                actualCells.RestoreSnapshot(actual);
            }

            expectedCells.ReadBox(region, expectedMaterials, expectedOccupancy);
            actualCells.ReadBox(region, actualMaterials, actualOccupancy);
            int different = 0;
            for (int index = 0; index < count; index++)
            {
                if (expectedMaterials[index] != actualMaterials[index] || expectedOccupancy[index] != actualOccupancy[index])
                {
                    different++;
                }
            }

            Assert.AreEqual(0, different, what + ": cells that differ in " + region);
            Assert.AreEqual(expectedCells.ChunkCount, actualCells.ChunkCount, what + ": chunk count");
        }

        public static async Task Within(Task task)
        {
            if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(AwaitTimeout)), task))
            {
                Assert.Fail("Not finished within " + AwaitTimeout.TotalSeconds + " s.");
            }

            await task;
        }

        public static async Task<T> Within<T>(Task<T> task)
        {
            if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(AwaitTimeout)), task))
            {
                Assert.Fail("Not finished within " + AwaitTimeout.TotalSeconds + " s.");
            }

            return await task;
        }

        public static Task<T> Within<T>(UniTask<T> task)
        {
            return Within(task.AsTask());
        }
    }
}
