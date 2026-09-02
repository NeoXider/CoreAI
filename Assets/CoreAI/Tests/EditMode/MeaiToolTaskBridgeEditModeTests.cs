using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="MeaiToolTaskBridge"/> must let a <c>ConfigureAwait(false)</c> awaiter (MEAI's, inside
    /// its binary) continue inline on the completing call stack even when a derived
    /// <see cref="SynchronizationContext"/> is current. A derived context stands in for Unity's; the
    /// editor's thread pool turns the WebGL hang into an observable hop to another thread.
    /// </summary>
    public sealed class MeaiToolTaskBridgeEditModeTests
    {
        private SynchronizationContext _previous;

        [SetUp]
        public void SetUp()
        {
            _previous = SynchronizationContext.Current;
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }

        [Test]
        public void Publish_CompletedBody_ReturnsTheSameTask()
        {
            Task<int> body = Task.FromResult(3);

            Assert.AreSame(body, MeaiToolTaskBridge.Publish(body));
        }

        [Test]
        public void Publish_NullBody_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MeaiToolTaskBridge.Publish<int>(null));
        }

        [Test]
        public void Publish_PendingBody_ConfigureAwaitFalseContinuationRunsInlineUnderAHostContext()
        {
            TaskCompletionSource<int> body = new();
            Task<int> surfaced = MeaiToolTaskBridge.Publish(body.Task);
            int continuationThread = -1;
            Task observer = ObserveLikeMeai(surfaced, id => continuationThread = id);
            HostSynchronizationContext host = new();
            SynchronizationContext.SetSynchronizationContext(host);
            int completingThread = Environment.CurrentManagedThreadId;

            body.SetResult(7);

            Assert.IsTrue(observer.IsCompleted,
                "the ConfigureAwait(false) continuation must have run inline, before SetResult returned");
            Assert.AreEqual(completingThread, continuationThread,
                "the continuation hopped to another thread: that is a thread-pool continuation, which never runs in the WebGL player");
            Assert.AreEqual(7, surfaced.Result);
            Assert.AreSame(host, SynchronizationContext.Current, "the host context must be restored after publishing");
        }

        /// <summary>
        /// The mechanism the bridge exists for. If a future runtime inlines this continuation, the
        /// bridge has become redundant and this test says so.
        /// </summary>
        [Test]
        public void RawPendingBody_ConfigureAwaitFalseContinuationLeavesTheCompletingThreadUnderAHostContext()
        {
            TaskCompletionSource<int> body = new();
            int continuationThread = -1;
            Task observer = ObserveLikeMeai(body.Task, id => continuationThread = id);
            SynchronizationContext.SetSynchronizationContext(new HostSynchronizationContext());
            int completingThread = Environment.CurrentManagedThreadId;

            body.SetResult(7);

            Assert.IsTrue(observer.Wait(TimeSpan.FromSeconds(5)), "the raw continuation never ran at all");
            Assert.AreNotEqual(completingThread, continuationThread,
                "without the bridge the continuation is expected to be queued to the thread pool");
        }

        [Test]
        public void Publish_FaultedBody_SurfacesTheOriginalException()
        {
            TaskCompletionSource<int> body = new();
            Task<int> surfaced = MeaiToolTaskBridge.Publish(body.Task);

            body.SetException(new InvalidOperationException("boom"));

            InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await surfaced);
            Assert.AreEqual("boom", ex.Message);
        }

        [Test]
        public void Publish_CanceledBody_SurfacesCancellation()
        {
            TaskCompletionSource<int> body = new();
            Task<int> surfaced = MeaiToolTaskBridge.Publish(body.Task);

            body.SetCanceled();

            Assert.IsTrue(surfaced.IsCanceled);
        }

        private static async Task ObserveLikeMeai(Task<int> task, Action<int> onContinued)
        {
            await task.ConfigureAwait(false);
            onContinued(Environment.CurrentManagedThreadId);
        }

        private sealed class HostSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) => d(state);

            public override void Send(SendOrPostCallback d, object state) => d(state);
        }
    }
}
