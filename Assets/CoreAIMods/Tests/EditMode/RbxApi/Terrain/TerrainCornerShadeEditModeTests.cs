using System;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free: the corner shade (block-game ambient occlusion) of a face's four corners. An open floor is not
    /// shaded; the floor beside a block darkens at the two corners that touch it, an inner corner between two walls
    /// is fully shaded, a wall's own face darkens where it meets the floor, cells across a chunk border shade as
    /// well, and Water or Air beside a corner shades nothing (twin).
    /// </summary>
    [TestFixture]
    public sealed class TerrainCornerShadeEditModeTests
    {
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");
        private static readonly byte Open = TerrainSkyExposure.CornerShadeLevels[0];
        private static readonly byte OneSide = TerrainSkyExposure.CornerShadeLevels[1];
        private static readonly byte InnerCorner = TerrainSkyExposure.CornerShadeLevels[3];

        private static TerrainChunkStore FloorStore()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.FillBox(new TerrainCellBox(-20, -4, -20, 40, 4, 40), Rock, TerrainOccupancy.Full);
            return store;
        }

        private static byte[] Shade(TerrainChunkStore store, int x, int y, int z, TerrainFace face)
        {
            TerrainSkyExposure exposure = new(store);
            exposure.PrepareChunk(TerrainChunkCoord.FromCell(x, y, z));
            exposure.GetCornerOcclusion(new TerrainFaceQuad(x, y, z, face), out byte corner0, out byte corner1,
                out byte corner2, out byte corner3);
            return new[] { corner0, corner1, corner2, corner3 };
        }

        private static int CountBelow(byte[] corners, byte level)
        {
            int count = 0;
            foreach (byte corner in corners)
            {
                count += corner < level ? 1 : 0;
            }

            return count;
        }

        [Test]
        public void AnOpenFloor_IsNotShaded()
        {
            CollectionAssert.AreEqual(new[] { Open, Open, Open, Open }, Shade(FloorStore(), 3, -1, 3, TerrainFace.PositiveY));
        }

        [Test]
        public void TheFloorBesideABlock_DarkensAtTheTwoCornersThatTouchIt()
        {
            TerrainChunkStore store = FloorStore();
            store.FillBox(new TerrainCellBox(4, 0, 3, 1, 1, 1), Rock, TerrainOccupancy.Full);
            byte[] corners = Shade(store, 3, -1, 3, TerrainFace.PositiveY);
            Assert.AreEqual(2, CountBelow(corners, Open), "two corners touch the block");
            foreach (byte corner in corners)
            {
                Assert.IsTrue(corner == Open || corner == OneSide, "one solid side shades one level");
            }
        }

        [Test]
        public void AnInnerCorner_BetweenTwoWalls_IsFullyShaded()
        {
            TerrainChunkStore store = FloorStore();
            store.FillBox(new TerrainCellBox(4, 0, 0, 1, 1, 8), Rock, TerrainOccupancy.Full);
            store.FillBox(new TerrainCellBox(0, 0, 4, 4, 1, 1), Rock, TerrainOccupancy.Full);
            byte[] corners = Shade(store, 3, -1, 3, TerrainFace.PositiveY);
            Assert.Contains(InnerCorner, corners, "the corner between both walls");
            Assert.AreEqual(3, CountBelow(corners, Open), "three corners touch a wall");
        }

        [Test]
        public void AWallsFace_DarkensWhereItMeetsTheFloor()
        {
            TerrainChunkStore store = FloorStore();
            store.FillBox(new TerrainCellBox(4, 0, 3, 1, 3, 1), Rock, TerrainOccupancy.Full);
            byte[] corners = Shade(store, 4, 0, 3, TerrainFace.NegativeX);
            Assert.AreEqual(2, CountBelow(corners, Open), "the two lower corners meet the floor");
        }

        [Test]
        public void ShadeReachesAcrossAChunkBorder()
        {
            TerrainChunkStore store = FloorStore();
            store.FillBox(new TerrainCellBox(16, 0, 3, 1, 1, 1), Rock, TerrainOccupancy.Full);
            byte[] corners = Shade(store, 15, -1, 3, TerrainFace.PositiveY);
            Assert.AreEqual(2, CountBelow(corners, Open), "a block in the next chunk shades this floor");
        }

        [Test]
        public void WaterOrAirBesideACorner_ShadesNothing_NegativeTwin()
        {
            TerrainChunkStore store = FloorStore();
            store.FillBox(new TerrainCellBox(4, 0, 0, 1, 1, 8), TerrainMaterials.Water, TerrainOccupancy.Full);
            CollectionAssert.AreEqual(new[] { Open, Open, Open, Open }, Shade(store, 3, -1, 3, TerrainFace.PositiveY));
        }

        [Test]
        public void ReadingShadeBeforeAPrepare_IsRefused()
        {
            TerrainSkyExposure exposure = new(FloorStore());
            Assert.Throws<InvalidOperationException>(() => exposure.GetCornerOcclusion(
                new TerrainFaceQuad(0, 0, 0, TerrainFace.PositiveY), out byte _, out byte _, out byte _, out byte _));
        }
    }
}
