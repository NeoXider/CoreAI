using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class FileConversationSummaryStoreEditModeTests
    {
        [Test]
        public void FileConversationSummaryStore_PersistsPerRole()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestSummary_" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);

            try
            {
                FileConversationSummaryStore store = new(root, null);
                const string role = "SmartChat";
                store.SaveSummary(role, "line a");
                Assert.AreEqual("line a", store.LoadSummary(role));
                store.SaveSummary(role, "line b");
                Assert.AreEqual("line b", store.LoadSummary(role));
                store.ClearSummary(role);
                Assert.AreEqual("", store.LoadSummary(role));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        [Test]
        public void SaveSummaryAsync_Then_LoadSummaryAsync_RoundTrips()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestSummary_" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);

            try
            {
                FileConversationSummaryStore store = new(root, null);
                store.SaveSummaryAsync("RoleA", "async summary").GetAwaiter().GetResult();
                Assert.AreEqual("async summary", store.LoadSummaryAsync("RoleA").GetAwaiter().GetResult());
                Assert.AreEqual("async summary", store.LoadSummary("RoleA"));

                store.ClearSummaryAsync("RoleA").GetAwaiter().GetResult();
                Assert.AreEqual("", store.LoadSummary("RoleA"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* best effort */ }
            }
        }

        [Test]
        public void SaveSummaryAsync_ConcurrentWrites_FinalFileIsValidJsonOfOneWrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestSummary_" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);

            try
            {
                FileConversationSummaryStore store = new(root, null);
                const string role = "ConcurrentRole";
                string[] values = Enumerable.Range(0, 24).Select(i => $"summary_{i}").ToArray();

                Task[] tasks = values
                    .Select(v => Task.Run(() => store.SaveSummaryAsync(role, v)))
                    .ToArray();
                Task.WaitAll(tasks);

                string path = Path.Combine(root, $"{role}.json");
                Assert.IsTrue(File.Exists(path));
                Assert.IsFalse(File.Exists(path + ".tmp"), "No leftover tmp file after atomic writes");

                // File must be valid JSON containing exactly one of the written summaries.
                string json = File.ReadAllText(path);
                Assert.DoesNotThrow(() => JsonConvert.DeserializeObject<object>(json));
                string loaded = store.LoadSummary(role);
                Assert.That(values, Does.Contain(loaded));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* best effort */ }
            }
        }
    }
}