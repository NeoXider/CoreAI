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

                    string stillRunning = "";
                    if (!task.IsCompleted)
                    {
                        ObserveFaultsOfAbandonedTask(task);
                        stillRunning = cancellationOnTimeout == null
                            ? " The task is STILL RUNNING and could not be cancelled: no CancellationTokenSource " +
                              "was passed to WaitTask, so it leaks into the following tests."
                            : " The task did not observe cancellation within 5s and is still running.";
                    }

                    Assert.Fail($"Timeout waiting '{operationName}' after {timeoutSeconds:0.##}s.{stillRunning}");
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

        /// <summary>
        /// Swallows the eventual failure of a task the wait gave up on, so it cannot resurface as an
        /// unobserved task exception inside an unrelated later test.
        /// </summary>
        private static void ObserveFaultsOfAbandonedTask(Task task)
        {
            task.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
