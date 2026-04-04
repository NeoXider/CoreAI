using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
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
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution(Key, "a = 1");
            Assert.IsTrue(s.TryGetSnapshot(Key, out LuaScriptVersionRecord snap));
            Assert.AreEqual("a = 1", snap.OriginalLua);
            Assert.AreEqual("a = 1", snap.CurrentLua);
            Assert.AreEqual(1, snap.History.Count);
        }

        [Test]
        public void Memory_SecondSuccess_PreservesOriginal_UpdatesCurrent()
        {
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution(Key, "v1");
            s.RecordSuccessfulExecution(Key, "v2");
            Assert.IsTrue(s.TryGetSnapshot(Key, out LuaScriptVersionRecord snap));
            Assert.AreEqual("v1", snap.OriginalLua);
            Assert.AreEqual("v2", snap.CurrentLua);
            Assert.AreEqual(2, snap.History.Count);
        }

        [Test]
        public void Memory_Reset_RestoresCurrentToOriginal()
        {
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution(Key, "v1");
            s.RecordSuccessfulExecution(Key, "v2");
            s.ResetToOriginal(Key);
            Assert.IsTrue(s.TryGetSnapshot(Key, out LuaScriptVersionRecord snap));
            Assert.AreEqual("v1", snap.OriginalLua);
            Assert.AreEqual("v1", snap.CurrentLua);
            Assert.AreEqual(1, snap.History.Count);
        }

        [Test]
        public void Memory_SeedThenRecord_KeepsOriginalFromSeed()
        {
            MemoryLuaScriptVersionStore s = new();
            s.SeedOriginal(Key, "seed", false);
            s.RecordSuccessfulExecution(Key, "edited");
            Assert.IsTrue(s.TryGetSnapshot(Key, out LuaScriptVersionRecord snap));
            Assert.AreEqual("seed", snap.OriginalLua);
            Assert.AreEqual("edited", snap.CurrentLua);
        }

        [Test]
        public void Memory_BuildProgrammerPromptSection_ContainsBaseline()
        {
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution(Key, "alpha");
            s.RecordSuccessfulExecution(Key, "beta");
            string section = s.BuildProgrammerPromptSection(Key);
            StringAssert.Contains("Lua_script_versioning", section);
            StringAssert.Contains(Key, section);
            StringAssert.Contains("alpha", section);
            StringAssert.Contains("beta", section);
        }

        [Test]
        public void AiPromptComposer_ProgrammerWithKey_AppendsVersionSection()
        {
            MemoryLuaScriptVersionStore versions = new();
            versions.RecordSuccessfulExecution("ui_logic", "print(1)");
            versions.RecordSuccessfulExecution("ui_logic", "print(2)");
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                versions);
            string u = composer.BuildUserPayload(new GameSessionSnapshot(), new AiTaskRequest
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
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution("rev", "v0");
            s.RecordSuccessfulExecution("rev", "v1");
            s.RecordSuccessfulExecution("rev", "v2");
            s.ResetToRevision("rev", 1);
            Assert.IsTrue(s.TryGetSnapshot("rev", out LuaScriptVersionRecord snap));
            Assert.AreEqual("v1", snap.CurrentLua);
            Assert.AreEqual(2, snap.History.Count);
        }

        [Test]
        public void Memory_ResetAll_RestoresEveryKeyToBaseline()
        {
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution("a", "a1");
            s.RecordSuccessfulExecution("a", "a2");
            s.RecordSuccessfulExecution("b", "b1");
            s.RecordSuccessfulExecution("b", "b2");
            s.ResetAllToOriginal();
            Assert.IsTrue(s.TryGetSnapshot("a", out LuaScriptVersionRecord sa));
            Assert.AreEqual("a1", sa.OriginalLua);
            Assert.AreEqual("a1", sa.CurrentLua);
            Assert.IsTrue(s.TryGetSnapshot("b", out LuaScriptVersionRecord sb));
            Assert.AreEqual("b1", sb.CurrentLua);
        }

        [Test]
        public void Memory_GetKnownKeys_IsSorted()
        {
            MemoryLuaScriptVersionStore s = new();
            s.RecordSuccessfulExecution("z", "1");
            s.RecordSuccessfulExecution("a", "1");
            IReadOnlyList<string> keys = s.GetKnownKeys();
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual("a", keys[0]);
            Assert.AreEqual("z", keys[1]);
        }

        [Test]
        public void FileStore_RoundTrip_PersistsAcrossInstances()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "v.json");
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
                FileLuaScriptVersionStore a = new(new NullGameLogger(), path);
                a.RecordSuccessfulExecution("k", "one");
                a.RecordSuccessfulExecution("k", "two");
            }

            FileLuaScriptVersionStore b = new(new NullGameLogger(), path);
            Assert.IsTrue(b.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.AreEqual("one", snap.OriginalLua);
            Assert.AreEqual("two", snap.CurrentLua);
            b.ResetToOriginal("k");

            FileLuaScriptVersionStore c = new(new NullGameLogger(), path);
            Assert.IsTrue(c.TryGetSnapshot("k", out LuaScriptVersionRecord snap2));
            Assert.AreEqual("one", snap2.CurrentLua);
        }

        [Test]
        public void FileStore_ResetAll_Persists()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "reset_all.json");
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
                FileLuaScriptVersionStore a = new(new NullGameLogger(), path);
                a.RecordSuccessfulExecution("x", "v1");
                a.RecordSuccessfulExecution("x", "v2");
                a.RecordSuccessfulExecution("y", "y0");
                a.ResetAllToOriginal();
            }

            FileLuaScriptVersionStore b = new(new NullGameLogger(), path);
            Assert.IsTrue(b.TryGetSnapshot("x", out LuaScriptVersionRecord sx));
            Assert.AreEqual("v1", sx.CurrentLua);
            Assert.IsTrue(b.TryGetSnapshot("y", out LuaScriptVersionRecord sy));
            Assert.AreEqual("y0", sy.CurrentLua);
        }
    }
}