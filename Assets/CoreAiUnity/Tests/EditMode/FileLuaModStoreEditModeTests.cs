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
    }
}