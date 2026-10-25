using System;
using System.IO;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Session;
using CoreAI.Messaging;
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
                DataOverlayPersistenceException error = Assert.Throws<DataOverlayPersistenceException>(() =>
                    store.RecordSuccessfulApply("k", "p2"));
                Assert.IsFalse(error.LocalCommitCompleted);
                Assert.IsTrue(store.TryGetCurrentPayload("k", out string live));
                Assert.AreEqual("p1", live);
            }
            finally
            {
                FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting = null;
            }

            FileDataOverlayVersionStore reopened = new(new NullGameLogger(), path);
            Assert.IsTrue(reopened.TryGetSnapshot("k", out DataOverlayVersionRecord snapshot));
            Assert.AreEqual("p1", snapshot.CurrentPayload);
            Assert.IsFalse(File.Exists(path + ".tmp"));
            store.RecordSuccessfulApply("k", "p2");
            FileDataOverlayVersionStore retried = new(new NullGameLogger(), path);
            Assert.IsTrue(retried.TryGetCurrentPayload("k", out string persisted));
            Assert.AreEqual("p2", persisted);
        }

        [Test]
        public void MOD010_EmptyOriginalSurvivesSeedResetAllAndFileReopen()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "empty-baseline.json");
            File.Delete(path);
            FileDataOverlayVersionStore store = new(new NullGameLogger(), path);
            store.RecordSuccessfulApply("empty", "");
            store.RecordSuccessfulApply("empty", "{}");
            store.SeedOriginal("empty", "replacement");
            Assert.IsTrue(store.TryGetSnapshot("empty", out DataOverlayVersionRecord snapshot));
            Assert.AreEqual("", snapshot.OriginalPayload);
            Assert.AreEqual("{}", snapshot.CurrentPayload);
            Assert.AreEqual(2, snapshot.History.Count);
            FileDataOverlayVersionStore reopened = new(new NullGameLogger(), path);
            reopened.ResetToOriginal("empty");
            Assert.IsTrue(reopened.TryGetCurrentPayload("empty", out string empty));
            Assert.AreEqual("", empty);
            reopened.RecordSuccessfulApply("empty", "{\"edit\":true}");
            reopened.ResetAllToOriginal();
            FileDataOverlayVersionStore final = new(new NullGameLogger(), path);
            Assert.IsTrue(final.TryGetSnapshot("empty", out DataOverlayVersionRecord finalSnapshot));
            Assert.AreEqual("", finalSnapshot.CurrentPayload);
            Assert.AreEqual("", finalSnapshot.OriginalPayload);
            Assert.AreEqual(1, finalSnapshot.History.Count);
        }

        private sealed class OverlayWritePolicy : ILuaVersioningWritePolicy
        {
            public bool CanWrite(string ownerModId, ActorContext? actorContext, string key, bool luaHistory)
                => ownerModId == "writer" && key == "k" && !luaHistory;
        }

        private sealed class OverlaySink : IAiGameCommandSink
        {
            public int Count;
            public void Publish(ApplyAiGameCommand command) => Count++;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MOD002_FailedLuaApplyDoesNotPublishAndAnIdenticalRetryPersists(bool refuseArming)
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "retry-" + refuseArming + ".json");
            File.Delete(path);
            FileDataOverlayVersionStore store = new(new NullGameLogger(), path);
            store.RecordSuccessfulApply("k", "{\"initial\":true}");
            OverlaySink sink = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                DataOverlayVersions = store, CommandSink = sink, VersioningWritePolicy = new OverlayWritePolicy()
            });
            ActorContext actor = new LocalActorIdentityProvider("overlay-writer")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            System.Threading.SynchronizationContext context = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                if (refuseArming) { FileDataOverlayVersionStore.PersistenceArmingForTesting = () => false; }
                else { FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting = () => throw new IOException("injected write failure"); }
                Assert.Catch(() => stack.Runtime.LoadMod(actor, "writer", "coreai_data_apply('k','{}')", LuaCapabilities.Read | LuaCapabilities.WorldEdit, persistToStore: false));
                Assert.AreEqual(0, sink.Count);
                Assert.IsFalse(stack.Runtime.IsLoaded(actor, "writer"));
                Assert.IsTrue(store.TryGetCurrentPayload("k", out string current));
                Assert.AreEqual("{\"initial\":true}", current);
                FileDataOverlayVersionStore.PersistenceArmingForTesting = null;
                FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting = null;
                stack.Runtime.LoadMod(actor, "writer", "coreai_data_apply('k','{}')", LuaCapabilities.Read | LuaCapabilities.WorldEdit, persistToStore: false);
                Assert.AreEqual(1, sink.Count);
                FileDataOverlayVersionStore reopened = new(new NullGameLogger(), path);
                Assert.IsTrue(reopened.TryGetCurrentPayload("k", out string persisted));
                Assert.AreEqual("{}", persisted);
            }
            finally
            {
                FileDataOverlayVersionStore.PersistenceArmingForTesting = null;
                FileDataOverlayVersionStore.BeforeAtomicReplaceForTesting = null;
                if (stack.Runtime.IsLoaded(actor, "writer")) { stack.Runtime.UnloadMod(actor, "writer"); }
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            }
        }

        [Test]
        public void MOD002_RefusedArmingReportsLocalCommitAndKeepsTheLiveBaseline()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestDataOverlays", "arming.json");
            File.Delete(path);
            FileDataOverlayVersionStore store = new(new NullGameLogger(), path);
            store.RecordSuccessfulApply("k", "old");
            try
            {
                FileDataOverlayVersionStore.PersistenceArmingForTesting = () => false;
                DataOverlayPersistenceException error = Assert.Throws<DataOverlayPersistenceException>(() => store.RecordSuccessfulApply("k", "new"));
                Assert.IsTrue(error.LocalCommitCompleted);
                Assert.IsTrue(store.TryGetCurrentPayload("k", out string current));
                Assert.AreEqual("old", current);
                FileDataOverlayVersionStore reopened = new(new NullGameLogger(), path);
                Assert.IsTrue(reopened.TryGetCurrentPayload("k", out string local));
                Assert.AreEqual("new", local);
            }
            finally { FileDataOverlayVersionStore.PersistenceArmingForTesting = null; }
            store.RecordSuccessfulApply("k", "new");
            Assert.IsTrue(store.TryGetCurrentPayload("k", out string retried));
            Assert.AreEqual("new", retried);
        }
    }
}
