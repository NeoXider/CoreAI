using System;
using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The session store must stay bounded: a client that reconnects on every play/stop cycle used to add
    /// an entry per <c>initialize</c> that was never removed.
    /// </summary>
    public sealed class McpSessionStoreEditModeTests
    {
        [Test]
        public void Issue_ReturnsDistinctIds_ThatAreKnown()
        {
            McpSessionStore store = new();

            string first = store.Issue();
            string second = store.Issue();

            Assert.AreNotEqual(first, second);
            Assert.IsTrue(store.IsKnown(first));
            Assert.IsTrue(store.IsKnown(second));
            Assert.IsFalse(store.IsKnown("never-issued"));
            Assert.IsFalse(store.IsKnown(null));
        }

        [Test]
        public void ExpiredSessions_ArePrunedAndReportedUnknown()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromMinutes(10), clock: () => now);

            string stale = store.Issue();
            now = now.AddMinutes(11);

            Assert.IsFalse(store.IsKnown(stale), "an expired id must not validate.");
            store.Issue();
            Assert.AreEqual(1, store.Count, "issuing must prune expired entries.");
        }

        [Test]
        public void ReconnectStorm_StaysUnderTheCap()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromHours(1), maxSessions: 8, clock: () =>
            {
                now = now.AddSeconds(1);
                return now;
            });

            for (int i = 0; i < 500; i++)
            {
                store.Issue();
            }

            Assert.LessOrEqual(store.Count, 8, "the store must not grow with every reconnect.");
        }

        [Test]
        public void CapEviction_DropsTheOldestIdFirst()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromHours(1), maxSessions: 2, clock: () =>
            {
                now = now.AddSeconds(1);
                return now;
            });

            string oldest = store.Issue();
            string newer = store.Issue();
            store.Issue();

            Assert.IsFalse(store.IsKnown(oldest));
            Assert.IsTrue(store.IsKnown(newer));
        }
    }
}
