using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Messaging;
using CoreAI.Session;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class DataOverlayVersionStoreEditModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly System.Collections.Generic.List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        [Test]
        public void Memory_ApplyThenReset_RestoresBaseline()
        {
            MemoryDataOverlayVersionStore s = new();
            s.RecordSuccessfulApply("prog.baseline", "{\"lvl\":1}");
            s.RecordSuccessfulApply("prog.baseline", "{\"lvl\":2}");
            s.ResetToOriginal("prog.baseline");
            Assert.IsTrue(s.TryGetCurrentPayload("prog.baseline", out string cur));
            Assert.AreEqual("{\"lvl\":1}", cur);
        }

        [Test]
        public void Memory_ResetAll_AllKeys()
        {
            MemoryDataOverlayVersionStore s = new();
            s.RecordSuccessfulApply("a", "1");
            s.RecordSuccessfulApply("a", "2");
            s.RecordSuccessfulApply("b", "x");
            s.RecordSuccessfulApply("b", "y");
            s.ResetAllToOriginal();
            Assert.IsTrue(s.TryGetCurrentPayload("a", out string ca));
            Assert.AreEqual("1", ca);
            Assert.IsTrue(s.TryGetCurrentPayload("b", out string cb));
            Assert.AreEqual("x", cb);
        }

        [Test]
        public void FileStore_RoundTrip()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "d.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            {
                FileDataOverlayVersionStore a = new(new NullGameLogger(), path);
                a.RecordSuccessfulApply("k", "{\"n\":1}");
                a.RecordSuccessfulApply("k", "{\"n\":2}");
            }

            FileDataOverlayVersionStore b = new(new NullGameLogger(), path);
            Assert.IsTrue(b.TryGetSnapshot("k", out DataOverlayVersionRecord snap));
            Assert.AreEqual("{\"n\":1}", snap.OriginalPayload);
            Assert.AreEqual("{\"n\":2}", snap.CurrentPayload);
        }

        [Test]
        public void AiPromptComposer_ProgrammerWithOverlayCsv_AppendsSections()
        {
            MemoryDataOverlayVersionStore data = new();
            data.RecordSuccessfulApply("arena.meta", "{\"xp\":0}");
            data.RecordSuccessfulApply("arena.meta", "{\"xp\":10}");
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(),
                data);
            string u = composer.BuildUserPayload(new GameSessionSnapshot(), new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "h",
                DataOverlayVersionKeysCsv = "arena.meta"
            });
            StringAssert.Contains("Mutation_state", u);
            StringAssert.Contains("arena.meta", u);
            StringAssert.Contains("\"xp\":0", u);
            StringAssert.Contains("\"xp\":10", u);
        }

        [Test]
        public void Memory_ResetToRevision_RollsBackCurrentAndTrimsHistory()
        {
            MemoryDataOverlayVersionStore s = new();
            s.RecordSuccessfulApply("k", "{\"n\":1}");
            s.RecordSuccessfulApply("k", "{\"n\":2}");
            s.RecordSuccessfulApply("k", "{\"n\":3}");
            s.ResetToRevision("k", 1);
            Assert.IsTrue(s.TryGetSnapshot("k", out DataOverlayVersionRecord snap));
            Assert.AreEqual("{\"n\":2}", snap.CurrentPayload);
            Assert.AreEqual(2, snap.History.Count);
        }

        [Test]
        public void VersioningLuaBindings_DataAndLuaReset_FromSandbox()
        {
            MemoryLuaScriptVersionStore luaStore = new();
            luaStore.SeedOriginal("slot", "print(1)", false);
            luaStore.RecordSuccessfulExecution("slot", "print(2)");

            MemoryDataOverlayVersionStore dataStore = new();
            dataStore.RecordSuccessfulApply("cfg", "{\"a\":1}");
            dataStore.RecordSuccessfulApply("cfg", "{\"a\":2}");

            LuaApiRegistry reg = new();
            new CoreAiVersioningLuaRuntimeBindings(luaStore, dataStore).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            env.RunChunk(script, "coreai_lua_reset('slot')\ncoreai_data_reset('cfg')");

            Assert.IsTrue(luaStore.TryGetSnapshot("slot", out LuaScriptVersionRecord ls));
            Assert.AreEqual("print(1)", ls.CurrentLua);
            Assert.IsTrue(dataStore.TryGetCurrentPayload("cfg", out string cur));
            Assert.AreEqual("{\"a\":1}", cur);
        }

        [Test]
        public void VersioningLuaBindings_ListKeys_FromSandbox()
        {
            MemoryLuaScriptVersionStore luaStore = new();
            luaStore.RecordSuccessfulExecution("a", "print(1)");
            luaStore.RecordSuccessfulExecution("b", "print(2)");
            MemoryDataOverlayVersionStore dataStore = new();
            dataStore.RecordSuccessfulApply("cfg_a", "{}");
            dataStore.RecordSuccessfulApply("cfg_b", "{}");
            LuaApiRegistry reg = new();
            new CoreAiVersioningLuaRuntimeBindings(luaStore, dataStore).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            DynValue dv = env.RunChunk(script, "return coreai_lua_list_keys() .. ';' .. coreai_data_list_keys()");
            string s = dv.ToObject<string>();
            StringAssert.Contains("a", s);
            StringAssert.Contains("b", s);
            StringAssert.Contains("cfg_a", s);
            StringAssert.Contains("cfg_b", s);
        }

        [Test]
        public void VersioningLuaBindings_DataApply_InvalidJson_Throws()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiVersioningLuaRuntimeBindings(
                new MemoryLuaScriptVersionStore(),
                new MemoryDataOverlayVersionStore(),
                sink,
                new DefaultDataOverlayPayloadValidator()).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_data_apply('bad','oops')"));
        }

        [Test]
        public void VersioningLuaBindings_DataApply_PublishesDataOverlayApplied()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiVersioningLuaRuntimeBindings(
                new MemoryLuaScriptVersionStore(),
                new MemoryDataOverlayVersionStore(),
                sink,
                new DefaultDataOverlayPayloadValidator()).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            env.RunChunk(script, "coreai_data_apply('cfg','{\"a\":1}')");
            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(AiGameCommandTypeIds.DataOverlayApplied, sink.Items[0].CommandTypeId);
            StringAssert.Contains("\"key\":\"cfg\"", sink.Items[0].JsonPayload);
        }
    }
}