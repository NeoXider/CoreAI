using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaScriptVersionStoreEditModeTests
    {
        private const string Key = "test_slot";

        [Test]
        public void Memory_FirstSuccess_SetsOriginalAndCurrent()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution(Key, "a = 1");
            Assert.IsTrue(s.TryGetSnapshot(Key, out var snap));
            Assert.AreEqual("a = 1", snap.OriginalLua);
            Assert.AreEqual("a = 1", snap.CurrentLua);
            Assert.AreEqual(1, snap.History.Count);
        }

        [Test]
        public void Memory_SecondSuccess_PreservesOriginal_UpdatesCurrent()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution(Key, "v1");
            s.RecordSuccessfulExecution(Key, "v2");
            Assert.IsTrue(s.TryGetSnapshot(Key, out var snap));
            Assert.AreEqual("v1", snap.OriginalLua);
            Assert.AreEqual("v2", snap.CurrentLua);
            Assert.AreEqual(2, snap.History.Count);
        }

        [Test]
        public void Memory_Reset_RestoresCurrentToOriginal()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution(Key, "v1");
            s.RecordSuccessfulExecution(Key, "v2");
            s.ResetToOriginal(Key);
            Assert.IsTrue(s.TryGetSnapshot(Key, out var snap));
            Assert.AreEqual("v1", snap.OriginalLua);
            Assert.AreEqual("v1", snap.CurrentLua);
            Assert.AreEqual(1, snap.History.Count);
        }

        [Test]
        public void Memory_SeedThenRecord_KeepsOriginalFromSeed()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.SeedOriginal(Key, "seed", false);
            s.RecordSuccessfulExecution(Key, "edited");
            Assert.IsTrue(s.TryGetSnapshot(Key, out var snap));
            Assert.AreEqual("seed", snap.OriginalLua);
            Assert.AreEqual("edited", snap.CurrentLua);
        }

        [Test]
        public void Memory_BuildProgrammerPromptSection_ContainsBaseline()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution(Key, "alpha");
            s.RecordSuccessfulExecution(Key, "beta");
            var section = s.BuildProgrammerPromptSection(Key);
            StringAssert.Contains("Lua_script_versioning", section);
            StringAssert.Contains(Key, section);
            StringAssert.Contains("alpha", section);
            StringAssert.Contains("beta", section);
        }

        [Test]
        public void AiPromptComposer_ProgrammerWithKey_AppendsVersionSection()
        {
            var versions = new MemoryLuaScriptVersionStore();
            versions.RecordSuccessfulExecution("ui_logic", "print(1)");
            versions.RecordSuccessfulExecution("ui_logic", "print(2)");
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                versions);
            var u = composer.BuildUserPayload(new GameSessionSnapshot(), new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "h",
                LuaScriptVersionKey = "ui_logic"
            });
            StringAssert.Contains("Mutation_state", u);
            StringAssert.Contains("print(1)", u);
            StringAssert.Contains("print(2)", u);
        }

        [Test]
        public void Memory_ResetToRevision_RollsBackCurrentAndTrimsHistory()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution("rev", "v0");
            s.RecordSuccessfulExecution("rev", "v1");
            s.RecordSuccessfulExecution("rev", "v2");
            s.ResetToRevision("rev", 1);
            Assert.IsTrue(s.TryGetSnapshot("rev", out var snap));
            Assert.AreEqual("v1", snap.CurrentLua);
            Assert.AreEqual(2, snap.History.Count);
        }

        [Test]
        public void Memory_ResetAll_RestoresEveryKeyToBaseline()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution("a", "a1");
            s.RecordSuccessfulExecution("a", "a2");
            s.RecordSuccessfulExecution("b", "b1");
            s.RecordSuccessfulExecution("b", "b2");
            s.ResetAllToOriginal();
            Assert.IsTrue(s.TryGetSnapshot("a", out var sa));
            Assert.AreEqual("a1", sa.OriginalLua);
            Assert.AreEqual("a1", sa.CurrentLua);
            Assert.IsTrue(s.TryGetSnapshot("b", out var sb));
            Assert.AreEqual("b1", sb.CurrentLua);
        }

        [Test]
        public void Memory_GetKnownKeys_IsSorted()
        {
            var s = new MemoryLuaScriptVersionStore();
            s.RecordSuccessfulExecution("z", "1");
            s.RecordSuccessfulExecution("a", "1");
            var keys = s.GetKnownKeys();
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual("a", keys[0]);
            Assert.AreEqual("z", keys[1]);
        }

        [Test]
        public void FileStore_RoundTrip_PersistsAcrossInstances()
        {
            var path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "v.json");
            if (File.Exists(path))
                File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, true);

            {
                var a = new FileLuaScriptVersionStore(path);
                a.RecordSuccessfulExecution("k", "one");
                a.RecordSuccessfulExecution("k", "two");
            }

            var b = new FileLuaScriptVersionStore(path);
            Assert.IsTrue(b.TryGetSnapshot("k", out var snap));
            Assert.AreEqual("one", snap.OriginalLua);
            Assert.AreEqual("two", snap.CurrentLua);
            b.ResetToOriginal("k");

            var c = new FileLuaScriptVersionStore(path);
            Assert.IsTrue(c.TryGetSnapshot("k", out var snap2));
            Assert.AreEqual("one", snap2.CurrentLua);
        }

        [Test]
        public void FileStore_ResetAll_Persists()
        {
            var path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "reset_all.json");
            if (File.Exists(path))
                File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, true);

            {
                var a = new FileLuaScriptVersionStore(path);
                a.RecordSuccessfulExecution("x", "v1");
                a.RecordSuccessfulExecution("x", "v2");
                a.RecordSuccessfulExecution("y", "y0");
                a.ResetAllToOriginal();
            }

            var b = new FileLuaScriptVersionStore(path);
            Assert.IsTrue(b.TryGetSnapshot("x", out var sx));
            Assert.AreEqual("v1", sx.CurrentLua);
            Assert.IsTrue(b.TryGetSnapshot("y", out var sy));
            Assert.AreEqual("y0", sy.CurrentLua);
        }
    }
}
