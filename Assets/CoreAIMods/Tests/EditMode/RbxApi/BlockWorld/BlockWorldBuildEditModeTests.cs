using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's build mod (<c>blockworld_build</c>) against a real Terrain store and a physics port
    /// that casts against its cells: the outline is drawn around the cell under the crosshair and follows it,
    /// a left click breaks exactly that cell and a right click places the hotbar block on the face it showed,
    /// both in the frame of the click; nothing changes beyond the reach, on bedrock, under a UI-processed click,
    /// or in survival without blocks. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldBuildEditModeTests
    {
        private const string Mod = "blockworld_build";
        private const int LeftButton = 0;
        private const int RightButton = 1;

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
            byte grass = BlockWorldTestWorld.Slot("Grass");
            _world.Terrain.Store.FillBox(TerrainCellBox.FromMinMax(-8, -2, -8, 9, 1, 9), grass, 255);
            _world.Load(Mod);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>Looks at the top face of cell (x, 0, z) from 10 studs up and 6 studs back.</summary>
        private void AimAtTopOf(int x, int z)
        {
            RbxVector3 face = new(x * 4 + 2, 4, z * 4 + 2);
            _world.Aim(new RbxVector3(face.X, face.Y + 10, face.Z - 6), face);
        }

        private static RbxVector3 Cell(int x, int y, int z)
        {
            return new RbxVector3(x, y, z);
        }

        private (RbxVector3 Center, RbxVector3 Size, bool Visible) OutlineBox()
        {
            RbxInstance folder = _world.Workspace.FindFirstChild("BlockWorldOutline");
            Assert.IsNotNull(folder, "the build mod owns an outline folder");
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            bool visible = true;
            int edges = 0;
            foreach (RbxInstance edge in folder.GetChildren())
            {
                Assert.IsTrue(_world.Roblox.PartSink.TryGetPartProperties(edge.Id, out PartProperties part));
                edges++;
                visible &= part.Transparency < 0.5f;
                minX = Math.Min(minX, part.Position.X - part.Size.X / 2);
                minY = Math.Min(minY, part.Position.Y - part.Size.Y / 2);
                minZ = Math.Min(minZ, part.Position.Z - part.Size.Z / 2);
                maxX = Math.Max(maxX, part.Position.X + part.Size.X / 2);
                maxY = Math.Max(maxY, part.Position.Y + part.Size.Y / 2);
                maxZ = Math.Max(maxZ, part.Position.Z + part.Size.Z / 2);
            }

            Assert.AreEqual(12, edges, "twelve edges");
            return (new RbxVector3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2),
                new RbxVector3(maxX - minX, maxY - minY, maxZ - minZ), visible);
        }

        private static void AssertNear(RbxVector3 expected, RbxVector3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, 0.01f, what + " X");
            Assert.AreEqual(expected.Y, actual.Y, 0.01f, what + " Y");
            Assert.AreEqual(expected.Z, actual.Z, 0.01f, what + " Z");
        }

        [Test]
        public void P6_Build_TheOutline_SurroundsTheCellUnderTheCrosshair_AndFollowsIt()
        {
            AimAtTopOf(0, 0);

            Assert.AreEqual(true, _world.Attribute("CrosshairHasTarget"));
            AssertNear(Cell(0, 0, 0), _world.Vector("CrosshairCell"), "the cell looked at");
            AssertNear(Cell(0, 1, 0), _world.Vector("CrosshairPlaceCell"), "the cell on its top face");
            AssertNear(Cell(0, 1, 0), _world.Vector("CrosshairNormal"), "the face normal");
            (RbxVector3 center, RbxVector3 size, bool visible) = OutlineBox();
            Assert.IsTrue(visible);
            AssertNear(new RbxVector3(2, 2, 2), center, "outline centre");
            Assert.AreEqual(4.1f, size.X, 0.02f, "a cell plus the edge thickness");

            AimAtTopOf(3, -2);

            AssertNear(Cell(3, 0, -2), _world.Vector("CrosshairCell"), "the new cell");
            AssertNear(new RbxVector3(14, 2, -6), OutlineBox().Center, "the outline followed it");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_LookingAtTheSky_ShowsNoOutline_NegativeTwin()
        {
            _world.Aim(new RbxVector3(2, 10, 2), new RbxVector3(2, 30, 40));

            Assert.AreEqual(false, _world.Attribute("CrosshairHasTarget"));
            Assert.IsFalse(OutlineBox().Visible, "nothing to outline");
        }

        [Test]
        public void P6_Build_Here_IsTheCrosshairsPlaceCell_WhenItHasATarget()
        {
            AimAtTopOf(3, 3);
            _world.Frame(20);

            Assert.AreEqual(Cell(3, 1, 3), _world.Attribute("BuildHereCell"), "the empty cell over the outlined block");
            Assert.AreEqual(_world.Attribute("CrosshairPlaceCell"), _world.Attribute("BuildHereCell"));
            Assert.AreEqual("crosshair", _world.Attribute("BuildHereFrom"));
        }

        [Test]
        public void P6_Build_Here_WithoutATarget_IsTheGroundThreeCellsAhead_Twin()
        {
            _world.ConnectLocalPlayer();
            _world.LoadSource("test_stand", "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new(10, 12, 10))");
            _world.Aim(new RbxVector3(10, 14, 10), new RbxVector3(10, 30, 50));
            _world.Frame(20);

            Assert.AreEqual(false, _world.Attribute("CrosshairHasTarget"), "the crosshair points at the sky");
            Assert.AreEqual(Cell(2, 1, 5), _world.Attribute("BuildHereCell"),
                "the first empty cell over the ground three cells ahead along the level look (+Z)");
            Assert.AreEqual("ahead", _world.Attribute("BuildHereFrom"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_ALeftClick_BreaksExactlyTheOutlinedCell_InTheFrameOfTheClick()
        {
            AimAtTopOf(1, 1);
            RbxVector3 outlined = _world.Vector("CrosshairCell");
            long cells = _world.Terrain.CountCells();

            _world.Input.SetMouseButton(LeftButton, true);
            _world.Frame();

            Assert.AreEqual(TerrainMaterials.Air, _world.Material((int)outlined.X, (int)outlined.Y, (int)outlined.Z),
                "the outlined cell is gone in the frame of the click");
            Assert.AreEqual(cells - 1, _world.Terrain.CountCells(), "and only that cell");
            Assert.AreEqual(1d, _world.Number("BlockWorldBroken"));
            _world.Input.SetMouseButton(LeftButton, false);
            _world.Frame();
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_ARightClick_PlacesTheHotbarBlock_OnTheOutlinedFace()
        {
            _world.SetAttribute("BlockWorldSelected", "Brick");
            AimAtTopOf(-2, 3);
            RbxVector3 place = _world.Vector("CrosshairPlaceCell");
            long cells = _world.Terrain.CountCells();

            _world.Input.SetMouseButton(RightButton, true);
            _world.Frame();

            Assert.AreEqual(BlockWorldTestWorld.Slot("Brick"), _world.Material((int)place.X, (int)place.Y, (int)place.Z));
            AssertNear(Cell(-2, 1, 3), place, "on top of the outlined cell");
            Assert.AreEqual(cells + 1, _world.Terrain.CountCells());
            Assert.AreEqual(1d, _world.Number("BlockWorldPlaced"));
            _world.Input.SetMouseButton(RightButton, false);
            _world.Frame();
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_AHotbarItemThatIsNotABlock_IsLeftToItsOwnMod()
        {
            _world.SetAttribute("BlockWorldSelected", "Torch");
            AimAtTopOf(0, 0);
            long revision = _world.Terrain.Store.Revision;

            _world.Click(RightButton);

            Assert.AreEqual(revision, _world.Terrain.Store.Revision, "the build mod places only Terrain materials");
        }

        [Test]
        public void P6_Build_AClickBeyondTheReach_ChangesNothing_NegativeTwin()
        {
            RbxVector3 face = new(2, 4, 2);
            _world.Aim(new RbxVector3(2, 24, -10), face);
            Assert.AreEqual(false, _world.Attribute("CrosshairHasTarget"), "23 studs away is out of reach");
            long revision = _world.Terrain.Store.Revision;

            _world.Click(LeftButton);
            _world.Click(RightButton);

            Assert.AreEqual(revision, _world.Terrain.Store.Revision);
            Assert.AreEqual(0d, _world.Number("BlockWorldBroken"));
            Assert.AreEqual(0d, _world.Number("BlockWorldPlaced"));

            _world.Aim(new RbxVector3(2, 16, -6), face);
            Assert.AreEqual(true, _world.Attribute("CrosshairHasTarget"), "16 studs away is in reach");
            _world.Click(LeftButton);
            Assert.AreNotEqual(revision, _world.Terrain.Store.Revision);
        }

        [Test]
        public void P6_Build_Bedrock_CannotBeBroken()
        {
            _world.Terrain.Store.FillBox(TerrainCellBox.FromMinMax(-8, -16, -8, 9, -15, 9), BlockWorldTestWorld.Slot("Basalt"), 255);
            _world.Aim(new RbxVector3(2, -50, -6), new RbxVector3(2, -60, 2));
            AssertNear(Cell(0, -16, 0), _world.Vector("CrosshairCell"), "looking at the bedrock");
            long revision = _world.Terrain.Store.Revision;

            _world.Click(LeftButton);

            Assert.AreEqual(revision, _world.Terrain.Store.Revision);
            StringAssert.Contains("Bedrock", (string)_world.Attribute("BlockWorldNotice"));
        }

        [Test]
        public void P6_Build_AClickTheUiTook_ChangesNothing_NegativeTwin()
        {
            AimAtTopOf(0, 0);
            long revision = _world.Terrain.Store.Revision;
            _world.Focus.SetCaptured("chat", true);

            _world.Click(LeftButton);

            Assert.AreEqual(revision, _world.Terrain.Store.Revision, "gameProcessed input never reaches the world");
            _world.Focus.SetCaptured("chat", false);
            _world.Click(LeftButton);
            Assert.AreNotEqual(revision, _world.Terrain.Store.Revision);
        }

        [Test]
        public void P6_Build_Survival_PlacesOnlyWhatWasBroken_AndCreativeIsEndless()
        {
            _world.SetAttribute("BlockWorldCreative", false);
            _world.Frame();
            _world.SetAttribute("BlockWorldSelected", "Grass");
            AimAtTopOf(0, 0);
            long cells = _world.Terrain.CountCells();

            _world.Click(RightButton);
            Assert.AreEqual(cells, _world.Terrain.CountCells(), "no Grass in the inventory yet");
            StringAssert.Contains("No Grass left", (string)_world.Attribute("BlockWorldNotice"));

            _world.Click(LeftButton);
            Assert.AreEqual(1d, _world.Number("BlockWorldCount_Grass"), "breaking collects the block");
            AimAtTopOf(4, 4);
            _world.Click(RightButton);
            Assert.AreEqual(cells, _world.Terrain.CountCells(), "one broken, one placed");
            Assert.AreEqual(0d, _world.Number("BlockWorldCount_Grass"));

            _world.Tap(BlockWorldTestWorld.KeyG);
            Assert.AreEqual(true, _world.Attribute("BlockWorldCreative"), "G switches back to creative");
            _world.Click(RightButton);
            _world.Click(RightButton);
            Assert.AreEqual(cells + 2, _world.Terrain.CountCells(), "creative places without counting");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_Survival_ATreeTrunkGivesPlanks()
        {
            _world.Terrain.Store.SetCell(0, 0, 0, BlockWorldTestWorld.Slot("Mud"), 255);
            _world.SetAttribute("BlockWorldCreative", false);
            _world.Frame();
            AimAtTopOf(0, 0);

            _world.Click(LeftButton);

            Assert.AreEqual(TerrainMaterials.Air, _world.Material(0, 0, 0), "the trunk cell was broken");
            Assert.AreEqual(1d, _world.Number("BlockWorldCount_WoodPlanks"), "a trunk is collected as planks");
            Assert.IsNull(_world.Attribute("BlockWorldCount_Mud"), "a trunk is never counted as Mud");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Build_HoldingTheButton_BreaksAgainEveryQuarterSecond()
        {
            AimAtTopOf(0, 0);
            _world.Input.SetMouseButton(LeftButton, true);
            _world.Frame();
            Assert.AreEqual(1d, _world.Number("BlockWorldBroken"));

            _world.Frame(31);

            Assert.AreEqual(3d, _world.Number("BlockWorldBroken"), "two repeats in the next half second");
            _world.Input.SetMouseButton(LeftButton, false);
            _world.Frame(30);
            Assert.AreEqual(3d, _world.Number("BlockWorldBroken"), "released, it stops");
        }
    }
}
