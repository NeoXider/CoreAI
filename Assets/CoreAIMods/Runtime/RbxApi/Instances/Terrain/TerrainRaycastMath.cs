using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// Turns a ray hit on a Terrain face (a point and the face normal, in studs) into the cell that
    /// was hit, its material and the exact axis normal, for <c>RaycastResult.Material</c> and
    /// <c>RaycastResult.Normal</c>.
    /// </summary>
    public static class TerrainRaycastMath
    {
        // WHY a hundredth of a stud: the point lies on the face, so stepping back along the normal by
        // far less than a cell lands inside the cell that owns the face, even at a cube edge.
        private const double InsetStuds = 0.01d;

        /// <summary>
        /// The cell behind a hit and its material; false when the cell behind the face is Air (a stale
        /// collider), in which case the hit should not be reported as Terrain.
        /// </summary>
        public static bool TryResolveHit(TerrainChunkStore store, RbxVector3 pointStuds, RbxVector3 normalStuds,
            out int cellX, out int cellY, out int cellZ, out byte material, out RbxVector3 axisNormal)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            axisNormal = SnapToAxis(normalStuds);
            double x = pointStuds.X - axisNormal.X * InsetStuds;
            double y = pointStuds.Y - axisNormal.Y * InsetStuds;
            double z = pointStuds.Z - axisNormal.Z * InsetStuds;
            cellX = (int)Math.Floor(x / TerrainBoxRasterizer.CellStuds);
            cellY = (int)Math.Floor(y / TerrainBoxRasterizer.CellStuds);
            cellZ = (int)Math.Floor(z / TerrainBoxRasterizer.CellStuds);
            material = store.GetMaterial(cellX, cellY, cellZ);
            return material != TerrainMaterials.Air;
        }

        /// <summary>The axis direction nearest to <paramref name="normal"/>; every Terrain face is axis-aligned.</summary>
        public static RbxVector3 SnapToAxis(RbxVector3 normal)
        {
            float ax = Math.Abs(normal.X);
            float ay = Math.Abs(normal.Y);
            float az = Math.Abs(normal.Z);
            if (ax >= ay && ax >= az)
            {
                return new RbxVector3(normal.X >= 0f ? 1f : -1f, 0f, 0f);
            }

            if (ay >= az)
            {
                return new RbxVector3(0f, normal.Y >= 0f ? 1f : -1f, 0f);
            }

            return new RbxVector3(0f, 0f, normal.Z >= 0f ? 1f : -1f);
        }
    }
}
