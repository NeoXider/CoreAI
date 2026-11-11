using System;
using System.Diagnostics;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainLua
{
    /// <summary>
    /// P4, Lua tier: <c>workspace.Terrain</c> and <c>Region3</c> through the real VM and the production
    /// Rbx registration, against the shapes <c>Terrain.yaml</c> and <c>Region3.yaml</c> document —
    /// including every error row of the DoD with the twin that passes.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainLuaEditModeTests
    {
        /// <summary>The example of the skill's block-world section (skill-sources/RbxApi, the terrain section), verbatim.</summary>
        internal const string SkillExample =
            "local terrain = workspace.Terrain\n"
            + "\n"
            + "-- Ground: a 64 x 4 x 64-cell grass slab whose top face is at y = 16 studs.\n"
            + "terrain:FillBlock(CFrame.new(128, 8, 128), Vector3.new(256, 16, 256), Enum.Material.Grass)\n"
            + "\n"
            + "-- Place and break single blocks by cell.\n"
            + "local function place(x, y, z, material)\n"
            + "\tterrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(x, y, z)), Vector3.new(4, 4, 4), material)\n"
            + "end\n"
            + "place(10, 4, 10, Enum.Material.Brick)\n"
            + "place(10, 3, 10, Enum.Material.Air)\n"
            + "\n"
            + "-- Generate an area with one WriteVoxels call.\n"
            + "local region = Region3.new(Vector3.new(64, 16, 0), Vector3.new(128, 32, 64))\n"
            + "local materials, occupancies = terrain:ReadVoxels(region, 4)\n"
            + "local size = materials.Size\n"
            + "for x = 1, size.X do\n"
            + "\tfor y = 1, size.Y do\n"
            + "\t\tfor z = 1, size.Z do\n"
            + "\t\t\tlocal solid = y <= 2 and (x + z) % 3 == 0\n"
            + "\t\t\tmaterials[x][y][z] = solid and Enum.Material.Rock or Enum.Material.Air\n"
            + "\t\t\toccupancies[x][y][z] = solid and 1 or 0\n"
            + "\t\tend\n"
            + "\tend\n"
            + "end\n"
            + "terrain:WriteVoxels(region, 4, materials, occupancies)\n"
            + "\n"
            + "-- The block under a point, and the cell to place a block on top of it.\n"
            + "local hit = workspace:Raycast(Vector3.new(42, 100, 42), Vector3.new(0, -200, 0))\n"
            + "if hit and hit.Instance == terrain then\n"
            + "\tlocal above = terrain:WorldToCell(hit.Position + hit.Normal * 2)\n"
            + "\tplace(above.X, above.Y, above.Z, Enum.Material.Cobblestone)\n"
            + "end\n";

        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling Lua fixtures: no Unity context for VM continuations.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static string ErrorOf(P4TerrainLuaHarness harness, string body)
        {
            object[] results = harness.Run(
                "local ok, err = pcall(function()\n" + body + "\nend)\nreturn ok, tostring(err)");
            Assert.AreEqual(false, results[0], "expected the call to raise; it returned");
            return (string)results[1];
        }

        [Test]
        public void P4_Lua_WorkspaceTerrain_IsTheOneTerrainInstance()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local t = workspace.Terrain\n"
                + "return typeof(t), t.ClassName, t.Name, t.Parent == workspace, t:IsA(\"Terrain\"), "
                + "t == workspace.Terrain, t:IsA(\"BasePart\")");

            Assert.AreEqual("Instance", results[0]);
            Assert.AreEqual("Terrain", results[1]);
            Assert.AreEqual("Terrain", results[2]);
            Assert.AreEqual(true, results[3]);
            Assert.AreEqual(true, results[4]);
            Assert.AreEqual(true, results[5], "one proxy per instance");
            Assert.AreEqual(false, results[6], "recorded deviation: Terrain is not a BasePart here");
        }

        [Test]
        public void P4_Lua_InstanceNewTerrain_IsRefused_AsNotCreatable()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness, "Instance.new(\"Terrain\")");

            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("Unable to create an Instance of type 'Terrain'", error);
        }

        [Test]
        public void P4_Lua_Region3_HasTheMirrorShape()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local r = Region3.new(Vector3.new(-1, 0, 3), Vector3.new(7, 4.5, 8))\n"
                + "local g = r:ExpandToGrid(4)\n"
                + "return typeof(r), r.Size == Vector3.new(8, 4.5, 5), r.CFrame.Position == Vector3.new(3, 2.25, 5.5), "
                + "g.Size == Vector3.new(12, 8, 8), tostring(Region3.new(Vector3.new(0, 0, 0), Vector3.new(4, 4, 4)))");

            Assert.AreEqual("Region3", results[0]);
            Assert.AreEqual(true, results[1]);
            Assert.AreEqual(true, results[2]);
            Assert.AreEqual(true, results[3], "ExpandToGrid floors (-4, 0, 0) and ceils (8, 8, 8)");
            Assert.AreEqual("2, 2, 2, 1, 0, 0, 0, 1, 0, 0, 0, 1; 4, 4, 4", results[4]);
        }

        [Test]
        public void P4_Lua_Region3_IsReadOnly_AndNamesUnknownMembers()
        {
            P4TerrainLuaHarness harness = new();

            StringAssert.Contains("read-only",
                ErrorOf(harness, "Region3.new(Vector3.new(), Vector3.new(4, 4, 4)).Size = Vector3.new()"));
            StringAssert.Contains("not a valid member of Region3",
                ErrorOf(harness, "local _ = Region3.new().Min"));
        }

        [Test]
        public void P4_Lua_FillBlockSlab_ReadVoxelsReturnsTheWrittenMaterials()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local terrain = workspace.Terrain\n"
                + "terrain:FillBlock(CFrame.new(128, 8, 128), Vector3.new(256, 16, 256), Enum.Material.Grass)\n"
                + "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(256, 16, 256))\n"
                + "local materials, occupancies = terrain:ReadVoxels(region, 4)\n"
                + "local size = materials.Size\n"
                + "local grass, full = 0, 0\n"
                + "for x = 1, size.X do for y = 1, size.Y do for z = 1, size.Z do\n"
                + "  if materials[x][y][z] == Enum.Material.Grass then grass = grass + 1 end\n"
                + "  if occupancies[x][y][z] == 1 then full = full + 1 end\n"
                + "end end end\n"
                + "return size == Vector3.new(64, 4, 64), occupancies.Size == size, grass, full, terrain:CountCells()");

            Assert.AreEqual(true, results[0], "64 x 4 x 64 cells");
            Assert.AreEqual(true, results[1]);
            Assert.AreEqual(16384d, results[2]);
            Assert.AreEqual(16384d, results[3]);
            Assert.AreEqual(16384d, results[4]);
        }

        [Test]
        public void P4_Lua_ReadVoxels_OutsideTheFilledCells_ReadsAirAtZero()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local m, o = workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(-8, -8, -8), Vector3.new(0, 0, 0)), 4)\n"
                + "return m[2][2][2] == Enum.Material.Air, o[2][2][2], m.Size == Vector3.new(2, 2, 2)");

            Assert.AreEqual(true, results[0]);
            Assert.AreEqual(0d, results[1]);
            Assert.AreEqual(true, results[2]);
        }

        [Test]
        public void P4_Lua_WriteVoxels_RoundTripsThroughReadVoxels()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local terrain = workspace.Terrain\n"
                + "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(16, 8, 12))\n"
                + "local materials, occupancies = terrain:ReadVoxels(region, 4)\n"
                + "local size = materials.Size\n"
                + "for x = 1, size.X do for y = 1, size.Y do for z = 1, size.Z do\n"
                + "  materials[x][y][z] = (y == 1) and Enum.Material.Rock or Enum.Material.Water\n"
                + "  occupancies[x][y][z] = (z == 3) and 0.5 or 1\n"
                + "end end end\n"
                + "terrain:WriteVoxels(region, 4, materials, occupancies)\n"
                + "local m2, o2 = terrain:ReadVoxels(region, 4)\n"
                + "return m2[4][1][1] == Enum.Material.Rock, m2[2][2][2] == Enum.Material.Water, o2[1][1][3], "
                + "terrain:CountCells()");

            Assert.AreEqual(true, results[0]);
            Assert.AreEqual(true, results[1]);
            Assert.AreEqual(128d / 255d, (double)results[2], 1e-12, "occupancy keeps 1/255 steps");
            Assert.AreEqual(4d * 2d * 3d, results[3]);
        }

        [Test]
        public void P4_Lua_ReadVoxels_RegionOverTheVoxelLimit_IsBadArgument()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(1024, 260, 1024)), 4)");

            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("4194304", error);
        }

        [Test]
        public void P4_Lua_ReadVoxels_UnalignedRegion_IsBadArgument_NamingExpandToGrid()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(6, 8, 8)), 4)");

            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("ExpandToGrid(4)", error);
        }

        [Test]
        public void P4_Lua_ReadVoxels_ResolutionOtherThan4_IsBadArgument()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(8, 8, 8)), 2)");

            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("resolution must be 4", error);
        }

        [Test]
        public void P4_Lua_ReadVoxels_AlignedRegionAtResolution4_Passes()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local region = Region3.new(Vector3.new(1, 1, 1), Vector3.new(6, 8, 8)):ExpandToGrid(4)\n"
                + "local m = workspace.Terrain:ReadVoxels(region, 4)\n"
                + "return m.Size == Vector3.new(2, 2, 2)");

            Assert.AreEqual(true, results[0]);
        }

        [Test]
        public void P4_Lua_FillBlock_WithANonTerrainMaterial_IsBadArgument_AndATerrainOneByNamePasses()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "workspace.Terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), Enum.Material.Plastic)");
            StringAssert.Contains("Enum.Material.Plastic is not a Terrain material", error);
            StringAssert.Contains("Grass", error, "the fix lists the Terrain materials");

            object[] results = harness.Run(
                "workspace.Terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), \"Rock\")\n"
                + "local m = workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(4, 4, 4)), 4)\n"
                + "return m[1][1][1] == Enum.Material.Rock");
            Assert.AreEqual(true, results[0], "an Enum argument takes the item's name, as in Roblox");
        }

        [Test]
        public void P4_Lua_WriteVoxels_WrongArraySize_IsBadArgument()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(8, 4, 4))\n"
                + "local m = {{{Enum.Material.Grass}}}\n"
                + "local o = {{{1}}}\n"
                + "workspace.Terrain:WriteVoxels(region, 4, m, o)");

            StringAssert.Contains("BAD_ARGUMENT", error);
            StringAssert.Contains("exactly 2 entries along X", error);
        }

        [Test]
        public void P4_Lua_WriteVoxels_ANonMaterialElement_IsBadArgument_NamingItsPosition()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(4, 4, 8))\n"
                + "workspace.Terrain:WriteVoxels(region, 4, {{{Enum.Material.Grass, 5}}}, {{{1, 1}}})");

            StringAssert.Contains("materials[1][1][2] is number", error);
            Assert.AreEqual(0d, harness.Run("return workspace.Terrain:CountCells()")[0], "a refused write writes nothing");
        }

        [Test]
        public void P4_Lua_DotCall_IsBadArgument_NamingTheColon()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness,
                "workspace.Terrain.FillBlock(CFrame.new(), Vector3.new(4, 4, 4), Enum.Material.Grass)");

            StringAssert.Contains("workspace.Terrain:FillBlock(...)", error);
        }

        [Test]
        public void P4_Lua_ReadTierScript_CannotWrite_ButCanRead()
        {
            P4TerrainLuaHarness harness = new(LuaCapabilities.Read);

            string error = ErrorOf(harness,
                "workspace.Terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), Enum.Material.Grass)");
            StringAssert.Contains("WorldEdit", error);

            object[] results = harness.Run(
                "local m = workspace.Terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(4, 4, 4)), 4)\n"
                + "return m[1][1][1] == Enum.Material.Air");
            Assert.AreEqual(true, results[0]);
            Assert.AreEqual(0, harness.TerrainInstance.CountCells());
        }

        [Test]
        public void P4_Lua_CellConversions_MatchTheFourStudGrid()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local t = workspace.Terrain\n"
                + "local cell = t:WorldToCell(Vector3.new(5, -1, 8))\n"
                + "return cell == Vector3.new(1, -1, 2), t:CellCenterToWorld(1, -1, 2) == Vector3.new(6, -2, 10), "
                + "t:CellCornerToWorld(1, -1, 2) == Vector3.new(4, -4, 8), t:CellCenterToWorld(cell.X, cell.Y, cell.Z) == "
                + "Vector3.new(6, -2, 10)");

            CollectionAssert.AreEqual(new object[] { true, true, true, true }, results);
        }

        [Test]
        public void P4_Lua_FillRegion_ReplaceMaterial_AndClear()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local t = workspace.Terrain\n"
                + "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(16, 4, 16))\n"
                + "t:FillRegion(region, 4, Enum.Material.Grass)\n"
                + "local filled = t:CountCells()\n"
                + "t:ReplaceMaterial(Region3.new(Vector3.new(0, 0, 0), Vector3.new(8, 4, 16)), 4, Enum.Material.Grass, Enum.Material.Sand)\n"
                + "local m = t:ReadVoxels(region, 4)\n"
                + "local sand = m[1][1][1] == Enum.Material.Sand and m[4][1][1] == Enum.Material.Grass\n"
                + "t:Clear()\n"
                + "return filled, sand, t:CountCells()");

            Assert.AreEqual(16d, results[0]);
            Assert.AreEqual(true, results[1]);
            Assert.AreEqual(0d, results[2]);
        }

        [Test]
        public void P4_Lua_RotatedFillBlock_FillsTheCellsWhoseCentresAreInside()
        {
            P4TerrainLuaHarness harness = new();

            object[] results = harness.Run(
                "local t = workspace.Terrain\n"
                + "t:FillBlock(CFrame.new(2, 2, 2) * CFrame.Angles(0, math.rad(45), 0), "
                + "Vector3.new(4, 4, 4 * math.sqrt(2) * 3), Enum.Material.Brick)\n"
                + "local m = t:ReadVoxels(Region3.new(Vector3.new(-4, 0, -4), Vector3.new(8, 4, 8)), 4)\n"
                + "return t:CountCells(), m[1][1][1] == Enum.Material.Brick, m[3][1][3] == Enum.Material.Brick, "
                + "m[3][1][1] == Enum.Material.Air");

            Assert.AreEqual(3d, results[0]);
            Assert.AreEqual(true, results[1]);
            Assert.AreEqual(true, results[2]);
            Assert.AreEqual(true, results[3]);
        }

        [Test]
        public void P4_Lua_AWrite_AdvancesTheTerrainRevision_AndAnUnchangedWriteDoesNot()
        {
            P4TerrainLuaHarness harness = new();
            Assert.IsTrue(harness.Registry.TryGetRecord(harness.TerrainInstance.Id, out InstanceRecord record));
            long before = record.Revision;

            harness.Run("workspace.Terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), Enum.Material.Grass)");
            long afterWrite = record.Revision;
            harness.Run("workspace.Terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), Enum.Material.Grass)");

            Assert.Greater(afterWrite, before);
            Assert.AreEqual(afterWrite, record.Revision, "a write that changes no cell is not a mutation");
        }

        [Test]
        public void P4_Lua_StubbedMember_RaisesTheLoudStub()
        {
            P4TerrainLuaHarness harness = new();

            string error = ErrorOf(harness, "workspace.Terrain:FillBall(Vector3.new(), 4, Enum.Material.Grass)");

            StringAssert.Contains("NOT_IMPLEMENTED", error);
            StringAssert.Contains("FillBall", error);
        }

        [Test]
        public void P4_Lua_SkillSectionExample_Runs()
        {
            P4TerrainLuaHarness harness = new();

            harness.Run(SkillExample);

            RbxTerrain terrain = harness.TerrainInstance;
            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            TerrainMaterials.TryGetSlot("Rock", out byte rock);
            Assert.AreEqual(brick, terrain.Store.GetMaterial(10, 4, 10));
            Assert.AreEqual(TerrainMaterials.Air, terrain.Store.GetMaterial(10, 3, 10));
            Assert.AreEqual(rock, terrain.Store.GetMaterial(17, 4, 3), "(x + z) % 3 == 0 at Lua x = 2, z = 4");
            Assert.AreEqual(TerrainMaterials.Air, terrain.Store.GetMaterial(17, 4, 2), "Lua x = 2, z = 3 stays empty");
            Assert.AreEqual(TerrainMaterials.Air, terrain.Store.GetMaterial(17, 6, 3), "only two generated layers");
        }

        [Test]
        public void P4_Lua_BoundSurface_IsExactlyTheShippedSubset()
        {
            P4TerrainLuaHarness harness = new();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "FillBlock", "FillRegion", "ReadVoxels", "WriteVoxels", "Clear", "ReplaceMaterial",
                    "WorldToCell", "CellCenterToWorld", "CellCornerToWorld", "CountCells"
                },
                harness.MethodNames);
        }

        [Test]
        public void P4_Lua_WriteVoxels_StreamingCost_IsMeasuredPerCell()
        {
            P4TerrainLuaHarness harness = new();
            Stopwatch clock = new();
            long allocatedBefore = 0;
            long allocated = 0;
            harness.DefineFunction("__p4_start", () =>
            {
                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                clock.Restart();
            });
            harness.DefineFunction("__p4_stop", () =>
            {
                clock.Stop();
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            });

            harness.Run(
                "local t = workspace.Terrain\n"
                + "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(256, 256, 256))\n"
                + "local m, o = {}, {}\n"
                + "for x = 1, 64 do local mx, ox = {}, {} m[x] = mx o[x] = ox\n"
                + "  for y = 1, 64 do local my, oy = {}, {} mx[y] = my ox[y] = oy\n"
                + "    for z = 1, 64 do my[z] = (y < 32) and Enum.Material.Rock or Enum.Material.Air oy[z] = (y < 32) and 1 or 0 end\n"
                + "  end\n"
                + "end\n"
                + "t:WriteVoxels(region, 4, m, o)\n"
                + "for x = 1, 64 do for y = 1, 64 do for z = 1, 64 do m[x][y][z] = Enum.Material.Ground end end end\n"
                + "__p4_start()\n"
                + "t:WriteVoxels(region, 4, m, o)\n"
                + "__p4_stop()");

            const double cells = 64d * 64d * 64d;
            TestContext.WriteLine("WriteVoxels of " + cells + " cells (warm): "
                                  + (clock.Elapsed.TotalMilliseconds * 1e6 / cells).ToString("0.0") + " ns and "
                                  + (allocated / cells).ToString("0.000") + " bytes per cell ("
                                  + clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, " + allocated + " bytes)");
            Assert.AreEqual(64 * 31 * 64, harness.TerrainInstance.CountCells());
            Assert.Less(allocated / cells, 1d, "a warm WriteVoxels allocates less than a byte per cell");
        }

        [Test]
        public void P4_Lua_FillBlockSlabAndReadVoxels_CostIsMeasured()
        {
            P4TerrainLuaHarness harness = new();
            Stopwatch clock = new();
            long allocatedBefore = 0;
            double[] milliseconds = new double[2];
            long[] bytes = new long[2];
            int lap = 0;
            harness.DefineFunction("__p4_start", () =>
            {
                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                clock.Restart();
            });
            harness.DefineFunction("__p4_stop", () =>
            {
                clock.Stop();
                bytes[lap] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                milliseconds[lap] = clock.Elapsed.TotalMilliseconds;
                lap++;
            });

            harness.Run(
                "local t = workspace.Terrain\n"
                + "local region = Region3.new(Vector3.new(0, 0, 0), Vector3.new(256, 16, 256))\n"
                + "__p4_start()\n"
                + "t:FillBlock(CFrame.new(128, 8, 128), Vector3.new(256, 16, 256), Enum.Material.Grass)\n"
                + "__p4_stop()\n"
                + "__p4_start()\n"
                + "local m, o = t:ReadVoxels(region, 4)\n"
                + "__p4_stop()");

            TestContext.WriteLine("FillBlock of the 64 x 4 x 64 slab (16,384 cells, 16 new chunks): "
                                  + milliseconds[0].ToString("0.00") + " ms, " + bytes[0] + " bytes ("
                                  + (bytes[0] / 16384d).ToString("0.0") + " per cell, chunk storage included)");
            TestContext.WriteLine("ReadVoxels of the same region: " + milliseconds[1].ToString("0.00") + " ms, "
                                  + bytes[1] + " bytes (" + (bytes[1] / 16384d).ToString("0.0")
                                  + " per cell, the two result tables included)");
            Assert.AreEqual(2, lap);
            Assert.AreEqual(16384, harness.TerrainInstance.CountCells());
        }
    }
}
