using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for the durability answer CoreAI gives its file-backed stores.
    /// <para>
    /// The defect these tests exist for: the old implementation awaited an <c>FS.syncfs</c> completion
    /// callback that Unity 6.3 never delivers, so a store's "is my write durable?" question had no
    /// answer at all and every caller sat until its own timeout - <c>memory action=write</c> reported a
    /// false failure after 30 s for data that was in fact on disk. The contract is now: answer
    /// immediately, and answer truthfully.
    /// </para>
    /// </summary>
    public sealed class CoreAiWebGlPersistenceEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: <see cref="WaitForCompletion_CancellationDoesNotBecomeSuccess"/> below is a synchronous
        /// [Test] that blocks on Assert.CatchAsync; detaching the context sends the awaited delegate's
        /// continuation to the thread pool instead of back onto this same blocked thread, which would
        /// deadlock.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public void SyncAsync_AnswersImmediately_AndNeverParksOnACallback()
        {
            UniTask<bool> pending = CoreAiWebGlPersistence.SyncAsync();

            Assert.AreEqual(UniTaskStatus.Succeeded, pending.Status,
                "Durability must be answered synchronously. A pending task here means a caller can " +
                "again wait forever on a confirmation channel that does not deliver.");
        }

        [Test]
        public async Task SyncAsync_OffWebGl_ReportsTheWriteAsDurable()
        {
            bool durable = await CoreAiWebGlPersistence.SyncAsync();

            Assert.IsTrue(durable, "Off WebGL the OS filesystem is durable once the write call returns.");
        }

        [Test]
        public void Sync_OffWebGl_ReportsTheWriteAsDurable()
        {
            Assert.IsTrue(CoreAiWebGlPersistence.Sync());
        }

        [Test]
        public void IsAutoSyncEnabled_OffWebGl_IsTrue()
        {
            Assert.IsTrue(CoreAiWebGlPersistence.IsAutoSyncEnabled,
                "Only a browser page can lack durable storage; every other platform always has it.");
        }

        [Test]
        public void SyncAsync_CancellationIsNotReportedAsDurability()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.Catch<OperationCanceledException>(
                () => CoreAiWebGlPersistence.SyncAsync(cancellation.Token));
        }
    }
}
