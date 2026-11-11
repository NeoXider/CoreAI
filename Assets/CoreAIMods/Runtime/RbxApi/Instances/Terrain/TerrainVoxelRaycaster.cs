using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>One ray hit on a Terrain cell face, in studs.</summary>
    public readonly struct TerrainRaycastHit
    {
        public TerrainRaycastHit(int cellX, int cellY, int cellZ, byte material, RbxVector3 position,
            RbxVector3 normal, double distance)
        {
            CellX = cellX;
            CellY = cellY;
            CellZ = cellZ;
            Material = material;
            Position = position;
            Normal = normal;
            Distance = distance;
        }

        public int CellX { get; }

        public int CellY { get; }

        public int CellZ { get; }

        /// <summary>Material slot of the hit cell.</summary>
        public byte Material { get; }

        public RbxVector3 Position { get; }

        /// <summary>The axis normal of the face the ray entered through.</summary>
        public RbxVector3 Normal { get; }

        public double Distance { get; }
    }

    /// <summary>
    /// An engine-free ray march over the cell grid (Amanatides and Woo): the first solid cell a ray
    /// enters, with the face it entered through. Water is passed through, as it has no collider, and
    /// the cell the ray starts in is never hit, as a ray leaving a collider does not hit it.
    /// </summary>
    /// <remarks>
    /// WHY engine-free when the Unity world casts against chunk colliders: headless worlds and tests
    /// have no physics engine, and the same answer has to come out of both.
    /// </remarks>
    public static class TerrainVoxelRaycaster
    {
        private const double CellStuds = TerrainBoxRasterizer.CellStuds;

        /// <summary>
        /// Casts from <paramref name="origin"/> along <paramref name="direction"/> (its length is the
        /// range, in studs, at most <see cref="RbxWorldPhysics.MaxRayLengthStuds"/>); false on a miss.
        /// </summary>
        public static bool TryRaycast(TerrainChunkStore store, RbxVector3 origin, RbxVector3 direction,
            out TerrainRaycastHit hit)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            hit = default;
            double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y
                                                                         + (double)direction.Z * direction.Z);
            if (!(length > 0d) || length > RbxWorldPhysics.MaxRayLengthStuds || store.ChunkCount == 0)
            {
                return false;
            }

            double ox = origin.X;
            double oy = origin.Y;
            double oz = origin.Z;
            if (double.IsNaN(ox + oy + oz) || double.IsInfinity(ox + oy + oz))
            {
                return false;
            }

            double dx = direction.X;
            double dy = direction.Y;
            double dz = direction.Z;
            int x = (int)Math.Floor(ox / CellStuds);
            int y = (int)Math.Floor(oy / CellStuds);
            int z = (int)Math.Floor(oz / CellStuds);
            int stepX = Math.Sign(dx);
            int stepY = Math.Sign(dy);
            int stepZ = Math.Sign(dz);
            double tMaxX = FirstBoundary(ox, dx, x, stepX);
            double tMaxY = FirstBoundary(oy, dy, y, stepY);
            double tMaxZ = FirstBoundary(oz, dz, z, stepZ);
            double tDeltaX = stepX != 0 ? CellStuds / Math.Abs(dx) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? CellStuds / Math.Abs(dy) : double.PositiveInfinity;
            double tDeltaZ = stepZ != 0 ? CellStuds / Math.Abs(dz) : double.PositiveInfinity;
            while (true)
            {
                double t;
                RbxVector3 normal;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    t = tMaxX;
                    x += stepX;
                    tMaxX += tDeltaX;
                    normal = new RbxVector3(-stepX, 0f, 0f);
                }
                else if (tMaxY <= tMaxZ)
                {
                    t = tMaxY;
                    y += stepY;
                    tMaxY += tDeltaY;
                    normal = new RbxVector3(0f, -stepY, 0f);
                }
                else
                {
                    t = tMaxZ;
                    z += stepZ;
                    tMaxZ += tDeltaZ;
                    normal = new RbxVector3(0f, 0f, -stepZ);
                }

                if (t > 1d)
                {
                    return false;
                }

                byte material = store.GetMaterial(x, y, z);
                if (material == TerrainMaterials.Air || material == TerrainMaterials.Water)
                {
                    continue;
                }

                hit = new TerrainRaycastHit(x, y, z, material,
                    new RbxVector3((float)(ox + dx * t), (float)(oy + dy * t), (float)(oz + dz * t)), normal,
                    t * length);
                return true;
            }
        }

        private static double FirstBoundary(double origin, double direction, int cell, int step)
        {
            if (step == 0)
            {
                return double.PositiveInfinity;
            }

            double boundary = (step > 0 ? cell + 1 : cell) * CellStuds;
            return (boundary - origin) / direction;
        }
    }
}
