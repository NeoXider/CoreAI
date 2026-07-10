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

        // ==================== F-11: retention policy (bounded history) ====================

        [Test]
        public void Memory_RetentionPolicy_100Revisions_BoundedHistory_OriginalAndCurrentIntact()
        {
            MemoryLuaScriptVersionStore s = new(maxIntermediateRevisions: 20);
            for (int i = 0; i < 100; i++)
            {
                s.RecordSuccessfulExecution("k", "v" + i);
            }

            Assert.IsTrue(s.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.LessOrEqual(snap.History.Count, 22, "original + 20 intermediate + current at most.");
            Assert.AreEqual("v0", snap.OriginalLua);
            Assert.AreEqual("v99", snap.CurrentLua);
            Assert.AreEqual(0, snap.History[0].Index, "Original keeps its original stable index.");
            Assert.AreEqual(99, snap.History[snap.History.Count - 1].Index, "Current keeps its original stable index.");

            s.ResetToOriginal("k");
            Assert.IsTrue(s.TryGetSnapshot("k", out LuaScriptVersionRecord afterReset));
            Assert.AreEqual("v0", afterReset.CurrentLua, "Revert-to-original still works after eviction.");
        }

        [Test]
        public void Memory_RetentionPolicy_ByteBudget_EvictsMiddleButKeepsOriginalAndCurrent()
        {
            // maxIntermediateRevisions is large so only the byte budget drives eviction; each 11-char
            // revision is 11 UTF-8 bytes, and a 15-byte budget cannot hold any intermediate revision
            // alongside original+current, so eviction converges to exactly those two entries.
            MemoryLuaScriptVersionStore s = new(maxIntermediateRevisions: 1000, maxTotalBytes: 15);
            for (int i = 0; i < 5; i++)
            {
                s.RecordSuccessfulExecution("k", "0123456789" + i);
            }

            Assert.IsTrue(s.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.AreEqual(2, snap.History.Count, "Byte budget evicts every middle revision, never original/current.");
            Assert.AreEqual(0, snap.History[0].Index);
            Assert.AreEqual(4, snap.History[1].Index);
        }

        [Test]
        public void Memory_RetentionPolicy_RevertToEvictedRevision_ReturnsNoChange()
        {
            MemoryLuaScriptVersionStore s = new(maxIntermediateRevisions: 2);
            for (int i = 0; i < 10; i++)
            {
                s.RecordSuccessfulExecution("k", "v" + i);
            }

            // Revision index 1 was evicted (only original(0) + last 2 intermediate + current(9) remain).
            Assert.IsFalse(s.ResetToRevisionChanged("k", 1), "Reverting to an evicted revision is a no-op.");
            Assert.IsTrue(s.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.AreEqual("v9", snap.CurrentLua, "State is unchanged after a no-op revert.");
        }

        [Test]
        public void Memory_RetentionPolicy_RevertToStillKeptRevision_UsesStableIndexNotPosition()
        {
            MemoryLuaScriptVersionStore s = new(maxIntermediateRevisions: 2);
            for (int i = 0; i < 10; i++)
            {
                s.RecordSuccessfulExecution("k", "v" + i);
            }

            // Original (index 0) is always kept regardless of position shifts caused by eviction.
            Assert.IsTrue(s.ResetToRevisionChanged("k", 0));
            Assert.IsTrue(s.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.AreEqual("v0", snap.CurrentLua);
        }

        [Test]
        public void FileStore_RetentionPolicy_BoundedAcrossManyRevisions_RoundTrips()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "retention.json");
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
                FileLuaScriptVersionStore a = new(new NullGameLogger(), path, maxIntermediateRevisions: 5);
                for (int i = 0; i < 50; i++)
                {
                    a.RecordSuccessfulExecution("k", "v" + i);
                }
            }

            FileLuaScriptVersionStore b = new(new NullGameLogger(), path);
            Assert.IsTrue(b.TryGetSnapshot("k", out LuaScriptVersionRecord snap));
            Assert.LessOrEqual(snap.History.Count, 7, "original + 5 intermediate + current at most.");
            Assert.AreEqual("v0", snap.OriginalLua);
            Assert.AreEqual("v49", snap.CurrentLua);
        }
    }
}