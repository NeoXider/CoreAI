using System;
using System.Threading.Tasks;
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
        public void ConcurrentInitializeStorm_NeverExceedsSessionCapacity()
        {
            McpSessionStore store = new(maxSessions: 4);
            Parallel.For(0, 1000, _ =>
            {
                store.Issue();
                Assert.LessOrEqual(store.Count, 4);
            });
            Assert.AreEqual(4, store.Count);
        }

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
            McpSessionStore store = new(TimeSpan.FromHours(1), 8, () =>
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
        public void ActiveSession_SlidesItsLifetime_PastTheFirstExpiry()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromMinutes(10), clock: () => now);
            string active = store.Issue();

            for (int minute = 8; minute <= 40; minute += 8)
            {
                now = new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);
                Assert.IsTrue(store.IsKnown(active), $"a session used every 8 minutes must survive minute {minute}.");
            }
        }

        [Test]
        public void IdleSession_ExpiresOneLifetimeAfterItsLastUse()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromMinutes(10), clock: () => now);
            string session = store.Issue();
            now = now.AddMinutes(9);
            Assert.IsTrue(store.IsKnown(session));

            now = now.AddMinutes(10).AddSeconds(1);

            Assert.IsFalse(store.IsKnown(session), "a lifetime of silence after the last use expires the session.");
        }

        [Test]
        public void CapEviction_DropsTheLeastRecentlyUsedId()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromHours(1), 2, () =>
            {
                now = now.AddSeconds(1);
                return now;
            });
            string oldestButActive = store.Issue();
            string idle = store.Issue();
            Assert.IsTrue(store.IsKnown(oldestButActive));

            store.Issue();

            Assert.IsTrue(store.IsKnown(oldestButActive), "a session in use must not be evicted for an idle one.");
            Assert.IsFalse(store.IsKnown(idle));
        }

        [Test]
        public void Remove_ForgetsOnlyTheNamedSession()
        {
            McpSessionStore store = new();
            string gone = store.Issue();
            string kept = store.Issue();

            Assert.IsTrue(store.Remove(gone));
            Assert.IsFalse(store.Remove(gone));
            Assert.IsFalse(store.Remove(null));
            Assert.IsFalse(store.IsKnown(gone));
            Assert.IsTrue(store.IsKnown(kept));
        }

        [Test]
        public void CapEviction_DropsTheOldestIdFirst()
        {
            DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            McpSessionStore store = new(TimeSpan.FromHours(1), 2, () =>
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
