using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// Terrain cells in the world package (<c>Terrain/voxels.bin</c>): saved, loaded and restored with the tree,
    /// refused before any side effect when they are not canonical, announced to older readers by the minimum
    /// reader version, and seen by the undo mark.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageTerrainEditModeTests
    {
        private const string WorldId = "worldpkg-terrain";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 13, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// A world with cells saves them in their own entry, raises the minimum reader version, reads back to the
        /// same package bytes, and restores the cells into the fresh world's Terrain.
        /// </summary>
        [Test]
        public void Terrain_WorldWithCells_RoundTripsByteIdentically_AndRestoresTheCells()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(-20, -4, -20, 40, 4, 40), "Grass");
            world.Fill(new TerrainCellBox(0, 0, 0, 3, 5, 3), "Rock");

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            CollectionAssert.Contains(EntryNames(package), RbxWorldPackageSerializer.TerrainEntryName);
            Assert.AreEqual(2, (int)Manifest(package)["minimum_reader_version"]);
            Assert.AreEqual(1, (int)Manifest(package)["format_version"]);
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            Assert.AreEqual(world.Terrain.Store.ChunkCount, read.Terrain.ChunkCount);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read), "save, load, save is byte-identical");

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            RbxTerrain terrain = RbxTerrainBootstrap.Find(restored.Game.FindFirstChildOfClass("Workspace"));
            Assert.IsNotNull(terrain);
            Assert.AreEqual(world.Terrain.Store.SolidCellCount, terrain.Store.SolidCellCount);
            Assert.AreEqual(world.Terrain.Store.GetMaterial(1, 2, 1), terrain.Store.GetMaterial(1, 2, 1));
            Assert.AreEqual(world.Terrain.Store.GetMaterial(-5, -1, 7), terrain.Store.GetMaterial(-5, -1, 7));
            Assert.AreEqual(TerrainMaterials.Air, terrain.Store.GetMaterial(1, 9, 1));
            restored.Game.Destroy();
        }

        /// <summary>
        /// Twin: a world whose Terrain has no cell writes no entry and keeps reader version 1, so its package is
        /// the one every release reads, byte for byte the package of the same world written before the entry.
        /// </summary>
        [Test]
        public void Terrain_WorldWithoutCells_WritesNoEntry_AndKeepsReaderVersionOne()
        {
            TerrainWorld world = TerrainWorld.Create();
            RbxWorldPackagePayload payload = world.Capture();

            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            Assert.IsNull(payload.Terrain);
            CollectionAssert.DoesNotContain(EntryNames(package), RbxWorldPackageSerializer.TerrainEntryName);
            Assert.AreEqual(1, (int)Manifest(package)["minimum_reader_version"]);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package, RbxWorldPackageLimits.Version1Baseline));
            world.Fill(new TerrainCellBox(0, 0, 0, 1, 1, 1), "Sand");
            world.Terrain.Store.Clear();
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(world.Capture()),
                "cells written and cleared again leave the package as it was");
        }

        /// <summary>
        /// A reader that predates the entry accepts reader version 1 only, so it refuses a package with cells by
        /// version; and no package may carry cells under version 1, which such a reader would open without them.
        /// </summary>
        [Test]
        public void Terrain_PackageWithCells_IsNeverReadableAsReaderVersionOne()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(0, 0, 0, 2, 2, 2), "Grass");
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            Assert.Greater((int)Manifest(package)["minimum_reader_version"], RbxWorldPackageSerializer.CurrentFormatVersion,
                "readers up to 7.56.0 accept minimum_reader_version up to CurrentFormatVersion (1)");

            byte[] downgraded = RewriteEntry(package, RbxWorldPackageSerializer.ManifestEntryName,
                text => text.Replace("\"minimum_reader_version\": 2", "\"minimum_reader_version\": 1"));

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(downgraded));
            StringAssert.Contains("declares minimum_reader_version 1", refused.Message);
        }

        /// <summary>
        /// The entry is refused, before anything is restored, when its bytes are not canonical, when it holds no
        /// cell (a writer leaves it out), and when it was cut short.
        /// </summary>
        [Test]
        public void Terrain_NonCanonicalEntry_IsRefusedByTheReader()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(0, 0, 0, 2, 2, 2), "Grass");
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            byte[] cells = EntryBytes(package, RbxWorldPackageSerializer.TerrainEntryName);

            byte[] truncated = new byte[cells.Length - 1];
            Array.Copy(cells, truncated, truncated.Length);
            byte[] empty = TerrainVoxelCodec.Encode(new TerrainChunkStore());
            byte[] badSignature = (byte[])cells.Clone();
            badSignature[0] = (byte)'X';

            foreach (byte[] entry in new[] { truncated, empty, badSignature })
            {
                byte[] broken = ReplaceEntryBytes(package, RbxWorldPackageSerializer.TerrainEntryName, entry);
                RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                    () => RbxWorldPackageSerializer.ReadPackage(broken));
                StringAssert.Contains(RbxWorldPackageSerializer.TerrainEntryName, refused.Message);
            }

            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the canonical entry reads");
        }

        /// <summary>
        /// The proof bounds the entry by its chunk count, so an ordinary block world is proven encodable; a bound
        /// past the entry limit proves nothing and the encode decides.
        /// </summary>
        [Test]
        public void Terrain_Proof_BoundsTheEntryByItsChunks()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(-64, -8, -64, 128, 8, 128), "Grass");
            RbxWorldPackagePayload payload = world.Capture();
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            Assert.IsTrue(RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long bound));
            Assert.GreaterOrEqual(bound, package.LongLength);
            RbxWorldPackageLimits tight = new(4 * 1024 * 1024, 64 * 1024, 8 * 1024 * 1024, 64);
            Assert.IsFalse(RbxWorldPackageSerializer.TryProveEncodable(payload, tight, out long _));
            Assert.IsNotNull(RbxWorldPackageSerializer.WritePackage(payload, tight),
                "the cells of a flat floor compress far below the bound, and the encode says so");
        }

        /// <summary>
        /// A cell write changes the world-state mark the undo service records an edit by, although the Terrain node
        /// is runtime infrastructure; twin: a world nobody wrote keeps its mark.
        /// </summary>
        [Test]
        public void Terrain_CellWrite_ChangesTheUndoMark_AndAQuietWorldKeepsIt()
        {
            TerrainWorld world = TerrainWorld.Create();
            RbxWorldStateMark before = RbxWorldStateMark.Of(world.Registry, 0L);

            Assert.AreEqual(before, RbxWorldStateMark.Of(world.Registry, 0L));
            world.Fill(new TerrainCellBox(0, 0, 0, 1, 1, 1), "Brick");

            Assert.AreNotEqual(before, RbxWorldStateMark.Of(world.Registry, 0L));
        }

        /// <summary>
        /// The snapshot an undo restores carries the cells of the moment it was taken: written after it, they are
        /// not in it, and restoring it puts the earlier cells back with the tree.
        /// </summary>
        [Test]
        public void Terrain_SnapshotTakenBeforeAnEdit_RestoresTheCellsOfThatMoment()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(0, 0, 0, 4, 1, 4), "Grass");
            RbxWorldPackagePayload before = world.Capture();
            world.Fill(new TerrainCellBox(0, 1, 0, 4, 1, 4), "Rock");
            world.Terrain.Store.SetCell(0, 0, 0, TerrainMaterials.Air, 0);

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                RbxWorldPackageSerializer.ReadPackage(RbxWorldPackageSerializer.WritePackage(before)));
            RbxTerrain terrain = RbxTerrainBootstrap.Find(restored.Game.FindFirstChildOfClass("Workspace"));

            Assert.AreEqual(16L, terrain.Store.SolidCellCount);
            Assert.AreEqual(Slot("Grass"), terrain.Store.GetMaterial(0, 0, 0));
            Assert.AreEqual(TerrainMaterials.Air, terrain.Store.GetMaterial(0, 1, 0));
            Assert.AreEqual(32L - 1L, world.Terrain.Store.SolidCellCount, "the live world kept its later cells");
            restored.Game.Destroy();
        }

        /// <summary>Two captures of the same cells digest alike; one changed cell changes the entry digest.</summary>
        [Test]
        public void Terrain_EntryDigest_FollowsTheCells()
        {
            TerrainWorld world = TerrainWorld.Create();
            world.Fill(new TerrainCellBox(0, 0, 0, 4, 1, 4), "Grass");
            byte[] first = RbxWorldPackageSerializer.ComputeEntryDigest(world.Capture());
            byte[] again = RbxWorldPackageSerializer.ComputeEntryDigest(world.Capture());
            world.Terrain.Store.SetCell(3, 0, 3, Slot("Sand"), 255);

            CollectionAssert.AreEqual(first, again);
            CollectionAssert.AreNotEqual(first, RbxWorldPackageSerializer.ComputeEntryDigest(world.Capture()));
        }

        private static byte Slot(string material)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(material, out byte slot), material);
            return slot;
        }

        private static List<string> EntryNames(byte[] package)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            List<string> names = new();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                names.Add(entry.FullName);
            }

            return names;
        }

        private static byte[] EntryBytes(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using MemoryStream output = new();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private static JObject Manifest(byte[] package)
        {
            return JObject.Parse(Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.ManifestEntryName)));
        }

        private static byte[] RewriteEntry(byte[] package, string name, Func<string, string> rewrite)
        {
            string text = new UTF8Encoding(false, true).GetString(EntryBytes(package, name));
            string rewritten = rewrite(text);
            Assert.AreNotEqual(text, rewritten, "precondition: the rewrite changed the entry");
            return ReplaceEntryBytes(package, name, new UTF8Encoding(false, true).GetBytes(rewritten));
        }

        private static byte[] ReplaceEntryBytes(byte[] package, string name, byte[] bytes)
        {
            List<(string Name, byte[] Bytes)> entries = new();
            foreach (string entryName in EntryNames(package))
            {
                entries.Add((entryName, entryName == name ? bytes : EntryBytes(package, entryName)));
            }

            return WorldPackageTestWorlds.BuildZip(entries.ToArray());
        }

        /// <summary>A real data model (its Terrain from the game bootstrap) and its Part store.</summary>
        private sealed class TerrainWorld
        {
            private TerrainWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink, RbxTerrain terrain)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
                Terrain = terrain;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            public RbxTerrain Terrain { get; }

            public static TerrainWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                RbxTerrain terrain = RbxTerrainBootstrap.Find(game.FindFirstChildOfClass("Workspace"));
                Assert.IsNotNull(terrain, "precondition: the game bootstrap gives Workspace its Terrain");
                InMemoryPartPropertySink sink = new(registry);
                RbxInstance part = registry.Create("Part");
                part.Name = "Marker";
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                sink.SetPartProperties(part.Id, in properties);
                part.Parent = registry.WorldRoot;
                return new TerrainWorld(registry, game, sink, terrain);
            }

            public void Fill(TerrainCellBox box, string material)
            {
                Terrain.Store.FillBox(box, Slot(material), 255);
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
