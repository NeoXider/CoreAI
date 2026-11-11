using System.Threading;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// The Lua counters a measurement reads: the runtime's own observability port
    /// (<see cref="IRbxRuntimeObservabilitySink"/>), supplied through production composition rather
    /// than read from guard internals. Totals only; a sampler turns them into per-frame and per-second rates.
    /// </summary>
    public sealed class PerfRuntimeCounters : IRbxRuntimeObservabilitySink
    {
        private long _guardedSteps;
        private long _threadResumes;
        private long _eventsDelivered;
        private long _completedOperations;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <summary>Guarded Lua instructions completed guards have charged so far.</summary>
        public long GuardedSteps => Interlocked.Read(ref _guardedSteps);

        /// <summary>Script-thread resumes the Lua adapter completed so far.</summary>
        public long ThreadResumes => Interlocked.Read(ref _threadResumes);

        /// <summary>Signal events delivered to live subscribers so far.</summary>
        public long EventsDelivered => Interlocked.Read(ref _eventsDelivered);

        /// <summary>Lua handler operations the runtime drained so far.</summary>
        public long CompletedOperations => Interlocked.Read(ref _completedOperations);

        /// <inheritdoc />
        public void RecordGuardedInstructionSteps(long count)
        {
            Interlocked.Add(ref _guardedSteps, count);
        }

        /// <inheritdoc />
        public void RecordThreadResumes(long count)
        {
            Interlocked.Add(ref _threadResumes, count);
        }

        /// <inheritdoc />
        public void RecordEventsDelivered(long count)
        {
            Interlocked.Add(ref _eventsDelivered, count);
        }

        /// <inheritdoc />
        public void RecordCompletedOperations(long count)
        {
            Interlocked.Add(ref _completedOperations, count);
        }

        /// <summary>Copies the current totals.</summary>
        public PerfCounterSnapshot Snapshot()
        {
            return new PerfCounterSnapshot(GuardedSteps, ThreadResumes, EventsDelivered, CompletedOperations);
        }
    }

    /// <summary>A point-in-time copy of <see cref="PerfRuntimeCounters"/>.</summary>
    public readonly struct PerfCounterSnapshot
    {
        /// <param name="guardedSteps">Guarded Lua instructions.</param>
        /// <param name="threadResumes">Script-thread resumes.</param>
        /// <param name="eventsDelivered">Signal events delivered.</param>
        /// <param name="completedOperations">Lua handler operations drained.</param>
        public PerfCounterSnapshot(long guardedSteps, long threadResumes, long eventsDelivered, long completedOperations)
        {
            GuardedSteps = guardedSteps;
            ThreadResumes = threadResumes;
            EventsDelivered = eventsDelivered;
            CompletedOperations = completedOperations;
        }

        /// <summary>Guarded Lua instructions.</summary>
        public long GuardedSteps { get; }

        /// <summary>Script-thread resumes.</summary>
        public long ThreadResumes { get; }

        /// <summary>Signal events delivered.</summary>
        public long EventsDelivered { get; }

        /// <summary>Lua handler operations drained.</summary>
        public long CompletedOperations { get; }

        /// <summary>The counts accumulated between <paramref name="earlier"/> and this snapshot.</summary>
        public PerfCounterSnapshot Since(PerfCounterSnapshot earlier)
        {
            return new PerfCounterSnapshot(
                GuardedSteps - earlier.GuardedSteps,
                ThreadResumes - earlier.ThreadResumes,
                EventsDelivered - earlier.EventsDelivered,
                CompletedOperations - earlier.CompletedOperations);
        }
    }
}
