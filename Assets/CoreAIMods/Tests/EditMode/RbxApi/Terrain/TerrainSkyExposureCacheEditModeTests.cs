using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free contracts of the exposure pass's two savings: a chunk prepared again with nothing changed near it
    /// takes the levels it kept and reads the same corners; an edit next to a chunk's border drops them, so the chunk
    /// is lit again and its corners change, while an edit far away recomputes nothing (twin); a write whose light was
    /// never collected drops every kept level; and the flood stops at the window's highest column top.
    /// </summary>
    [TestFixture]
    public sealed class TerrainSkyExposureCacheEditModeTests
    {
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");
        private static readonly TerrainChunkCoord Ground = new(0, -1, 0);

        private static TerrainChunkStore Plain()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.FillBox(new TerrainCellBox(-40, -4, -40, 200, 4, 200), Grass, TerrainOccupancy.Full);
            return store;
        }

        /// <summary>Lights the store's changes as the renderer does: the top map first, then the light it collects.</summary>
        private static TerrainSkyExposure WatchLikeTheRenderer(TerrainChunkStore store)
        {
            TerrainSkyExposure exposure = new(store);
            List<TerrainChunkCoord> relit = new();
            store.Changed += exposure.OnStoreChanged;
            store.Changed += change =>
            {
                relit.Clear();
                exposure.CollectLightAffectedChunks(change, relit);
            };
            return exposure;
        }

        private static byte[] Shade(TerrainSkyExposure exposure, TerrainChunkCoord coord, int x, int y, int z)
        {
            exposure.PrepareChunk(coord);
            exposure.GetCornerOcclusion(new TerrainFaceQuad(x, y, z, TerrainFace.PositiveY), out byte corner0,
                out byte corner1, out byte corner2, out byte corner3);
            return new[] { corner0, corner1, corner2, corner3 };
        }

        [Test]
        public void PreparingAChunkAgain_WithNothingChanged_TakesItsKeptLevels_AndReadsTheSame()
        {
            TerrainChunkStore store = Plain();
            store.SetCell(3, 1, 3, Rock, TerrainOccupancy.Full);
            TerrainSkyExposure exposure = WatchLikeTheRenderer(store);

            byte[] first = Shade(exposure, Ground, 3, -1, 3);
            byte[] second = Shade(exposure, Ground, 3, -1, 3);

            Assert.AreEqual(1, exposure.ChunkLightCount, "lit once");
            Assert.AreEqual(1, exposure.ChunkCacheHitCount, "then taken from what it kept");
            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void AnEditNextToAChunkBorder_RelightsTheNeighbour()
        {
            TerrainChunkStore store = Plain();
            TerrainSkyExposure exposure = WatchLikeTheRenderer(store);
            byte[] before = Shade(exposure, Ground, 15, -1, 5);
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, before, "open ground, no shade");

            store.SetCell(16, 0, 5, Rock, TerrainOccupancy.Full);
            long lit = exposure.ChunkLightCount;
            byte[] after = Shade(exposure, Ground, 15, -1, 5);

            Assert.AreEqual(lit + 1, exposure.ChunkLightCount, "the block in the next chunk drops what this chunk kept");
            CollectionAssert.AreNotEqual(before, after, "and the corners beside it are shaded now");
        }

        [Test]
        public void AnEditFarAway_RecomputesNothing_NegativeTwin()
        {
            TerrainChunkStore store = Plain();
            TerrainSkyExposure exposure = WatchLikeTheRenderer(store);
            Shade(exposure, Ground, 15, -1, 5);

            store.SetCell(140, 0, 140, Rock, TerrainOccupancy.Full);
            long lit = exposure.ChunkLightCount;
            long hits = exposure.ChunkCacheHitCount;
            Shade(exposure, Ground, 15, -1, 5);

            Assert.AreEqual(lit, exposure.ChunkLightCount, "nothing near the chunk changed: no light pass");
            Assert.AreEqual(hits + 1, exposure.ChunkCacheHitCount);
        }

        [Test]
        public void AWriteWhoseLightIsNeverCollected_DropsEveryKeptLevel()
        {
            TerrainChunkStore store = Plain();
            TerrainSkyExposure exposure = new(store);
            store.Changed += exposure.OnStoreChanged;
            Shade(exposure, Ground, 15, -1, 5);

            store.SetCell(140, 0, 140, Rock, TerrainOccupancy.Full);
            long lit = exposure.ChunkLightCount;
            Shade(exposure, Ground, 15, -1, 5);

            Assert.AreEqual(lit + 1, exposure.ChunkLightCount,
                "a caller that only watches the store still reads fresh light");
        }

        [Test]
        public void TheFlood_StopsAtTheWindowsHighestColumnTop_AndLightsUnderAnOverhangAsBefore()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.FillBox(new TerrainCellBox(-20, 0, -20, 40, 18, 40), Grass, TerrainOccupancy.Full);
            store.SetCell(0, 19, 0, Rock, TerrainOccupancy.Full);
            TerrainSkyExposure exposure = WatchLikeTheRenderer(store);
            TerrainChunkCoord above = new(0, 1, 0);

            exposure.PrepareChunk(above);
            exposure.GetCornerExposures(new TerrainFaceQuad(0, 17, 0, TerrainFace.PositiveY), out byte corner0,
                out byte _, out byte _, out byte _);

            int windowRows = TerrainChunkCoord.Size + 2 + 2 * exposure.Reach;
            Assert.Less(exposure.LastFloodRows, windowRows, "the rows above the highest top are not flooded");
            Assert.AreEqual(19 - (16 - 1 - exposure.Reach) + 2, exposure.LastFloodRows,
                "up to the row just above the highest top (y 19)");
            Assert.Less(corner0, TerrainSkyExposure.FullExposure, "the ground under the overhang is shaded");
            Assert.Greater(corner0, 0, "but lit from the open sides");
        }
    }
}
