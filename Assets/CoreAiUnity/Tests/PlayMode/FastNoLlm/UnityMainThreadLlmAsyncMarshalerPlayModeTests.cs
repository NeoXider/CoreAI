using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Guards <see cref="UnityMainThreadLlmAsyncMarshaler"/> against regressions where MEAI continues
    /// on the CLR thread pool (<c>ConfigureAwait(false)</c>) and tools must bounce back to the player loop.
    /// </summary>
    public sealed class UnityMainThreadLlmAsyncMarshalerPlayModeTests
    {
        /// <summary>
        /// The true Unity main thread ID, captured in <see cref="SetUp"/> which
        /// always runs on the main thread (NUnit runner guarantee in PlayMode).
        /// We cannot rely on <c>Thread.CurrentThread.ManagedThreadId</c> inside
        /// a coroutine body after <c>yield return</c> — some Unity versions
        /// resume coroutines on worker threads.
        /// </summary>
        private int _unityMainThreadId;

        [SetUp]
        public void SetUp()
        {
            // SetUp always runs on the main thread in Unity Test Runner.
            _unityMainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [UnityTest]
        public IEnumerator AfterSwitchToThreadPool_InvokeAsync_RunsDelegateOnUnityMainManagedThread()
        {
            // The marshaler uses a volatile mirror of Application.isPlaying updated via
            // Application.onBeforeRender. We need to wait enough frames for both:
            // (a) Application.isPlaying to be true, and
            // (b) the mirror to be primed by onBeforeRender.
            // Without this, the marshaler may inline on the thread pool (stale mirror = 0).
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            yield return new WaitForEndOfFrame();

            Assert.IsTrue(Application.isPlaying, "Test must run in Play Mode");

            int mainThreadId = _unityMainThreadId;
            TaskCompletionSource<int> tcs = new();

            UniTask.Void(async () =>
            {
                try
                {
                    await UniTask.SwitchToThreadPool();

#if !UNITY_WEBGL
                    Assert.AreNotEqual(mainThreadId, Thread.CurrentThread.ManagedThreadId,
                        "Fixture must leave the synchronous Unity test thread via SwitchToThreadPool.");
#endif

                    int inFactory =
                        await UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                            () => Task.FromResult(Thread.CurrentThread.ManagedThreadId),
                            CancellationToken.None);

                    Assert.AreEqual(mainThreadId, inFactory,
                        $"Marshaler should run delegate on Unity main thread ({mainThreadId}), but ran on {inFactory}.");
                    tcs.TrySetResult(inFactory);
                }
                catch (System.Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            yield return new WaitUntil(() => tcs.Task.IsCompleted);
            Assert.IsFalse(tcs.Task.IsFaulted, tcs.Task.IsFaulted ? tcs.Task.Exception?.ToString() : "");
            Assert.AreEqual(mainThreadId, tcs.Task.Result,
                $"Final result should match main thread ({mainThreadId}).");
        }
    }
}