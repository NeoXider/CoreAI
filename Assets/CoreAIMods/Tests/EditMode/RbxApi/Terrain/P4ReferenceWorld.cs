using System;
using CoreAI.Mods.Rbx.Instances.Terrain;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// The P4 reference world: 256 x 64 x 256 cells of rolling ground (rock, ground, a grass or sand
    /// top), caves carved through the rock, and a sea at height 30. Deterministic, so its encoded size
    /// and bytes are goldens.
    /// </summary>
    internal static class P4ReferenceWorld
    {
        public const int SizeX = 256;
        public const int SizeY = 64;
        public const int SizeZ = 256;
        public const int SeaLevel = 30;

        public static TerrainCellBox Box => new(0, 0, 0, SizeX, SizeY, SizeZ);

        /// <summary>Region-ordered buffers of the whole world.</summary>
        public static void Generate(out byte[] materials, out byte[] occupancy)
        {
            TerrainCellBox box = Box;
            materials = new byte[box.CellCount];
            occupancy = new byte[box.CellCount];
            TerrainMaterials.TryGetSlot("Rock", out byte rock);
            TerrainMaterials.TryGetSlot("Ground", out byte ground);
            TerrainMaterials.TryGetSlot("Grass", out byte grass);
            TerrainMaterials.TryGetSlot("Sand", out byte sand);
            for (int x = 0; x < SizeX; x++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    int height = Height(x, z);
                    for (int y = 0; y < SizeY; y++)
                    {
                        byte material = TerrainMaterials.Air;
                        if (y < height - 4)
                        {
                            material = IsCave(x, y, z) ? TerrainMaterials.Air : rock;
                        }
                        else if (y < height - 1)
                        {
                            material = ground;
                        }
                        else if (y == height - 1)
                        {
                            material = height > SeaLevel ? grass : sand;
                        }
                        else if (y < SeaLevel)
                        {
                            material = TerrainMaterials.Water;
                        }

                        int index = box.RegionIndexOf(x, y, z);
                        materials[index] = material;
                        occupancy[index] = material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full;
                    }
                }
            }
        }

        public static void Fill(TerrainChunkStore store)
        {
            Generate(out byte[] materials, out byte[] occupancy);
            store.WriteBox(Box, materials, occupancy);
        }

        private static int Height(int x, int z)
        {
            double value = 30d + 8d * Math.Sin(x * 0.07d) + 6d * Math.Cos(z * 0.05d) + 4d * Math.Sin((x + z) * 0.11d);
            return Math.Max(8, Math.Min(56, (int)Math.Floor(value)));
        }

        private static bool IsCave(int x, int y, int z)
        {
            if (y < 3)
            {
                return false;
            }

            double value = Math.Sin(x * 0.19d) * Math.Cos(y * 0.23d) + Math.Sin(z * 0.17d) * Math.Cos(x * 0.13d)
                           + Math.Sin(y * 0.29d + z * 0.07d);
            return value > 1.4d;
        }
    }
}
