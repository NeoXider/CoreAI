using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: the default face-culled mesher. A face is drawn only where it can be seen
    /// (solid against Air or Water, Water against Air), across chunk borders too; the face corners
    /// wind counter-clockwise from outside; re-meshing a warm buffer allocates nothing.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainMesherEditModeTests
    {
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");
        private static readonly byte Rock = P4TerrainTestWorld.Slot("Rock");

        private static TerrainChunkMeshBuffers Mesh(TerrainChunkStore store, TerrainChunkCoord coord)
        {
            TerrainChunkMeshBuffers buffers = new();
            new BlockyTerrainChunkMesher().BuildChunk(store, coord, buffers);
            return buffers;
        }

        private static TerrainChunkStore Store()
        {
            return new TerrainChunkStore(TerrainLimits.Desktop);
        }

        [Test]
        public void P4_Mesh_OneCell_DrawsSixFacesOfItsMaterial()
        {
            TerrainChunkStore store = Store();
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, new TerrainChunkCoord(0, 0, 0));

            Assert.AreEqual(6, buffers.QuadCount);
            Assert.AreEqual(6, buffers.GetQuads(Grass).Count);
            HashSet<TerrainFace> faces = new();
            foreach (TerrainFaceQuad quad in buffers.GetQuads(Grass))
            {
                Assert.AreEqual((3, 4, 5), (quad.CellX, quad.CellY, quad.CellZ));
                faces.Add(quad.Face);
            }

            Assert.AreEqual(6, faces.Count, "one face per direction");
        }

        [Test]
        public void P4_Mesh_TwoTouchingCells_HideTheSharedFace()
        {
            TerrainChunkStore store = Store();
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);
            store.SetCell(4, 4, 5, Rock, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, new TerrainChunkCoord(0, 0, 0));

            Assert.AreEqual(10, buffers.QuadCount);
            Assert.AreEqual(5, buffers.GetQuads(Grass).Count);
            Assert.AreEqual(5, buffers.GetQuads(Rock).Count);
        }

        [Test]
        public void P4_Mesh_SolidNextToWater_DrawsTheSolidFace_AndNoWaterFaceAgainstIt()
        {
            TerrainChunkStore store = Store();
            store.SetCell(3, 4, 5, Grass, TerrainOccupancy.Full);
            store.SetCell(4, 4, 5, TerrainMaterials.Water, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, new TerrainChunkCoord(0, 0, 0));

            Assert.AreEqual(6, buffers.GetQuads(Grass).Count, "the grass face seen through the water is drawn");
            Assert.AreEqual(5, buffers.GetQuads(TerrainMaterials.Water).Count, "no water face against the grass");
        }

        [Test]
        public void P4_Mesh_TouchingWaterCells_DrawNoFaceBetweenThem()
        {
            TerrainChunkStore store = Store();
            store.FillBox(new TerrainCellBox(0, 0, 0, 2, 1, 1), TerrainMaterials.Water, TerrainOccupancy.Full);

            TerrainChunkMeshBuffers buffers = Mesh(store, new TerrainChunkCoord(0, 0, 0));

            Assert.AreEqual(10, buffers.GetQuads(TerrainMaterials.Water).Count);
        }

        [Test]
        public void P4_Mesh_FacesAreCulledAcrossAChunkBorder()
        {
            TerrainChunkStore store = Store();
            store.SetCell(15, 0, 0, Grass, TerrainOccupancy.Full);
            store.SetCell(16, 0, 0, Grass, TerrainOccupancy.Full);

            Assert.AreEqual(5, Mesh(store, new TerrainChunkCoord(0, 0, 0)).QuadCount);
            Assert.AreEqual(5, Mesh(store, new TerrainChunkCoord(1, 0, 0)).QuadCount);
        }

        [Test]
        public void P4_Mesh_FullChunk_DrawsOnlyItsShell_AndNothingWhenEveryNeighbourIsFull()
        {
            TerrainChunkStore store = Store();
            store.FillBox(new TerrainCellBox(0, 0, 0, 16, 16, 16), Rock, TerrainOccupancy.Full);

            Assert.AreEqual(6 * 16 * 16, Mesh(store, new TerrainChunkCoord(0, 0, 0)).QuadCount);

            store.FillBox(new TerrainCellBox(-16, -16, -16, 48, 48, 48), Rock, TerrainOccupancy.Full);
            Assert.AreEqual(0, Mesh(store, new TerrainChunkCoord(0, 0, 0)).QuadCount);
        }

        [Test]
        public void P4_Mesh_UnallocatedChunk_IsEmpty()
        {
            Assert.AreEqual(0, Mesh(Store(), new TerrainChunkCoord(4, 4, 4)).QuadCount);
        }

        [Test]
        public void P4_Mesh_BreakingABorderCell_ChangesExactlyTheTwoChunksTheStoreAsksToRemesh()
        {
            TerrainChunkStore store = Store();
            store.FillBox(new TerrainCellBox(-16, -16, -16, 48, 48, 48), Rock, TerrainOccupancy.Full);
            Dictionary<TerrainChunkCoord, int> before = new();
            List<TerrainChunkCoord> all = new();
            store.CopyChunkCoords(all);
            foreach (TerrainChunkCoord coord in all)
            {
                before[coord] = Mesh(store, coord).QuadCount;
            }

            List<TerrainChunkCoord> remesh = new();
            store.Changed += change => remesh.AddRange(change.RemeshChunks);
            store.SetCell(0, 8, 8, TerrainMaterials.Air, 0);

            CollectionAssert.AreEquivalent(
                new[] { new TerrainChunkCoord(-1, 0, 0), new TerrainChunkCoord(0, 0, 0) }, remesh);
            Assert.AreEqual(before[new TerrainChunkCoord(0, 0, 0)] + 5,
                Mesh(store, new TerrainChunkCoord(0, 0, 0)).QuadCount, "five hole faces inside the chunk");
            Assert.AreEqual(before[new TerrainChunkCoord(-1, 0, 0)] + 1,
                Mesh(store, new TerrainChunkCoord(-1, 0, 0)).QuadCount, "one hole face in the -X neighbour");
            foreach (TerrainChunkCoord coord in all)
            {
                if (!remesh.Contains(coord))
                {
                    Assert.AreEqual(before[coord], Mesh(store, coord).QuadCount, "unchanged mesh in " + coord);
                }
            }
        }

        [Test]
        public void P4_Mesh_FaceCornersWindCounterClockwiseFromOutside()
        {
            foreach (TerrainFace face in new[]
                     {
                         TerrainFace.PositiveX, TerrainFace.NegativeX, TerrainFace.PositiveY,
                         TerrainFace.NegativeY, TerrainFace.PositiveZ, TerrainFace.NegativeZ
                     })
            {
                RbxVector3 c0 = Corner(face, 0);
                RbxVector3 c1 = Corner(face, 1);
                RbxVector3 c2 = Corner(face, 2);
                RbxVector3 c3 = Corner(face, 3);
                RbxVector3 normal = TerrainFaceGeometry.GetNormal(face);
                Assert.Greater((c1 - c0).Cross(c2 - c0).Dot(normal), 0f, face + " first triangle");
                Assert.Greater((c2 - c0).Cross(c3 - c0).Dot(normal), 0f, face + " second triangle");
                RbxVector3 centre = (c0 + c2) * 0.5f - new RbxVector3(0.5f, 0.5f, 0.5f);
                Assert.AreEqual(normal * 0.5f, centre, face + " lies on the side its normal names");
            }
        }

        [Test]
        public void P4_Mesh_ReferenceWorld_MeshesEveryChunk_AndAWarmRemeshAllocatesNothing()
        {
            TerrainChunkStore store = new(TerrainLimits.WebGl);
            P4ReferenceWorld.Fill(store);
            List<TerrainChunkCoord> coords = new();
            store.CopyChunkCoords(coords);
            TerrainChunkMeshBuffers buffers = new();
            BlockyTerrainChunkMesher mesher = new();
            long quads = 0;
            int largest = 0;
            Stopwatch clock = Stopwatch.StartNew();
            foreach (TerrainChunkCoord coord in coords)
            {
                mesher.BuildChunk(store, coord, buffers);
                quads += buffers.QuadCount;
                largest = System.Math.Max(largest, buffers.QuadCount);
            }

            clock.Stop();
            TestContext.WriteLine("reference world: " + coords.Count + " chunks, " + quads + " faces (largest chunk "
                                  + largest + "), meshed in " + clock.Elapsed.TotalMilliseconds.ToString("0.0")
                                  + " ms (" + (clock.Elapsed.TotalMilliseconds / coords.Count).ToString("0.000")
                                  + " ms per chunk)");
            Assert.Less(largest * 4, 65536, "every chunk fits a 16-bit index buffer");

            long bytes = P4Allocation.Measure(() =>
            {
                foreach (TerrainChunkCoord coord in coords)
                {
                    mesher.BuildChunk(store, coord, buffers);
                }
            });
            Assert.AreEqual(0, bytes, "re-meshing into warm buffers allocates nothing");
        }

        private static RbxVector3 Corner(TerrainFace face, int corner)
        {
            TerrainFaceGeometry.GetCorner(face, corner, out int dx, out int dy, out int dz);
            return new RbxVector3(dx, dy, dz);
        }
    }
}
