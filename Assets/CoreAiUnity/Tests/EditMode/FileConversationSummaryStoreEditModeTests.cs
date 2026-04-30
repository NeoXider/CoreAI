using System.IO;
using CoreAI.Ai;
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
                const string role = "PlayerChat";
                store.SaveSummary(role, "line a");
                Assert.AreEqual("line a", store.LoadSummary(role));
                store.SaveSummary(role, "line b");
                Assert.AreEqual("line b", store.LoadSummary(role));
                store.ClearSummary(role);
                Assert.AreEqual("", store.LoadSummary(role));
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { /* best effort */ }
            }
        }
    }
}
