using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    internal static class PlayModeTestAwait
    {
        public static IEnumerator WaitTask(Task task, float timeoutSeconds, string operationName)
        {
            var started = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                    Assert.Fail($"Timeout waiting '{operationName}' after {timeoutSeconds:0.##}s.");
                yield return null;
            }

            if (task.IsFaulted)
                Assert.Fail(task.Exception?.GetBaseException().Message ?? $"Task faulted: {operationName}");
        }

        public static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string operationName)
        {
            var started = Time.realtimeSinceStartup;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                    Assert.Fail($"Timeout waiting '{operationName}' after {timeoutSeconds:0.##}s.");
                yield return null;
            }
        }
    }
}
