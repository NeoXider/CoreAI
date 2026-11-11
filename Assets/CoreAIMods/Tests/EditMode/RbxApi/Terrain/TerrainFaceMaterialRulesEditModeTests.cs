using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free: the face rule that draws a Grass cell as a grass block. Grass on top, earth on the sides with a
    /// grass band along their top edge, earth underneath; a Grass cell under a solid cell keeps plain earth sides;
    /// Ground and LeafyGrass draw as before; the plain rules draw every material on every face; a host's own rule
    /// applies; and the cell itself still reads, raycasts and saves as Grass.
    /// </summary>
    [TestFixture]
    public sealed class TerrainFaceMaterialRulesEditModeTests
    {
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");
        private static readonly byte Ground = P4TerrainTestWorld.Slot("Ground");
        private static readonly byte LeafyGrass = P4TerrainTestWorld.Slot("LeafyGrass");
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");
        private static readonly byte Snow = P4TerrainTestWorld.Slot("Snow");

        private static TerrainChunkMeshBuffers Mesh(TerrainChunkStore store, TerrainFaceMaterialRules rules = null)
        {
            TerrainChunkMeshBuffers buffers = new();
            new BlockyTerrainChunkMesher(rules).BuildChunk(store, new TerrainChunkCoord(0, 0, 0), buffers);
            return buffers;
        }

        private static List<string> Describe(IReadOnlyList<TerrainFaceQuad> quads)
        {
            List<string> faces = new();
            foreach (TerrainFaceQuad quad in quads)
            {
                faces.Add(quad.Face + (quad.Band == TerrainFaceBand.Whole ? "" : " " + quad.Band));
            }

            faces.Sort(StringComparer.Ordinal);
            return faces;
        }

        [Test]
        public void FaceRule_AGrassCellOpenAbove_IsAGrassBlock()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store);

            CollectionAssert.AreEqual(new[]
            {
                "NegativeX Fringe", "NegativeZ Fringe", "PositiveX Fringe", "PositiveY", "PositiveZ Fringe"
            }, Describe(buffers.GetQuads(Grass)), "grass on top and in a band along each side's top edge");
            CollectionAssert.AreEqual(new[]
            {
                "NegativeX BelowFringe", "NegativeY", "NegativeZ BelowFringe", "PositiveX BelowFringe",
                "PositiveZ BelowFringe"
            }, Describe(buffers.GetQuads(Ground)), "earth under the band and underneath");
            foreach (TerrainFaceQuad quad in buffers.GetQuads(Grass))
            {
                if (quad.Band == TerrainFaceBand.Fringe)
                {
                    Assert.AreEqual(13f / 16f, quad.CornerHeight(0), 1e-6f, "the band is three sixteenths of the cell");
                    Assert.AreEqual(1f, quad.CornerHeight(1));
                }
            }

            Assert.AreEqual(1f, buffers.GetQuads(Grass)[0].CornerHeight(1));
        }

        [Test]
        public void FaceRule_AGrassCellUnderASolidCell_ShowsPlainEarthOnItsSides()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);
            store.SetCell(3, 5, 5, Rock, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store);

            CollectionAssert.IsEmpty(buffers.GetQuads(Grass), "its top is hidden and its sides carry no band");
            CollectionAssert.AreEqual(new[] { "NegativeX", "NegativeY", "NegativeZ", "PositiveX", "PositiveZ" },
                Describe(buffers.GetQuads(Ground)));
        }

        [Test]
        public void FaceRule_AGrassCellUnderWater_KeepsItsBand()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);
            store.SetCell(3, 5, 5, TerrainMaterials.Water, TerrainOccupancy.Full);

            Assert.AreEqual(5, Mesh(store).GetQuads(Grass).Count, "the top and four bands");
        }

        [Test]
        public void FaceRule_GroundAndLeafyGrass_AreDrawnAsTheyWere()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(1, 1, 1, Ground, TerrainOccupancy.Full);
            store.SetCell(8, 1, 1, LeafyGrass, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store);

            Assert.AreEqual(6, buffers.GetQuads(Ground).Count);
            Assert.AreEqual(6, buffers.GetQuads(LeafyGrass).Count);
            Assert.AreEqual(12, buffers.QuadCount);
            foreach (TerrainFaceQuad quad in buffers.GetQuads(LeafyGrass))
            {
                Assert.AreEqual(TerrainFaceBand.Whole, quad.Band, "leaves have no band");
            }
        }

        [Test]
        public void FaceRule_ThePlainRules_DrawGrassOnEveryFace()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, TerrainFaceMaterialRules.None);

            Assert.AreEqual(6, buffers.GetQuads(Grass).Count);
            Assert.AreEqual(6, buffers.QuadCount);
        }

        [Test]
        public void FaceRule_AHostsOwnRule_PutsSnowOnRock()
        {
            TerrainFaceMaterialRules rules = TerrainFaceMaterialRules.None.With(Rock,
                new TerrainFaceMaterialRule(Snow, Rock, Rock, 0));
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Rock, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, rules);

            CollectionAssert.AreEqual(new[] { "PositiveY" }, Describe(buffers.GetQuads(Snow)));
            Assert.AreEqual(5, buffers.GetQuads(Rock).Count, "no band: the sides are whole");
            Assert.AreEqual(TerrainFaceMaterialRule.Plain(Grass).Top, TerrainFaceMaterialRules.None.For(Grass).Side,
                "With returns a copy; the shared rules are unchanged");
            Assert.AreEqual(Rock, TerrainFaceMaterialRules.None.For(Rock).Top);
        }

        [Test]
        public void FaceRule_IsADrawingRule_TheCellStillReadsAndRaycastsAsGrass()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);

            Assert.AreEqual(Grass, store.GetMaterial(3, 4, 5));
            RbxVector3 onTheSide = new(4f * 4f, 4f * 4f + 3.5f, 5f * 4f + 2f);
            Assert.IsTrue(TerrainRaycastMath.TryResolveHit(store, onTheSide, new RbxVector3(1f, 0f, 0f), out int x,
                out int y, out int z, out byte slot, out RbxVector3 _), "a hit in the band of the +X side");
            Assert.AreEqual((3, 4, 5), (x, y, z));
            Assert.AreEqual(Grass, slot, "the band is drawn with grass, the cell is Grass");
            RbxVector3 belowTheBand = new(4f * 4f, 4f * 4f + 1f, 5f * 4f + 2f);
            Assert.IsTrue(TerrainRaycastMath.TryResolveHit(store, belowTheBand, new RbxVector3(1f, 0f, 0f), out _,
                out _, out _, out byte below, out RbxVector3 _));
            Assert.AreEqual(Grass, below, "drawn with earth, still a Grass cell");
        }

        [Test]
        public void FaceRule_RefusesUnknownSlotsAndABandAsTallAsTheCell()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainFaceMaterialRule(200, Rock, Rock, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainFaceMaterialRule(Rock, Rock, Rock, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainFaceMaterialRules.None.With(200, TerrainFaceMaterialRule.Plain(Rock)));
        }
    }
}
