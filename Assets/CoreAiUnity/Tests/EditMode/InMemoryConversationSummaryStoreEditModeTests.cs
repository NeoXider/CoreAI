using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class InMemoryConversationSummaryStoreEditModeTests
    {
        [Test]
        public void RoundTrip_PerRoleIsolation()
        {
            InMemoryConversationSummaryStore store = new();
            Assert.AreEqual("", store.LoadSummary("a"));
            store.SaveSummary("a", "summary-a");
            store.SaveSummary("b", "summary-b");

            Assert.AreEqual("summary-a", store.LoadSummary("a"));
            Assert.AreEqual("summary-b", store.LoadSummary("b"));
        }

        [Test]
        public void ClearSummary_RemovesKey()
        {
            InMemoryConversationSummaryStore store = new();
            store.SaveSummary("r", "x");
            store.ClearSummary("r");
            Assert.AreEqual("", store.LoadSummary("r"));
        }

        [Test]
        public void SaveSummary_Whitespace_TrimsRoleId()
        {
            InMemoryConversationSummaryStore store = new();
            store.SaveSummary("  role  ", "v");
            Assert.AreEqual("v", store.LoadSummary("role"));
        }
    }
}
