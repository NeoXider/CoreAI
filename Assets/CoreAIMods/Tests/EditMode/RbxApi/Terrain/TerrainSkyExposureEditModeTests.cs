using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free: the sky exposure that darkens caves. A cell under the open sky is fully exposed, light
    /// enters sideways under an overhang and fades inward, rock and a one-cell roof keep it out, water does
    /// not block it, the answer is the same whichever chunk asks (checked against a flood of the whole
    /// world), and a write relights only the chunks within reach of what it changed.
    /// </summary>
    [TestFixture]
    public sealed class TerrainSkyExposureEditModeTests
    {
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");

        private static TerrainChunkStore Store()
        {
            return new TerrainChunkStore(TerrainLimits.Desktop);
        }

        private static void Fill(TerrainChunkStore store, int x, int y, int z, int sizeX, int sizeY, int sizeZ,
            byte material)
        {
            store.FillBox(new TerrainCellBox(x, y, z, sizeX, sizeY, sizeZ), material,
                material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full);
        }

        private static TerrainSkyExposure Watch(TerrainChunkStore store)
        {
            TerrainSkyExposure exposure = new(store);
            store.Changed += exposure.OnStoreChanged;
            return exposure;
        }

        /// <summary>The four corner exposures of the face of cell (x, y, z) that looks along <paramref name="face"/>.</summary>
        private static byte[] Corners(TerrainSkyExposure exposure, int x, int y, int z, TerrainFace face)
        {
            exposure.PrepareChunk(TerrainChunkCoord.FromCell(x, y, z));
            exposure.GetCornerExposures(new TerrainFaceQuad(x, y, z, face), out byte corner0, out byte corner1,
                out byte corner2, out byte corner3);
            return new[] { corner0, corner1, corner2, corner3 };
        }

        [Test]
        public void SkyExposure_AnOpenColumn_IsFullyExposed()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            TerrainSkyExposure exposure = Watch(store);

            Assert.AreEqual(exposure.Reach, exposure.SampleLevel(3, 0, 3));
            Assert.AreEqual(1f, exposure.SampleExposure(3, 0, 3));
            Assert.AreEqual(1f, exposure.SampleExposure(15, 0, -16), "a cell on a chunk border");
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, Corners(exposure, 15, -1, 15, TerrainFace.PositiveY));
            Assert.AreEqual(-1, exposure.GetColumnTop(3, 3));
        }

        [Test]
        public void SkyExposure_AStepInTheOpen_LeavesBothTerracesFullyExposed()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            Fill(store, 0, 0, -20, 20, 3, 40, Grass);
            TerrainSkyExposure exposure = Watch(store);

            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, Corners(exposure, -1, -1, 0, TerrainFace.PositiveY),
                "the foot of a three-cell cliff still sees the sky above it");
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, Corners(exposure, 0, 1, 0, TerrainFace.NegativeX),
                "the cliff face itself");
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, Corners(exposure, 1, 2, 0, TerrainFace.PositiveY));
        }

        [Test]
        public void SkyExposure_UnderAOneCellOverhang_IsBetweenZeroAndOne_AndFallsInward()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            Fill(store, 0, 2, -10, 24, 1, 20, Rock);
            TerrainSkyExposure exposure = Watch(store);

            float edge = exposure.SampleExposure(0, 0, 0);
            float inward = exposure.SampleExposure(2, 0, 0);
            float deep = exposure.SampleExposure(8, 0, 0);
            Assert.Greater(edge, 0f);
            Assert.Less(edge, 1f);
            Assert.Less(inward, edge, "light fades going in");
            Assert.AreEqual(0f, deep, "seven cells in, nothing is left");
            byte[] floor = Corners(exposure, 0, -1, 0, TerrainFace.PositiveY);
            Assert.That(floor, Has.Some.GreaterThan(Corners(exposure, 2, -1, 0, TerrainFace.PositiveY)[0]),
                "the floor brightens towards the opening");
        }

        [Test]
        public void SkyExposure_TheSameOverhangWalledIn_IsDark()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            Fill(store, -1, 0, -11, 14, 3, 22, Rock);
            Fill(store, 0, 0, -10, 12, 2, 20, TerrainMaterials.Air);
            TerrainSkyExposure exposure = Watch(store);

            Assert.AreEqual(0f, exposure.SampleExposure(0, 0, 0), "the cell that was the bright edge");
            Assert.AreEqual(0f, exposure.SampleExposure(5, 1, 3));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Corners(exposure, 0, -1, 0, TerrainFace.PositiveY));
        }

        [Test]
        public void SkyExposure_DeepUnderRock_IsZero()
        {
            TerrainChunkStore store = Store();
            Fill(store, -10, -10, -10, 20, 20, 20, Rock);
            Fill(store, -1, -1, -1, 3, 2, 3, TerrainMaterials.Air);
            TerrainSkyExposure exposure = Watch(store);

            Assert.AreEqual(0, exposure.SampleLevel(0, 0, 0));
            Assert.AreEqual(0f, exposure.SampleExposure(0, -1, 0));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Corners(exposure, 0, -2, 0, TerrainFace.PositiveY));
            Assert.AreEqual(1f, exposure.SampleExposure(0, 10, 0), "the cell on top of the block");
        }

        [Test]
        public void SkyExposure_AWaterSurfaceUnderOpenSky_IsFullyExposed_AndCaveWaterIsNot()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -6, -20, 40, 4, 40, Rock);
            Fill(store, -20, -2, -20, 40, 2, 40, TerrainMaterials.Water);
            Fill(store, 4, -2, 4, 10, 6, 10, Rock);
            Fill(store, 5, -2, 5, 8, 3, 8, TerrainMaterials.Water);
            Fill(store, 5, 1, 5, 8, 1, 8, TerrainMaterials.Air);
            TerrainSkyExposure exposure = Watch(store);

            Assert.AreEqual(exposure.Reach, exposure.SampleLevel(-10, -1, -10), "water does not block the sky");
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 },
                Corners(exposure, -10, -1, -10, TerrainFace.PositiveY), "the sea's surface");
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 },
                Corners(exposure, -10, -3, -10, TerrainFace.PositiveY), "the sea floor under open water");
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Corners(exposure, 8, 0, 8, TerrainFace.PositiveY),
                "the surface of a pool sealed under rock");
        }

        [Test]
        public void SkyExposure_AOneCellRoof_LetsNoLightThrough()
        {
            TerrainChunkStore store = Store();
            Fill(store, -30, -4, -30, 60, 4, 60, Rock);
            Fill(store, -16, 0, -16, 32, 3, 32, Rock);
            Fill(store, -15, 0, -15, 30, 2, 30, TerrainMaterials.Air);
            Fill(store, -15, 0, 0, 1, 2, 1, TerrainMaterials.Air);
            Fill(store, -16, 0, 0, 1, 2, 1, TerrainMaterials.Air);
            TerrainSkyExposure exposure = Watch(store);

            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, Corners(exposure, 0, 2, 0, TerrainFace.PositiveY),
                "the roof's top");
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Corners(exposure, 0, 2, 0, TerrainFace.NegativeY),
                "the same cell's underside, one cell below the sky");
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Corners(exposure, 0, -1, 0, TerrainFace.PositiveY));
            Assert.Greater(exposure.SampleExposure(-15, 0, 0), 0f, "the doorway lets light in");
        }

        [Test]
        public void SkyExposure_ACornerNeverLeaksLightAroundTwoBlockingSides()
        {
            TerrainChunkStore store = Store();
            Fill(store, -10, -4, -10, 20, 4, 20, Rock);
            Fill(store, -3, 0, -3, 4, 2, 5, Rock);
            Fill(store, 1, 0, -3, 1, 2, 4, Rock);
            store.SetCell(0, 0, 0, TerrainMaterials.Air, 0);
            TerrainSkyExposure exposure = Watch(store);

            Assert.AreEqual(1f, exposure.SampleExposure(1, 0, 1), "the open cell diagonally outside the pocket");
            Assert.AreEqual(0f, exposure.SampleExposure(0, 0, 0), "the pocket itself is sealed");
            byte[] corners = Corners(exposure, 0, -1, 0, TerrainFace.PositiveY);
            Assert.AreEqual(0, corners[2], "corner (1,1,1): both cells beside it block, so the open diagonal is left out");
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, corners);
        }

        [Test]
        public void SkyExposure_MatchesAFloodOfTheWholeWorld_AcrossChunkBorders()
        {
            TerrainChunkStore store = Store();
            BuildHillWithCaves(store);
            TerrainSkyExposure exposure = Watch(store);
            TerrainCellBox world = new(-36, -10, -36, 72, 36, 72);
            int[,,] expected = FloodWholeWorld(store, world, exposure.Reach);

            int compared = 0;
            int lit = 0;
            for (int y = -6; y < 14; y++)
            {
                for (int z = -20; z < 28; z++)
                {
                    for (int x = -20; x < 28; x++)
                    {
                        if (TerrainSkyExposure.BlocksSky(store.GetMaterial(x, y, z)))
                        {
                            continue;
                        }

                        int want = expected[x - world.MinX, y - world.MinY, z - world.MinZ];
                        Assert.AreEqual(want, exposure.SampleLevel(x, y, z), "level of cell (" + x + ", " + y + ", " + z + ")");
                        compared++;
                        lit += want > 0 && want < exposure.Reach ? 1 : 0;
                    }
                }
            }

            Assert.Greater(compared, 1000);
            Assert.Greater(lit, 50, "the world has cells lit only from the side");
        }

        [Test]
        public void SkyExposure_OpeningAShaft_RaisesExposureBelowIt_AndNowhereElse()
        {
            TerrainChunkStore store = Store();
            BuildHillWithCaves(store);
            TerrainSkyExposure exposure = Watch(store);
            TerrainCellBox probe = new(-20, -6, -20, 40, 20, 40);
            float[,,] before = SampleAll(exposure, store, probe);
            List<TerrainChunkCoord> relit = new();
            store.Changed += change => exposure.CollectLightAffectedChunks(change, relit);

            Fill(store, 10, 2, 1, 1, 9, 1, TerrainMaterials.Air);

            float[,,] after = SampleAll(exposure, store, probe);
            int raised = 0;
            for (int x = 0; x < probe.SizeX; x++)
            {
                for (int y = 0; y < probe.SizeY; y++)
                {
                    for (int z = 0; z < probe.SizeZ; z++)
                    {
                        if (float.IsNaN(after[x, y, z]) || float.IsNaN(before[x, y, z]))
                        {
                            continue;
                        }

                        int cx = probe.MinX + x;
                        int cy = probe.MinY + y;
                        int cz = probe.MinZ + z;
                        int distance = Math.Abs(cx - 10) + Math.Max(0, -cy) + Math.Max(0, cy - 10) + Math.Abs(cz - 1);
                        if (after[x, y, z] > before[x, y, z])
                        {
                            raised++;
                            Assert.Less(distance, exposure.Reach + 1,
                                "only cells within reach of the shaft gain light: (" + cx + ", " + cy + ", " + cz + ")");
                        }
                        else
                        {
                            Assert.AreEqual(before[x, y, z], after[x, y, z],
                                "no cell loses light: (" + cx + ", " + cy + ", " + cz + ")");
                        }
                    }
                }
            }

            Assert.Greater(raised, 10, "the tunnel under the shaft is lit");
            Assert.AreEqual(1f, exposure.SampleExposure(10, 0, 1), "straight under the shaft is open sky");
            Assert.Greater(exposure.SampleExposure(7, 0, 1), 0f);
            Assert.AreEqual(0f, exposure.SampleExposure(-6, 0, 1), "the middle of the tunnel stays dark");
            CollectionAssert.Contains(relit, new TerrainChunkCoord(0, -1, 0), "the chunk of the tunnel's floor");
            CollectionAssert.DoesNotContain(relit, new TerrainChunkCoord(-2, 0, 0), "a chunk out of reach");
        }

        [Test]
        public void SkyExposure_PlacingABlockOnAnOpenPlain_RelightsNoChunk()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            TerrainSkyExposure exposure = Watch(store);
            List<TerrainChunkCoord> relit = new();
            store.Changed += change => exposure.CollectLightAffectedChunks(change, relit);

            store.SetCell(15, 0, 15, Rock, TerrainOccupancy.Full);
            store.SetCell(15, 0, 15, TerrainMaterials.Air, 0);

            CollectionAssert.IsEmpty(relit);
        }

        [Test]
        public void SkyExposure_CarvingASealedCavityDeepInRock_RelightsNoChunk()
        {
            TerrainChunkStore store = Store();
            Fill(store, -40, -40, -40, 80, 40, 80, Rock);
            TerrainSkyExposure exposure = Watch(store);
            List<TerrainChunkCoord> relit = new();
            store.Changed += change => exposure.CollectLightAffectedChunks(change, relit);

            store.SetCell(0, -20, 0, TerrainMaterials.Air, 0);

            CollectionAssert.IsEmpty(relit);
        }

        [Test]
        public void SkyExposure_AFloatingBlockAbovePlainGround_ShadesTheGroundBelowIt_AndRelightsItsChunk()
        {
            TerrainChunkStore store = Store();
            Fill(store, -20, -4, -20, 40, 4, 40, Grass);
            TerrainSkyExposure exposure = Watch(store);
            List<TerrainChunkCoord> relit = new();
            store.Changed += change => exposure.CollectLightAffectedChunks(change, relit);

            store.SetCell(5, 4, 5, Rock, TerrainOccupancy.Full);

            Assert.Less(exposure.SampleExposure(5, 0, 5), 1f, "the cell under the block no longer sees straight up");
            Assert.Greater(exposure.SampleExposure(5, 0, 5), 0.5f, "but it is open on every side");
            CollectionAssert.Contains(relit, new TerrainChunkCoord(0, -1, 0), "the ground's chunk is relit");
        }

        [Test]
        public void SkyExposure_WarmChunkLighting_AllocatesNothing()
        {
            TerrainChunkStore store = Store();
            BuildHillWithCaves(store);
            TerrainSkyExposure exposure = Watch(store);
            TerrainChunkCoord coord = new(0, 0, 0);
            exposure.PrepareChunk(coord);
            TerrainFaceQuad quad = new(3, -1, 1, TerrainFace.PositiveY);

            long bytes = P4Allocation.Measure(() =>
            {
                exposure.PrepareChunk(coord);
                exposure.GetCornerExposures(in quad, out byte _, out byte _, out byte _, out byte _);
                exposure.SampleExposure(3, 0, 1);
            });

            Assert.AreEqual(0, bytes);
        }

        [Test]
        public void SkyExposure_ReadingCornersBeforeAnyChunkIsPrepared_Throws()
        {
            TerrainSkyExposure exposure = new(Store());

            Assert.Throws<InvalidOperationException>(() =>
                exposure.GetCornerExposures(new TerrainFaceQuad(0, 0, 0, TerrainFace.PositiveY), out byte _,
                    out byte _, out byte _, out byte _));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainSkyExposure(Store(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainSkyExposure(Store(), TerrainSkyExposure.MaxReach + 1));
        }

        [Test]
        public void ChangeSet_ChangedCells_IsTheBoxOfTheCellsThatChanged_AndEmptyForAReset()
        {
            TerrainChunkStore store = Store();
            Fill(store, 0, 0, 0, 4, 1, 4, Rock);
            List<TerrainCellBox> boxes = new();
            store.Changed += change => boxes.Add(change.ChangedCells);

            Fill(store, -2, 0, 1, 6, 1, 1, Rock);
            store.SetCell(17, 3, -5, Grass, TerrainOccupancy.Full);
            store.Clear();

            Assert.AreEqual(TerrainCellBox.FromMinMax(-2, 0, 1, 0, 1, 2), boxes[0],
                "the four cells that were rock already are left out");
            Assert.AreEqual(new TerrainCellBox(17, 3, -5, 1, 1, 1), boxes[1]);
            Assert.IsTrue(boxes[2].IsEmpty, "a reset names chunks, not cells");
        }

        /// <summary>
        /// A hill over a ground plate with a tunnel through it (mouth in a cliff face at x = -16), a flooded
        /// stretch, a side chamber under a one-cell roof with a doorway, and a sealed cavity; it spans eight chunks.
        /// </summary>
        private static void BuildHillWithCaves(TerrainChunkStore store)
        {
            Fill(store, -32, -6, -32, 64, 6, 64, Rock);
            Fill(store, -16, 0, -16, 32, 11, 32, Rock);
            Fill(store, -16, 0, 0, 30, 2, 3, TerrainMaterials.Air);
            Fill(store, 2, -2, 0, 6, 2, 3, TerrainMaterials.Air);
            Fill(store, 2, -2, 0, 6, 1, 3, TerrainMaterials.Water);
            Fill(store, -6, 0, -10, 8, 2, 8, TerrainMaterials.Air);
            Fill(store, -3, 0, -2, 1, 2, 2, TerrainMaterials.Air);
            Fill(store, 9, 6, -12, 3, 2, 3, TerrainMaterials.Air);
            Fill(store, 20, 0, 20, 6, 4, 6, Rock);
            Fill(store, 21, 0, 21, 4, 3, 4, TerrainMaterials.Air);
            Fill(store, 21, 0, 23, 5, 2, 1, TerrainMaterials.Air);
        }

        private static float[,,] SampleAll(TerrainSkyExposure exposure, TerrainChunkStore store, TerrainCellBox box)
        {
            float[,,] values = new float[box.SizeX, box.SizeY, box.SizeZ];
            for (int x = 0; x < box.SizeX; x++)
            {
                for (int y = 0; y < box.SizeY; y++)
                {
                    for (int z = 0; z < box.SizeZ; z++)
                    {
                        int cx = box.MinX + x;
                        int cy = box.MinY + y;
                        int cz = box.MinZ + z;
                        values[x, y, z] = TerrainSkyExposure.BlocksSky(store.GetMaterial(cx, cy, cz))
                            ? float.NaN
                            : exposure.SampleExposure(cx, cy, cz);
                    }
                }
            }

            return values;
        }

        /// <summary>
        /// The reference answer, computed independently of the windowed one: every non-blocking cell of the
        /// box that no blocking cell covers is a source at full level, and a breadth-first flood through
        /// non-blocking cells lowers the level by one per step. The box must leave every block of the world
        /// at least <paramref name="reach"/> cells from its sides.
        /// </summary>
        private static int[,,] FloodWholeWorld(TerrainChunkStore store, TerrainCellBox box, int reach)
        {
            int[,,] levels = new int[box.SizeX, box.SizeY, box.SizeZ];
            bool[,,] blocking = new bool[box.SizeX, box.SizeY, box.SizeZ];
            Queue<(int, int, int)> queue = new();
            for (int x = 0; x < box.SizeX; x++)
            {
                for (int z = 0; z < box.SizeZ; z++)
                {
                    bool covered = false;
                    for (int y = box.SizeY - 1; y >= 0; y--)
                    {
                        bool blocks = TerrainSkyExposure.BlocksSky(store.GetMaterial(box.MinX + x, box.MinY + y, box.MinZ + z));
                        blocking[x, y, z] = blocks;
                        covered |= blocks;
                        if (!blocks && !covered)
                        {
                            levels[x, y, z] = reach;
                            queue.Enqueue((x, y, z));
                        }
                    }
                }
            }

            (int, int, int)[] steps = { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
            while (queue.Count > 0)
            {
                (int x, int y, int z) = queue.Dequeue();
                int next = levels[x, y, z] - 1;
                if (next <= 0)
                {
                    continue;
                }

                foreach ((int dx, int dy, int dz) in steps)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    int nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= box.SizeX || ny >= box.SizeY || nz >= box.SizeZ
                        || blocking[nx, ny, nz] || levels[nx, ny, nz] >= next)
                    {
                        continue;
                    }

                    levels[nx, ny, nz] = next;
                    queue.Enqueue((nx, ny, nz));
                }
            }

            return levels;
        }
    }
}
