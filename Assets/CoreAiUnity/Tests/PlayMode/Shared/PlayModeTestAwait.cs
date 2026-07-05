using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    public static class PlayModeTestAwait
    {
        public static IEnumerator WaitTask(Task task, float timeoutSeconds, string operationName)
        {
            return WaitTask(task, timeoutSeconds, operationName, null);
        }

        public static IEnumerator WaitTask(
            Task task,
            float timeoutSeconds,
            string operationName,
            CancellationTokenSource cancellationOnTimeout)
        {
            float started = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    cancellationOnTimeout?.Cancel();
                    float cancelStarted = Time.realtimeSinceStartup;
                    while (!task.IsCompleted && Time.realtimeSinceStartup - cancelStarted <= 5f)
                    {
                        yield return null;
                    }

                    Assert.Fail($"Timeout waiting '{operationName}' after {timeoutSeconds:0.##}s.");
                }

                yield return null;
            }

            if (task.IsCanceled)
            {
                Assert.Fail($"Task canceled: {operationName}");
            }

            if (task.IsFaulted)
            {
                Assert.Fail(task.Exception?.GetBaseException().Message ?? $"Task faulted: {operationName}");
            }
        }

        public static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    Assert.Fail($"Timeout waiting '{operationName}' after {timeoutSeconds:0.##}s.");
                }

                yield return null;
            }
        }
    }
}