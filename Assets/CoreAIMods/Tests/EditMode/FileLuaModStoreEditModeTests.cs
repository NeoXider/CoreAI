using System.IO;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class FileLuaModStoreEditModeTests
    {
        private string _root;
        private FileLuaModStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "CoreAITestLuaModStore_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _store = new FileLuaModStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            _store?.Dispose();
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

        [Test]
        public void FileLuaModStore_SetGet_RoundTripsValue()
        {
            _store.Set("mod", "key", "value");

            Assert.AreEqual("value", _store.Get("mod", "key"));
        }

        [Test]
        public void FileLuaModStore_ContentionFailsImmediatelyWithoutMutation()
        {
            System.Reflection.FieldInfo gateField = typeof(FileLuaModStore).GetField(
                "_gate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(gateField);
            System.Threading.SemaphoreSlim gate =
                (System.Threading.SemaphoreSlim)gateField.GetValue(_store);
            Assert.IsTrue(gate.Wait(0));
            try
            {
                System.InvalidOperationException getError =
                    Assert.Throws<System.InvalidOperationException>(() =>
                        _store.Get("mod", "key"));
                StringAssert.Contains("busy", getError.Message);
                Assert.Throws<System.InvalidOperationException>(() =>
                    _store.Set("mod", "key", "blocked"));
                Assert.Throws<System.InvalidOperationException>(() =>
                    _store.Clear("mod"));
            }
            finally
            {
                gate.Release();
            }

            Assert.AreEqual("", _store.Get("mod", "key"));
        }

        [Test]
        public void FileLuaModStore_Get_MissingKeyReturnsEmptyString()
        {
            Assert.AreEqual("", _store.Get("mod", "missing"));
        }

        [Test]
        public void FileLuaModStore_Set_NullValueRemovesKey()
        {
            _store.Set("mod", "key", "value");

            _store.Set("mod", "key", null);

            Assert.AreEqual("", _store.Get("mod", "key"));
        }

        [Test]
        public void FileLuaModStore_Clear_RemovesFile()
        {
            _store.Set("mod", "key", "value");

            _store.Clear("mod");

            Assert.AreEqual("", _store.Get("mod", "key"));
            Assert.AreEqual(0, Directory.GetFiles(_root, "*.json").Length);
        }

        [Test]
        public void FileLuaModStore_SecondStoreInstance_ReadsPersistedValue()
        {
            _store.Set("mod", "key", "value");
            _store.Dispose();
            _store = new FileLuaModStore(_root);

            Assert.AreEqual("value", _store.Get("mod", "key"));
        }

        [Test]
        public void FileLuaModStore_SimilarIds_DoNotCollide()
        {
            _store.Set("A/B", "key", "slash");
            _store.Set("A_B", "key", "underscore");

            Assert.AreEqual("slash", _store.Get("A/B", "key"));
            Assert.AreEqual("underscore", _store.Get("A_B", "key"));
            Assert.AreEqual(2, Directory.GetFiles(_root, "*.json").Length);
        }

        [Test]
        public void FileLuaModStore_CaseDistinctIdsRemainIsolatedAcrossRestartAndClear()
        {
            _store.Set("Case", "shared", "upper");
            _store.Set("case", "shared", "lower");

            Assert.AreEqual("upper", _store.Get("Case", "shared"));
            Assert.AreEqual("lower", _store.Get("case", "shared"));
            Assert.AreEqual(2, Directory.GetFiles(_root, "*.json").Length);

            _store.Dispose();
            _store = new FileLuaModStore(_root);
            Assert.AreEqual("upper", _store.Get("Case", "shared"));
            Assert.AreEqual("lower", _store.Get("case", "shared"));

            _store.Clear("Case");
            Assert.AreEqual("", _store.Get("Case", "shared"));
            Assert.AreEqual("lower", _store.Get("case", "shared"));

            _store.Dispose();
            _store = new FileLuaModStore(_root);
            Assert.AreEqual("", _store.Get("Case", "shared"));
            Assert.AreEqual("lower", _store.Get("case", "shared"));
            Assert.AreEqual(1, Directory.GetFiles(_root, "*.json").Length);
        }

        [Test]
        public void FileLuaModStore_LegacyFileIsClaimedByOneExactIdAndMigratedToHash()
        {
            File.WriteAllText(
                Path.Combine(_root, "Case.json"),
                "{\"shared\":\"legacy\"}");

            Assert.AreEqual("legacy", _store.Get("Case", "shared"));
            Assert.AreEqual("", _store.Get("case", "shared"));
            string[] migratedFiles = Directory.GetFiles(_root, "*.json");
            Assert.AreEqual(1, migratedFiles.Length);
            StringAssert.StartsWith("id-", Path.GetFileNameWithoutExtension(migratedFiles[0]));

            _store.Dispose();
            _store = new FileLuaModStore(_root);
            Assert.AreEqual("legacy", _store.Get("Case", "shared"));
            Assert.AreEqual("", _store.Get("case", "shared"));
        }

        [Test]
        public void FileLuaModStore_DifferentStoreIds_IsolateSameModId()
        {
            FileLuaModStore storeA = new(_root, storeId: "demo-a");
            FileLuaModStore storeB = new(_root, storeId: "demo-b");
            try
            {
                storeA.Set("mod", "key", "from-a");

                Assert.AreEqual("", storeB.Get("mod", "key"),
                    "A value saved under one store id must be invisible to another store id.");
                Assert.AreEqual("from-a", storeA.Get("mod", "key"));

                storeB.Set("mod", "key", "from-b");
                Assert.AreEqual("from-a", storeA.Get("mod", "key"),
                    "A write under one store id must not leak into another store id.");

                string dirA = Path.Combine(_root, "Stores", "demo-a");
                string dirB = Path.Combine(_root, "Stores", "demo-b");
                Assert.AreEqual(1, Directory.GetFiles(dirA, "*.json").Length,
                    "Each store id must persist its files in its own subdirectory.");
                Assert.AreEqual(1, Directory.GetFiles(dirB, "*.json").Length);
            }
            finally
            {
                storeA.Dispose();
                storeB.Dispose();
            }
        }

        [Test]
        public void FileLuaModStore_EmptyStoreId_KeepsSharedRootPath()
        {
            FileLuaModStore defaulted = new(_root, storeId: "");
            try
            {
                defaulted.Set("mod", "key", "value");

                Assert.AreEqual(1, Directory.GetFiles(_root, "*.json").Length,
                    "An empty store id must keep today's shared root path unchanged.");
                Assert.AreEqual("value", _store.Get("mod", "key"),
                    "An id-less store must read what another id-less store on the same root wrote.");
            }
            finally
            {
                defaulted.Dispose();
            }
        }

        [Test]
        public void DisposedStore_LateHandlerCalls_AreSilentNoOps()
        {
            // Regression: mod handlers are driven by a per-frame tick that can fire once more while the
            // owning scope tears down; a late store_set used to throw ObjectDisposedException out of the
            // mod handler ("The semaphore has been disposed") and fail unrelated PlayMode tests.
            _store.Set("tetris3d", "score", "42");
            Assert.AreEqual("42", _store.Get("tetris3d", "score"));

            _store.Dispose();

            Assert.DoesNotThrow(() => _store.Set("tetris3d", "score", "late-write"),
                "store_set from a late mod tick must not throw after the scope disposed the store.");
            Assert.AreEqual("", _store.Get("tetris3d", "score"),
                "Reads after dispose degrade to the empty default instead of throwing.");
            Assert.DoesNotThrow(() => _store.Clear("tetris3d"));

            _store = new FileLuaModStore(_root);
            Assert.AreEqual("42", _store.Get("tetris3d", "score"),
                "A write attempted after dispose must not reach the file.");
        }
    }
}
