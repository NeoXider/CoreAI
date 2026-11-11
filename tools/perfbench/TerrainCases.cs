using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// The engine-free half of a Terrain chunk build: the face-culled mesher (with the grass-block face rule),
    /// the sky exposure of a chunk (window flood and every face corner), and the light pass a single edit adds
    /// next to a cave or on an open plain. The Unity half (mesh upload, collider cooking) is the P4 capture's.
    /// </summary>
    internal static class TerrainCases
    {
        public static void Add(List<MicroCase> cases)
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            FillReferenceWorld(store);
            List<TerrainChunkCoord> coords = new();
            store.CopyChunkCoords(coords);
            TerrainChunkMeshBuffers buffers = new();
            BlockyTerrainChunkMesher mesher = new();
            TerrainSkyExposure exposure = new(store);
            store.Changed += exposure.OnStoreChanged;
            int chunks = coords.Count;

            cases.Add(new MicroCase("terrain", "mesh one chunk, reference world (" + chunks + " chunks)", ops =>
            {
                long quads = 0;
                for (int i = 0; i < ops; i++)
                {
                    mesher.BuildChunk(store, coords[i % chunks], buffers);
                    quads += buffers.QuadCount;
                }

                return quads;
            }, "per chunk; the grass-block face rule on"));

            cases.Add(new MicroCase("terrain", "sky exposure: light one chunk's window (gather, classify, flood)", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    exposure.PrepareChunk(coords[i % chunks]);
                    sum++;
                }

                return sum;
            }, "per chunk, every chunk of the reference world in turn"));

            List<TerrainChunkCoord> sample = new();
            List<TerrainChunkMeshBuffers> sampleFaces = new();
            for (int index = 0; index < chunks && sample.Count < 64; index += Math.Max(1, chunks / 64))
            {
                TerrainChunkMeshBuffers faces = new();
                mesher.BuildChunk(store, coords[index], faces);
                if (faces.QuadCount > 0)
                {
                    sample.Add(coords[index]);
                    sampleFaces.Add(faces);
                }
            }

            cases.Add(new MicroCase("terrain", "sky exposure: light one chunk and every face corner (no mesher)", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    int pick = i % sample.Count;
                    sum += Light(exposure, sample[pick], sampleFaces[pick]);
                }

                return sum;
            }, "per chunk, 64 chunks spread over the reference world, faces meshed beforehand"));

            List<TerrainChunkCoord> pending = new();
            store.Changed += change =>
            {
                pending.AddRange(change.RemeshChunks);
                exposure.CollectLightAffectedChunks(change, pending);
            };
            FindLitCaveWall(store, exposure, out int wallX, out int wallY, out int wallZ);
            int toggle = 0;
            cases.Add(new MicroCase("terrain",
                "edit next to a lit cave: write + re-mesh and light every chunk it queues", ops =>
                {
                    long work = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        toggle ^= 1;
                        pending.Clear();
                        store.SetCell(wallX, wallY, wallZ, toggle == 1 ? TerrainMaterials.Air : Rock,
                            TerrainOccupancy.Full);
                        work += Rebuild(store, exposure, mesher, buffers, pending);
                    }

                    return work;
                }, "per edit: a wall cell of a cave lit from its mouth, broken and put back in turn; cell ("
                   + wallX + ", " + wallY + ", " + wallZ + ")"));

            cases.Add(new MicroCase("terrain", "edit on an open plain: write + re-mesh and light every chunk it queues",
                ops =>
                {
                    long work = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        toggle ^= 1;
                        pending.Clear();
                        store.SetCell(200, 60, 200, toggle == 1 ? Rock : TerrainMaterials.Air, TerrainOccupancy.Full);
                        work += Rebuild(store, exposure, mesher, buffers, pending);
                    }

                    return work;
                }, "per edit: a block placed on and taken off the open ground"));
        }

        private static readonly byte Rock = SlotOf("Rock");

        private static long Rebuild(TerrainChunkStore store, TerrainSkyExposure exposure,
            BlockyTerrainChunkMesher mesher, TerrainChunkMeshBuffers buffers, List<TerrainChunkCoord> pending)
        {
            long work = 0;
            for (int index = 0; index < pending.Count; index++)
            {
                mesher.BuildChunk(store, pending[index], buffers);
                work += Light(exposure, pending[index], buffers) + 1;
            }

            return work;
        }

        /// <summary>
        /// A solid cell beside a non-blocking cell that the sky lights from the side. The reference world's caves
        /// are sealed, so a shaft is opened from the surface into the first cave the scan meets.
        /// </summary>
        private static void FindLitCaveWall(TerrainChunkStore store, TerrainSkyExposure exposure, out int wallX,
            out int wallY, out int wallZ)
        {
            OpenAShaft(store, exposure);
            for (int x = 8; x < 248; x += 3)
            {
                for (int z = 8; z < 248; z += 3)
                {
                    for (int y = 4; y < 40; y++)
                    {
                        if (TerrainSkyExposure.BlocksSky(store.GetMaterial(x, y, z))
                            || exposure.GetColumnTop(x, z) < y)
                        {
                            continue;
                        }

                        int level = exposure.SampleLevel(x, y, z);
                        if (level > 0 && level < exposure.Reach
                                      && TerrainSkyExposure.BlocksSky(store.GetMaterial(x + 1, y, z)))
                        {
                            wallX = x + 1;
                            wallY = y;
                            wallZ = z;
                            return;
                        }
                    }
                }
            }

            throw new InvalidOperationException("the reference world has no cave lit from the side");
        }

        private static void OpenAShaft(TerrainChunkStore store, TerrainSkyExposure exposure)
        {
            for (int x = 40; x < 216; x++)
            {
                for (int z = 40; z < 216; z += 7)
                {
                    int top = exposure.GetColumnTop(x, z);
                    for (int y = 4; y < top; y++)
                    {
                        if (!TerrainSkyExposure.BlocksSky(store.GetMaterial(x, y, z)))
                        {
                            store.FillBox(new TerrainCellBox(x, y + 1, z, 1, top - y, 1), TerrainMaterials.Air, 0);
                            return;
                        }
                    }
                }
            }

            throw new InvalidOperationException("the reference world has no cave to open a shaft into");
        }

        private static long Light(TerrainSkyExposure exposure, TerrainChunkCoord coord, TerrainChunkMeshBuffers buffers)
        {
            exposure.PrepareChunk(coord);
            long sum = 0;
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                IReadOnlyList<TerrainFaceQuad> quads = buffers.GetQuads((byte)slot);
                for (int index = 0; index < quads.Count; index++)
                {
                    TerrainFaceQuad quad = quads[index];
                    exposure.GetCornerExposures(in quad, out byte a, out byte b, out byte c, out byte d);
                    sum += a + b + c + d;
                }
            }

            return sum;
        }

        private static byte SlotOf(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        /// <summary>The P4 reference world: 256 x 64 x 256 cells of rolling ground over rock, caves and a sea.</summary>
        private static void FillReferenceWorld(TerrainChunkStore store)
        {
            TerrainCellBox box = new(0, 0, 0, 256, 64, 256);
            byte[] materials = new byte[box.CellCount];
            byte[] occupancy = new byte[box.CellCount];
            byte ground = SlotOf("Ground");
            byte grass = SlotOf("Grass");
            byte sand = SlotOf("Sand");
            for (int x = 0; x < 256; x++)
            {
                for (int z = 0; z < 256; z++)
                {
                    double value = 30d + 8d * Math.Sin(x * 0.07d) + 6d * Math.Cos(z * 0.05d) + 4d * Math.Sin((x + z) * 0.11d);
                    int height = Math.Max(8, Math.Min(56, (int)Math.Floor(value)));
                    for (int y = 0; y < 64; y++)
                    {
                        byte material = TerrainMaterials.Air;
                        if (y < height - 4)
                        {
                            double cave = Math.Sin(x * 0.19d) * Math.Cos(y * 0.23d)
                                          + Math.Sin(z * 0.17d) * Math.Cos(x * 0.13d) + Math.Sin(y * 0.29d + z * 0.07d);
                            material = y >= 3 && cave > 1.4d ? TerrainMaterials.Air : Rock;
                        }
                        else if (y < height - 1)
                        {
                            material = ground;
                        }
                        else if (y == height - 1)
                        {
                            material = height > 30 ? grass : sand;
                        }
                        else if (y < 30)
                        {
                            material = TerrainMaterials.Water;
                        }

                        int index = box.RegionIndexOf(x, y, z);
                        materials[index] = material;
                        occupancy[index] = material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full;
                    }
                }
            }

            store.WriteBox(box, materials, occupancy);
        }
    }
}
