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
        [UnityTest]
        public IEnumerator AfterSwitchToThreadPool_InvokeAsync_RunsDelegateOnUnityMainManagedThread()
        {
            int mainCapturedAtTestStart = Thread.CurrentThread.ManagedThreadId;
            var tcs = new TaskCompletionSource<int>();

            UniTask.Void(async () =>
            {
                try
                {
                    await UniTask.SwitchToThreadPool();

#if !UNITY_WEBGL
                    Assert.AreNotEqual(mainCapturedAtTestStart, Thread.CurrentThread.ManagedThreadId,
                        "Fixture must leave the synchronous Unity test thread via SwitchToThreadPool.");
#endif

                    int inFactory =
                        await UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                            () => Task.FromResult(Thread.CurrentThread.ManagedThreadId),
                            CancellationToken.None);

                    Assert.AreEqual(mainCapturedAtTestStart, inFactory);
                    tcs.TrySetResult(inFactory);
                }
                catch (System.Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            yield return new WaitUntil(() => tcs.Task.IsCompleted);
            Assert.IsFalse(tcs.Task.IsFaulted, tcs.Task.IsFaulted ? tcs.Task.Exception?.ToString() : "");
            Assert.AreEqual(mainCapturedAtTestStart, tcs.Task.Result);
        }
    }
}
