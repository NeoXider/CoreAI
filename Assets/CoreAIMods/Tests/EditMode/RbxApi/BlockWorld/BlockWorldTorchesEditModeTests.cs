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
    /// P6, the Block World's torch mod (<c>blockworld_torches</c>): a right click with the torch in hand puts a torch
    /// with a PointLight on the outlined face (through the build mod's real crosshair), a left click on it takes it
    /// back, a torch whose block is broken drops, a ceiling is refused, and the mod's explicit light budget of 48 is
    /// held: the 49th torch is refused with a notice and taking one back frees a place. Every rule has its twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldTorchesEditModeTests
    {
        private const string Mod = "blockworld_torches";
        private const int Budget = 48;
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
            _world.Terrain.Store.FillBox(TerrainCellBox.FromMinMax(-12, -2, -12, 13, 1, 13),
                BlockWorldTestWorld.Slot("Rock"), 255);
            _world.SetAttribute("BlockWorldSelected", "Torch");
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxInstance Folder => _world.Workspace.FindFirstChild("BlockWorldTorches");

        private List<RbxInstance> Lights()
        {
            List<RbxInstance> lights = new();
            foreach (RbxInstance descendant in Folder.GetDescendants())
            {
                if (descendant.ClassName == "PointLight")
                {
                    lights.Add(descendant);
                }
            }

            return lights;
        }

        /// <summary>Stands in for the build mod's crosshair: the top face of cell (x, 0, z).</summary>
        private void PointAtFloor(int x, int z)
        {
            _world.SetAttribute("CrosshairHasTarget", true);
            _world.SetAttribute("CrosshairPlaceCell", new RbxVector3(x, 1, z));
            _world.SetAttribute("CrosshairNormal", new RbxVector3(0, 1, 0));
            _world.SetAttribute("CrosshairPartName", "");
        }

        [Test]
        public void P6_Torches_ARightClick_PutsALitTorchOnTheOutlinedFloor_AndALeftClickTakesItBack()
        {
            _world.Load("blockworld_build");
            _world.Load(Mod);
            RbxVector3 face = new(2, 4, 2);
            _world.Aim(new RbxVector3(2, 14, -4), face);
            long revision = _world.Terrain.Store.Revision;

            _world.Click(RightButton);

            RbxInstance stick = Folder.FindFirstChild("Torch_0_1_0");
            Assert.IsNotNull(stick, "a torch in the cell above the outlined block");
            Assert.IsTrue(_world.Roblox.PartSink.TryGetPartProperties(stick.Id, out PartProperties part));
            Assert.AreEqual(2f, part.Position.X, 0.01f);
            Assert.AreEqual(2f, part.Position.Z, 0.01f);
            Assert.Less(part.Position.Y, 6f, "it stands on the floor of its cell");
            List<RbxInstance> lights = Lights();
            Assert.AreEqual(1, lights.Count);
            RbxPointLight light = (RbxPointLight)lights[0];
            Assert.Greater(light.Range, 20f);
            Assert.IsFalse(light.Shadows);
            Assert.AreEqual(revision, _world.Terrain.Store.Revision, "a torch is a Part, no Terrain cell changes");

            _world.Frame(2);
            Assert.AreEqual(stick.Name, _world.Attribute("CrosshairPartName"), "the crosshair now rests on the torch");
            _world.Click(LeftButton);

            Assert.IsNull(Folder.FindFirstChild("Torch_0_1_0"), "taken back");
            Assert.AreEqual(revision, _world.Terrain.Store.Revision, "the block under it was not broken too");
            _world.AssertHealthy("blockworld_build", Mod);
        }

        [Test]
        public void P6_Torches_TheLightBudget_RefusesThe49thTorch_AndTakingOneBackFreesAPlace()
        {
            _world.Load(Mod);
            int placed = 0;
            for (int x = -12; x <= 12 && placed < Budget; x += 2)
            {
                for (int z = -12; z <= 12 && placed < Budget; z += 2)
                {
                    PointAtFloor(x, z);
                    _world.Click(RightButton);
                    placed++;
                }
            }

            Assert.AreEqual(Budget, Lights().Count, "48 lit torches");
            Assert.AreEqual((double)Budget, _world.Number("BlockWorldTorches"));

            PointAtFloor(11, 11);
            _world.Click(RightButton);

            Assert.AreEqual(Budget, Lights().Count, "the 49th is refused");
            Assert.IsNull(Folder.FindFirstChild("Torch_11_1_11"));
            StringAssert.Contains("Torch budget reached", (string)_world.Attribute("BlockWorldNotice"));

            _world.SetAttribute("CrosshairPartName", "TorchFlame_-12_1_-12");
            _world.Click(LeftButton);
            Assert.AreEqual(Budget - 1, Lights().Count, "a click on a flame takes its torch back");

            PointAtFloor(11, 11);
            _world.Click(RightButton);
            Assert.IsNotNull(Folder.FindFirstChild("Torch_11_1_11"), "the freed place is used");
            Assert.AreEqual(Budget, Lights().Count);
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Torches_ATorchWhoseBlockIsBroken_Drops()
        {
            _world.Load(Mod);
            PointAtFloor(3, 3);
            _world.Click(RightButton);
            PointAtFloor(5, 5);
            _world.Click(RightButton);
            Assert.AreEqual(2, Lights().Count);

            _world.Terrain.Store.SetCell(3, 0, 3, TerrainMaterials.Air, 0);
            _world.Frame(3);

            Assert.IsNull(Folder.FindFirstChild("Torch_3_1_3"), "its support is gone");
            Assert.IsNotNull(Folder.FindFirstChild("Torch_5_1_5"), "the other torch still stands");
        }

        [Test]
        public void P6_Torches_TheTorchesOfASavedWorld_AreAdopted_NotDuplicated()
        {
            _world.LoadSource("test_saved", @"
local folder = Instance.new('Folder')
folder.Name = 'BlockWorldTorches'
folder.Parent = workspace
local stick = Instance.new('Part')
stick.Name = 'Torch_4_1_4'
stick:SetAttribute('TorchCell', Vector3.new(4, 1, 4))
stick:SetAttribute('TorchSupport', Vector3.new(4, 0, 4))
stick.Parent = folder
local flame = Instance.new('Part')
flame.Name = 'TorchFlame_4_1_4'
flame.Parent = folder
Instance.new('PointLight').Parent = flame");
            _world.Load(Mod);

            Assert.AreEqual(1d, _world.Number("BlockWorldTorches"), "the saved torch counts against the budget");
            int folders = 0;
            foreach (RbxInstance child in _world.Workspace.GetChildren())
            {
                folders += child.Name == "BlockWorldTorches" ? 1 : 0;
            }

            Assert.AreEqual(1, folders, "one folder of torches");
            _world.SetAttribute("CrosshairPartName", "Torch_4_1_4");
            _world.Click(LeftButton);
            Assert.AreEqual(0d, _world.Number("BlockWorldTorches"), "and it can be taken back");
        }

        [Test]
        public void P6_Torches_ACeiling_IsRefused_NegativeTwin()
        {
            _world.Load(Mod);
            _world.SetAttribute("CrosshairHasTarget", true);
            _world.SetAttribute("CrosshairPlaceCell", new RbxVector3(0, -3, 0));
            _world.SetAttribute("CrosshairNormal", new RbxVector3(0, -1, 0));

            _world.Click(RightButton);

            Assert.AreEqual(0, Lights().Count);
            StringAssert.Contains("not ceilings", (string)_world.Attribute("BlockWorldNotice"));
        }

        [Test]
        public void P6_Torches_WithABlockInHand_ARightClickPutsNoTorch_NegativeTwin()
        {
            _world.Load(Mod);
            _world.SetAttribute("BlockWorldSelected", "Brick");
            PointAtFloor(0, 0);

            _world.Click(RightButton);

            Assert.AreEqual(0, Lights().Count);
        }
    }
}
