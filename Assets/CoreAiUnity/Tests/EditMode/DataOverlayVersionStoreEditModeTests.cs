using System;
using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class DataOverlayVersionStoreEditModeTests
    {
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

        // ==================== F-11: retention policy (bounded history) ====================

        [Test]
        public void Memory_RetentionPolicy_100Revisions_BoundedHistory_OriginalAndCurrentIntact()
        {
            MemoryDataOverlayVersionStore s = new(20);
            for (int i = 0; i < 100; i++)
            {
                s.RecordSuccessfulApply("k", "p" + i);
            }

            Assert.IsTrue(s.TryGetSnapshot("k", out DataOverlayVersionRecord snap));
            Assert.LessOrEqual(snap.History.Count, 22, "original + 20 intermediate + current at most.");
            Assert.AreEqual("p0", snap.OriginalPayload);
            Assert.AreEqual("p99", snap.CurrentPayload);
            Assert.AreEqual(0, snap.History[0].Index);
            Assert.AreEqual(99, snap.History[snap.History.Count - 1].Index);

            s.ResetToOriginal("k");
            Assert.IsTrue(s.TryGetCurrentPayload("k", out string afterReset));
            Assert.AreEqual("p0", afterReset, "Revert-to-original still works after eviction.");
        }

        [Test]
        public void Memory_RetentionPolicy_ByteBudget_EvictsMiddleButKeepsOriginalAndCurrent()
        {
            MemoryDataOverlayVersionStore s = new(1000, 15);
            for (int i = 0; i < 5; i++)
            {
                s.RecordSuccessfulApply("k", "0123456789" + i);
            }

            Assert.IsTrue(s.TryGetSnapshot("k", out DataOverlayVersionRecord snap));
            Assert.AreEqual(2, snap.History.Count, "Byte budget evicts every middle revision, never original/current.");
            Assert.AreEqual(0, snap.History[0].Index);
            Assert.AreEqual(4, snap.History[1].Index);
        }

        [Test]
        public void Memory_RetentionPolicy_RevertToEvictedRevision_ReturnsNoChange()
        {
            MemoryDataOverlayVersionStore s = new(2);
            for (int i = 0; i < 10; i++)
            {
                s.RecordSuccessfulApply("k", "p" + i);
            }

            Assert.IsFalse(s.ResetToRevisionChanged("k", 1), "Reverting to an evicted revision is a no-op.");
            Assert.IsTrue(s.TryGetCurrentPayload("k", out string cur));
            Assert.AreEqual("p9", cur, "State is unchanged after a no-op revert.");
        }

        [Test]
        public void FileStore_RetentionPolicy_BoundedAcrossManyRevisions_RoundTrips()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "retention.json");
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
                FileDataOverlayVersionStore a = new(new NullGameLogger(), path, 5);
                for (int i = 0; i < 50; i++)
                {
                    a.RecordSuccessfulApply("k", "p" + i);
                }
            }

            FileDataOverlayVersionStore b = new(new NullGameLogger(), path);
            Assert.IsTrue(b.TryGetSnapshot("k", out DataOverlayVersionRecord snap));
            Assert.LessOrEqual(snap.History.Count, 7, "original + 5 intermediate + current at most.");
            Assert.AreEqual("p0", snap.OriginalPayload);
            Assert.AreEqual("p49", snap.CurrentPayload);
        }

        [Test]
        public void FileStore_InterruptedAtomicWrite_PreservesLiveRevisionHistory()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "atomic.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Delete(path);
            File.Delete(path + ".tmp");
            FileDataOverlayVersionStore store = new(new NullGameLogger(), path);
            store.RecordSuccessfulApply("k", "p1");

            try
            {
                FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting =
                    () => throw new IOException("simulated crash");
                store.RecordSuccessfulApply("k", "p2");
            }
            finally
            {
                FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting = null;
            }

            FileDataOverlayVersionStore reopened = new(new NullGameLogger(), path);
            Assert.IsTrue(reopened.TryGetSnapshot("k", out DataOverlayVersionRecord snapshot));
            Assert.AreEqual("p1", snapshot.CurrentPayload);
            Assert.IsTrue(File.Exists(path + ".tmp"));
        }
    }
}
