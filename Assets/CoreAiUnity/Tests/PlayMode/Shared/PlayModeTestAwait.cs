using System;
using System.Collections;
using System.Collections.Generic;
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
                        ObserveFaults(task);
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
        /// Swallows the eventual failure of a task nobody awaits any more (a wait that gave up, a request
        /// cancelled in teardown), so it cannot resurface as an unobserved task exception inside an unrelated
        /// later test.
        /// </summary>
        internal static void ObserveFaults(Task task)
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

    /// <summary>
    /// Owns the cancellation sources, the in-flight requests and the wall-clock budget of one live PlayMode test.
    /// <para>
    /// When the Unity Test Framework reaches a test's <c>[Timeout]</c> (180 s when none is declared) it stops
    /// driving the test coroutine and never disposes it, so no <c>finally</c> or <c>using</c> in the test body
    /// runs. A <c>using CancellationTokenSource</c> local inside that coroutine is then never cancelled - and
    /// <see cref="CancellationTokenSource.Dispose()"/> does not cancel anyway - so the in-flight request keeps
    /// running into the next test. For the same reason handles, settings, setups and subscriptions the request
    /// uses belong in fields that <c>[UnityTearDown]</c> disposes after the drain. Tests take their sources from
    /// <see cref="CreateCancellation"/>, pass every wait through <see cref="Cap"/> so the test's own cancelling
    /// wait fires before the framework's, register the request tasks with <see cref="Track{T}"/>, and run
    /// <see cref="CancelAllAndDrain"/> (or at least <see cref="CancelAll"/>) first thing in <c>[UnityTearDown]</c>.
    /// </para>
    /// <para>
    /// Cancellation is client-side: it stops this process's request, while a serial provider bridge may
    /// still finish the abandoned turn before it serves the next one.
    /// </para>
    /// </summary>
    public sealed class LiveTestRequestScope
    {
        /// <summary>
        /// Seconds held back from the framework budget: the 5 s cancellation grace of
        /// <see cref="PlayModeTestAwait"/>, the assertions after the wait, the teardown drain, and the framework
        /// clock that already ran before this scope was created.
        /// </summary>
        public const float ReserveSeconds = 20f;

        /// <summary>Upper bound of the teardown wait for cancelled requests in <see cref="CancelAllAndDrain"/>.</summary>
        public const float DrainSeconds = 5f;

        private readonly List<CancellationTokenSource> _sources = new();
        private readonly List<Task> _tracked = new();
        private readonly float _deadline;

        /// <summary>
        /// Starts the budget now. Create it at the start of [UnitySetUp] or the test body, passing the same
        /// constant the test's <c>[Timeout]</c> attribute uses.
        /// </summary>
        public LiveTestRequestScope(int frameworkTimeoutMilliseconds)
        {
            _deadline = Time.realtimeSinceStartup + frameworkTimeoutMilliseconds / 1000f - ReserveSeconds;
        }

        /// <summary>Seconds left before the reserve; never negative.</summary>
        public float SecondsLeft => Mathf.Max(0f, _deadline - Time.realtimeSinceStartup);

        /// <summary>Returns <paramref name="desiredSeconds"/> clamped to [0, <see cref="SecondsLeft"/>].</summary>
        public float Cap(float desiredSeconds)
        {
            return Mathf.Clamp(desiredSeconds, 0f, SecondsLeft);
        }

        /// <summary>Creates a source that <see cref="CancelAll"/> cancels and disposes; do not dispose it yourself.</summary>
        public CancellationTokenSource CreateCancellation()
        {
            CancellationTokenSource source = new();
            _sources.Add(source);
            return source;
        }

        /// <summary>
        /// Registers an in-flight request so teardown observes its fault and can wait for it to wind down;
        /// returns <paramref name="task"/> unchanged.
        /// </summary>
        public T Track<T>(T task) where T : Task
        {
            if (task != null)
            {
                _tracked.Add(task);
            }

            return task;
        }

        /// <summary>
        /// Cancels and disposes every source, and observes the faults of tracked tasks. Safe to call more than
        /// once. Prefer <see cref="CancelAllAndDrain"/> when the teardown disposes the client next.
        /// </summary>
        public void CancelAll()
        {
            CancelSources();
            ObserveTrackedFaults();
            DisposeSources();
        }

        /// <summary>
        /// Cancels every source, then yields until the tracked tasks finish (at most <paramref name="maxSeconds"/>)
        /// before disposing the sources. WHY: a cancelled pipeline unwinds on later frames; disposing the client
        /// in the same frame lets those continuations run against a disposed handle.
        /// </summary>
        public IEnumerator CancelAllAndDrain(float maxSeconds = DrainSeconds)
        {
            CancelSources();
            ObserveTrackedFaults();

            float started = Time.realtimeSinceStartup;
            while (HasRunningTrackedTask() && Time.realtimeSinceStartup - started < maxSeconds)
            {
                yield return null;
            }

            if (HasRunningTrackedTask())
            {
                Debug.LogWarning($"[LiveTestRequestScope] a cancelled request was still running after {maxSeconds:0.#}s; " +
                                 "teardown continues and its fault stays observed.");
            }

            DisposeSources();
            _tracked.Clear();
        }

        private bool HasRunningTrackedTask()
        {
            foreach (Task task in _tracked)
            {
                if (!task.IsCompleted)
                {
                    return true;
                }
            }

            return false;
        }

        private void ObserveTrackedFaults()
        {
            foreach (Task task in _tracked)
            {
                PlayModeTestAwait.ObserveFaults(task);
            }
        }

        private void CancelSources()
        {
            foreach (CancellationTokenSource source in _sources)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException ex)
                {
                    // WHY: teardown must keep releasing the remaining sources even if one registration throws.
                    Debug.LogWarning($"[LiveTestRequestScope] cancellation callback failed: {ex.GetBaseException().Message}");
                }
            }
        }

        private void DisposeSources()
        {
            foreach (CancellationTokenSource source in _sources)
            {
                source.Dispose();
            }

            _sources.Clear();
        }
    }
}
