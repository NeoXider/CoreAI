using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Session;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class DataOverlayVersionStoreEditModeTests
    {
        [Test]
        public void Memory_ApplyThenReset_RestoresBaseline()
        {
            var s = new MemoryDataOverlayVersionStore();
            s.RecordSuccessfulApply("prog.baseline", "{\"lvl\":1}");
            s.RecordSuccessfulApply("prog.baseline", "{\"lvl\":2}");
            s.ResetToOriginal("prog.baseline");
            Assert.IsTrue(s.TryGetCurrentPayload("prog.baseline", out var cur));
            Assert.AreEqual("{\"lvl\":1}", cur);
        }

        [Test]
        public void Memory_ResetAll_AllKeys()
        {
            var s = new MemoryDataOverlayVersionStore();
            s.RecordSuccessfulApply("a", "1");
            s.RecordSuccessfulApply("a", "2");
            s.RecordSuccessfulApply("b", "x");
            s.RecordSuccessfulApply("b", "y");
            s.ResetAllToOriginal();
            Assert.IsTrue(s.TryGetCurrentPayload("a", out var ca));
            Assert.AreEqual("1", ca);
            Assert.IsTrue(s.TryGetCurrentPayload("b", out var cb));
            Assert.AreEqual("x", cb);
        }

        [Test]
        public void FileStore_RoundTrip()
        {
            var path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "d.json");
            if (File.Exists(path))
                File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, true);

            {
                var a = new FileDataOverlayVersionStore(path);
                a.RecordSuccessfulApply("k", "{\"n\":1}");
                a.RecordSuccessfulApply("k", "{\"n\":2}");
            }

            var b = new FileDataOverlayVersionStore(path);
            Assert.IsTrue(b.TryGetSnapshot("k", out var snap));
            Assert.AreEqual("{\"n\":1}", snap.OriginalPayload);
            Assert.AreEqual("{\"n\":2}", snap.CurrentPayload);
        }

        [Test]
        public void AiPromptComposer_ProgrammerWithOverlayCsv_AppendsSections()
        {
            var data = new MemoryDataOverlayVersionStore();
            data.RecordSuccessfulApply("arena.meta", "{\"xp\":0}");
            data.RecordSuccessfulApply("arena.meta", "{\"xp\":10}");
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(),
                data);
            var u = composer.BuildUserPayload(new GameSessionSnapshot(), new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "h",
                DataOverlayVersionKeysCsv = "arena.meta"
            });
            StringAssert.Contains("Data_overlay_versioning", u);
            StringAssert.Contains("arena.meta", u);
            StringAssert.Contains("\"xp\":0", u);
            StringAssert.Contains("\"xp\":10", u);
        }

        [Test]
        public void VersioningLuaBindings_DataAndLuaReset_FromSandbox()
        {
            var luaStore = new MemoryLuaScriptVersionStore();
            luaStore.SeedOriginal("slot", "print(1)", false);
            luaStore.RecordSuccessfulExecution("slot", "print(2)");

            var dataStore = new MemoryDataOverlayVersionStore();
            dataStore.RecordSuccessfulApply("cfg", "{\"a\":1}");
            dataStore.RecordSuccessfulApply("cfg", "{\"a\":2}");

            var reg = new LuaApiRegistry();
            new CoreAiVersioningLuaRuntimeBindings(luaStore, dataStore).RegisterGameplayApis(reg);
            var env = new SecureLuaEnvironment();
            var script = env.CreateScript(reg);
            env.RunChunk(script, "coreai_lua_reset('slot')\ncoreai_data_reset('cfg')");

            Assert.IsTrue(luaStore.TryGetSnapshot("slot", out var ls));
            Assert.AreEqual("print(1)", ls.CurrentLua);
            Assert.IsTrue(dataStore.TryGetCurrentPayload("cfg", out var cur));
            Assert.AreEqual("{\"a\":1}", cur);
        }
    }
}
