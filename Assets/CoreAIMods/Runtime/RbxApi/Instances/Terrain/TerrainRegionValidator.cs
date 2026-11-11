using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The mirror's argument rules for the region-based Terrain calls (<c>ReadVoxels</c>,
    /// <c>WriteVoxels</c>, <c>FillRegion</c>, <c>ReplaceMaterial</c>): the resolution "must be 4", the
    /// region "must be aligned to the voxel grid", and it may hold at most 4,194,304 voxels. Each
    /// refusal is a BAD_ARGUMENT whose fix names the call that repairs it.
    /// </summary>
    public static class TerrainRegionValidator
    {
        /// <summary>The only resolution the mirror accepts.</summary>
        public const double Resolution = 4d;

        /// <summary>The mirror's region limit: "currently 4194304 voxels".</summary>
        public const long MaxRegionVoxels = 4_194_304;

        // WHY a tolerance at all: a corner computed in Lua (16 * 0.1 * 10) can miss a multiple of 4 by
        // one float ulp; a millionth of a stud is far below anything a script means.
        private const double AlignmentTolerance = 1e-6;

        /// <summary>The cells a region covers, after every rule passed.</summary>
        /// <param name="member">The call, e.g. <c>Terrain:ReadVoxels</c>, used in the message.</param>
        public static TerrainCellBox Validate(in RbxRegion3 region, double resolution, string member)
        {
            RequireResolution(resolution, member);
            RbxVector3 min = region.Min;
            RbxVector3 max = region.Max;
            if (!IsFinite(min) || !IsFinite(max))
            {
                throw RbxError.BadArgument(
                    member + " region has a non-finite corner (" + min + "; " + max + ")",
                    "build the region from finite numbers, e.g. Region3.new(Vector3.new(0, 0, 0), Vector3.new(16, 16, 16))");
            }

            RbxVector3 size = region.Size;
            if (size.X < 0f || size.Y < 0f || size.Z < 0f)
            {
                throw RbxError.BadArgument(
                    member + " region has a negative size (" + size + ")",
                    "pass the smaller corner first: Region3.new(min, max) with min below max on every axis");
            }

            if (!IsAligned(min.X) || !IsAligned(min.Y) || !IsAligned(min.Z)
                || !IsAligned(max.X) || !IsAligned(max.Y) || !IsAligned(max.Z))
            {
                throw RbxError.BadArgument(
                    member + " region is not aligned to the 4-stud voxel grid (corners " + min + " and " + max + ")",
                    "call region:ExpandToGrid(4) first, or use corners that are multiples of 4");
            }

            int minX = ToCell(min.X);
            int minY = ToCell(min.Y);
            int minZ = ToCell(min.Z);
            long sizeX = ToCell(max.X) - (long)minX;
            long sizeY = ToCell(max.Y) - (long)minY;
            long sizeZ = ToCell(max.Z) - (long)minZ;
            double voxels = (double)sizeX * sizeY * sizeZ;
            if (voxels > MaxRegionVoxels)
            {
                throw RbxError.BadArgument(
                    member + " region holds " + voxels.ToString("0", CultureInfo.InvariantCulture)
                    + " voxels; the limit is " + MaxRegionVoxels,
                    "split the region into parts of at most " + MaxRegionVoxels
                    + " voxels, e.g. 256 x 64 x 256 cells (1024 x 256 x 1024 studs)");
            }

            return new TerrainCellBox(minX, minY, minZ, (int)sizeX, (int)sizeY, (int)sizeZ);
        }

        /// <summary>BAD_ARGUMENT unless <paramref name="resolution"/> is 4.</summary>
        public static void RequireResolution(double resolution, string member)
        {
            if (resolution != Resolution)
            {
                throw RbxError.BadArgument(
                    member + " resolution must be 4, got "
                    + resolution.ToString("R", CultureInfo.InvariantCulture),
                    "pass 4 as the resolution, e.g. terrain:ReadVoxels(region, 4)");
            }
        }

        private static bool IsFinite(RbxVector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X)
                   && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
                   && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }

        private static bool IsAligned(float studs)
        {
            double cells = studs / Resolution;
            return Math.Abs(cells - Math.Round(cells)) <= AlignmentTolerance;
        }

        private static int ToCell(float studs)
        {
            double cells = Math.Round(studs / Resolution);
            return (int)Math.Max(int.MinValue / 2d, Math.Min(int.MaxValue / 2d, cells));
        }
    }
}
