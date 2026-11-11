using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// Rasterizes an oriented box (a <c>FillBlock</c> CFrame and size, in studs) onto the 4-stud cell
    /// grid: a cell is inside when its centre is inside the box or exactly on its surface.
    /// </summary>
    /// <remarks>
    /// WHY centres and not overlap: a block world has no partial cells, so a cell is either the
    /// box's or not. With this rule a 4 x 4 x 4 box centred on a cell centre fills exactly that cell,
    /// an 8-stud box aligned to the grid fills 2 x 2 x 2 cells, and a box centred on a grid corner
    /// fills the cells it half-covers. WHY doubles: the box can sit thousands of studs from the
    /// origin, and a float projection there would put boundary centres on either side at random.
    /// </remarks>
    public readonly struct TerrainBoxRasterizer
    {
        /// <summary>Studs along one cell edge (the Roblox Terrain grid).</summary>
        public const double CellStuds = 4d;

        private const double SurfaceTolerance = 1e-4;
        private const double CellRange = 1 << 30;

        private readonly double _centreX;
        private readonly double _centreY;
        private readonly double _centreZ;
        private readonly double _axisXx;
        private readonly double _axisXy;
        private readonly double _axisXz;
        private readonly double _axisYx;
        private readonly double _axisYy;
        private readonly double _axisYz;
        private readonly double _axisZx;
        private readonly double _axisZy;
        private readonly double _axisZz;
        private readonly double _halfX;
        private readonly double _halfY;
        private readonly double _halfZ;

        /// <summary>The box of <paramref name="cframe"/> and <paramref name="sizeStuds"/>; the size must be finite and not negative.</summary>
        public TerrainBoxRasterizer(in RbxCFrame cframe, RbxVector3 sizeStuds)
        {
            RbxVector3 position = cframe.Position;
            RbxVector3 axisX = cframe.XVector;
            RbxVector3 axisY = cframe.YVector;
            RbxVector3 axisZ = cframe.ZVector;
            _centreX = position.X;
            _centreY = position.Y;
            _centreZ = position.Z;
            _axisXx = axisX.X;
            _axisXy = axisX.Y;
            _axisXz = axisX.Z;
            _axisYx = axisY.X;
            _axisYy = axisY.Y;
            _axisYz = axisY.Z;
            _axisZx = axisZ.X;
            _axisZy = axisZ.Y;
            _axisZz = axisZ.Z;
            _halfX = sizeStuds.X * 0.5d;
            _halfY = sizeStuds.Y * 0.5d;
            _halfZ = sizeStuds.Z * 0.5d;

            double extentX = Math.Abs(_axisXx) * _halfX + Math.Abs(_axisYx) * _halfY + Math.Abs(_axisZx) * _halfZ;
            double extentY = Math.Abs(_axisXy) * _halfX + Math.Abs(_axisYy) * _halfY + Math.Abs(_axisZy) * _halfZ;
            double extentZ = Math.Abs(_axisXz) * _halfX + Math.Abs(_axisYz) * _halfY + Math.Abs(_axisZz) * _halfZ;
            CellBounds = TerrainCellBox.FromMinMax(
                FirstCentreAtOrAbove(_centreX - extentX), FirstCentreAtOrAbove(_centreY - extentY),
                FirstCentreAtOrAbove(_centreZ - extentZ),
                LastCentreAtOrBelow(_centreX + extentX) + 1, LastCentreAtOrBelow(_centreY + extentY) + 1,
                LastCentreAtOrBelow(_centreZ + extentZ) + 1);
        }

        /// <summary>The axis-aligned cells that can hold an inside centre (a superset when rotated).</summary>
        public TerrainCellBox CellBounds { get; }

        /// <summary>True when the centre of cell (x, y, z) lies inside the box or on its surface.</summary>
        public bool ContainsCellCentre(int x, int y, int z)
        {
            double dx = (x + 0.5d) * CellStuds - _centreX;
            double dy = (y + 0.5d) * CellStuds - _centreY;
            double dz = (z + 0.5d) * CellStuds - _centreZ;
            double localX = dx * _axisXx + dy * _axisXy + dz * _axisXz;
            if (Math.Abs(localX) > _halfX + SurfaceTolerance)
            {
                return false;
            }

            double localY = dx * _axisYx + dy * _axisYy + dz * _axisYz;
            if (Math.Abs(localY) > _halfY + SurfaceTolerance)
            {
                return false;
            }

            double localZ = dx * _axisZx + dy * _axisZy + dz * _axisZz;
            return Math.Abs(localZ) <= _halfZ + SurfaceTolerance;
        }

        private static int FirstCentreAtOrAbove(double studs)
        {
            double cell = Math.Ceiling(studs / CellStuds - 0.5d - SurfaceTolerance / CellStuds);
            return (int)Math.Max(-CellRange, Math.Min(CellRange, cell));
        }

        private static int LastCentreAtOrBelow(double studs)
        {
            double cell = Math.Floor(studs / CellStuds - 0.5d + SurfaceTolerance / CellStuds);
            return (int)Math.Max(-CellRange, Math.Min(CellRange, cell));
        }
    }
}
