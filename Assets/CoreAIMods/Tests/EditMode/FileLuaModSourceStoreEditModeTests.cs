using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using CoreAI.Mods.Rbx.Instances;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// File-backed <see cref="ILuaModSourceStore"/> tests mirroring
    /// <see cref="FileLuaModStoreEditModeTests"/>: a temp directory per test, a Save -> TryLoad
    /// round-trip of source plus manifest, List of stored packages, SetActive rewriting the manifest
    /// without losing source, and Delete removing the package.
    /// </summary>
    public sealed class FileLuaModSourceStoreEditModeTests
    {
        private string _root;
        private FileLuaModSourceStore _store;
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: three synchronous [Test]s in this fixture (the ExactReplacement_Sync* cases) block on
        /// Assert.ThrowsAsync/CatchAsync; detaching the context sends the awaited delegate's continuation
        /// to the thread pool instead of back onto this same blocked thread, which would deadlock.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _root = Path.Combine(Path.GetTempPath(), "CoreAITestLuaModSourceStore_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _store = new FileLuaModSourceStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            FileLuaModSourceStore.PersistenceArmingForTesting = null;
            FileLuaModSourceStore.FailFirstCanonicalInstallationForTesting = null;
            _store = null;
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);

            try
            {
                if (!string.IsNullOrEmpty(_root) && Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        private static LuaModManifest Manifest(string id, bool active = true)
        {
            return new LuaModManifest
            {
                Id = id,
                Name = id,
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = active
            };
        }

        private sealed class PersistenceLog : CoreAI.Logging.ILog
        {
            public readonly List<string> Errors = new();
            public void Debug(string message, string tag = null) { }
            public void Info(string message, string tag = null) { }
            public void Warn(string message, string tag = null) { }
            public void Error(string message, string tag = null) => Errors.Add(message);
        }

        [Test]
        public void FirstCanonicalSave_TransientManifestFailureDoesNotPoisonItsRetry()
        {
            int attempts = 0;
            FileLuaModSourceStore.FailFirstCanonicalInstallationForTesting = () => ++attempts == 1;
            LuaModSourcePersistenceException failed = Assert.Throws<LuaModSourcePersistenceException>(() =>
                _store.SaveStrict("first", "return 'retry'", Manifest("first")));

            Assert.IsFalse(failed.LocalCommitCompleted);
            Assert.IsEmpty(Directory.GetDirectories(_root), "No failed canonical installation or staging folder may remain.");
            Assert.IsFalse(_store.TryLoad("first", out _, out _));

            _store.SaveStrict("first", "return 'retry'", Manifest("first"));

            Assert.AreEqual(2, attempts);
            using FileLuaModSourceStore reopened = new(_root);
            Assert.IsTrue(reopened.TryLoad("first", out string source, out LuaModManifest manifest));
            Assert.AreEqual("return 'retry'", source);
            Assert.AreEqual("first", manifest.Id);
            Assert.AreEqual(1, Directory.GetDirectories(_root).Length);
        }

        [Test]
        public void FirstCanonicalSave_PreservesExistingCorruptIdentityEvidence()
        {
            _store.SaveStrict("existing", "return 'kept'", Manifest("existing"));
            string directory = Directory.GetDirectories(_root)[0];
            string manifestPath = Path.Combine(directory, "manifest.json");
            File.WriteAllText(manifestPath, "{ invalid identity");

            LuaModSourcePersistenceException refused = Assert.Throws<LuaModSourcePersistenceException>(() =>
                _store.SaveStrict("existing", "return 'replacement'", Manifest("existing")));

            Assert.IsFalse(refused.LocalCommitCompleted);
            Assert.AreEqual("{ invalid identity", File.ReadAllText(manifestPath));
            Assert.AreEqual("return 'kept'", File.ReadAllText(Path.Combine(directory, "main.lua")));
        }

        [Test]
        public void MOD002_StrictSourceSaveReportsArmingFailureAndAnIdenticalRetryRearms()
        {
            FileLuaModSourceStore.PersistenceArmingForTesting = () => false;
            ILuaModSourceStrictWriter strict = _store;
            LuaModSourcePersistenceException error = Assert.Throws<LuaModSourcePersistenceException>(() =>
                strict.SaveStrict("mod", "return 1", Manifest("mod")));
            Assert.IsTrue(error.LocalCommitCompleted);
            Assert.IsTrue(_store.TryLoad("mod", out string local, out _));
            Assert.AreEqual("return 1", local);
            int confirmations = 0;
            FileLuaModSourceStore.PersistenceArmingForTesting = () => { confirmations++; return true; };
            strict.SaveStrict("mod", "return 1", Manifest("mod"));
            Assert.AreEqual(1, confirmations);
            using FileLuaModSourceStore reopened = new(_root);
            Assert.IsTrue(reopened.TryLoad("mod", out string persisted, out _));
            Assert.AreEqual("return 1", persisted);
        }

        [Test]
        public void MOD002_StrictSourceSaveReportsIncompleteLocalWriteAndRemainsRetryable()
        {
            ILuaModSourceStrictWriter strict = _store;
            strict.SaveStrict("mod", "return 1", Manifest("mod"));
            string sourcePath = Directory.GetFiles(_root, "main.lua", SearchOption.AllDirectories)[0];
            Directory.CreateDirectory(sourcePath + ".tmp");
            LuaModSourcePersistenceException error = Assert.Throws<LuaModSourcePersistenceException>(() =>
                strict.SaveStrict("mod", "return 2", Manifest("mod")));
            Assert.IsFalse(error.LocalCommitCompleted);
            Assert.IsTrue(_store.TryLoad("mod", out string previous, out _));
            Assert.AreEqual("return 1", previous);
            Directory.Delete(sourcePath + ".tmp");
            strict.SaveStrict("mod", "return 2", Manifest("mod"));
            Assert.IsTrue(_store.TryLoad("mod", out string retried, out _));
            Assert.AreEqual("return 2", retried);
        }

        [Test]
        public void HUB003_StrictSourceRemovalReportsArmingFailureAndAbsentRetryRearms()
        {
            _store.SaveStrict("mod", "return 1", Manifest("mod"));
            FileLuaModSourceStore.PersistenceArmingForTesting = () => false;
            ILuaModSourceStrictRemover strict = _store;
            LuaModSourcePersistenceException failure = Assert.Throws<LuaModSourcePersistenceException>(() =>
                strict.DeleteStrict("mod"));
            Assert.IsTrue(failure.LocalCommitCompleted);
            Assert.IsFalse(_store.TryLoad("mod", out _, out _));
            int armingRequests = 0;
            FileLuaModSourceStore.PersistenceArmingForTesting = () => { armingRequests++; return true; };

            strict.DeleteStrict("mod");

            Assert.AreEqual(1, armingRequests, "An already absent local package still needs the refused arming retried.");
            using FileLuaModSourceStore reopened = new(_root);
            Assert.IsFalse(reopened.TryLoad("mod", out _, out _));
        }

        [Test]
        public void HUB003_StrictSourceRemovalPreservesUnreadableIdentityEvidenceUntilRecovery()
        {
            _store.SaveStrict("mod", "return 1", Manifest("mod"));
            string manifestPath = Directory.GetFiles(_root, "manifest.json", SearchOption.AllDirectories)[0];
            string manifest = File.ReadAllText(manifestPath);
            File.WriteAllText(manifestPath, "{");
            LuaModSourcePersistenceException failure = Assert.Throws<LuaModSourcePersistenceException>(() =>
                _store.DeleteStrict("mod"));
            Assert.IsFalse(failure.LocalCommitCompleted);
            Assert.AreEqual("{", File.ReadAllText(manifestPath));
            File.WriteAllText(manifestPath, manifest);

            _store.DeleteStrict("mod");

            Assert.IsFalse(_store.TryLoad("mod", out _, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HUB003_StrictSourceMutationCancellationPropagatesAndRetryRearms(bool remove)
        {
            _store.SaveStrict("mod", "return 1", Manifest("mod"));
            FileLuaModSourceStore.PersistenceArmingForTesting = () => throw new OperationCanceledException();
            Assert.Throws<OperationCanceledException>(() =>
            {
                if (remove) { _store.DeleteStrict("mod"); }
                else { _store.SaveStrict("mod", "return 2", Manifest("mod")); }
            });
            int armingRequests = 0;
            FileLuaModSourceStore.PersistenceArmingForTesting = () => { armingRequests++; return true; };

            if (remove) { _store.DeleteStrict("mod"); }
            else { _store.SaveStrict("mod", "return 2", Manifest("mod")); }

            Assert.AreEqual(1, armingRequests);
            Assert.AreEqual(!remove, _store.TryLoad("mod", out _, out _));
        }

        [Test]
        public void MOD002_BestEffortSourceMutationsLogRefusedArmingAfterLocalCommit()
        {
            PersistenceLog log = new();
            _store.Dispose();
            _store = new FileLuaModSourceStore(_root, log);
            FileLuaModSourceStore.PersistenceArmingForTesting = () => false;
            _store.Save("mod", "return 1", Manifest("mod"));
            _store.SetActive("mod", false);
            _store.Delete("mod");
            Assert.AreEqual(3, log.Errors.Count);
            foreach (string message in log.Errors) { StringAssert.Contains("committed locally", message); }
            Assert.IsFalse(_store.TryLoad("mod", out _, out _));
        }

        [Test]
        public void MOD008_OrdinarySourceStoreIsolatesLegacySanitizerAndCaseCollisions()
        {
            string[] ids = { "a/b", "a_b_3a8e75c1", "Case", "case" };
            for (int i = 0; i < ids.Length; i++) { _store.Save(ids[i], "return " + i, Manifest(ids[i])); }
            _store = new FileLuaModSourceStore(_root);
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.IsTrue(_store.TryLoad(ids[i], out string source, out LuaModManifest manifest));
                Assert.AreEqual(ids[i], manifest.Id);
                Assert.AreEqual("return " + i, source);
            }

            _store.SetActive(ids[0], false);
            Assert.IsTrue(_store.TryLoad(ids[1], out _, out LuaModManifest other));
            Assert.IsTrue(other.Active);
            _store.Delete(ids[1]);
            Assert.IsTrue(_store.TryLoad(ids[0], out _, out LuaModManifest retained));
            Assert.IsFalse(retained.Active);
            Assert.IsFalse(_store.TryLoad(ids[1], out _, out _));
            Assert.AreEqual(3, _store.List().Count);
        }

        [Test]
        public void MOD008_LegacySourceMigrationRequiresExactManifestIdentity()
        {
            string legacy = Path.Combine(_root, "a_b_3a8e75c1");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "manifest.json"), "{\"Id\":\"a/b\",\"Active\":true}");
            File.WriteAllText(Path.Combine(legacy, "main.lua"), "return 'legacy'");
            Assert.IsFalse(_store.TryLoad("a_b_3a8e75c1", out _, out _));
            _store.Delete("a_b_3a8e75c1");
            Assert.IsTrue(Directory.Exists(legacy));
            Assert.IsTrue(_store.TryLoad("a/b", out string source, out LuaModManifest manifest));
            Assert.AreEqual("return 'legacy'", source);
            Assert.AreEqual("a/b", manifest.Id);
            Assert.IsFalse(Directory.Exists(legacy));
            _store.Save("a_b_3a8e75c1", "return 'separate'", Manifest("a_b_3a8e75c1"));
            Assert.IsTrue(_store.TryLoad("a/b", out string original, out _));
            Assert.AreEqual(source, original);
        }

        [Test]
        public void MOD008_StoreNamespacesAreCaseSafeAndLegacyMigrationNeverMerges()
        {
            string[] ids = { "a/b", "a_b_3a8e75c1", "Case", "case" };
            for (int i = 0; i < ids.Length; i++)
            {
                using FileLuaModSourceStore store = new(_root, storeId: ids[i]);
                store.Save("same", "return " + i, Manifest("same"));
            }
            for (int i = 0; i < ids.Length; i++)
            {
                using FileLuaModSourceStore store = new(_root, storeId: ids[i]);
                Assert.IsTrue(store.TryLoad("same", out string source, out _));
                Assert.AreEqual("return " + i, source);
            }

            string legacy = Path.Combine(_root, "Stores", "legacy");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "evidence.txt"), "legacy namespace");
            Assert.IsFalse(LuaModStoreId.TryMigrateLegacy(_root, "legacy", (id, path) => false));
            Assert.IsTrue(Directory.Exists(legacy));
            Assert.IsTrue(LuaModStoreId.TryMigrateLegacy(_root, "legacy", (id, path) => id == "legacy"));
            string destination = LuaModStoreId.ApplyTo(_root, "legacy");
            Assert.AreEqual("legacy namespace", File.ReadAllText(Path.Combine(destination, "evidence.txt")));
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "evidence.txt"), "do not overwrite");
            Assert.IsFalse(LuaModStoreId.TryMigrateLegacy(_root, "legacy", (id, path) => true));
            Assert.AreEqual("legacy namespace", File.ReadAllText(Path.Combine(destination, "evidence.txt")));
            Assert.AreEqual("do not overwrite", File.ReadAllText(Path.Combine(legacy, "evidence.txt")));
        }

        [Test]
        public void FileLuaModSourceStore_SaveTryLoad_RoundTripsSourceAndManifest()
        {
            _store.Save("mod", "local x = 1", Manifest("mod"));

            Assert.IsTrue(_store.TryLoad("mod", out string source, out LuaModManifest manifest));
            Assert.AreEqual("local x = 1", source);
            Assert.IsNotNull(manifest);
            Assert.AreEqual("mod", manifest.Id);
            Assert.AreEqual(LuaCapabilities.Read.ToString(), manifest.Capabilities);
            Assert.IsTrue(manifest.Active);
        }

        [Test]
        public void FileLuaModSourceStore_TryLoad_MissingIdReturnsFalse()
        {
            Assert.IsFalse(_store.TryLoad("missing", out string source, out LuaModManifest manifest));
            Assert.AreEqual("", source);
            Assert.IsNull(manifest);
        }

        [Test]
        public void FileLuaModSourceStore_List_ReturnsEveryStoredManifest()
        {
            _store.Save("a", "local a = 1", Manifest("a"));
            _store.Save("b", "local b = 1", Manifest("b", false));

            System.Collections.Generic.IReadOnlyList<LuaModManifest> manifests = _store.List();

            Assert.AreEqual(2, manifests.Count);
            bool sawA = false;
            bool sawB = false;
            foreach (LuaModManifest manifest in manifests)
            {
                if (manifest.Id == "a")
                {
                    sawA = true;
                }
                else if (manifest.Id == "b")
                {
                    sawB = true;
                }
            }

            Assert.IsTrue(sawA && sawB, "List must return manifests for both active and dormant packages.");
        }

        [Test]
        public void FileLuaModSourceStore_SetActive_RewritesManifestKeepingSource()
        {
            _store.Save("mod", "local x = 1", Manifest("mod"));

            _store.SetActive("mod", false);

            Assert.IsTrue(_store.TryLoad("mod", out string source, out LuaModManifest manifest));
            Assert.IsFalse(manifest.Active, "SetActive must flip the persisted Active flag.");
            Assert.AreEqual("local x = 1", source, "SetActive must not touch the persisted source.");
        }

        [Test]
        public void FileLuaModSourceStore_Delete_RemovesPackage()
        {
            _store.Save("mod", "local x = 1", Manifest("mod"));
            Assert.IsTrue(_store.TryLoad("mod", out _, out _));

            _store.Delete("mod");

            Assert.IsFalse(_store.TryLoad("mod", out _, out _));
            Assert.AreEqual(0, _store.List().Count);
        }

        [Test]
        public void FileLuaModSourceStore_SecondInstance_ReadsPersistedPackage()
        {
            _store.Save("mod", "local x = 1", Manifest("mod"));
            _store = new FileLuaModSourceStore(_root);

            Assert.IsTrue(_store.TryLoad("mod", out string source, out LuaModManifest manifest));
            Assert.AreEqual("local x = 1", source);
            Assert.AreEqual("mod", manifest.Id);
        }

        [Test]
        public void FileLuaModSourceStore_DifferentStoreIds_IsolateSameModId()
        {
            FileLuaModSourceStore storeA = new(_root, storeId: "demo-a");
            FileLuaModSourceStore storeB = new(_root, storeId: "demo-b");

            storeA.Save("mod", "local a = 1", Manifest("mod"));

            Assert.IsFalse(storeB.TryLoad("mod", out _, out _),
                "A package saved under one store id must be invisible to another store id.");
            Assert.AreEqual(0, storeB.List().Count,
                "List under an unused store id must not surface another id's packages.");
            Assert.IsTrue(storeA.TryLoad("mod", out string source, out _));
            Assert.AreEqual("local a = 1", source);

            Assert.IsTrue(Directory.Exists(LuaModStoreId.ApplyTo(_root, "demo-a")),
                "Each store id must persist its packages in its own subdirectory.");
            Assert.IsFalse(Directory.Exists(LuaModStoreId.ApplyTo(_root, "demo-b")));
        }

        [Test]
        public void FileLuaModSourceStore_EmptyStoreId_KeepsSharedRootPath()
        {
            FileLuaModSourceStore defaulted = new(_root, storeId: null);

            defaulted.Save("mod", "local x = 1", Manifest("mod"));

            Assert.IsTrue(_store.TryLoad("mod", out string source, out _),
                "An id-less store must read what another id-less store on the same root wrote.");
            Assert.AreEqual("local x = 1", source);
            Assert.AreEqual(1, Directory.GetDirectories(_root).Length,
                "An empty store id keeps packages directly under the shared root.");
        }

        [Test]
        public void ExactReplacement_SyncFailureLeavesDefaultSourceSetUnchanged()
        {
            _store.Save("old", "return 'old'", Manifest("old"));
            FileLuaModSourceStore failing = new(
                _root,
                persistenceSyncAsync: _ => UniTask.FromResult(false));
            RbxWorldModSource replacement = new(
                Manifest("new"),
                "return 'new'");

            IOException error = Assert.ThrowsAsync<IOException>(async () =>
                await failing.PrepareExactReplacementAsync(new[] { replacement }));

            StringAssert.Contains("durable persistence was not confirmed", error.Message);
            Assert.IsTrue(_store.TryLoad("old", out string oldSource, out _));
            Assert.AreEqual("return 'old'", oldSource);
            Assert.IsFalse(_store.TryLoad("new", out _, out _));
            Assert.AreEqual(1, _store.List().Count);
            _store.Save("after-failure", "return 'usable'", Manifest("after-failure"));
            Assert.IsTrue(_store.TryLoad("after-failure", out string usableSource, out _));
            Assert.AreEqual("return 'usable'", usableSource);
        }

        [Test]
        public void ExactReplacement_SyncExceptionUnpoisonsRootAndPreservesDefaultPair()
        {
            _store.Save("old", "return 'old'", Manifest("old"));
            int syncCalls = 0;
            FileLuaModSourceStore failing = new(
                _root,
                persistenceSyncAsync: _ =>
                {
                    syncCalls++;
                    if (syncCalls == 1)
                    {
                        throw new InvalidOperationException("injected sync exception");
                    }

                    return UniTask.FromResult(true);
                });

            IOException error = Assert.ThrowsAsync<IOException>(async () =>
                await failing.PrepareExactReplacementAsync(new[]
                {
                    new RbxWorldModSource(Manifest("new"), "return 'new'")
                }));

            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
            Assert.IsTrue(_store.TryLoad("old", out string oldSource, out _));
            Assert.AreEqual("return 'old'", oldSource);
            _store.Save("after-exception", "return 'usable'", Manifest("after-exception"));
            Assert.IsTrue(_store.TryLoad("after-exception", out _, out _));
        }

        [Test]
        public void ExactReplacement_CancellationDuringSyncUnpoisonsRootAndPreservesDefaultPair()
        {
            _store.Save("old", "return 'old'", Manifest("old"));
            CancellationTokenSource cancellation = new();
            FileLuaModSourceStore failing = new(
                _root,
                persistenceSyncAsync: token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return UniTask.FromResult(true);
                });

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await failing.PrepareExactReplacementAsync(
                    new[]
                    {
                        new RbxWorldModSource(Manifest("new"), "return 'new'")
                    },
                    cancellation.Token));

            Assert.IsTrue(_store.TryLoad("old", out string oldSource, out _));
            Assert.AreEqual("return 'old'", oldSource);
            _store.Save("after-cancel", "return 'usable'", Manifest("after-cancel"));
            Assert.IsTrue(_store.TryLoad("after-cancel", out _, out _));
            cancellation.Dispose();
        }

        [Test]
        public async Task ExactReplacement_CrossInstanceMutationFailsClosedDuringDurableSync()
        {
            TaskCompletionSource<bool> persistence = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            FileLuaModSourceStore preparing = new(
                _root,
                persistenceSyncAsync: async _ => await persistence.Task);
            FileLuaModSourceStore racing = new(_root);
            RbxWorldModSource replacement = new(
                Manifest("new"),
                "return 'new'");

            Task<IRbxWorldModSourceReplacement> preparation = preparing
                .PrepareExactReplacementAsync(new[] { replacement })
                .AsTask();

            InvalidOperationException saveError = Assert.Throws<InvalidOperationException>(() =>
                racing.Save("racer", "return 'racer'", Manifest("racer")));
            StringAssert.Contains("durable exact-source preparation is pending", saveError.Message);
            InvalidOperationException readError = Assert.Throws<InvalidOperationException>(() =>
                racing.TryLoad("new", out _, out _));
            StringAssert.Contains("durable exact-source preparation is pending", readError.Message);
            persistence.SetResult(true);
            IRbxWorldModSourceReplacement prepared = await preparation;
            await prepared.RollbackAsync(CancellationToken.None);
            prepared.Dispose();
        }

        [Test]
        public void ExactReplacement_CaseAndLegacySanitizerCollisionsRoundTripExactly()
        {
            _store.Save("old", "return 'old'", Manifest("old", active: false));
            FileLuaModSourceStore preparing = new(
                _root,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
            RbxWorldModSource[] replacements =
            {
                new(Manifest("a/b"), "return 'slash'"),
                new(Manifest("a_b_3a8e75c1", active: false), "return 'literal'"),
                new(Manifest("Case"), "return 'upper'"),
                new(Manifest("case", active: false), "return 'lower'")
            };

            IRbxWorldModSourceReplacement prepared = preparing
                .PrepareExactReplacementAsync(replacements)
                .GetAwaiter().GetResult();

            Assert.AreEqual(4, prepared.SourceStore.List().Count);
            for (int index = 0; index < replacements.Length; index++)
            {
                RbxWorldModSource expected = replacements[index];
                Assert.IsTrue(prepared.SourceStore.TryLoad(
                    expected.Manifest.Id,
                    out string source,
                    out LuaModManifest manifest));
                Assert.AreEqual(expected.Source, source);
                Assert.AreEqual(expected.Manifest.Id, manifest.Id);
                Assert.AreEqual(expected.Manifest.Active, manifest.Active);
            }

            prepared.Activate();
            prepared.RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
            prepared.Dispose();
            Assert.AreEqual(1, _store.List().Count);
            Assert.IsTrue(_store.TryLoad("old", out string oldSource, out LuaModManifest oldManifest));
            Assert.AreEqual("return 'old'", oldSource);
            Assert.IsFalse(oldManifest.Active);
            Assert.IsFalse(_store.TryLoad("a/b", out _, out _));
            Assert.IsFalse(_store.TryLoad("Case", out _, out _));
        }

        [Test]
        public void ExactReplacement_CrashBeforeWorldSelectionRestartsDefaultPair()
        {
            _store.Save("old", "return 'old'", Manifest("old"));
            FileLuaModSourceStore preparing = new(
                _root,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
            RbxWorldModSource replacement = new(
                Manifest("new"),
                "return 'new'");
            IRbxWorldModSourceReplacement prepared = preparing
                .PrepareExactReplacementAsync(new[] { replacement })
                .GetAwaiter().GetResult();

            Assert.IsTrue(prepared.SourceStore.TryLoad(
                "new",
                out string preparedSource,
                out _));
            Assert.AreEqual("return 'new'", preparedSource);
            prepared.Activate();
            prepared.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
            prepared.Dispose();

            FileLuaModSourceStore restartedDefault = new(_root);
            Assert.IsTrue(restartedDefault.TryLoad("old", out string oldSource, out _));
            Assert.AreEqual("return 'old'", oldSource);
            Assert.IsFalse(restartedDefault.TryLoad("new", out _, out _));
        }

        [Test]
        public void ExactReplacement_CleanupBoundsVersionsAndKeepsCurrentSourceFacade()
        {
            FileLuaModSourceStore preparing = new(
                _root,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
            IRbxWorldModSourceReplacement current = null;
            for (int index = 0; index < 7; index++)
            {
                string id = "version-" + index;
                current = preparing.PrepareExactReplacementAsync(new[]
                    {
                        new RbxWorldModSource(Manifest(id), "return '" + id + "'")
                    })
                    .GetAwaiter().GetResult();
                current.Activate();
                current.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
                current.Dispose();
            }

            string sessionsRoot = Path.Combine(_root, ".world-sessions");
            Assert.LessOrEqual(Directory.GetDirectories(sessionsRoot).Length, 3);
            Assert.IsTrue(current.SourceStore.TryLoad(
                "version-6",
                out string source,
                out LuaModManifest manifest));
            Assert.AreEqual("return 'version-6'", source);
            Assert.AreEqual("version-6", manifest.Id);
            FileLuaModSourceStore restartedDefault = new(_root);
            Assert.AreEqual(0, restartedDefault.List().Count);
        }

        [Test]
        public void ExactReplacement_RestartModelKeepsSelectedOldWorldAndDefaultOldSources()
        {
            _store.Save("old-source", "return 'old'", Manifest("old-source"));
            InstanceRegistry oldRegistry = new(worldId: "old-world");
            RbxDataModel oldGame = DataModelBootstrap.CreateGame(oldRegistry);
            RbxInstance marker = oldRegistry.Create("Folder");
            marker.Name = "OldWorldMarker";
            marker.Parent = oldRegistry.WorldRoot;
            RbxWorldPackagePayload selectedOldPackage = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    oldRegistry,
                    oldGame,
                    null,
                    new RbxWorldSettings
                    {
                        WorldId = "old-world"
                    },
                    modSourceStore: _store));
            FileLuaModSourceStore preparing = new(
                _root,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
            IRbxWorldModSourceReplacement unselected = preparing
                .PrepareExactReplacementAsync(new[]
                {
                    new RbxWorldModSource(
                        Manifest("new-source"),
                        "return 'new'")
                })
                .GetAwaiter().GetResult();
            unselected.Activate();
            unselected.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
            unselected.Dispose();

            FileLuaModSourceStore restartedSources = new(_root);
            RbxWorldPackageRestoreResult restartedWorld =
                RbxWorldPackageSerializer.RestoreFresh(selectedOldPackage);

            Assert.AreEqual("old-world", restartedWorld.Registry.WorldId);
            Assert.IsNotNull(
                restartedWorld.Registry.WorldRoot.FindFirstChild("OldWorldMarker"));
            Assert.IsTrue(restartedSources.TryLoad(
                "old-source",
                out string oldSource,
                out _));
            Assert.AreEqual("return 'old'", oldSource);
            Assert.IsFalse(restartedSources.TryLoad("new-source", out _, out _));
            Assert.AreEqual("old-source", selectedOldPackage.Mods[0].Manifest.Id);
        }

        /// <summary>
        /// A1-01: a source beyond the world-package mod limit is never created. The store used to write
        /// a 257th distinct id, after which every capture of the world threw, so no autosave, save or
        /// gated mutation could run until a mod was forgotten; the store kept it across restarts.
        /// B2-07: the refusal is thrown to the caller, which used to go on as if the source were kept.
        /// </summary>
        [Test]
        public void Save_DistinctIdBeyondTheWorldPackageModLimit_IsRefused_ExistingIdsStillUpdate()
        {
            for (int index = 0; index < RbxWorldPackageSerializer.MaximumMods; index++)
            {
                string id = "mod" + index.ToString("D3");
                _store.Save(id, "return " + index, Manifest(id, active: false));
            }

            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, _store.List().Count, "precondition: the store is full");

            RbxWorldPackageFormatLimitException refused = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => _store.Save("one-too-many", "return 'refused'", Manifest("one-too-many")),
                "the caller hears that the source was not kept");
            StringAssert.Contains("'one-too-many'", refused.Message);
            StringAssert.Contains("'forget'", refused.Message, "the refusal names the way out");

            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, _store.List().Count);
            Assert.IsFalse(_store.TryLoad("one-too-many", out _, out _), "the 257th distinct source must not be created");
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "one-too-many")));

            _store.Save("mod000", "return 'updated'", Manifest("mod000"));
            Assert.IsTrue(_store.TryLoad("mod000", out string updated, out LuaModManifest manifest));
            Assert.AreEqual("return 'updated'", updated, "an id the store already holds is updated, never refused");
            Assert.IsTrue(manifest.Active);

            _store.Delete("mod001");
            _store.Save("one-too-many", "return 'accepted'", Manifest("one-too-many"));
            Assert.IsTrue(_store.TryLoad("one-too-many", out string accepted, out _), "forgetting one mod makes room");
            Assert.AreEqual("return 'accepted'", accepted);
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, _store.List().Count);

            RbxWorldPackagePayload captured = CaptureWith(_store);
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, captured.Mods.Count, "the full store stays capturable");
        }

        /// <summary>
        /// B2-07: the session admitted a new mod by counting the manifests <see cref="FileLuaModSourceStore.List"/>
        /// could read, while <see cref="FileLuaModSourceStore.Save"/> counted every folder holding a manifest
        /// file, so one unreadable manifest let a mod run whose source the store then refused. The store's
        /// admission answers exactly what its save does, with the same refusal.
        /// </summary>
        [Test]
        public void Admission_CountsAFolderWhoseManifestCannotBeRead_AndAgreesWithSave()
        {
            for (int index = 0; index < RbxWorldPackageSerializer.MaximumMods - 1; index++)
            {
                string id = "mod" + index.ToString("D3");
                _store.Save(id, "return " + index, Manifest(id, active: false));
            }

            Directory.CreateDirectory(Path.Combine(_root, "broken"));
            File.WriteAllText(Path.Combine(_root, "broken", "manifest.json"), "{ not json");
            File.WriteAllText(Path.Combine(_root, "broken", "main.lua"), "return 0");
            ILuaModSourceAdmission admission = _store;

            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods - 1, _store.List().Count,
                "precondition: the unreadable manifest is not listed");
            Assert.IsFalse(admission.CanAdmit("newcomer", out string refusal), "the unreadable folder still counts");
            RbxWorldPackageFormatLimitException refused = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => _store.Save("newcomer", "return 'x'", Manifest("newcomer")));
            Assert.AreEqual(refusal, refused.Message, "one rule, one refusal");
            Assert.IsTrue(admission.CanAdmit("mod000", out string existing), "a stored id is always kept");
            Assert.AreEqual("", existing);

            LuaModSourcePersistenceException deletion = Assert.Throws<LuaModSourcePersistenceException>(() =>
                _store.DeleteStrict("broken"));
            Assert.IsFalse(deletion.LocalCommitCompleted);
            string corruptDirectory = Path.Combine(_root, "broken");
            Assert.AreEqual("{ not json", File.ReadAllText(Path.Combine(corruptDirectory, "manifest.json")));
            Assert.AreEqual("return 0", File.ReadAllText(Path.Combine(corruptDirectory, "main.lua")));
            Assert.IsFalse(admission.CanAdmit("newcomer", out _), "Unreadable identity evidence still occupies admission.");
            Directory.Delete(corruptDirectory, true);
            Assert.IsTrue(admission.CanAdmit("newcomer", out _), "a freed folder makes room");
            _store.Save("newcomer", "return 'x'", Manifest("newcomer"));
            Assert.IsTrue(_store.TryLoad("newcomer", out _, out _));
        }

        /// <summary>A1-01 twin: an exact world source set larger than one package can hold is refused before any byte is written.</summary>
        [Test]
        public void ExactReplacement_SourceSetBeyondTheWorldPackageModLimit_IsRefusedBeforeWriting()
        {
            RbxWorldModSource[] mods = new RbxWorldModSource[RbxWorldPackageSerializer.MaximumMods + 1];
            for (int index = 0; index < mods.Length; index++)
            {
                string id = "mod" + index.ToString("D3");
                mods[index] = new RbxWorldModSource(Manifest(id), "return " + index);
            }

            FileLuaModSourceStore preparing = new(_root, persistenceSyncAsync: _ => UniTask.FromResult(true));

            RbxWorldPackageFormatLimitException refused = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => preparing.PrepareExactReplacementAsync(mods).GetAwaiter().GetResult());

            StringAssert.Contains(RbxWorldPackageSerializer.MaximumMods.ToString(), refused.Message);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".world-sessions")), "nothing was written");
        }

        private static RbxWorldPackagePayload CaptureWith(ILuaModSourceStore sources)
        {
            InstanceRegistry registry = new(worldId: "limit-world");
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            try
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    null,
                    new RbxWorldSettings { WorldId = "limit-world" },
                    modSourceStore: sources));
            }
            finally
            {
                game.Destroy();
            }
        }
    }
}
