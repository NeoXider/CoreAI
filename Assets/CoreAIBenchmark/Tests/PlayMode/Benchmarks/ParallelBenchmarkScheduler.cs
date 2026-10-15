#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Steps one coroutine by hand so several can share a single <c>[UnityTest]</c> coroutine. A nested
    /// <see cref="IEnumerator"/> is flattened onto a stack and resumed in the same frame (as Unity starts a
    /// nested coroutine), every other yielded value is kept as <see cref="PendingWait"/>, and a throw from any
    /// level ends only this routine.
    /// </summary>
    internal sealed class CoroutineStepper : IDisposable
    {
        private readonly Stack<IEnumerator> _stack = new();
        private const int MaxMovesPerStep = 1024;

        public CoroutineStepper(IEnumerator routine)
        {
            _stack.Push(routine ?? throw new ArgumentNullException(nameof(routine)));
        }

        public bool IsDone { get; private set; }

        /// <summary>The exception that ended the routine, or null.</summary>
        public Exception Error { get; private set; }

        /// <summary>The value the routine last yielded (null = resume next frame).</summary>
        public object PendingWait { get; private set; }

        /// <summary>True when the routine wants to resume after rendering finished this frame.</summary>
        public bool WaitsForEndOfFrame => !IsDone && PendingWait is WaitForEndOfFrame;

        /// <summary>
        /// True when the routine may resume in this frame's update. Unsupported instructions fail the routine
        /// rather than silently shortening a timed wait; use <see cref="WaitForSecondsRealtime"/> for delays.
        /// </summary>
        public bool IsReadyForUpdate
        {
            get
            {
                if (IsDone)
                {
                    return false;
                }

                return PendingWait switch
                {
                    null => true,
                    WaitForEndOfFrame => false,
                    CustomYieldInstruction custom => !custom.keepWaiting,
                    AsyncOperation operation => operation.isDone,
                    _ => throw new NotSupportedException(
                        $"Unsupported benchmark coroutine wait: {PendingWait.GetType().Name}. Use null or WaitForSecondsRealtime.")
                };
            }
        }

        /// <summary>Runs the routine until its next yield; a nested enumerator starts immediately.</summary>
        public void Step()
        {
            if (IsDone)
            {
                return;
            }

            PendingWait = null;
            int moves = 0;
            while (_stack.Count > 0 && moves++ < MaxMovesPerStep)
            {
                IEnumerator top = _stack.Peek();
                bool moved;
                object current;
                try
                {
                    moved = top.MoveNext();
                    current = moved ? top.Current : null;
                    if (!moved)
                    {
                        _stack.Pop();
                        (top as IDisposable)?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Error = ex;
                    Finish();
                    return;
                }

                if (!moved)
                {
                    continue;
                }

                // WHY: custom waits implement IEnumerator too; keeping them pending lets the scheduler
                // check the target deadline even while keepWaiting stays true forever.
                if (current is IEnumerator nested && current is not CustomYieldInstruction)
                {
                    _stack.Push(nested);
                    continue;
                }

                PendingWait = current;
                return;
            }

            IsDone = _stack.Count == 0;
        }

        /// <summary>Abandons the routine, running the <c>finally</c> blocks of every unfinished level.</summary>
        public void Dispose()
        {
            Finish();
        }

        /// <summary>Ends a suspended routine with a scheduler fault, releasing every coroutine level.</summary>
        public void Abort(Exception error)
        {
            Error = error;
            Finish();
        }

        private void Finish()
        {
            while (_stack.Count > 0)
            {
                IEnumerator level = _stack.Pop();
                try
                {
                    (level as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    Error ??= ex;
                }
            }

            PendingWait = null;
            IsDone = true;
        }
    }

    /// <summary>
    /// Runs several benchmark coroutines inside one PlayMode coroutine on the Unity main thread. No threads:
    /// every Unity API call stays where it already ran, and only the model requests overlap.
    /// </summary>
    internal static class CooperativeScheduler
    {
        /// <summary>
        /// Runs every routine to completion, at most <paramref name="maxConcurrent"/> at a time, starting them in
        /// list order. Each frame steps every routine whose wait is over; a routine waiting for the end of the
        /// frame is stepped after this coroutine itself yields <see cref="WaitForEndOfFrame"/>, so render-texture
        /// reads after a camera render keep their meaning. A routine that throws is passed to
        /// <paramref name="onFault"/> with its index; the others keep running.
        /// </summary>
        public static IEnumerator RunAll(IReadOnlyList<Func<IEnumerator>> routines, int maxConcurrent,
            Action<int, Exception> onFault, Func<int, Exception> interrupt = null)
        {
            int count = routines?.Count ?? 0;
            int limit = maxConcurrent <= 0 ? count : Math.Min(maxConcurrent, count);
            List<(int Index, CoroutineStepper Stepper)> running = new();
            int next = 0;
            try
            {
                while (next < count || running.Count > 0)
                {
                    while (running.Count < limit && next < count)
                    {
                        int index = next++;
                        try
                        {
                            running.Add((index, new CoroutineStepper(routines[index]())));
                        }
                        catch (Exception ex)
                        {
                            onFault?.Invoke(index, ex);
                        }
                    }

                    foreach ((int Index, CoroutineStepper Stepper) entry in running.ToArray())
                    {
                        try
                        {
                            Exception interruption = interrupt?.Invoke(entry.Index);
                            if (interruption != null)
                            {
                                throw interruption;
                            }

                            if (entry.Stepper.IsReadyForUpdate)
                            {
                                entry.Stepper.Step();
                            }
                        }
                        catch (Exception ex)
                        {
                            entry.Stepper.Abort(ex);
                        }
                    }

                    Reap(running, onFault);
                    if (running.Exists(entry => entry.Stepper.WaitsForEndOfFrame))
                    {
                        // WHY: Editor batch mode never resumes WaitForEndOfFrame. Reading the previous
                        // frame on the next update keeps all other targets and their deadlines alive.
                        yield return Application.isBatchMode ? null : new WaitForEndOfFrame();
                        foreach ((int Index, CoroutineStepper Stepper) entry in running.ToArray())
                        {
                            if (entry.Stepper.WaitsForEndOfFrame)
                            {
                                entry.Stepper.Step();
                            }
                        }

                        Reap(running, onFault);
                    }

                    if (next < count || running.Count > 0)
                    {
                        yield return null;
                    }
                }
            }
            finally
            {
                foreach ((int Index, CoroutineStepper Stepper) entry in running)
                {
                    entry.Stepper.Dispose();
                }
            }
        }

        private static void Reap(List<(int Index, CoroutineStepper Stepper)> running, Action<int, Exception> onFault)
        {
            for (int i = running.Count - 1; i >= 0; i--)
            {
                if (!running[i].Stepper.IsDone)
                {
                    continue;
                }

                if (running[i].Stepper.Error != null)
                {
                    onFault?.Invoke(running[i].Index, running[i].Stepper.Error);
                }

                running.RemoveAt(i);
            }
        }
    }

    /// <summary>One continuous ownership of the <see cref="BenchmarkVisualLane"/>.</summary>
    internal readonly struct VisualLaneHold
    {
        public VisualLaneHold(object owner, long startTimestamp, long endTimestamp)
        {
            Owner = owner;
            StartTimestamp = startTimestamp;
            EndTimestamp = endTimestamp;
        }

        public object Owner { get; }

        /// <summary><see cref="Stopwatch.GetTimestamp"/> when the owner entered.</summary>
        public long StartTimestamp { get; }

        /// <summary><see cref="Stopwatch.GetTimestamp"/> when the owner left; 0 while it still holds the lane.</summary>
        public long EndTimestamp { get; }
    }

    /// <summary>
    /// Admits one shared-scene scenario or report capture at a time across parallel targets.
    /// <para>
    /// WHY a lane rather than separate scenes: the Rbx binder places every part at the model's own world
    /// coordinates (<c>RbxSpace</c> has no per-host origin), so two castles would overlap at the origin, bump each
    /// other's unanchored bodies and cast shadows into each other's frames; the agent camera service resolves its
    /// camera with a global search; captures switch <c>RenderSettings</c> ambient lighting and add directional
    /// lights; and the model card renders at the origin. Holding the lane makes the owner the only one that
    /// creates scene objects, so nothing another target built can appear in its screenshots or camera frames.
    /// </para>
    /// <para>
    /// Waiters keep a FIFO place in line; a priority waiter (a short report capture) goes ahead of the queued
    /// scenarios. An opportunistic <see cref="TryEnter"/> without <c>joinQueue</c> never jumps the line.
    /// </para>
    /// </summary>
    internal sealed class BenchmarkVisualLane
    {
        private readonly List<object> _queue = new();
        private readonly HashSet<object> _priorityWaiters = new();
        private readonly List<VisualLaneHold> _holds = new();
        private long _holderSince;

        /// <summary>The current owner, or null when the lane is free.</summary>
        public object Holder { get; private set; }

        public int QueueLength => _queue.Count;

        /// <summary>Every completed or current hold, oldest first.</summary>
        public IReadOnlyList<VisualLaneHold> Holds
        {
            get
            {
                List<VisualLaneHold> holds = new(_holds);
                if (Holder != null)
                {
                    holds.Add(new VisualLaneHold(Holder, _holderSince, 0));
                }

                return holds;
            }
        }

        /// <summary>
        /// Takes the lane when it is free and nobody queued ahead of <paramref name="owner"/>. With
        /// <paramref name="joinQueue"/> a refused caller keeps its place in line (ahead of the plain waiters with
        /// <paramref name="priority"/>); without it a refused attempt leaves no trace.
        /// </summary>
        public bool TryEnter(object owner, bool joinQueue, bool priority = false)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (ReferenceEquals(Holder, owner))
            {
                throw new InvalidOperationException("The visual lane is not re-entrant.");
            }

            int position = _queue.IndexOf(owner);
            if (Holder == null && (_queue.Count == 0 || position == 0))
            {
                if (position == 0)
                {
                    _queue.RemoveAt(0);
                    _priorityWaiters.Remove(owner);
                }

                Holder = owner;
                _holderSince = Stopwatch.GetTimestamp();
                return true;
            }

            if (joinQueue && position < 0)
            {
                if (priority)
                {
                    int insertAt = 0;
                    while (insertAt < _queue.Count && _priorityWaiters.Contains(_queue[insertAt]))
                    {
                        insertAt++;
                    }

                    _queue.Insert(insertAt, owner);
                    _priorityWaiters.Add(owner);
                }
                else
                {
                    _queue.Add(owner);
                }
            }

            return false;
        }

        /// <summary>Waits (one check per frame) until <paramref name="owner"/> holds the lane.</summary>
        public IEnumerator Enter(object owner, bool priority = false)
        {
            bool entered = false;
            try
            {
                while (!(entered = TryEnter(owner, true, priority)))
                {
                    yield return null;
                }
            }
            finally
            {
                if (!entered)
                {
                    Leave(owner);
                }
            }
        }

        /// <summary>Releases the lane; only the holder may call it.</summary>
        public void Exit(object owner)
        {
            if (!ReferenceEquals(Holder, owner))
            {
                throw new InvalidOperationException("Only the holder can leave the visual lane.");
            }

            _holds.Add(new VisualLaneHold(owner, _holderSince, Stopwatch.GetTimestamp()));
            Holder = null;
        }

        /// <summary>
        /// Drops <paramref name="owner"/> from the line, and releases the lane when it holds it. WHY: a waiter that
        /// stops (Stop button, budget, fault) would otherwise sit at the head of the queue and block everyone.
        /// </summary>
        public void Leave(object owner)
        {
            if (owner == null)
            {
                return;
            }

            _queue.Remove(owner);
            _priorityWaiters.Remove(owner);
            if (ReferenceEquals(Holder, owner))
            {
                Exit(owner);
            }
        }
    }
}
#endif
#endif
