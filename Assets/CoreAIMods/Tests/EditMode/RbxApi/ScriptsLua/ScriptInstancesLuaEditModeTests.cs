using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step B1 through the real mod runtime: a mod without a declared context runs exactly as before (the
    /// legacy mode of SC-8) while its <c>script</c> is now a bound <see cref="RbxBaseScript"/> at the same place;
    /// Lua sees no script members yet, and an <c>Instance.new</c> script is an unbound instance that runs nothing.
    /// </summary>
    [TestFixture]
    public sealed class ScriptInstancesLuaEditModeTests
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

        private RbxBaseScript ScriptOf(string modId)
        {
            RbxInstance serverScripts = _world.Roblox.Game.FindFirstChildOfClass("ServerScriptService");
            RbxInstance folder = serverScripts.FindFirstChild(modId);
            Assert.IsNotNull(folder, "the legacy placement keeps ServerScriptService/<id> (Folder)");
            return folder.FindFirstChildOfClass("Script") as RbxBaseScript;
        }

        [Test]
        public void SC_8_AModWithoutAContext_RunsAsToday_AndItsScriptIsABoundLegacyBaseScript()
        {
            _world.Load("legacy-hud", @"
store_set('view', script.ClassName .. '|' .. tostring(script:IsA('BaseScript')) .. '|'
  .. tostring(script:IsA('LuaSourceContainer')) .. '|' .. script.Name .. '|' .. script.Parent.ClassName .. '|'
  .. tostring(script.Parent.Parent == game:GetService('ServerScriptService')))
store_set('ran', 'yes')");

            Assert.AreEqual("Script|true|true|legacy-hud|Folder|true", _world.Get("legacy-hud", "view"),
                "Lua sees the same script value, at the same path, as before step B1");
            Assert.AreEqual("yes", _world.Get("legacy-hud", "ran"));
            RbxBaseScript script = ScriptOf("legacy-hud");
            Assert.IsNotNull(script, "the catalog builds the synthetic script as an RbxBaseScript");
            Assert.AreEqual("legacy-hud", script.SourceId, "it names the mod it runs");
            Assert.IsTrue(script.Enabled);
            Assert.AreEqual(RunContextValues.Legacy, script.RunContext, "no declared context is the legacy context");
        }

        [Test]
        public void SC_8_Negative_ALegacyScriptReadsItsEnabled_AndAnInstanceNewScriptRunsNothing()
        {
            _world.Load("probe", @"
local ok, err = pcall(function() return script.Enabled end)
store_set('enabled', tostring(ok) .. '|' .. tostring(err))
local ok2, err2 = pcall(function() script.Source = 'print(1)' end)
store_set('source', tostring(ok2) .. '|' .. tostring(err2))
local made = Instance.new('Script')
made.Name = 'Authored'
made.Parent = workspace
local module = Instance.new('ModuleScript')
module.Parent = made
store_set('made', made.ClassName .. '|' .. module.ClassName .. '|' .. tostring(module:IsA('BaseScript')))");
            _world.Frame();

            Assert.AreEqual("true|true", _world.Get("probe", "enabled"), "MVP4 step C2 binds BaseScript.Enabled");
            StringAssert.StartsWith("false|", _world.Get("probe", "source"), "a mod never writes a Source (SC-2)");
            Assert.AreEqual("Script|ModuleScript|false", _world.Get("probe", "made"));
            RbxBaseScript made = _world.Roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("Authored")
                as RbxBaseScript;
            Assert.IsNotNull(made);
            Assert.IsFalse(made.IsBound, "an Instance.new script names no source and so runs nothing (SC-17)");
        }
    }
}
