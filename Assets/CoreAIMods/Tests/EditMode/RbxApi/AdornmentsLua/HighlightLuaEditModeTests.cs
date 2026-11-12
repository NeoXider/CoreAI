using System.Threading;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.AdornmentsLua
{
    /// <summary>
    /// <c>Highlight</c> from Lua: created without the <c>Ui</c> capability, every mirror member reads and writes as in
    /// Roblox (<c>Enum.HighlightDepthMode</c>, Color3 fill and outline, finite transparencies), a wrong type is refused
    /// with the member named, and a property signal fires once per real change. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class HighlightLuaEditModeTests
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
        public void AHighlight_IsMadeWithoutTheUiCapability_AndItsMembersRoundTrip()
        {
            _world.Load("glow", @"
local part = Instance.new('Part')
part.Name = 'Target'
part.Anchored = true
part.Parent = workspace
local h = Instance.new('Highlight')
h.Name = 'Glow'
store_set('defaults', tostring(h.DepthMode) .. '|' .. tostring(h.Enabled) .. '|' .. tostring(h.FillColor) .. '|'
  .. tostring(h.FillTransparency) .. '|' .. tostring(h.OutlineColor) .. '|' .. tostring(h.OutlineTransparency) .. '|'
  .. tostring(h.Adornee))
h.Parent = part
h.DepthMode = Enum.HighlightDepthMode.Occluded
h.FillColor = Color3.new(0, 1, 0)
h.FillTransparency = 0.25
h.OutlineColor = Color3.new(0, 0, 1)
h.OutlineTransparency = 0.5
h.Enabled = false
store_set('read', tostring(h.DepthMode) .. '|' .. tostring(h.FillColor) .. '|' .. tostring(h.FillTransparency) .. '|'
  .. tostring(h.OutlineColor) .. '|' .. tostring(h.OutlineTransparency) .. '|' .. tostring(h.Enabled) .. '|'
  .. tostring(h:IsA('Instance')))
local fired = 0
h:GetPropertyChangedSignal('FillColor'):Connect(function() fired = fired + 1; store_set('fired', tostring(fired)) end)
h.FillColor = Color3.new(1, 1, 1)
h.FillColor = Color3.new(1, 1, 1)
local ok, err = pcall(function() h.Adornee = 5 end)
store_set('bad', tostring(ok) .. '|' .. tostring(err))
local okDepth, errDepth = pcall(function() h.DepthMode = Enum.Material.Plastic end)
store_set('badDepth', tostring(okDepth))
h.DepthMode = 'AlwaysOnTop'
store_set('byName', tostring(h.DepthMode))
h.Adornee = part
store_set('adornee', h.Adornee.Name)",
                LuaCapabilities.All & ~LuaCapabilities.Ui);
            _world.Roblox.Scheduler.Advance(0d);

            Assert.AreEqual("Enum.HighlightDepthMode.AlwaysOnTop|true|1, 0, 0|0.5|1, 1, 1|0|nil",
                _world.Get("glow", "defaults"));
            Assert.AreEqual("Enum.HighlightDepthMode.Occluded|0, 1, 0|0.25|0, 0, 1|0.5|false|true",
                _world.Get("glow", "read"));
            Assert.AreEqual("1", _world.Get("glow", "fired"), "one signal for one real change");
            StringAssert.StartsWith("false|", _world.Get("glow", "bad"));
            Assert.AreEqual("false", _world.Get("glow", "badDepth"), "an item of another enum is refused");
            Assert.AreEqual("Enum.HighlightDepthMode.AlwaysOnTop", _world.Get("glow", "byName"),
                "an item name is taken, as in Roblox");
            Assert.AreEqual("Target", _world.Get("glow", "adornee"));

            RbxInstance target = _world.Roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("Target");
            RbxHighlight highlight = (RbxHighlight)target.FindFirstChild("Glow");
            Assert.AreEqual(RbxHighlightDepthMode.AlwaysOnTop, highlight.DepthMode);
            Assert.AreEqual(new RbxColor3(1f, 1f, 1f), highlight.FillColor);
            Assert.AreSame(target, highlight.EffectiveAdornee);
        }

        [Test]
        public void Negative_ANonFiniteTransparency_IsRefusedFromLua()
        {
            _world.Load("nan", @"
local h = Instance.new('Highlight')
local ok, err = pcall(function() h.FillTransparency = 0 / 0 end)
store_set('nan', tostring(ok) .. '|' .. tostring(err))
store_set('kept', tostring(h.FillTransparency))");

            StringAssert.StartsWith("false|", _world.Get("nan", "nan"));
            StringAssert.Contains("finite", _world.Get("nan", "nan"));
            Assert.AreEqual("0.5", _world.Get("nan", "kept"));
        }
    }
}
