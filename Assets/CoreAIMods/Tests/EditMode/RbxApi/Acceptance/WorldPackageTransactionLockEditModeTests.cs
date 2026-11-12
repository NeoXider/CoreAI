using System;
using System.Threading;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The package store's transaction lock (TODO.md, the kept-world hang): a waiter is resumed by the release itself,
    /// never through the task scheduler, which a WebGL player cannot run without a context; and a hang names the holder.
    /// </summary>
    public sealed class WorldPackageTransactionLockEditModeTests
    {
        private static readonly object FileSystem = new();

        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            // WHY no context: a WebGL player resumes a context-less task continuation on the thread pool, which never
            // runs there; the lock must hand over without one.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static string Root(string name)
        {
            return "transaction-lock-test/" + name + "/" + Guid.NewGuid().ToString("N");
        }

        [Test]
        public void AWaiter_IsHandedTheTransaction_InsideTheRelease_WithNoContextAndNoScheduler()
        {
            string root = Root("handover");
            IDisposable first = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None, "first")
                .GetAwaiter().GetResult();
            UniTask<IDisposable> second = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None, "second");
            UniTask<IDisposable> third = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None, "third");

            Assert.AreEqual(UniTaskStatus.Pending, second.Status, "the second waits while the first holds");
            first.Dispose();
            Assert.AreEqual(UniTaskStatus.Succeeded, second.Status, "the release itself resumed the second, in order");
            Assert.AreEqual(UniTaskStatus.Pending, third.Status, "the third still waits behind the second");

            second.GetAwaiter().GetResult().Dispose();
            Assert.AreEqual(UniTaskStatus.Succeeded, third.Status);
            third.GetAwaiter().GetResult().Dispose();
            IDisposable after = WorldPackageTransactions.TryEnter(root, FileSystem, "after");
            Assert.IsNotNull(after, "free again once all let go");
            after.Dispose();
        }

        [Test]
        public void ACancelledWaiter_IsSkipped_AndTheNextOneGetsTheTransaction()
        {
            string root = Root("cancel");
            IDisposable first = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None, "first")
                .GetAwaiter().GetResult();
            CancellationTokenSource cancel = new();
            UniTask<IDisposable> cancelled = WorldPackageTransactions.EnterAsync(root, FileSystem, cancel.Token, "cancelled");
            UniTask<IDisposable> next = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None, "next");

            cancel.Cancel();
            Assert.AreEqual(UniTaskStatus.Canceled, cancelled.Status);
            first.Dispose();

            Assert.AreEqual(UniTaskStatus.Succeeded, next.Status, "the cancelled waiter does not swallow the hand-over");
            next.GetAwaiter().GetResult().Dispose();
            IDisposable after = WorldPackageTransactions.TryEnter(root, FileSystem, "after");
            Assert.IsNotNull(after);
            after.Dispose();
        }

        [Test]
        public void TheHolder_AndItsWaiters_AreNamedInTheSaveSteps()
        {
            string root = Root("named");
            IDisposable holder = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None,
                "transaction_lock_test_holder").GetAwaiter().GetResult();
            UniTask<IDisposable> waiter = WorldPackageTransactions.EnterAsync(root, FileSystem, CancellationToken.None,
                "transaction_lock_test_waiter");

            string steps = WorldSaveStages.DescribeStuck(TimeSpan.Zero);
            StringAssert.Contains("store: holds the package store's transaction: transaction_lock_test_holder", steps);
            StringAssert.Contains("store: waiting for the package store's transaction: transaction_lock_test_waiter", steps);

            holder.Dispose();
            waiter.GetAwaiter().GetResult().Dispose();
            StringAssert.DoesNotContain("transaction_lock_test_", WorldSaveStages.DescribeStuck(TimeSpan.Zero),
                "both steps end with their transactions");
        }
    }
}
