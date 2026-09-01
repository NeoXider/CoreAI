using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiWebGlPersistenceEditModeTests
    {
        [Test]
        public async Task SyncAsync_InEditor_CompletesAsDurableNoOp()
        {
            bool completed = await CoreAiWebGlPersistence.SyncAsync();

            Assert.IsTrue(completed);
        }

        [Test]
        public async Task WaitForCompletion_LostCallback_EndsAtFiniteTimeoutBranch()
        {
            UniTaskCompletionSource<bool> callback = new();

            CoreAiWebGlPersistence.CompletionWaitResult result =
                await CoreAiWebGlPersistence.WaitForCompletionAsync(
                    callback.Task,
                    UniTask.CompletedTask);

            Assert.IsFalse(result.Completed);
            Assert.IsFalse(result.Succeeded);
        }

        [Test]
        public async Task WaitForCompletion_CallbackWinsWithoutWaitingForTimeout()
        {
            UniTaskCompletionSource<bool> timeout = new();

            CoreAiWebGlPersistence.CompletionWaitResult result =
                await CoreAiWebGlPersistence.WaitForCompletionAsync(
                    UniTask.FromResult(true),
                    timeout.Task);

            Assert.IsTrue(result.Completed);
            Assert.IsTrue(result.Succeeded);
        }

        [Test]
        public void WaitForCompletion_CancellationDoesNotBecomeSuccess()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            UniTaskCompletionSource<bool> callback = new();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await CoreAiWebGlPersistence.WaitForCompletionAsync(
                    callback.Task,
                    UniTask.FromCanceled(cancellation.Token)));
        }
    }
}
