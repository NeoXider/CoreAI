using System;
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

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "CoreAITestLuaModSourceStore_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _store = new FileLuaModSourceStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            _store = null;

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

            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "Stores", "demo-a")),
                "Each store id must persist its packages in its own subdirectory.");
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Stores", "demo-b", "mod")));
        }

        [Test]
        public void FileLuaModSourceStore_EmptyStoreId_KeepsSharedRootPath()
        {
            FileLuaModSourceStore defaulted = new(_root, storeId: null);

            defaulted.Save("mod", "local x = 1", Manifest("mod"));

            Assert.IsTrue(_store.TryLoad("mod", out string source, out _),
                "An id-less store must read what another id-less store on the same root wrote.");
            Assert.AreEqual("local x = 1", source);
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "mod")),
                "An empty store id must keep today's shared root layout unchanged.");
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
    }
}
