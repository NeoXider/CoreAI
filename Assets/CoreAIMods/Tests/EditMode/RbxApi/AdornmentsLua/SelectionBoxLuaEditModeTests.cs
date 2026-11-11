using System.Threading;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.AdornmentsLua
{
    /// <summary>
    /// <c>SelectionBox</c> from Lua: created without the <c>Ui</c> capability (it is drawn in the world, not on the UI
    /// layer), its members read and write as in Roblox, <c>Adornee</c> takes an Instance or nil, and a ray never hits
    /// it. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class SelectionBoxLuaEditModeTests
    {
        private SynchronizationContext _savedContext;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = UxmlServiceTestWorld.WithFake();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void ASelectionBox_IsMadeWithoutTheUiCapability_AndItsMembersRoundTrip()
        {
            _world.Load("outline", @"
local part = Instance.new('Part')
part.Name = 'Target'
part.Anchored = true
part.Parent = workspace
local box = Instance.new('SelectionBox')
box.Name = 'Outline'
box.Adornee = part
box.LineThickness = 0.25
box.Color3 = Color3.new(1, 1, 0)
box.SurfaceTransparency = 0.75
box.Parent = workspace
store_set('read', box.Adornee.Name .. '|' .. tostring(box.LineThickness) .. '|' .. tostring(box.Color3) .. '|'
  .. tostring(box.SurfaceTransparency) .. '|' .. tostring(box.Visible) .. '|' .. tostring(box:IsA('GuiBase3d')))
local ok, err = pcall(function() box.Adornee = 5 end)
store_set('bad', tostring(ok) .. '|' .. tostring(err))
box.Adornee = nil
store_set('cleared', tostring(box.Adornee))
box.Adornee = part
local hit = workspace:Raycast(Vector3.new(0, 50, 0), Vector3.new(0, -100, 0))
store_set('ray', hit and hit.Instance.ClassName or 'nil')",
                LuaCapabilities.All & ~LuaCapabilities.Ui);

            Assert.AreEqual("Target|0.25|1, 1, 0|0.75|true|true", _world.Get("outline", "read"));
            StringAssert.StartsWith("false|", _world.Get("outline", "bad"));
            StringAssert.Contains("an Instance or nil", _world.Get("outline", "bad"));
            Assert.AreEqual("nil", _world.Get("outline", "cleared"));
            Assert.AreNotEqual("SelectionBox", _world.Get("outline", "ray"), "a selection box is never hit by a ray");
            RbxSelectionBox box = (RbxSelectionBox)_world.Roblox.Game.FindFirstChildOfClass("Workspace")
                .FindFirstChild("Outline");
            Assert.AreEqual("Target", box.Adornee.Name);
        }

        [Test]
        public void Negative_AScreenGui_StillNeedsTheUiCapability()
        {
            _world.Load("gui", @"
local ok, err = pcall(function() return Instance.new('ScreenGui') end)
store_set('gui', tostring(ok) .. '|' .. tostring(err))",
                LuaCapabilities.All & ~LuaCapabilities.Ui);

            StringAssert.StartsWith("false|", _world.Get("gui", "gui"));
            StringAssert.Contains("UI_CAPABILITY_REQUIRED", _world.Get("gui", "gui"));
        }
    }
}
