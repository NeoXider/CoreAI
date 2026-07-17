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
