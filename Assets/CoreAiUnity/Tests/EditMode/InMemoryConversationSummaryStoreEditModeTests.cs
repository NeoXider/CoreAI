using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class InMemoryConversationSummaryStoreEditModeTests
    {
        private static async Task<Exception> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class FixedScopeProvider : IAgentMemoryScopeProvider
        {
            public bool ResolveCalled;

            public AgentMemoryScope GetScope(string roleId)
            {
                ResolveCalled = true;
                return new AgentMemoryScope("tenant", "user", "session", "");
            }
        }

        private sealed class SyncOnlyStore : IConversationSummaryStore
        {
            public int SaveCalls;
            public string LastRole;

            public string LoadSummary(string roleId)
            {
                return "sync:" + roleId;
            }

            public void SaveSummary(string roleId, string summary)
            {
                SaveCalls++;
                LastRole = roleId;
            }

            public void ClearSummary(string roleId)
            {
            }
        }

        private sealed class AsyncCapableStore : IConversationSummaryStore, IAsyncConversationSummaryStore
        {
            public int SyncCalls;
            public string LastAsyncRole;

            public string LoadSummary(string roleId)
            {
                SyncCalls++;
                throw new NotImplementedException("Async path must not use the synchronous fallback.");
            }

            public void SaveSummary(string roleId, string summary)
            {
                SyncCalls++;
                throw new NotImplementedException("Async path must not use the synchronous fallback.");
            }

            public void ClearSummary(string roleId)
            {
                SyncCalls++;
                throw new NotImplementedException("Async path must not use the synchronous fallback.");
            }

            public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastAsyncRole = roleId;
                return Task.FromResult("async:" + roleId);
            }

            public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastAsyncRole = roleId;
                return Task.CompletedTask;
            }

            public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastAsyncRole = roleId;
                return Task.CompletedTask;
            }
        }

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

        [Test]
        public async Task AsyncContract_RoundTripsDirectly()
        {
            IAsyncConversationSummaryStore store = new InMemoryConversationSummaryStore();
            Assert.AreEqual("", await store.LoadSummaryAsync("a"));
            await store.SaveSummaryAsync("a", "v");
            Assert.AreEqual("v", await store.LoadSummaryAsync("a"));
            await store.ClearSummaryAsync("a");
            Assert.AreEqual("", await store.LoadSummaryAsync("a"));
        }

        [Test]
        public async Task NullStore_AsyncIsNoOpAndHonoursCancellation()
        {
            IAsyncConversationSummaryStore store = new NullConversationSummaryStore();
            await store.SaveSummaryAsync("a", "v");
            Assert.AreEqual("", await store.LoadSummaryAsync("a"));
            await store.ClearSummaryAsync("a");

            using (CancellationTokenSource cts = new())
            {
                cts.Cancel();
                Exception failure = await CaptureAsync(() => store.SaveSummaryAsync("a", "v", cts.Token));
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }
        }

        [Test]
        public async Task Adapter_ForwardsAsyncCapableWithoutBlockingFallback()
        {
            AsyncCapableStore inner = new();
            BlockingSyncSummaryStoreAsyncAdapter adapter = new(inner);
            await adapter.SaveSummaryAsync("role", "v");
            Assert.AreEqual(0, inner.SyncCalls);
            Assert.That(inner.LastAsyncRole, Does.StartWith("role"));
        }

        [Test]
        public async Task Adapter_ExplicitSyncFallbackIsDocumentedOptIn()
        {
            SyncOnlyStore inner = new();
            BlockingSyncSummaryStoreAsyncAdapter adapter = new(inner);
            await adapter.SaveSummaryAsync("role", "v");
            Assert.AreEqual(1, inner.SaveCalls);
            Assert.AreEqual("role", inner.LastRole);
            Assert.AreEqual("sync:role", await adapter.LoadSummaryAsync("role"));
        }

        [Test]
        public async Task ScopedAsync_ResolvesScopeBeforeAwaitsAndSkipsSyncFallback()
        {
            FixedScopeProvider provider = new();
            AsyncCapableStore inner = new();
            ScopedConversationSummaryStoreDecorator decorator = new(inner, provider);

            Task save = decorator.SaveSummaryAsync("role", "v");
            Assert.IsTrue(provider.ResolveCalled, "Scope must resolve synchronously before any await.");
            await save;

            Assert.AreEqual(0, inner.SyncCalls, "Async-capable inner must not hit the sync fallback.");
            Assert.That(inner.LastAsyncRole, Does.StartWith("scope-v1-"));

            string loaded = await decorator.LoadSummaryAsync("role");
            Assert.That(loaded, Does.StartWith("async:scope-v1-"));
        }

        [Test]
        public async Task ScopedAsync_SyncOnlyBackendRejectedUnlessExplicitOptIn()
        {
            FixedScopeProvider provider = new();
            SyncOnlyStore inner = new();
            ScopedConversationSummaryStoreDecorator strict = new(inner, provider);

            Exception failure = await CaptureAsync(() => strict.SaveSummaryAsync("role", "v"));
            Assert.That(failure, Is.InstanceOf<NotSupportedException>());
            Assert.AreEqual(0, inner.SaveCalls);

            ScopedConversationSummaryStoreDecorator explicitFallback = new(inner, provider, allowBlockingSyncFallback: true);
            await explicitFallback.SaveSummaryAsync("role", "v");
            Assert.AreEqual(1, inner.SaveCalls);
            Assert.That(inner.LastRole, Does.StartWith("scope-v1-"));
        }

        [Test]
        public async Task ScopedAsync_CancelledBeforeCall_DoesNotTouchInner()
        {
            FixedScopeProvider provider = new();
            SyncOnlyStore inner = new();
            ScopedConversationSummaryStoreDecorator decorator = new(inner, provider, allowBlockingSyncFallback: true);

            using (CancellationTokenSource cts = new())
            {
                cts.Cancel();
                Exception failure = await CaptureAsync(() => decorator.SaveSummaryAsync("role", "v", cts.Token));
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }

            Assert.AreEqual(0, inner.SaveCalls);
        }
    }
}
