using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
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
    }
}
