using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression: in the Editor outside Play Mode, <see cref="UnityMainThreadLlmAsyncMarshaler"/>
    /// must not await <see cref="Cysharp.Threading.Tasks.UniTask.SwitchToMainThread"/> - Edit Mode tests
    /// and tooling sometimes block the managed main thread on <c>Task.Wait</c> while MEAI continues on
    /// the thread pool; a main-thread hop would deadlock (player loop not pumping).
    /// It must also not treat <see cref="Application.isPlaying"/> as fatal when probed from a worker thread
    /// (otherwise tool bodies fail with **get_isPlaying can only be called from the main thread**).
    /// </summary>
    public sealed class UnityMainThreadLlmAsyncMarshalerEditModeTests
    {
        [Test]
        public void InvokeAsync_WhenNotPlaying_CompletesUnderMainThreadWait_FromThreadPool()
        {
            Assert.IsFalse(Application.isPlaying, "Precondition: Edit Mode - Application.isPlaying must be false.");

            int mainId = Thread.CurrentThread.ManagedThreadId;
            Task<int> worker = Task.Run(async () =>
            {
                Assert.AreNotEqual(mainId, Thread.CurrentThread.ManagedThreadId,
                    "Precondition: work item must run off the Unity test/main thread.");

                return await UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                        () => Task.FromResult(42),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            });

            bool completed = worker.Wait(System.TimeSpan.FromSeconds(15));
            Assert.IsTrue(completed, "Marshaler must not deadlock the editor when the main thread waits on Task.Run.");
            Assert.AreEqual(42, worker.Result);
        }

        [Test]
        public async Task InvokeAsync_WhenNotPlaying_FactoryRunsOnInvokerThreadForSyncFactory()
        {
            Assert.IsFalse(Application.isPlaying);

            int callerId = Thread.CurrentThread.ManagedThreadId;
            int factoryThreadId =
                await UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                    () => Task.FromResult(Thread.CurrentThread.ManagedThreadId),
                    CancellationToken.None);

            Assert.AreEqual(callerId, factoryThreadId,
                "Bypass path (no SwitchToMainThread) should invoke the factory on the caller's thread " +
                "for a synchronous tool body.");
        }

        [Test]
        public async Task InvokeAsync_FromThreadPool_CompletesWithAsyncAwait_AvoidsIsPlayingOnWorker()
        {
            Assert.IsFalse(Application.isPlaying);

            int value = await Task.Run(async () =>
                    await UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                            () => Task.FromResult(11),
                            CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.AreEqual(11, value);
        }
    }
}
