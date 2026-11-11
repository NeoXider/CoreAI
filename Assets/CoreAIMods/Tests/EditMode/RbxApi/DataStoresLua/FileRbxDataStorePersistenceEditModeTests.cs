using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.DataStores;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.DataStoresLua
{
    /// <summary>
    /// The game's DataStore files: a restart reads exactly what was written (any UTF-16 text included), the namespace
    /// folder follows the one storage naming rule, an unreadable file is set aside and reported without taking the
    /// game down, a page that did not arm WebGL persistence is a failed write, and every path fits a storage root of
    /// 121 characters.
    /// </summary>
    [TestFixture]
    public sealed class FileRbxDataStorePersistenceEditModeTests
    {
        private readonly List<string> _directories = new();
        private Func<bool> _previousArming;

        [SetUp]
        public void ArmPersistence()
        {
            _previousArming = FileRbxDataStorePersistence.PersistenceArmingForTesting;
            FileRbxDataStorePersistence.PersistenceArmingForTesting = () => true;
        }

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            FileRbxDataStorePersistence.PersistenceArmingForTesting = _previousArming;
            foreach (string directory in _directories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            _directories.Clear();
        }

        [Test]
        public void ARestart_ReadsBackEveryEntryExactly_TextOutsideAsciiIncluded()
        {
            string root = NewRoot();
            FileRbxDataStorePersistence storage = new(root);
            RbxDataStoreFile file = new() { Name = "PlayerData", Scope = "global" };
            file.Entries.Add(new RbxDataStoreEntry
            {
                Key = "Player_1",
                ValueJson = "{\"name\":\"é中😀\",\"lone\":\"\ud800\"}",
                CreatedMilliseconds = 1L,
                UpdatedMilliseconds = 2L,
                Version = 3L,
                UserIds = new[] { 1L, 9007199254740993L },
                MetadataJson = "{\"origin\":\"quest\"}"
            });

            storage.Save("block-world-demo", file);
            IReadOnlyList<RbxDataStoreLoadResult> loaded = new FileRbxDataStorePersistence(root).LoadNamespace("block-world-demo");

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(RbxDataStoreLoadStatus.Loaded, loaded[0].Status);
            RbxDataStoreEntry entry = loaded[0].File.Entries[0];
            Assert.AreEqual(file.Entries[0].ValueJson, entry.ValueJson);
            Assert.AreEqual(3L, entry.Version);
            CollectionAssert.AreEqual(new[] { 1L, 9007199254740993L }, entry.UserIds);
            Assert.AreEqual("{\"origin\":\"quest\"}", entry.MetadataJson);
            Assert.IsEmpty(new FileRbxDataStorePersistence(root).LoadNamespace(""), "another namespace is empty");
        }

        [Test]
        public void TheFolders_FollowTheStorageNamingRule()
        {
            string root = NewRoot();
            FileRbxDataStorePersistence storage = new(root);

            Assert.AreEqual(root, storage.DirectoryFor(""), "the main game keeps its data at the root");
            Assert.AreEqual(Path.Combine(root, "Stores", CoreAiStorageNames.StoreFolderName("block-world-demo")),
                storage.DirectoryFor("block-world-demo"));
            Assert.AreEqual(CoreAiStorageNames.StoreFolderName("Coins\nglobal") + ".json",
                Path.GetFileName(storage.PathFor("", "Coins", "global")));
            Assert.AreNotEqual(storage.FileIdFor("Coins", "global"), storage.FileIdFor("coins", "global"),
                "names that differ only in case keep different files");
            string longest = storage.PathFor(RbxDataStoreNamespaces.Compose("block-world-demo", "world"), "Coins", "global")
                             + ".tmp";
            Assert.AreEqual(92, longest.Length - root.Length + "\\CoreAI\\DataStores".Length);
        }

        [Test]
        public void AnUnreadableFile_IsSetAsideAndReported_AndTheBackendKeepsTheGameRunning()
        {
            string root = NewRoot();
            FileRbxDataStorePersistence storage = new(root);
            RbxDataStoreFile file = new() { Name = "PlayerData", Scope = "global" };
            file.Entries.Add(new RbxDataStoreEntry { Key = "Player_1", ValueJson = "5", Version = 1L });
            storage.Save("", file);
            string path = storage.PathFor("", "PlayerData", "global");
            File.WriteAllText(path, "{ this is not json");
            List<string> reports = new();
            RbxDataStoreBackend backend = new(new FileRbxDataStorePersistence(root), "", null, () => 0d, () => 0L, reports.Add);
            RbxDataStoreService service = new(ClassCatalog.CreateMvp1(), backend, "");
            RbxGlobalDataStore store = service.GetDataStore("PlayerData");

            RbxDataStoreRequest first = service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Get, store, "Player_1", "m"));
            RbxDataStoreRequest second = service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Get, store, "Player_1", "m"));

            Assert.AreEqual(RbxDataStoreRequestState.Failed, first.State);
            StringAssert.StartsWith("501:", first.Error.RawMessage);
            Assert.AreEqual(RbxDataStoreRequestState.Done, second.State);
            Assert.IsFalse(File.Exists(path), "the unreadable file was moved aside");
            Assert.IsTrue(File.Exists(path + ".corrupt"), "and kept for the host");
            Assert.AreEqual(1, reports.Count);
            StringAssert.Contains(".corrupt", reports[0]);
        }

        [Test]
        public void AFileNamedForAnotherStore_IsUnreadable_NotSilentlyAnotherStoresData()
        {
            string root = NewRoot();
            FileRbxDataStorePersistence storage = new(root);
            RbxDataStoreFile coins = new() { Name = "Coins", Scope = "global" };
            coins.Entries.Add(new RbxDataStoreEntry { Key = "k", ValueJson = "1", Version = 1L });
            storage.Save("", coins);
            File.Move(storage.PathFor("", "Coins", "global"), storage.PathFor("", "Gems", "global"));

            IReadOnlyList<RbxDataStoreLoadResult> loaded = storage.LoadNamespace("");

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(RbxDataStoreLoadStatus.Corrupt, loaded[0].Status);
            StringAssert.Contains("names a store other than", loaded[0].Problem);
        }

        [Test]
        public void APageThatDidNotArmPersistence_IsAFailedWrite_ScriptsSee502_AndTheDataStaysReadable()
        {
            string root = NewRoot();
            FileRbxDataStorePersistence.PersistenceArmingForTesting = () => false;
            List<string> reports = new();
            RbxDataStoreOptions options = new() { WriteDelaySeconds = 0d };
            RbxDataStoreBackend backend = new(new FileRbxDataStorePersistence(root), "", options, () => 0d, () => 0L,
                reports.Add);
            RbxDataStoreService service = new(ClassCatalog.CreateMvp1(), backend, "");
            RbxGlobalDataStore store = service.GetDataStore("Coins");
            service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Set, store, "a", "m") { ValueJson = "1" });

            service.FlushDue();
            RbxDataStoreRequest refused = service.Submit(
                new RbxDataStoreRequest(RbxDataStoreOperation.Set, store, "b", "m") { ValueJson = "2" });

            Assert.AreEqual(RbxDataStoreRequestState.Failed, refused.State);
            StringAssert.StartsWith("502:", refused.Error.RawMessage);
            StringAssert.Contains("did not arm automatic persistence", refused.Error.RawMessage);
            Assert.AreEqual("1", service.Database.Read("Coins", "global", "a").ValueJson);
            Assert.AreEqual(1, reports.Count);
        }

        [Test]
        public void AStoreLeftWithoutKeys_LosesItsFile()
        {
            string root = NewRoot();
            RbxDataStoreOptions options = new() { WriteDelaySeconds = 0d };
            RbxDataStoreBackend backend = new(new FileRbxDataStorePersistence(root), "", options, () => 0d, () => 0L);
            RbxDataStoreService service = new(ClassCatalog.CreateMvp1(), backend, "");
            RbxGlobalDataStore store = service.GetDataStore("Coins");
            service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Set, store, "a", "m") { ValueJson = "1" });
            service.FlushDue();
            string path = ((FileRbxDataStorePersistence)backend.Persistence).PathFor("", "Coins", "global");
            Assert.IsTrue(File.Exists(path));

            service.Submit(new RbxDataStoreRequest(RbxDataStoreOperation.Remove, store, "a", "m"));
            service.FlushDue();

            Assert.IsFalse(File.Exists(path));
        }

        [Test]
        public void ARootOf121Characters_KeepsTheLongestDataStorePath()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("the store's path limit applies to Windows paths only");
            }

            string parent = Path.Combine(Path.GetTempPath(), "CoreAI-DataStore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string storageRoot = parent.Length + 1 < 121 ? Path.Combine(parent, new string('d', 121 - parent.Length - 1)) : parent;
            if (storageRoot.Length != 121)
            {
                Assert.Ignore("the temporary folder '" + Path.GetTempPath() + "' is too long for a root of 121");
            }

            Directory.CreateDirectory(storageRoot);
            _directories.Add(parent);
            using IDisposable scope = CoreAiStorageRoot.Override(storageRoot);
            FileRbxDataStorePersistence storage = new();
            RbxDataStoreFile file = new() { Name = new string('n', 50), Scope = new string('s', 50) };
            file.Entries.Add(new RbxDataStoreEntry { Key = new string('k', 50), ValueJson = "1", Version = 1L });
            string dataNamespace = RbxDataStoreNamespaces.Compose("block-world-demo", "world-7");

            storage.Save(dataNamespace, file);

            Assert.AreEqual(1, new FileRbxDataStorePersistence().LoadNamespace(dataNamespace).Count);
            Assert.LessOrEqual(storage.PathFor(dataNamespace, file.Name, file.Scope).Length + ".tmp".Length, 259);
        }

        private string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-DataStore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            _directories.Add(root);
            return root;
        }
    }
}
