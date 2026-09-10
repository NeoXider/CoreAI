using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class FileSkillStoreAsyncEditModeTests
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

        private string _root;
        [SetUp] public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "coreai-skills-async-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        private static SkillRecord Record(string text = "main") => new("Skill", "description", text, Array.Empty<string>());
        private static Task<int> Save(FileSkillStore store, SkillRecord record, ILlmAsyncMarshaler host = null,
            Func<int, CancellationToken, Task> publish = null, CancellationToken token = default)
            => store.MutateAndPublishAsync(record.Id, _ => SkillStoreMutation<int>.SaveRecord(record, 1), publish,
                host ?? PassThroughLlmAsyncMarshaler.Instance, token);
        private static async Task Entered(Task signal)
        { Assert.AreSame(signal, await Task.WhenAny(signal, Task.Delay(5000)), "The controlled boundary must be reached."); await signal; }

        private sealed class Host : ILlmAsyncMarshaler
        {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: a test here waits on a Task from the calling thread (Assert.ThrowsAsync/CatchAsync, or
        /// a blocking read of a Task local). Under Unity's SynchronizationContext the awaited
        /// continuation is posted back to the very thread the wait is holding, and the EditMode batch
        /// stops with no results file - silence, not a failure.
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

            private readonly AsyncLocal<bool> _inside = new();
            internal bool Inside => _inside.Value;
            public async Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                bool previous = _inside.Value;
                _inside.Value = true;
                try { return await factory(); }
                finally { _inside.Value = previous; }
            }
        }

        [Test]
        public async Task AsyncWrite_HoldsAliasOrderingThroughFlushAndPublication_WithoutBlockingSyncCaller()
        {
            Host host = new();
            TaskCompletionSource<bool> entered = new();
            TaskCompletionSource<bool> flush = new();
            TaskCompletionSource<bool> published = new();
            TaskCompletionSource<bool> releasePublication = new();
            using FileSkillStore first = new(_root, host: host, confirmDurabilityAsync: token =>
            {
                Assert.IsTrue(host.Inside);
                Assert.IsFalse(token.CanBeCanceled);
                entered.TrySetResult(true);
                return flush.Task;
            });
            string aliasRoot = Path.Combine(_root, ".");
            if (Path.DirectorySeparatorChar == '\\') aliasRoot = aliasRoot.ToUpperInvariant();
            using FileSkillStore second = new(aliasRoot, host: host);
            Task<int> write = first.MutateAndPublishAsync("Skill", current =>
            {
                Assert.IsTrue(host.Inside);
                return SkillStoreMutation<int>.SaveRecord(Record(), 1);
            }, async (value, token) =>
            {
                Assert.IsTrue(host.Inside);
                published.TrySetResult(true);
                await releasePublication.Task;
            }, host);
            Task<int> later = null;
            try
            {
                await Entered(entered.Task);
                Task sync = Task.Run(() => Assert.Throws<InvalidOperationException>(() => second.List()));
                await Entered(sync);
                later = second.MutateAndPublishAsync("skill", current =>
                    SkillStoreMutation<int>.SaveRecord(new SkillRecord(current.Id, "new", "second", Array.Empty<string>(), 1), 2), null, host);
                Assert.IsFalse(later.IsCompleted);
                flush.SetResult(true);
                await Entered(published.Task);
                Assert.AreNotSame(later, await Task.WhenAny(later, Task.Delay(200)),
                    "The next writer cannot overtake catalog publication while its controlled callback is still pending.");
            }
            finally { flush.TrySetResult(true); releasePublication.TrySetResult(true); await write; if (later != null) await later; }
            Assert.AreEqual("second", (await second.LoadAsync("SKILL")).Instructions);
            Assert.AreEqual(1, (await second.ListAsync()).Count);
        }

        [Test]
        public async Task FailedFlush_NewInstanceMustConfirmBeforeReading_AndDoesNotReplayMutation()
        {
            bool confirmed = false;
            int preparations = 0;
            int publications = 0;
            using FileSkillStore writer = new(_root, host: PassThroughLlmAsyncMarshaler.Instance,
                confirmDurabilityAsync: _ => Task.FromResult(confirmed));
            Assert.ThrowsAsync<SkillStoreDurabilityException>(async () => await writer.MutateAndPublishAsync("Skill", _ =>
            { preparations++; return SkillStoreMutation<int>.SaveRecord(Record(), 1); },
                (_, token) => { publications++; return Task.CompletedTask; }, PassThroughLlmAsyncMarshaler.Instance));
            using FileSkillStore reader = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            Assert.Throws<InvalidOperationException>(() => reader.TryLoad("Skill", out _));
            Assert.ThrowsAsync<SkillStoreDurabilityException>(async () => await reader.ListAsync());
            confirmed = true;
            Assert.AreEqual("main", (await reader.LoadAsync("Skill")).Instructions);
            Assert.AreEqual(1, preparations);
            Assert.AreEqual(0, publications, "Read-time recovery confirms storage; rehydration owns later catalog publication.");
        }

        [Test]
        public async Task CancellationBeforeWritePreservesBytesAndCatalog()
        {
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            await Save(store, Record("old"));
            using CancellationTokenSource cancellation = new();
            int published = 0;
            Assert.CatchAsync<OperationCanceledException>(async () => await store.MutateAndPublishAsync("Skill", _ =>
            { cancellation.Cancel(); return SkillStoreMutation<int>.SaveRecord(Record("new"), 1); },
                (_, token) => { published++; return Task.CompletedTask; }, PassThroughLlmAsyncMarshaler.Instance, cancellation.Token));
            Assert.AreEqual("old", (await store.LoadAsync("Skill")).Instructions);
            Assert.AreEqual(0, published);
        }

        [Test]
        public async Task CancellationAfterWriteSettlesFlushAndPublishesOnce()
        {
            TaskCompletionSource<bool> entered = new();
            TaskCompletionSource<bool> flush = new();
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance,
                confirmDurabilityAsync: token => { Assert.IsFalse(token.CanBeCanceled); entered.TrySetResult(true); return flush.Task; });
            using CancellationTokenSource cancellation = new();
            int publications = 0;
            Task<int> write = Save(store, Record(), publish: (_, token) =>
            { Assert.IsFalse(token.CanBeCanceled); publications++; return Task.CompletedTask; }, token: cancellation.Token);
            try
            {
                await Entered(entered.Task);
                cancellation.Cancel();
                Assert.IsFalse(write.IsCompleted);
            }
            finally { flush.TrySetResult(true); }
            Assert.AreEqual(1, await write);
            Assert.AreEqual(1, publications);
            Assert.AreEqual("main", (await store.LoadAsync("Skill")).Instructions);
        }

        [Test]
        public async Task CorruptExistingRecordAndOversizeWriteNeverOverwriteCommittedSkill()
        {
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            await Save(store, Record());
            string path = Directory.GetFiles(_root, "*.json")[0];
            string original = File.ReadAllText(path);
            Assert.ThrowsAsync<InvalidDataException>(async () => await Save(store, Record(new string('x', FileSkillStore.MaxRecordBytes))));
            Assert.AreEqual(original, File.ReadAllText(path));
            File.WriteAllText(path, "{broken");
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.ListAsync());
            Assert.ThrowsAsync<InvalidDataException>(async () => await Save(store, Record("replacement")));
            Assert.AreEqual("{broken", File.ReadAllText(path));
        }

        [Test]
        public async Task RevisionWriteFailureReportsWarningWhileCommittedSkillRemainsAvailable()
        {
            string versionsPath = Path.Combine(_root, "blocked-versions.json");
            Directory.CreateDirectory(versionsPath);
            using FileSkillStore store = new(Path.Combine(_root, "skills"), host: PassThroughLlmAsyncMarshaler.Instance);
            FileLuaScriptVersionStore revisions = new(new NullGameLogger(), versionsPath, host: PassThroughLlmAsyncMarshaler.Instance);
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, revisions, _ => null,
                callbackContext: PassThroughLlmAsyncMarshaler.Instance);
            SkillAuthoringResult result = await coordinator.CreateAsync("Skill", "description", "instructions", Array.Empty<string>());
            Assert.IsTrue(result.Success);
            Assert.AreEqual(false, result.RevisionRecorded, "A failed revision file write cannot be reported as recorded.");
            Assert.AreEqual("instructions", (await store.LoadAsync("Skill")).Instructions);
            Assert.IsNotNull(catalog.Get("Skill"));
        }

        [Test]
        public async Task PublicationFailureIsAnExplicitCommittedOutcome()
        {
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            Assert.ThrowsAsync<SkillStorePublicationException>(async () => await Save(store, Record(),
                publish: (_, token) => throw new InvalidOperationException("publication failed")));
            Assert.AreEqual("main", (await store.LoadAsync("Skill")).Instructions);
        }

        [Test]
        public async Task MultifileLegacyReadMigrationAndMainUpdatePreserveReferenceDocuments()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "Skill.json"),
                "{\"Id\":\"Skill\",\"Description\":\"legacy\",\"Instructions\":\"main\",\"ToolNames\":[],\"Version\":0," +
                "\"Sections\":[{\"Name\":\"SKILL.md\",\"Content\":\"main\"},{\"Name\":\"reference.md\",\"Content\":\"reference facts\"}]}");
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            SkillRecord loaded = await store.LoadAsync("skill");
            Assert.AreEqual(2, loaded.Sections.Count);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "Skill.json")), "A successful async load migrates the legacy path.");
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(), _ => null,
                callbackContext: PassThroughLlmAsyncMarshaler.Instance);
            await coordinator.RehydrateFromStoreAsync();
            SkillAuthoringResult updated = await coordinator.UpdateAsync("skill", null, "new main", null);
            Assert.IsTrue(updated.Success);
            using FileSkillStore reopened = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            SkillRecord result = await reopened.LoadAsync("SKILL");
            Assert.AreEqual("new main", result.Sections[0].Content);
            Assert.AreEqual("reference facts", result.Sections[1].Content);
        }

        [Test]
        public async Task FailedLegacyMigrationFlushCannotExposeUnconfirmedRecords()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "Skill.json"), "{\"Id\":\"Skill\",\"Instructions\":\"legacy\",\"Version\":0}");
            bool confirmed = false;
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance,
                confirmDurabilityAsync: _ => Task.FromResult(confirmed));
            Assert.ThrowsAsync<SkillStoreDurabilityException>(async () => await store.LoadAsync("Skill"));
            Assert.Throws<InvalidOperationException>(() => store.List());
            confirmed = true;
            Assert.AreEqual("legacy", (await store.LoadAsync("Skill")).Instructions);
            Assert.AreEqual(1, Directory.GetFiles(_root, "*.json").Length);
        }

        [Test]
        public async Task ReentrantAsyncFlushFailsExplicitlyInsteadOfWaitingOnItsOwnGate()
        {
            using FileSkillStore reader = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);
            using FileSkillStore writer = new(_root, host: PassThroughLlmAsyncMarshaler.Instance,
                confirmDurabilityAsync: async _ => { await reader.ListAsync(); return true; });
            Task write = Save(writer, Record());
            Assert.AreSame(write, await Task.WhenAny(write, Task.Delay(5000)), "Reentrant confirmation must not deadlock.");
            Assert.ThrowsAsync<SkillStoreDurabilityException>(async () => await write);
        }

        [Test]
        public async Task SynchronousMigrationCannotReturnARecordBeforeItsRequiredAsyncConfirmation()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "Skill.json"), "{\"Id\":\"Skill\",\"Instructions\":\"legacy\",\"Version\":0}");
            bool confirmed = false;
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance,
                confirmDurabilityAsync: _ => Task.FromResult(confirmed));
            Assert.Throws<InvalidOperationException>(() => store.TryLoad("Skill", out _));
            Assert.ThrowsAsync<SkillStoreDurabilityException>(async () => await store.LoadAsync("Skill"));
            confirmed = true;
            Assert.AreEqual("legacy", (await store.LoadAsync("Skill")).Instructions);
        }

        // WHY these three: the browser build poisoned itself here. QueueDurability used to park a
        // pending durability confirmation UNCONDITIONALLY under "#if UNITY_WEBGL && !UNITY_EDITOR",
        // but only the async entry points can clear one, and the live callers of the synchronous API
        // are synchronous themselves (SkillAuthoringCoordinator, AgentBuilder). The first skill
        // written in the player succeeded and every later synchronous call threw "Skill durability is
        // unconfirmed"; one skill file left under a legacy name turned even a plain read into that
        // exception, because the read migrates and the migration parked the mark. The editor saw none
        // of it - the "#else" branch already carried the condition. The two behaviour tests pin the
        // contract for every platform, and the third pins that the branch which broke it stays gone.
        // The same correction lives in FileLuaScriptVersionStore.Mutate.
        [Test]
        public void SyncWrites_WithEngineOwnedDurability_LeaveTheSyncApiUsable()
        {
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);

            store.Save(Record("first"));
            Assert.DoesNotThrow(() => store.Save(Record("second")),
                "A second synchronous write must not be blocked by the first one's durability.");
            Assert.DoesNotThrow(() => store.List());
            Assert.IsTrue(store.TryLoad("Skill", out SkillRecord loaded));
            Assert.AreEqual("second", loaded.Instructions);
            Assert.DoesNotThrow(() => store.Delete("Skill"));
            Assert.IsEmpty(store.List());
        }

        [Test]
        public void SyncLoad_OfALegacyFileName_MigratesAndLeavesTheStoreUsable()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "Skill.json"),
                "{\"Id\":\"Skill\",\"Instructions\":\"legacy\",\"Version\":0}");
            using FileSkillStore store = new(_root, host: PassThroughLlmAsyncMarshaler.Instance);

            Assert.IsTrue(store.TryLoad("skill", out SkillRecord loaded),
                "A read must return the record it just migrated, not throw over its own durability.");
            Assert.AreEqual("legacy", loaded.Instructions);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "Skill.json")),
                "The synchronous read migrates the legacy path.");
            Assert.DoesNotThrow(() => store.List(),
                "A migration the engine already made durable must not block every later call.");
        }

        [Test]
        public void DurabilityMark_IsNeverParkedFromAWebGlOnlyBranch_InEitherFileBackedStore()
        {
            string assets = UnityEngine.Application.dataPath;
            string[] guarded =
            {
                Path.Combine(assets, "CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FileSkillStore.cs"),
                Path.Combine(assets, "CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileLuaScriptVersionStore.cs")
            };
            foreach (string path in guarded)
            {
                Assert.IsTrue(File.Exists(path), $"Guarded source not found: {path}");
                string name = Path.GetFileName(path);
                bool insideWebGlOnly = false;
                bool queuesForHook = false;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("#if", StringComparison.Ordinal))
                    {
                        insideWebGlOnly = line.Contains("UNITY_WEBGL") && line.Contains("!UNITY_EDITOR");
                        continue;
                    }

                    if (line.StartsWith("#else", StringComparison.Ordinal) ||
                        line.StartsWith("#endif", StringComparison.Ordinal))
                    {
                        insideWebGlOnly = false;
                        continue;
                    }

                    if (line.StartsWith("//", StringComparison.Ordinal) || !line.Contains("RecordPending("))
                    {
                        continue;
                    }

                    Assert.IsFalse(insideWebGlOnly,
                        $"{name}: a pending durability confirmation must never be parked from a WebGL-only " +
                        "branch. Only the async entry points can clear one, the live synchronous callers " +
                        "cannot, and the editor never runs that branch - so the poisoned store shipped.");
                    queuesForHook |= line.Contains("_customConfirmation");
                }

                Assert.IsTrue(queuesForHook,
                    $"{name}: the synchronous path must still queue a confirmation for a CALLER-SUPPLIED hook.");
            }
        }
    }
}
