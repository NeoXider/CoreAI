using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    // WHY no [NonParallelizable]: Unity ships a stripped NUnit (com.unity.ext.nunit,
    // net40/unity-custom) that does not contain the parallel-execution attributes, so the attribute
    // fails to compile and takes down every player build, not just the test run. The Unity test
    // runner executes edit-mode tests sequentially anyway, so there is nothing to opt out of.
    public sealed class LuaScriptVersionStoreEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: a test here waits on a Task from the calling thread
        /// (Assert.ThrowsAsync/CatchAsync does exactly that). Under Unity's SynchronizationContext the
        /// awaited continuation is posted back to the very thread the wait is holding, and the whole
        /// EditMode run hangs at this fixture with no results file - not a failure, silence.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

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
            MemoryLuaScriptVersionStore s = new(20);
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
            MemoryLuaScriptVersionStore s = new(1000, 15);
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
            MemoryLuaScriptVersionStore s = new(2);
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
            MemoryLuaScriptVersionStore s = new(2);
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
                FileLuaScriptVersionStore a = new(new NullGameLogger(), path, 5);
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

        [Test]
        public void FileStore_InterruptedAtomicWrite_PreservesLiveRevisionHistory()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CoreAI_TestLuaVersions", "atomic.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Delete(path);
            File.Delete(path + ".tmp");
            FileLuaScriptVersionStore store = new(new NullGameLogger(), path);
            store.RecordSuccessfulExecution("k", "v1");

            try
            {
                FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting =
                    () => throw new IOException("simulated crash");
                Assert.Throws<IOException>(() => store.RecordSuccessfulExecution("k", "v2"));
            }
            finally
            {
                FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = null;
            }

            FileLuaScriptVersionStore reopened = new(new NullGameLogger(), path);
            Assert.IsTrue(reopened.TryGetSnapshot("k", out LuaScriptVersionRecord snapshot));
            Assert.AreEqual("v1", snapshot.CurrentLua);
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path), "atomic.json.*.tmp"));
        }

        [Test]
        public void FileStore_ConstructorDoesNotCreateDirectoriesOrReadCorruptFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-lazy-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(root, "versions.json");
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path);
                Assert.IsFalse(Directory.Exists(root), "Construction must not perform filesystem work.");
                Directory.CreateDirectory(root);
                File.WriteAllText(path, "{corrupt");
                Assert.DoesNotThrow(() => new FileLuaScriptVersionStore(new NullGameLogger(), path));
                Assert.Throws<InvalidDataException>(() => store.GetKnownKeys());
                Assert.AreEqual("{corrupt", File.ReadAllText(path));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileStore_AsyncFailedFlushRemainsPendingAcrossInstances_AndRetryDoesNotAddRevision()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-flush-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            bool confirmed = false;
            try
            {
                FileLuaScriptVersionStore writer = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance,
                    confirmDurabilityAsync: _ => Task.FromResult(confirmed));
                Assert.ThrowsAsync<IOException>(async () => await writer.RecordSuccessfulExecutionAsync("key", "source"));
                FileLuaScriptVersionStore reader = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                Assert.Throws<InvalidOperationException>(() => reader.GetKnownKeys());
                Assert.ThrowsAsync<IOException>(async () => await reader.GetSnapshotAsync("key"));
                confirmed = true;
                LuaScriptVersionRecord record = await reader.GetSnapshotAsync("key");
                Assert.AreEqual("source", record.CurrentLua);
                Assert.AreEqual(1, record.History.Count);
                await reader.RecordSuccessfulExecutionAsync("key", "source");
                Assert.AreEqual(1, (await reader.GetSnapshotAsync("key")).History.Count);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileStore_AsyncCancellationAfterWriteSettlesBeforeAliasWriterCanStart()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-order-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            TaskCompletionSource<bool> entered = new();
            TaskCompletionSource<bool> flush = new();
            Task write = null;
            Task later = null;
            using CancellationTokenSource cancellation = new();
            try
            {
                FileLuaScriptVersionStore first = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance,
                    confirmDurabilityAsync: token => { Assert.IsFalse(token.CanBeCanceled); entered.TrySetResult(true); return flush.Task; });
                string aliasPath = Path.Combine(root, ".", "versions.json");
                if (Path.DirectorySeparatorChar == '\\') aliasPath = aliasPath.ToUpperInvariant();
                FileLuaScriptVersionStore second = new(new NullGameLogger(), aliasPath,
                    host: PassThroughLlmAsyncMarshaler.Instance);
                write = first.RecordSuccessfulExecutionAsync("key", "first", cancellation.Token);
                Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
                cancellation.Cancel();
                Assert.IsFalse(write.IsCompleted);
                Task sync = Task.Run(() => Assert.Throws<InvalidOperationException>(() => second.GetKnownKeys()));
                Assert.AreSame(sync, await Task.WhenAny(sync, Task.Delay(5000)));
                await sync;
                later = second.RecordSuccessfulExecutionAsync("key", "second");
                Assert.IsFalse(later.IsCompleted);
                flush.TrySetResult(true);
                await write;
                await later;
                LuaScriptVersionRecord result = await second.GetSnapshotAsync("key");
                Assert.AreEqual("first", result.OriginalLua);
                Assert.AreEqual("second", result.CurrentLua);
                Assert.AreEqual(2, result.History.Count);
            }
            finally
            {
                flush.TrySetResult(true);
                if (write != null) await write;
                if (later != null) await later;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task FileStore_AsyncCancellationAtSwapAndParseFailurePreservePreviousBytes()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-failure-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                await store.RecordSuccessfulExecutionAsync("key", "old");
                string original = File.ReadAllText(path);
                using CancellationTokenSource cancellation = new();
                FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = cancellation.Cancel;
                try
                {
                    Assert.CatchAsync<OperationCanceledException>(async () =>
                        await store.RecordSuccessfulExecutionAsync("key", "new", cancellation.Token));
                }
                finally { FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = null; }
                Assert.AreEqual(original, File.ReadAllText(path));
                File.WriteAllText(path, "{corrupt");
                Assert.ThrowsAsync<InvalidDataException>(async () => await store.GetSnapshotAsync("key"));
                Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecordSuccessfulExecutionAsync("key", "replacement"));
                Assert.AreEqual("{corrupt", File.ReadAllText(path));
                File.WriteAllText(path, original);
                Assert.AreEqual("old", (await store.GetSnapshotAsync("key")).CurrentLua);
            }
            finally { FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = null; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileStore_LegacyJsonUtilityPayloadAndStableRevisionIndicesSurviveAsyncRewrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-legacy-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, "{\"slots\":[{\"scriptKey\":\"legacy\",\"originalLua\":\"first\",\"currentLua\":\"second\",\"history\":[" +
                    "{\"index\":0,\"source\":\"first\",\"utcTicks\":638935920000000001}," +
                    "{\"index\":7,\"source\":\"second\",\"utcTicks\":638935920000000002}]}]}");
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                LuaScriptVersionRecord before = await store.GetSnapshotAsync("legacy");
                Assert.AreEqual("first", before.OriginalLua);
                Assert.AreEqual("second", before.CurrentLua);
                Assert.AreEqual(638935920000000002L, before.History[1].UtcTicks);
                await store.RecordSuccessfulExecutionAsync("legacy", "third");
                FileLuaScriptVersionStore reopened = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                LuaScriptVersionRecord after = await reopened.GetSnapshotAsync("legacy");
                Assert.AreEqual(before.OriginalLua, after.OriginalLua);
                Assert.AreEqual(before.History[1].UtcTicks, after.History[1].UtcTicks);
                Assert.AreEqual(before.History[1].Index + 1, after.History[2].Index);
                Assert.AreEqual("third", after.CurrentLua);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileStore_DesktopAtomicWorkDoesNotHoldTheCallingThread()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-worker-" + Guid.NewGuid().ToString("N"));
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new(false);
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int fileThread = callerThread;
            Task write = null;
            try
            {
                FileLuaScriptVersionStore store = new(new NullGameLogger(), Path.Combine(root, "versions.json"),
                    host: PassThroughLlmAsyncMarshaler.Instance);
                FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = () =>
                {
                    fileThread = Thread.CurrentThread.ManagedThreadId;
                    entered.TrySetResult(true);
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test must release pending filesystem work.");
                };
                write = store.RecordSuccessfulExecutionAsync("key", "source");
                Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
                await Task.Yield();
                Assert.IsFalse(write.IsCompleted, "The caller can continue while private filesystem work is pending.");
                Assert.AreNotEqual(callerThread, fileThread, "Desktop filesystem work must not run inline on the caller.");
            }
            finally
            {
                release.Set();
                FileLuaScriptVersionStore.BeforeAtomicReplaceForTesting = null;
                if (write != null) await write;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestCase("{}")]
        [TestCase("{\"slots\":null}")]
        public async Task FileStore_MissingSlotCollectionCannotBecomeAnEmptySuccessfulLoad(string invalidJson)
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-schema-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, invalidJson);
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                Assert.ThrowsAsync<InvalidDataException>(async () => await store.GetKnownKeysAsync());
                Assert.AreEqual(invalidJson, File.ReadAllText(path));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileStore_OversizedVersionDoesNotReplaceThePreviousHistory()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-limit-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                await store.RecordSuccessfulExecutionAsync("key", "old");
                string oldBytes = File.ReadAllText(path);
                Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecordSuccessfulExecutionAsync("key", new string('x', FileLuaScriptVersionStore.MaxStoreBytes)));
                Assert.AreEqual(oldBytes, File.ReadAllText(path));
                Assert.AreEqual("old", (await store.GetSnapshotAsync("key")).CurrentLua);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // WHY: the browser build poisoned itself here. The synchronous Mutate() used to park a pending
        // durability confirmation under "#if UNITY_WEBGL && !UNITY_EDITOR", but only the async entry
        // points can clear one and the production callers of this API are synchronous
        // (LuaCsModRuntime.RecordRevision and friends). The first mod that recorded a revision
        // succeeded and every later synchronous call threw "Version durability is unconfirmed" - two
        // red errors per mod load in the player.
        [Test]
        public void FileStore_RepeatedSyncWrites_LeaveTheSyncApiUsable()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-sync-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                store.SeedOriginal("mod_a", "print('a')");
                Assert.DoesNotThrow(() => store.RecordSuccessfulExecution("mod_a", "print('a2')"));
                Assert.DoesNotThrow(() => store.SeedOriginal("mod_b", "print('b')"));
                Assert.DoesNotThrow(() => store.GetKnownKeys());
                Assert.IsTrue(store.TryGetSnapshot("mod_a", out LuaScriptVersionRecord snapshot));
                Assert.AreEqual("print('a2')", snapshot.CurrentLua);
                Assert.AreEqual(2, store.GetKnownKeys().Count);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void FileStore_SyncWrite_WithoutDurableStorage_FailsVisiblyAndKeepsReading()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-versions-nodurable-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "versions.json");
            try
            {
                FileLuaScriptVersionStore store = new(new NullGameLogger(), path, host: PassThroughLlmAsyncMarshaler.Instance);
                store.SeedOriginal("mod_a", "print('a')");
                store.FlushDurabilityForTesting = () => false;

                Assert.Throws<IOException>(() => store.RecordSuccessfulExecution("mod_a", "print('a2')"),
                    "A page with no durable storage must fail the write visibly, not report success.");

                store.FlushDurabilityForTesting = null;
                Assert.DoesNotThrow(() => store.GetKnownKeys(),
                    "A refused flush must not leave the store unusable for every later call.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
