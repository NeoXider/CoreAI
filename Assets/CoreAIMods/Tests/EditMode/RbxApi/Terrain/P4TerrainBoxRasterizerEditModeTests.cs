using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: the <c>FillBlock</c> rasterizer. A cell belongs to the block when its centre
    /// lies inside the oriented box or on its surface (recorded deviation: Roblox fills partial
    /// occupancy instead).
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainBoxRasterizerEditModeTests
    {
        private static List<(int X, int Y, int Z)> Inside(RbxCFrame cframe, RbxVector3 size)
        {
            TerrainBoxRasterizer rasterizer = new(cframe, size);
            TerrainCellBox bounds = rasterizer.CellBounds;
            List<(int X, int Y, int Z)> cells = new();
            for (int x = bounds.MinX - 1; x <= bounds.MaxX; x++)
            {
                for (int y = bounds.MinY - 1; y <= bounds.MaxY; y++)
                {
                    for (int z = bounds.MinZ - 1; z <= bounds.MaxZ; z++)
                    {
                        if (!rasterizer.ContainsCellCentre(x, y, z))
                        {
                            continue;
                        }

                        Assert.IsTrue(x >= bounds.MinX && x < bounds.MaxX && y >= bounds.MinY && y < bounds.MaxY
                                      && z >= bounds.MinZ && z < bounds.MaxZ,
                            "an inside cell lies outside CellBounds: " + (x, y, z));
                        cells.Add((x, y, z));
                    }
                }
            }

            return cells;
        }

        [Test]
        public void P4_Raster_OneCellBlockAtACellCentre_FillsExactlyThatCell()
        {
            List<(int X, int Y, int Z)> cells = Inside(
                RbxCFrame.FromPosition(RbxTerrain.CellCenterToWorld(3, -2, 7)), new RbxVector3(4f, 4f, 4f));

            CollectionAssert.AreEqual(new[] { (3, -2, 7) }, cells);
        }

        [Test]
        public void P4_Raster_GridAlignedEightStudBlock_FillsTwoByTwoByTwo()
        {
            List<(int X, int Y, int Z)> cells = Inside(RbxCFrame.FromPosition(4f, 4f, 4f), new RbxVector3(8f, 8f, 8f));

            Assert.AreEqual(8, cells.Count);
            CollectionAssert.Contains(cells, (0, 0, 0));
            CollectionAssert.Contains(cells, (1, 1, 1));
        }

        [Test]
        public void P4_Raster_BlockCentredOnAGridCorner_FillsTheCellsItHalfCovers()
        {
            List<(int X, int Y, int Z)> cells = Inside(RbxCFrame.FromPosition(0f, 0f, 0f), new RbxVector3(4f, 4f, 4f));

            Assert.AreEqual(8, cells.Count, "centres on the surface count as inside");
            CollectionAssert.Contains(cells, (-1, -1, -1));
            CollectionAssert.Contains(cells, (0, 0, 0));
        }

        [Test]
        public void P4_Raster_RotatedBar_FillsOnlyTheCellsWhoseCentresAreInside()
        {
            RbxCFrame cframe = RbxCFrame.FromPosition(2f, 2f, 2f) * RbxCFrame.Angles(0f, (float)(Math.PI / 4d), 0f);
            float length = (float)(4d * Math.Sqrt(2d) * 3d);

            List<(int X, int Y, int Z)> cells = Inside(cframe, new RbxVector3(4f, 4f, length));

            CollectionAssert.AreEquivalent(new[] { (-1, 0, -1), (0, 0, 0), (1, 0, 1) }, cells,
                "a 45-degree bar covers the diagonal; the axis neighbours' centres are 2.83 studs off its axis");
        }

        [Test]
        public void P4_Raster_RotatedBar_HasALargerAxisAlignedSweep_ThanItsCells()
        {
            RbxCFrame cframe = RbxCFrame.FromPosition(2f, 2f, 2f) * RbxCFrame.Angles(0f, (float)(Math.PI / 4d), 0f);
            TerrainBoxRasterizer rasterizer = new(cframe, new RbxVector3(4f, 4f, 16.97f));

            Assert.Greater(rasterizer.CellBounds.CellCount, 3);
        }

        [Test]
        public void P4_Raster_FarFromTheOrigin_StillFillsExactlyOneCell()
        {
            List<(int X, int Y, int Z)> cells = Inside(
                RbxCFrame.FromPosition(RbxTerrain.CellCenterToWorld(7999, 10, -7999)), new RbxVector3(4f, 4f, 4f));

            CollectionAssert.AreEqual(new[] { (7999, 10, -7999) }, cells);
        }

        [Test]
        public void P4_Raster_ZeroSizeBlockOffACellCentre_FillsNothing()
        {
            List<(int X, int Y, int Z)> cells = Inside(RbxCFrame.FromPosition(1f, 1f, 1f), RbxVector3.Zero);

            Assert.IsEmpty(cells);
        }
    }
}
