using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: the voxel chunk store behind <c>workspace.Terrain</c> — sparse chunks, cell
    /// normalisation, one change event per operation with the exact re-mesh set, budgets that refuse a
    /// write without touching a cell, bounds, copy-on-write snapshots and the allocation cost of a write.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainChunkStoreEditModeTests
    {
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");
        private static readonly byte Sand = P4TerrainTestWorld.Slot("Sand");

        [Test]
        public void P4_Store_EmptyStore_ReadsAirEverywhere_AndAllocatesNothing()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());

            store.GetCell(3, -7, 12, out byte material, out byte occupancy);

            Assert.AreEqual(TerrainMaterials.Air, material);
            Assert.AreEqual(0, occupancy);
            Assert.AreEqual(0, store.ChunkCount);
            Assert.AreEqual(0, store.SolidCellCount);
        }

        [Test]
        public void P4_Store_SetCell_AllocatesOneChunk_AndReadsBack()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());

            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);

            store.GetCell(3, 4, 5, out byte material, out byte occupancy);
            Assert.AreEqual(Grass, material);
            Assert.AreEqual(TerrainOccupancy.Full, occupancy);
            Assert.AreEqual(1, store.ChunkCount);
            Assert.AreEqual(1, store.SolidCellCount);
            Assert.IsTrue(store.TryGetChunk(new TerrainChunkCoord(0, 0, 0), out TerrainChunk chunk));
            Assert.AreEqual(1, chunk.SolidCount);
        }

        [Test]
        public void P4_Store_NegativeCell_LivesInTheNegativeChunk_AtLocal15()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());

            store.SetCell(-1, -16, -17, Rock, TerrainOccupancy.Full);

            Assert.IsTrue(store.TryGetChunk(new TerrainChunkCoord(-1, -1, -2), out TerrainChunk chunk));
            Assert.AreEqual(Rock, chunk.Materials[TerrainChunk.IndexOf(15, 0, 15)]);
            Assert.AreEqual(Rock, store.GetMaterial(-1, -16, -17));
        }

        [Test]
        public void P4_Store_ClearingTheLastCell_ReleasesTheChunk()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            store.SetCell(1, 1, 1, Grass, TerrainOccupancy.Full);

            store.SetCell(1, 1, 1, TerrainMaterials.Air, 0);

            Assert.AreEqual(0, store.ChunkCount);
            Assert.AreEqual(0, store.SolidCellCount);
        }

        [Test]
        public void P4_Store_ZeroOccupancyIsAir_AndAirHasZeroOccupancy()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());

            store.SetCell(0, 0, 0, Grass, 0);
            store.SetCell(1, 0, 0, TerrainMaterials.Air, TerrainOccupancy.Full);

            Assert.AreEqual(0, store.SolidCellCount, "neither write makes a cell");
            store.GetCell(1, 0, 0, out byte material, out byte occupancy);
            Assert.AreEqual(TerrainMaterials.Air, material);
            Assert.AreEqual(0, occupancy);
        }

        [Test]
        public void P4_Store_Changed_FiresOncePerOperation_WithTheTouchedChunks()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            List<List<TerrainChunkCoord>> events = new();
            store.Changed += change => events.Add(new List<TerrainChunkCoord>(change.ChangedChunks));

            store.FillBox(new TerrainCellBox(0, 0, 0, 20, 2, 2), Grass, TerrainOccupancy.Full);

            Assert.AreEqual(1, events.Count, "a 20-cell-wide fill is one operation");
            CollectionAssert.AreEqual(
                new[] { new TerrainChunkCoord(0, 0, 0), new TerrainChunkCoord(1, 0, 0) }, events[0]);
        }

        [Test]
        public void P4_Store_WritingTheSameValueAgain_RaisesNothing()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            store.FillBox(new TerrainCellBox(0, 0, 0, 4, 4, 4), Grass, TerrainOccupancy.Full);
            int events = 0;
            long revision = store.Revision;
            store.Changed += _ => events++;

            store.FillBox(new TerrainCellBox(0, 0, 0, 4, 4, 4), Grass, TerrainOccupancy.Full);

            Assert.AreEqual(0, events);
            Assert.AreEqual(revision, store.Revision, "an unchanged write does not advance the revision");
        }

        [Test]
        public void P4_Store_InteriorCellChange_RemeshesOnlyItsChunk()
        {
            TerrainChunkStore store = FilledThreeByOne();
            TerrainChangeSet seen = null;
            List<TerrainChunkCoord> remesh = new();
            store.Changed += change =>
            {
                seen = change;
                remesh.AddRange(change.RemeshChunks);
            };

            store.SetCell(24, 8, 8, TerrainMaterials.Air, 0);

            Assert.IsNotNull(seen);
            CollectionAssert.AreEqual(new[] { new TerrainChunkCoord(1, 0, 0) }, remesh);
        }

        [Test]
        public void P4_Store_BorderCellChange_RemeshesItsChunkAndTheNeighbourSharingTheFace()
        {
            TerrainChunkStore store = FilledThreeByOne();
            List<TerrainChunkCoord> remesh = new();
            store.Changed += change => remesh.AddRange(change.RemeshChunks);

            store.SetCell(16, 8, 8, TerrainMaterials.Air, 0);

            CollectionAssert.AreEqual(
                new[] { new TerrainChunkCoord(0, 0, 0), new TerrainChunkCoord(1, 0, 0) }, remesh,
                "local x = 0 shares a face with the -X chunk only; the +X chunk is untouched");
        }

        [Test]
        public void P4_Store_BorderCellChange_NextToAnUnallocatedChunk_RemeshesOnlyItsChunk()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            List<TerrainChunkCoord> remesh = new();
            store.Changed += change =>
            {
                remesh.Clear();
                remesh.AddRange(change.RemeshChunks);
            };

            store.SetCell(15, 0, 0, Grass, TerrainOccupancy.Full);

            CollectionAssert.AreEqual(new[] { new TerrainChunkCoord(0, 0, 0) }, remesh);
        }

        [Test]
        public void P4_Store_FillPastTheCellBudget_IsRefused_AndChangesNothing()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100));
            store.FillBox(new TerrainCellBox(0, 0, 0, 5, 5, 2), Grass, TerrainOccupancy.Full);
            int events = 0;
            store.Changed += _ => events++;

            RbxError error = Assert.Throws<RbxError>(() =>
                store.FillBox(new TerrainCellBox(0, 0, 2, 5, 5, 3), Rock, TerrainOccupancy.Full));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            StringAssert.Contains("125", error.Message, "the message names the cell count the write would reach");
            Assert.AreEqual(50, store.SolidCellCount);
            Assert.AreEqual(TerrainMaterials.Air, store.GetMaterial(0, 0, 2));
            Assert.AreEqual(0, events);
        }

        [Test]
        public void P4_Store_FillExactlyToTheCellBudget_Passes()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100));
            store.FillBox(new TerrainCellBox(0, 0, 0, 5, 5, 2), Grass, TerrainOccupancy.Full);

            store.FillBox(new TerrainCellBox(0, 0, 2, 5, 5, 2), Rock, TerrainOccupancy.Full);

            Assert.AreEqual(100, store.SolidCellCount);
        }

        [Test]
        public void P4_Store_OverwritingSolidCells_DoesNotCountAgainstTheBudget()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100));
            store.FillBox(new TerrainCellBox(0, 0, 0, 10, 10, 1), Grass, TerrainOccupancy.Full);

            store.FillBox(new TerrainCellBox(0, 0, 0, 10, 10, 1), Rock, TerrainOccupancy.Full);

            Assert.AreEqual(100, store.SolidCellCount);
            Assert.AreEqual(Rock, store.GetMaterial(9, 9, 0));
        }

        [Test]
        public void P4_Store_WritePastTheChunkBudget_IsRefused()
        {
            TerrainChunkStore store = new(new TerrainLimits(-64, -64, -64, 64, 64, 64, 100_000, 2));

            RbxError error = Assert.Throws<RbxError>(() =>
                store.FillBox(new TerrainCellBox(0, 0, 0, 48, 1, 1), Grass, TerrainOccupancy.Full));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            StringAssert.Contains("chunks", error.Message);
            Assert.AreEqual(0, store.ChunkCount);
        }

        [Test]
        public void P4_Store_WriteWithinTheChunkBudget_Passes()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100_000, maxChunks: 2));

            store.FillBox(new TerrainCellBox(0, 0, 0, 32, 1, 1), Grass, TerrainOccupancy.Full);

            Assert.AreEqual(2, store.ChunkCount);
        }

        [Test]
        public void P4_Store_FillOutsideTheBounds_IsBadArgument_AndAnOrientedFillIsClipped()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());

            RbxError error = Assert.Throws<RbxError>(() =>
                store.FillBox(new TerrainCellBox(30, 0, 0, 4, 1, 1), Grass, TerrainOccupancy.Full));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("bounds", error.Message);

            TerrainBoxRasterizer block = new(RbxCFrame.FromPosition(128f, 2f, 2f), new RbxVector3(16f, 4f, 4f));
            store.FillOrientedBox(block, Grass, TerrainOccupancy.Full);

            Assert.AreEqual(Grass, store.GetMaterial(31, 0, 0), "the in-bounds cells are filled");
            Assert.AreEqual(2, store.SolidCellCount, "cells 30 and 31; 32 and 33 lie outside");
        }

        [Test]
        public void P4_Store_WriteBoxAndReadBox_UseTheNestedArrayOrder_ZFastest()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            TerrainCellBox box = new(-1, 2, 3, 2, 3, 4);
            byte[] materials = new byte[box.CellCount];
            byte[] occupancy = new byte[box.CellCount];
            materials[box.RegionIndexOf(-1, 2, 3)] = Grass;
            occupancy[box.RegionIndexOf(-1, 2, 3)] = TerrainOccupancy.Full;
            materials[box.RegionIndexOf(0, 4, 6)] = Sand;
            occupancy[box.RegionIndexOf(0, 4, 6)] = 128;

            store.WriteBox(box, materials, occupancy);

            Assert.AreEqual(0, box.RegionIndexOf(-1, 2, 3));
            Assert.AreEqual(1, box.RegionIndexOf(-1, 2, 4), "z varies fastest");
            Assert.AreEqual(4, box.RegionIndexOf(-1, 3, 3), "then y");
            Assert.AreEqual(12, box.RegionIndexOf(0, 2, 3), "then x");
            Assert.AreEqual(Grass, store.GetMaterial(-1, 2, 3));
            store.GetCell(0, 4, 6, out byte sand, out byte half);
            Assert.AreEqual(Sand, sand);
            Assert.AreEqual(128, half);

            byte[] readMaterials = new byte[box.CellCount];
            byte[] readOccupancy = new byte[box.CellCount];
            store.ReadBox(box, readMaterials, readOccupancy);
            CollectionAssert.AreEqual(materials, readMaterials);
            CollectionAssert.AreEqual(occupancy, readOccupancy);
        }

        [Test]
        public void P4_Store_ReplaceMaterial_AirSourceFillsEmptyCells_AirTargetClears()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            store.FillBox(new TerrainCellBox(0, 0, 0, 4, 1, 1), Grass, TerrainOccupancy.Full);
            store.SetCell(1, 0, 0, Sand, 100);

            long replaced = store.ReplaceMaterial(new TerrainCellBox(0, 0, 0, 4, 1, 1), Grass, Rock);
            Assert.AreEqual(3, replaced);
            store.GetCell(1, 0, 0, out byte untouched, out byte keptOccupancy);
            Assert.AreEqual(Sand, untouched);
            Assert.AreEqual(100, keptOccupancy);

            store.ReplaceMaterial(new TerrainCellBox(0, 0, 0, 6, 1, 1), TerrainMaterials.Air, TerrainMaterials.Water);
            Assert.AreEqual(TerrainMaterials.Water, store.GetMaterial(5, 0, 0), "Air as the source selects empty cells");

            store.ReplaceMaterial(new TerrainCellBox(0, 0, 0, 6, 1, 1), Rock, TerrainMaterials.Air);
            Assert.AreEqual(TerrainMaterials.Air, store.GetMaterial(0, 0, 0), "Air as the target empties cells");
            Assert.AreEqual(3, store.SolidCellCount, "Sand and two Water cells remain");
        }

        [Test]
        public void P4_Store_Clear_ReportsEveryChunk_AsAReset()
        {
            TerrainChunkStore store = FilledThreeByOne();
            TerrainChangeSet seen = null;
            int changed = 0;
            store.Changed += change =>
            {
                seen = change;
                changed = change.ChangedChunks.Count;
            };

            store.Clear();

            Assert.IsTrue(seen.IsReset);
            Assert.AreEqual(3, changed);
            Assert.AreEqual(0, store.ChunkCount);
            Assert.AreEqual(0, store.SolidCellCount);
        }

        [Test]
        public void P4_Store_ClearOfAnEmptyStore_RaisesNothing()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            int events = 0;
            store.Changed += _ => events++;

            store.Clear();

            Assert.AreEqual(0, events);
        }

        [Test]
        public void P4_Store_ChangedHandlerWritingToTheStore_IsRefused()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny());
            store.Changed += _ => store.SetCell(9, 9, 9, Rock, TerrainOccupancy.Full);

            Assert.Throws<InvalidOperationException>(() => store.SetCell(0, 0, 0, Grass, TerrainOccupancy.Full));
            Assert.AreEqual(Grass, store.GetMaterial(0, 0, 0), "the outer write itself completed");
            Assert.AreEqual(TerrainMaterials.Air, store.GetMaterial(9, 9, 9));
        }

        [Test]
        public void P4_Store_Snapshot_SharesChunks_AndAWriteAfterwardsDoesNotReachIt()
        {
            TerrainChunkStore store = FilledThreeByOne();
            store.TryGetChunk(new TerrainChunkCoord(0, 0, 0), out TerrainChunk before);

            TerrainSnapshot snapshot = store.CaptureSnapshot();
            store.SetCell(1, 1, 1, Sand, TerrainOccupancy.Full);

            Assert.IsTrue(snapshot.TryGetChunk(new TerrainChunkCoord(0, 0, 0), out TerrainChunk captured));
            Assert.AreSame(before, captured, "capture shares the chunk instead of copying it");
            Assert.AreEqual(Grass, captured.Materials[TerrainChunk.IndexOf(1, 1, 1)]);
            store.TryGetChunk(new TerrainChunkCoord(0, 0, 0), out TerrainChunk after);
            Assert.AreNotSame(before, after, "the first write copied the shared chunk");
            Assert.IsTrue(store.TryGetChunk(new TerrainChunkCoord(1, 0, 0), out TerrainChunk untouched));
            Assert.IsTrue(snapshot.TryGetChunk(new TerrainChunkCoord(1, 0, 0), out TerrainChunk sharedUntouched));
            Assert.AreSame(sharedUntouched, untouched, "chunks nobody wrote stay shared");
        }

        [Test]
        public void P4_Store_RestoreSnapshot_PutsTheCellsBack_AndReportsOnlyTheChunksThatDiffer()
        {
            TerrainChunkStore store = FilledThreeByOne();
            TerrainSnapshot snapshot = store.CaptureSnapshot();
            store.SetCell(1, 1, 1, Sand, TerrainOccupancy.Full);
            store.SetCell(4, 20, 1, Rock, TerrainOccupancy.Full);
            List<TerrainChunkCoord> changed = new();
            store.Changed += change => changed.AddRange(change.ChangedChunks);

            store.RestoreSnapshot(snapshot);

            Assert.AreEqual(Grass, store.GetMaterial(1, 1, 1));
            Assert.AreEqual(TerrainMaterials.Air, store.GetMaterial(4, 20, 1));
            Assert.AreEqual(snapshot.SolidCellCount, store.SolidCellCount);
            CollectionAssert.AreEquivalent(
                new[] { new TerrainChunkCoord(0, 0, 0), new TerrainChunkCoord(0, 1, 0) }, changed);
        }

        [Test]
        public void P4_Store_SnapshotCapture_CostsOneReferencePerChunk_NotTheCells()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.FillBox(new TerrainCellBox(0, 0, 0, 128, 16, 128), Grass, TerrainOccupancy.Full);
            Assert.AreEqual(64, store.ChunkCount);

            long bytes = P4Allocation.Measure(() => store.CaptureSnapshot());

            Assert.Less(bytes, 64 * 64, "capturing 64 chunks (512 KiB of cells) allocates the map only, got "
                                        + bytes + " bytes");
        }

        [Test]
        public void P4_Store_RepeatedSingleCellWrites_AllocateNothing_OnceWarm()
        {
            TerrainChunkStore store = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100_000));
            store.FillBox(new TerrainCellBox(0, 0, 0, 16, 16, 16), Grass, TerrainOccupancy.Full);
            store.SetCell(15, 15, 15, Rock, TerrainOccupancy.Full);
            store.SetCell(15, 15, 15, Grass, TerrainOccupancy.Full);

            long bytes = P4Allocation.Measure(() =>
            {
                for (int index = 0; index < 100; index++)
                {
                    store.SetCell(index % 16, 3, 3, (index & 1) == 0 ? Rock : Grass, TerrainOccupancy.Full);
                }
            });

            Assert.AreEqual(0, bytes, "placing and breaking cells in an allocated chunk allocates nothing");
        }

        private static TerrainChunkStore FilledThreeByOne()
        {
            TerrainChunkStore store = new(new TerrainLimits(-64, -64, -64, 64, 64, 64, 100_000, 64));
            store.FillBox(new TerrainCellBox(0, 0, 0, 48, 16, 16), Grass, TerrainOccupancy.Full);
            return store;
        }
    }
}
