using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreAI.Mods.Tests")]
[assembly: InternalsVisibleTo("CoreAI.Mods")]

namespace CoreAI.Mods.Rbx.Instances.Scheduling
{
    /// <summary>Roblox frame phases exposed by the engine-free scheduler pipeline.</summary>
    public enum SchedulerPhase
    {
        PreAnimation,
        PreSimulation,
        PostSimulation,
        Heartbeat,
        InputProcessing,
        PreRender
    }

    /// <summary>
    /// Optional capability of an <see cref="IRbxScriptThread"/> whose adapter can end a thread during a
    /// resume that still reports success (for example a lifetime cap that kills the thread after the
    /// slice completed). When such a thread is dead after a successful resume and
    /// <see cref="TerminalFault"/> is not null, <see cref="ModScheduler"/> reports that error through
    /// <see cref="ModScheduler.ThreadFaulted"/> instead of dropping the thread silently.
    /// </summary>
    public interface IRbxScriptThreadTerminalFault
    {
        /// <summary>
        /// The structured error that ended the thread inside a resume reported as successful, or null
        /// when the thread completed normally or its failure is surfaced through another channel.
        /// </summary>
        RbxError TerminalFault { get; }
    }

    /// <summary>
    /// Deterministic, engine-free scheduler for immediate, deferred, waiting, delayed, and
    /// host-completion-backed script threads. One <see cref="Advance"/> call executes one complete
    /// logical frame in the canonical R4.2 order.
    /// <para>
    /// Fault containment: a failure inside one callback of the frame (a thread that faults, a thread
    /// resumed after it died outside the scheduler, a signal handler that throws, a signal cascade or
    /// fan-out over budget, a throwing <see cref="PhaseReached"/> subscriber or host callback) is
    /// contained where it happens. Only the offending thread, invocation or chain is dropped; every
    /// later phase, thread and mod still runs in the same frame. A failure owned by a mod is reported
    /// through <see cref="ThreadFaulted"/>, anything else through <see cref="HostFaulted"/>. A failure
    /// nobody observes (no subscriber on its event) is rethrown by <see cref="Advance"/> once the frame
    /// has completed, so it can never disappear silently.
    /// </para>
    /// </summary>
    public sealed class ModScheduler
    {
        public const int DefaultMaxThreadsPerActor = 256;
        public const int EmergencyMaxThreads = 4096;
        public const int MaxSignalGenerations = 10;

        /// <summary>
        /// Default number of signal handler invocations one owning mod may queue within one resumption
        /// point before the rest are dropped and the mod is faulted (M2-12: the generation cap limits a
        /// cascade's depth, this limits its width).
        /// </summary>
        public const int DefaultMaxSignalInvocationsPerOwner = 16384;

        /// <summary>Default ceiling on signal invocations waiting in the queue across all owners.</summary>
        public const int DefaultMaxQueuedSignalInvocations = 65536;

        /// <summary>
        /// Upper bound on {deferred threads, signal handlers} drain rounds at one resumption point.
        /// Work a handler defers runs in the same resumption point (R4.8); work left after the last round
        /// runs at the next resumption point, exactly as it did before rounds existed.
        /// </summary>
        public const int MaxDrainRoundsPerResumptionPoint = 10;

        private const int MinStaleTimedEntriesBeforeCompaction = 64;

        // WHY a NUL character: mod ids are author-visible names and can never contain one, so the
        // shared budget of connections no mod owns can never be confused with a real mod's budget.
        private const string HostSignalChargeKey = "\0host";

        private enum PipelineStage
        {
            ResumptionPoint,
            PreAnimation,
            PreSimulation,
            PostSimulation,
            ResumeDelayed,
            Heartbeat,
            InputProcessing,
            PreRender
        }

        private enum ThreadScheduleState
        {
            Idle,
            Running,
            Deferred,
            Waiting,
            Delayed,
            WaitingForCompletion,
            WaitingForSignal,
            Canceled
        }

        private sealed class ThreadReferenceComparer : IEqualityComparer<IRbxScriptThread>
        {
            public static readonly ThreadReferenceComparer Instance = new();

            public bool Equals(IRbxScriptThread x, IRbxScriptThread y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(IRbxScriptThread obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        /// <summary>
        /// Scheduler bookkeeping for one live thread. Records are pooled only when their thread died
        /// inside the resume that started it (see <see cref="TryReleaseRecord"/>); <see cref="Reset"/>
        /// and <see cref="Clear"/> are the complete per-field policy for a reused record.
        /// </summary>
        private sealed class ThreadRecord
        {
            public ThreadRecord(IRbxScriptThread thread, string ownerModId)
            {
                Reset(thread, ownerModId);
            }

            public IRbxScriptThread Thread { get; private set; }

            public string OwnerModId { get; private set; }

            public ThreadScheduleState State { get; set; }

            public object[] DeferredArguments { get; set; }

            public CompletionWaitEntry CompletionWait { get; set; }

            public RbxInstance ReadableTombstone { get; set; }

            public long SignalWaitGeneration { get; set; }

            /// <summary>
            /// True while exactly one live wait, delay or signal-timeout heap entry targets this record.
            /// Cleared when that entry is popped or becomes stale, so stale heap entries can be counted
            /// and compacted lazily instead of scanning every heap on each cancel (M2-26).
            /// </summary>
            public bool HasTimedEntry { get; set; }

            /// <summary>
            /// Sequence of the one timed entry allowed to resume this record; 0 when none. A heap entry
            /// with any other sequence is stale, so a rescheduled thread can never be resumed twice by
            /// the entry it left behind (M2-14).
            /// </summary>
            public long TimedSequence { get; set; }

            /// <summary>Sequence of the one deferred-queue entry allowed to resume this record; 0 when none.</summary>
            public long DeferredSequence { get; set; }

            /// <summary>
            /// Actor whose induced-thread budget this record is charged to instead of its owner's actor
            /// quota (MP-10: a handler started by another actor's remote call, and every thread that
            /// handler starts in turn); null for every other thread.
            /// </summary>
            public string QuotaActorId { get; set; }

            /// <summary>
            /// Live threads <see cref="QuotaActorId"/>'s budget allows; the threads this one starts are
            /// held to the same number. Zero when the record is charged to its owner.
            /// </summary>
            public int QuotaLimit { get; set; }

            /// <summary>
            /// The message of the last induced-budget refusal raised inside this thread, so the thread's
            /// death by that refusal is told apart from a fault of its owner's code; null when none.
            /// </summary>
            public string InducedRefusalMessage { get; set; }

            /// <summary>Re-arms a record for a new thread and owner; every per-thread field restarts.</summary>
            public void Reset(IRbxScriptThread thread, string ownerModId)
            {
                Thread = thread;
                OwnerModId = ownerModId;
                State = ThreadScheduleState.Idle;
                DeferredArguments = null;
                CompletionWait = null;
                ReadableTombstone = null;
                HasTimedEntry = false;
                TimedSequence = 0;
                DeferredSequence = 0;
                QuotaActorId = null;
                QuotaLimit = 0;
                InducedRefusalMessage = null;
                // WHY: SignalWaitGeneration keeps counting across tenants on purpose. A timeout entry is
                // matched by (record, generation); a monotonic counter can never re-produce a value an
                // earlier tenant used, so a stale entry can never resume a later tenant.
            }

            /// <summary>Drops every reference before the record waits in the pool.</summary>
            public void Clear()
            {
                Thread = null;
                OwnerModId = null;
                State = ThreadScheduleState.Idle;
                DeferredArguments = null;
                CompletionWait = null;
                ReadableTombstone = null;
                HasTimedEntry = false;
                TimedSequence = 0;
                DeferredSequence = 0;
                QuotaActorId = null;
                QuotaLimit = 0;
                InducedRefusalMessage = null;
            }
        }

        /// <summary>
        /// One deferred-queue slot. Only the slot whose sequence the record still names may resume it,
        /// so a thread moved out of the queue (M2-14) leaves a harmless stale slot behind.
        /// </summary>
        private readonly struct DeferredEntry
        {
            public DeferredEntry(ThreadRecord record, long sequence)
            {
                Record = record;
                Sequence = sequence;
            }

            public ThreadRecord Record { get; }

            public long Sequence { get; }
        }

        private readonly struct PendingSignalFault
        {
            public PendingSignalFault(string ownerModId, RbxError error)
            {
                OwnerModId = ownerModId;
                Error = error;
            }

            public string OwnerModId { get; }

            public RbxError Error { get; }
        }

        private sealed class SignalInvocation
        {
            public SignalInvocation(RbxScriptConnection connection, object[] arguments,
                RbxInstance readableTombstone, int generation, string[] chain, string quotaActorId)
            {
                Connection = connection;
                Arguments = arguments;
                ReadableTombstone = readableTombstone;
                Generation = generation;
                Chain = chain;
                QuotaActorId = quotaActorId;
            }

            public RbxScriptConnection Connection { get; }

            public object[] Arguments { get; }

            public RbxInstance ReadableTombstone { get; }

            public int Generation { get; }

            public string[] Chain { get; }

            /// <summary>Actor on whose behalf the fire happened (see <see cref="BeginSignalsOnBehalfOf"/>).</summary>
            public string QuotaActorId { get; }
        }

        private abstract class TimedEntry
        {
            protected TimedEntry(ThreadRecord record, double deadline, long earliestFrame, long sequence)
            {
                Record = record;
                Deadline = deadline;
                EarliestFrame = earliestFrame;
                Sequence = sequence;
            }

            public ThreadRecord Record { get; }

            public double Deadline { get; }

            public long EarliestFrame { get; }

            public long Sequence { get; }
        }

        private sealed class WaitEntry : TimedEntry
        {
            public WaitEntry(ThreadRecord record, double scheduledAt, double deadline,
                long earliestFrame, long sequence)
                : base(record, deadline, earliestFrame, sequence)
            {
                ScheduledAt = scheduledAt;
            }

            public double ScheduledAt { get; }
        }

        private sealed class DelayEntry : TimedEntry
        {
            public DelayEntry(ThreadRecord record, object[] arguments, double deadline,
                long earliestFrame, long sequence)
                : base(record, deadline, earliestFrame, sequence)
            {
                Arguments = arguments;
            }

            public object[] Arguments { get; }
        }

        private sealed class SignalWaitTimeoutEntry : TimedEntry
        {
            public SignalWaitTimeoutEntry(ThreadRecord record, long generation,
                Func<object[]> resumeArguments, double deadline, long earliestFrame, long sequence)
                : base(record, deadline, earliestFrame, sequence)
            {
                Generation = generation;
                ResumeArguments = resumeArguments;
            }

            public long Generation { get; }

            public Func<object[]> ResumeArguments { get; }
        }

        private sealed class HostCallbackEntry : TimedEntry
        {
            public HostCallbackEntry(Action callback, double deadline,
                long earliestFrame, long sequence)
                : base(null, deadline, earliestFrame, sequence)
            {
                Callback = callback;
            }

            public Action Callback { get; }
        }

        private sealed class CompletionWaitEntry
        {
            public CompletionWaitEntry(ThreadRecord record, RbxSchedulerCompletion completion,
                long sequence)
            {
                Record = record;
                Completion = completion;
                Sequence = sequence;
            }

            public ThreadRecord Record { get; }

            public RbxSchedulerCompletion Completion { get; }

            public long Sequence { get; }

            public bool IsSignaled { get; set; }
        }

        private sealed class MinHeap<T>
        {
            private readonly Comparison<T> _comparison;
            private readonly List<T> _items = new();

            public MinHeap(Comparison<T> comparison)
            {
                _comparison = comparison;
            }

            public int Count => _items.Count;

            public void Add(T item)
            {
                _items.Add(item);
                SiftUp(_items.Count - 1);
            }

            public T Peek()
            {
                return _items[0];
            }

            public T Pop()
            {
                T root = _items[0];
                int lastIndex = _items.Count - 1;
                T last = _items[lastIndex];
                _items.RemoveAt(lastIndex);
                if (_items.Count > 0)
                {
                    _items[0] = last;
                    SiftDown(0);
                }

                return root;
            }

            public int RemoveWhere(Predicate<T> predicate)
            {
                int touchedCount = _items.Count;
                int writeIndex = 0;
                for (int readIndex = 0; readIndex < _items.Count; readIndex++)
                {
                    T item = _items[readIndex];
                    if (!predicate(item))
                    {
                        _items[writeIndex] = item;
                        writeIndex++;
                    }
                }

                if (writeIndex == _items.Count)
                {
                    return touchedCount;
                }

                _items.RemoveRange(writeIndex, _items.Count - writeIndex);
                for (int index = (_items.Count / 2) - 1; index >= 0; index--)
                {
                    SiftDown(index);
                }

                return touchedCount;
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parentIndex = (index - 1) / 2;
                    if (_comparison(_items[index], _items[parentIndex]) >= 0)
                    {
                        return;
                    }

                    T item = _items[index];
                    _items[index] = _items[parentIndex];
                    _items[parentIndex] = item;
                    index = parentIndex;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    int leftIndex = (index * 2) + 1;
                    if (leftIndex >= _items.Count)
                    {
                        return;
                    }

                    int rightIndex = leftIndex + 1;
                    int smallestIndex = rightIndex < _items.Count
                        && _comparison(_items[rightIndex], _items[leftIndex]) < 0
                        ? rightIndex
                        : leftIndex;
                    if (_comparison(_items[smallestIndex], _items[index]) >= 0)
                    {
                        return;
                    }

                    T item = _items[index];
                    _items[index] = _items[smallestIndex];
                    _items[smallestIndex] = item;
                    index = smallestIndex;
                }
            }
        }

        private static readonly object[] EmptyArguments = Array.Empty<object>();
        private static readonly PipelineStage[] Pipeline =
        {
            PipelineStage.ResumptionPoint,
            PipelineStage.PreAnimation,
            PipelineStage.ResumptionPoint,
            PipelineStage.PreSimulation,
            PipelineStage.ResumptionPoint,
            PipelineStage.PostSimulation,
            PipelineStage.ResumptionPoint,
            PipelineStage.ResumeDelayed,
            PipelineStage.ResumptionPoint,
            PipelineStage.Heartbeat,
            PipelineStage.ResumptionPoint,
            PipelineStage.InputProcessing,
            PipelineStage.ResumptionPoint,
            PipelineStage.PreRender,
            PipelineStage.ResumptionPoint
        };

        private static readonly string[] PhaseSubscriberSources =
        {
            "PhaseReached(PreAnimation) subscriber",
            "PhaseReached(PreSimulation) subscriber",
            "PhaseReached(PostSimulation) subscriber",
            "PhaseReached(Heartbeat) subscriber",
            "PhaseReached(InputProcessing) subscriber",
            "PhaseReached(PreRender) subscriber"
        };

        /// <summary>Idle records kept for reuse; anything beyond this is left to the GC as before.</summary>
        internal const int MaxPooledRecords = 64;

        private readonly IRbxScriptThreadFactory _threadFactory;
        private readonly IRbxTimeSource _timeSource;
        private readonly Dictionary<IRbxScriptThread, ThreadRecord> _records =
            new(ThreadReferenceComparer.Instance);
        private readonly Stack<ThreadRecord> _recordPool = new();
        private readonly Queue<DeferredEntry> _deferredQueue = new();
        private readonly List<DeferredEntry> _drainBuffer = new();
        private readonly Dictionary<string, int> _inducedThreadsByQuotaActor = new(StringComparer.Ordinal);
        private readonly Queue<SignalInvocation> _signalQueue = new();
        private readonly List<SignalInvocation> _signalDrainBuffer = new();
        private readonly List<TimedEntry> _delayedBatchBuffer = new();
        private readonly object _completionGate = new();
        private readonly Dictionary<RbxSchedulerCompletion, CompletionWaitEntry>
            _completionRegistrations = new();
        private readonly SortedDictionary<long, CompletionWaitEntry> _readyCompletions = new();
        private readonly List<CompletionWaitEntry> _completionBuffer = new();
        private readonly MinHeap<WaitEntry> _waitHeap;
        private readonly MinHeap<DelayEntry> _delayHeap;
        private readonly MinHeap<SignalWaitTimeoutEntry> _signalWaitTimeoutHeap;
        private readonly MinHeap<HostCallbackEntry> _hostHeap;
        private readonly Predicate<WaitEntry> _isStaleWait;
        private readonly Predicate<DelayEntry> _isStaleDelay;
        private readonly Predicate<SignalWaitTimeoutEntry> _isStaleSignalTimeout;
        private readonly Dictionary<string, int> _signalChargeByOwner = new(StringComparer.Ordinal);
        private readonly HashSet<string> _signalOverloadReportedOwners = new(StringComparer.Ordinal);
        private readonly List<PendingSignalFault> _pendingSignalFaults = new();
        private readonly object _subscriberGate = new();
        private Func<string, string> _actorIdResolver;
        private Action<SchedulerPhase, double> _phaseReached;
        private Action<SchedulerPhase, double>[] _phaseReachedSubscribers =
            Array.Empty<Action<SchedulerPhase, double>>();
        private Action<string, RbxError> _threadFaulted;
        private Action<string, RbxError>[] _threadFaultedSubscribers =
            Array.Empty<Action<string, RbxError>>();
        private Action<string, bool> _threadResumeSucceeded;
        private Action<string, bool>[] _threadResumeSucceededSubscribers =
            Array.Empty<Action<string, bool>>();
        private Action<string, Exception> _hostFaulted;
        private Action<string, Exception>[] _hostFaultedSubscribers =
            Array.Empty<Action<string, Exception>>();
        private Action<IRbxScriptThread> _threadRetired;
        private Action<IRbxScriptThread>[] _threadRetiredSubscribers =
            Array.Empty<Action<IRbxScriptThread>>();

        private long _frameIndex;
        private long _sequence;
        private bool _advancing;
        private bool _delayedBatchStarted;
        private bool _promotingCompletions;
        private bool _drainingSignals;
        private bool _flushingSignalFaults;
        private bool _signalOverloadReportedForHost;
        private int _currentSignalGeneration;
        private int _staleTimedEntries;
        private string[] _currentSignalChain;
        private string _currentInvocationOwnerModId;
        private string _currentInvocationQuotaActorId;
        private string _enqueueQuotaActorId;
        private string _runningOwnerModId;
        private ThreadRecord _runningRecord;
        private Action<string, RbxError> _inducedThreadRefused;
        private RbxInstance _currentSignalTombstone;
        private Exception _heldFault;
        private PipelineStage? _currentStage;

        /// <summary>Configured per-actor live-thread quota.</summary>
        public int MaxThreadsPerActor { get; private set; } = DefaultMaxThreadsPerActor;

        /// <summary>
        /// Signal handler invocations one owning mod may queue within one resumption point; see
        /// <see cref="ConfigureSignalBudget"/>.
        /// </summary>
        public int MaxSignalInvocationsPerOwner { get; private set; } = DefaultMaxSignalInvocationsPerOwner;

        /// <summary>Ceiling on queued signal invocations across all owners; see <see cref="ConfigureSignalBudget"/>.</summary>
        public int MaxQueuedSignalInvocations { get; private set; } = DefaultMaxQueuedSignalInvocations;

        public ModScheduler(IRbxScriptThreadFactory threadFactory, IRbxTimeSource timeSource)
        {
            if (threadFactory == null)
            {
                throw RbxError.BadArgument(
                    "ModScheduler requires an IRbxScriptThreadFactory",
                    "inject the scripting adapter's thread factory");
            }

            if (timeSource == null)
            {
                throw RbxError.BadArgument(
                    "ModScheduler requires an IRbxTimeSource",
                    "inject a deterministic scaled-time source");
            }

            _threadFactory = threadFactory;
            _timeSource = timeSource;
            ValidateClock(_timeSource.CurrentTime, "initial");
            _waitHeap = new MinHeap<WaitEntry>(
                (WaitEntry left, WaitEntry right) => CompareTimedEntries(left, right));
            _delayHeap = new MinHeap<DelayEntry>(
                (DelayEntry left, DelayEntry right) => CompareTimedEntries(left, right));
            _signalWaitTimeoutHeap = new MinHeap<SignalWaitTimeoutEntry>(
                (SignalWaitTimeoutEntry left, SignalWaitTimeoutEntry right) =>
                    CompareTimedEntries(left, right));
            _hostHeap = new MinHeap<HostCallbackEntry>(
                (HostCallbackEntry left, HostCallbackEntry right) =>
                    CompareTimedEntries(left, right));
            _isStaleWait = entry => !IsLiveInState(entry.Record, ThreadScheduleState.Waiting)
                                    || entry.Record.TimedSequence != entry.Sequence;
            _isStaleDelay = entry => !IsLiveInState(entry.Record, ThreadScheduleState.Delayed)
                                     || entry.Record.TimedSequence != entry.Sequence;
            _isStaleSignalTimeout = entry =>
                !IsLiveInState(entry.Record, ThreadScheduleState.WaitingForSignal)
                || entry.Record.SignalWaitGeneration != entry.Generation
                || entry.Record.TimedSequence != entry.Sequence;
        }

        /// <summary>Configures actor attribution and the per-actor live-thread quota.</summary>
        public void ConfigureActorQuota(int maxThreadsPerActor, Func<string, string> actorIdResolver)
        {
            MaxThreadsPerActor = Math.Max(1, maxThreadsPerActor);
            _actorIdResolver = actorIdResolver;
        }

        /// <summary>
        /// Configures the signal fan-out budget (M2-12). <paramref name="maxInvocationsPerOwner"/> caps
        /// the handler invocations queued for connections of one owning mod within one resumption point
        /// (connections no mod owns share one such budget); <paramref name="maxQueuedInvocations"/> caps
        /// the whole queue. An invocation over either limit is dropped and its owner receives one
        /// <see cref="RbxErrorCode.BudgetExceeded"/> fault per resumption point through
        /// <see cref="ThreadFaulted"/> (<see cref="HostFaulted"/> for connections no mod owns). Both
        /// values are clamped to at least one.
        /// </summary>
        public void ConfigureSignalBudget(int maxInvocationsPerOwner, int maxQueuedInvocations)
        {
            MaxSignalInvocationsPerOwner = Math.Max(1, maxInvocationsPerOwner);
            MaxQueuedSignalInvocations = Math.Max(1, maxQueuedInvocations);
        }

        /// <summary>Current logical frame number; the first <see cref="Advance"/> enters frame one.</summary>
        public long FrameIndex => _frameIndex;

        /// <summary>Current injected scaled scheduler time.</summary>
        public double CurrentTime => _timeSource.CurrentTime;

        internal long CompletionPromotionTouchCount { get; private set; }

        internal Action CompletionSnapshotCaptured { get; set; }

        internal int CompletionWaitCount
        {
            get
            {
                lock (_completionGate)
                {
                    return _completionRegistrations.Count;
                }
            }
        }

        /// <summary>Current live thread count for lifecycle and bounded-churn verification.</summary>
        public int LiveThreadCount => _records.Count;

        /// <summary>Idle records waiting for reuse; never counted as live threads.</summary>
        internal int PooledRecordCount => _recordPool.Count;

        /// <summary>Wait, delay and signal-timeout entries held by the heaps, stale ones included.</summary>
        internal int TimedEntryCount =>
            _waitHeap.Count + _delayHeap.Count + _signalWaitTimeoutHeap.Count;

        /// <summary>Heap entries visited while removing cancelled or killed work (M2-26 regression counter).</summary>
        internal long QueuedWorkScanCount { get; private set; }

        /// <summary>
        /// Actor on whose behalf the signal invocation being dispatched right now was fired (see
        /// <see cref="BeginSignalsOnBehalfOf"/>); null outside such a dispatch and inside every thread
        /// resume, so only the handler the invocation starts is charged to that actor (MP-10).
        /// </summary>
        internal string CurrentSignalQuotaActorId => _currentInvocationQuotaActorId;

        /// <summary>Live threads charged to <paramref name="quotaActorId"/>'s induced-thread budget (MP-10).</summary>
        internal int CountInducedThreads(string quotaActorId)
        {
            return quotaActorId != null
                   && _inducedThreadsByQuotaActor.TryGetValue(quotaActorId.Trim(), out int count)
                ? count
                : 0;
        }

        /// <summary>
        /// <c>task.spawn</c>, <c>task.defer</c> and <c>task.delay</c> calls refused because the thread
        /// making them is charged to another actor whose induced-thread budget was full (A4-01).
        /// </summary>
        internal long InducedThreadRefusals { get; private set; }

        /// <summary>
        /// Raised with the charged actor and the refusal each time <see cref="InducedThreadRefusals"/>
        /// grows, before the refusal is raised to the calling script. A subscriber that throws is
        /// reported through <see cref="HostFaulted"/>.
        /// </summary>
        internal event Action<string, RbxError> InducedThreadRefused
        {
            add => _inducedThreadRefused += value;
            remove => _inducedThreadRefused -= value;
        }

        /// <summary>
        /// Raised at each observable phase boundary in canonical pipeline order. Each subscriber is
        /// contained on its own: one that throws is reported through <see cref="HostFaulted"/> and the
        /// remaining subscribers and phases still run.
        /// </summary>
        public event Action<SchedulerPhase, double> PhaseReached
        {
            add
            {
                lock (_subscriberGate)
                {
                    _phaseReached += value;
                    _phaseReachedSubscribers = ToSubscriberArray(_phaseReached);
                }
            }
            remove
            {
                lock (_subscriberGate)
                {
                    _phaseReached -= value;
                    _phaseReachedSubscribers = ToSubscriberArray(_phaseReached);
                }
            }
        }

        /// <summary>
        /// Raised for every contained failure attributed to a mod (ownerModId, error): a thread that
        /// faulted (killed and unregistered first), a thread resumed after it died outside the scheduler,
        /// a signal handler of a connection the mod owns that threw, and a signal cascade or fan-out
        /// budget overflow the mod caused. Each subscriber is contained on its own. If no subscriber
        /// exists the error is thrown: immediately outside <see cref="Advance"/>, and once the frame has
        /// completed inside it, so the fault cannot disappear silently.
        /// </summary>
        public event Action<string, RbxError> ThreadFaulted
        {
            add
            {
                lock (_subscriberGate)
                {
                    _threadFaulted += value;
                    _threadFaultedSubscribers = ToSubscriberArray(_threadFaulted);
                }
            }
            remove
            {
                lock (_subscriberGate)
                {
                    _threadFaulted -= value;
                    _threadFaultedSubscribers = ToSubscriberArray(_threadFaulted);
                }
            }
        }

        /// <summary>
        /// Raised after every resume of a scheduler thread that yielded or completed without a fault,
        /// with the owning mod id and whether the thread completed. Owner-scoped success signal for
        /// per-mod consecutive-fault streaks (a mod runtime resets its quarantine streak here). Raised on
        /// the hot path, once per successful resume, so subscribers must be cheap; each subscriber is
        /// contained on its own and a throwing one is reported through <see cref="HostFaulted"/>.
        /// </summary>
        public event Action<string, bool> ThreadResumeSucceeded
        {
            add
            {
                lock (_subscriberGate)
                {
                    _threadResumeSucceeded += value;
                    _threadResumeSucceededSubscribers = ToSubscriberArray(_threadResumeSucceeded);
                }
            }
            remove
            {
                lock (_subscriberGate)
                {
                    _threadResumeSucceeded -= value;
                    _threadResumeSucceededSubscribers = ToSubscriberArray(_threadResumeSucceeded);
                }
            }
        }

        /// <summary>
        /// Raised for every contained failure that belongs to no mod (source, exception): a throwing
        /// <see cref="PhaseReached"/> subscriber, a throwing <see cref="ScheduleHostCallback"/> callback,
        /// a throwing handler of a connection no mod owns, signal overload caused by host code, and a
        /// subscriber of this scheduler's other events that threw. If no subscriber exists the exception
        /// is thrown: immediately outside <see cref="Advance"/>, and once the frame has completed inside
        /// it (only the first unobserved failure of a frame is rethrown).
        /// </summary>
        public event Action<string, Exception> HostFaulted
        {
            add
            {
                lock (_subscriberGate)
                {
                    _hostFaulted += value;
                    _hostFaultedSubscribers = ToSubscriberArray(_hostFaulted);
                }
            }
            remove
            {
                lock (_subscriberGate)
                {
                    _hostFaulted -= value;
                    _hostFaultedSubscribers = ToSubscriberArray(_hostFaulted);
                }
            }
        }

        /// <summary>
        /// Raised once for every thread this scheduler stops tracking, however it ended: completed,
        /// faulted, cancelled or killed with its owner. Lets an adapter drop its own per-thread
        /// bookkeeping (tracked-thread ledgers, wait connections) the moment the thread is gone instead of
        /// holding it until the mod unloads (M2-07, M2-18). Raised on the hot path; each subscriber is
        /// contained on its own and a throwing one is reported through <see cref="HostFaulted"/>.
        /// </summary>
        internal event Action<IRbxScriptThread> ThreadRetired
        {
            add
            {
                lock (_subscriberGate)
                {
                    _threadRetired += value;
                    _threadRetiredSubscribers = ToSubscriberArray(_threadRetired);
                }
            }
            remove
            {
                lock (_subscriberGate)
                {
                    _threadRetired -= value;
                    _threadRetiredSubscribers = ToSubscriberArray(_threadRetired);
                }
            }
        }

        /// <summary>
        /// Creates and immediately resumes a thread to its first yield or completion. A live thread
        /// this scheduler already owns (a task handle) is resumed now with <paramref name="args"/>
        /// instead (M2-14): a parked thread continues, and a deferred or delayed one runs now and loses
        /// its pending slot. See <see cref="TakeForReschedule"/> for the refused cases. A new thread
        /// started by a thread charged to another actor is charged to that actor too (see
        /// <see cref="CreateScheduledRecord"/>).
        /// </summary>
        public IRbxScriptThread Spawn(string ownerModId, object callable, object[] args)
        {
            if (callable is IRbxScriptThread existing)
            {
                ThreadRecord moved = TakeForReschedule(ownerModId, existing, "task.spawn", false);
                ResumeThread(moved, CopyArguments(args));
                return existing;
            }

            ThreadRecord record = CreateScheduledRecord(ownerModId, callable, "task.spawn");
            IRbxScriptThread thread = record.Thread;
            ResumeThread(record, CopyArguments(args));
            TryReleaseRecord(record);
            return thread;
        }

        /// <summary>Creates a scheduler-owned signal callback with its destruction tombstone scope.</summary>
        internal IRbxScriptThread SpawnSignal(string ownerModId, object callable, object[] args)
        {
            ThreadRecord record;
            try
            {
                record = CreateRecord(ownerModId, callable);
            }
            catch (RbxError error)
            {
                ReportThreadFault(ownerModId, error);
                return null;
            }

            IRbxScriptThread thread = record.Thread;
            record.ReadableTombstone = _currentSignalTombstone;
            ResumeThread(record, CopyArguments(args));
            TryReleaseRecord(record);
            return thread;
        }

        /// <summary>
        /// Starts a signal callback on behalf of another actor (MP-10): the thread still runs as
        /// <paramref name="ownerModId"/>'s code, but it is charged to <paramref name="quotaActorId"/>'s
        /// induced-thread budget, which allows <paramref name="maxLiveThreads"/> such threads at once,
        /// instead of the owner's actor quota, and so is every thread it starts in turn (see
        /// <see cref="CreateScheduledRecord"/>). A remote caller can therefore exhaust only its own budget,
        /// never the host's. Over that budget (or at <see cref="EmergencyMaxThreads"/>) nothing starts,
        /// null is returned with <paramref name="refusal"/> set, and nothing is reported to the owner:
        /// the owner did nothing wrong, so the caller answers the remote side instead. A null or blank
        /// <paramref name="quotaActorId"/> behaves exactly like the three-argument overload.
        /// </summary>
        internal IRbxScriptThread SpawnSignal(string ownerModId, object callable, object[] args,
            string quotaActorId, int maxLiveThreads, out RbxError refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(quotaActorId))
            {
                return SpawnSignal(ownerModId, callable, args);
            }

            string quotaActor = quotaActorId.Trim();
            int limit = Math.Max(1, maxLiveThreads);
            if (_records.Count >= EmergencyMaxThreads)
            {
                refusal = new RbxError(
                    RbxErrorCode.ThreadCap,
                    "actor '" + quotaActor + "' cannot start a handler of mod '" + ownerModId
                    + "': emergency live scheduler threads ceiling reached (" + EmergencyMaxThreads + ")",
                    "retry after the server's running handlers finish");
                return null;
            }

            if (CountInducedThreads(quotaActor) >= limit)
            {
                refusal = new RbxError(
                    RbxErrorCode.BudgetExceeded,
                    "actor '" + quotaActor + "' already has " + limit
                    + " handler threads in flight that its remote calls started; mod '" + ownerModId
                    + "' did not start another one",
                    "wait for earlier remote calls to finish before sending more");
                return null;
            }

            ThreadRecord record;
            try
            {
                record = CreateRecord(ownerModId, callable, quotaActor, limit);
            }
            catch (RbxError error)
            {
                ReportThreadFault(ownerModId, error);
                return null;
            }

            IRbxScriptThread thread = record.Thread;
            record.ReadableTombstone = _currentSignalTombstone;
            ResumeThread(record, CopyArguments(args));
            TryReleaseRecord(record);
            return thread;
        }

        /// <summary>
        /// Creates the record of a new <c>task.spawn</c>, <c>task.defer</c> or <c>task.delay</c>
        /// thread. Started from a thread charged to another actor's induced-thread budget, the new
        /// thread is charged to that same actor and held to the same limit; over that limit nothing is
        /// created and <see cref="RbxErrorCode.BudgetExceeded"/> is raised to the calling script.
        /// </summary>
        /// <remarks>
        /// WHY inherited: a handler a remote client's call started runs that client's request, and
        /// what it schedules is still that request. Charged to the handler's owner instead, one client
        /// firing at a handler that calls <c>task.delay(60, f)</c> filled the host's whole thread quota
        /// and got the host's gameplay mod quarantined (A4-01). WHY the refusal is remembered on the
        /// running record: when it ends the thread, the thread died of the sender's budget, not of a
        /// fault in its owner's code, and <see cref="ResumeThread"/> must not charge it to the owner.
        /// </remarks>
        private ThreadRecord CreateScheduledRecord(string ownerModId, object callable, string operation)
        {
            ThreadRecord running = _runningRecord;
            string quotaActor = running?.QuotaActorId;
            if (quotaActor == null)
            {
                return CreateRecord(ownerModId, callable);
            }

            int limit = Math.Max(1, running.QuotaLimit);
            if (CountInducedThreads(quotaActor) >= limit)
            {
                RbxError refusal = new(
                    RbxErrorCode.BudgetExceeded,
                    "actor '" + quotaActor + "' already has " + limit
                    + " threads in flight that its remote calls started; " + operation
                    + " in mod '" + ownerModId + "' did not start another one",
                    "wait for earlier remote calls to finish before sending more");
                running.InducedRefusalMessage = refusal.RawMessage;
                InducedThreadRefusals++;
                RaiseInducedThreadRefused(quotaActor, refusal);
                throw refusal;
            }

            return CreateRecord(ownerModId, callable, quotaActor, limit);
        }

        private void RaiseInducedThreadRefused(string quotaActorId, RbxError refusal)
        {
            Action<string, RbxError> handlers = _inducedThreadRefused;
            if (handlers == null)
            {
                return;
            }

            try
            {
                handlers(quotaActorId, refusal);
            }
            catch (Exception exception)
            {
                ReportHostFault("InducedThreadRefused subscriber", exception);
            }
        }

        /// <summary>
        /// True when <paramref name="error"/> is the induced-budget refusal raised inside the record's
        /// own thread: the thread died of its sender's budget, which is not its owner's fault.
        /// </summary>
        private static bool DiedOfInducedRefusal(ThreadRecord record, RbxError error)
        {
            return record.QuotaActorId != null
                   && record.InducedRefusalMessage != null
                   && error != null
                   && error.Code == RbxErrorCode.BudgetExceeded
                   && string.Equals(error.RawMessage, record.InducedRefusalMessage,
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Marks every signal invocation queued from now until <see cref="EndSignalsOnBehalfOf"/> as
        /// fired on behalf of <paramref name="quotaActorId"/>, so the handler thread each one starts can
        /// be charged to that actor (MP-10). Returns the previous scope for the matching end call.
        /// </summary>
        internal string BeginSignalsOnBehalfOf(string quotaActorId)
        {
            string previous = _enqueueQuotaActorId;
            _enqueueQuotaActorId = string.IsNullOrWhiteSpace(quotaActorId) ? null : quotaActorId.Trim();
            return previous;
        }

        /// <summary>Restores the scope <see cref="BeginSignalsOnBehalfOf"/> replaced.</summary>
        internal void EndSignalsOnBehalfOf(string previous)
        {
            _enqueueQuotaActorId = previous;
        }

        /// <summary>
        /// Undoes a scheduled wait that never suspended its thread (M2-19): the adapter scheduled
        /// task.wait, signal:Wait or a completion wait and then the yield itself was refused, so the
        /// thread kept running with a record that still says it waits. Call it only for a thread that
        /// is demonstrably executing right now. Returns true when a wait was rolled back; its timed
        /// entry, completion registration and signal-timeout generation are abandoned and the record
        /// is Running again, so the thread's next wait schedules normally.
        /// </summary>
        internal bool RollbackUnfinishedYield(IRbxScriptThread thread)
        {
            if (thread == null || !_records.TryGetValue(thread, out ThreadRecord record))
            {
                return false;
            }

            switch (record.State)
            {
                case ThreadScheduleState.Waiting:
                case ThreadScheduleState.WaitingForSignal:
                case ThreadScheduleState.WaitingForCompletion:
                    RemoveQueuedWork(record);
                    record.SignalWaitGeneration++;
                    record.State = ThreadScheduleState.Running;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Validates a thread handed back to <see cref="Spawn"/>, <see cref="Defer"/> or
        /// <see cref="Delay"/> and detaches it from whatever slot it held (M2-14). Accepted: a parked
        /// thread (suspended outside the scheduler, for example by a native <c>coroutine.yield</c>), a
        /// deferred or delayed thread (its old slot is abandoned), and, for defer and delay only, the
        /// running thread itself. Refused loudly: a dead thread, another mod's or another scheduler's
        /// thread, the running thread for spawn, and a thread suspended in a scheduler wait.
        /// </summary>
        private ThreadRecord TakeForReschedule(string ownerModId, IRbxScriptThread thread,
            string operation, bool allowRunning)
        {
            ValidateOwnerModId(ownerModId);
            if (!_records.TryGetValue(thread, out ThreadRecord record))
            {
                if (thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead)
                {
                    throw RbxError.BadArgument(
                        operation + " cannot resume a dead thread",
                        "a finished or cancelled task never runs again; schedule its function anew");
                }

                throw RbxError.BadArgument(
                    operation + " received a thread not owned by this scheduler",
                    "pass a thread returned by task.spawn, task.defer or task.delay in this world");
            }

            if (!string.Equals(record.OwnerModId, ownerModId, StringComparison.Ordinal))
            {
                throw RbxError.BadArgument(
                    operation + " received a thread owned by mod " + record.OwnerModId
                    + ", not " + ownerModId,
                    "reschedule a thread only from the mod that created it");
            }

            if (thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead)
            {
                throw RbxError.BadArgument(
                    operation + " cannot resume a dead thread",
                    "a finished or cancelled task never runs again; schedule its function anew");
            }

            switch (record.State)
            {
                case ThreadScheduleState.Idle:
                    return record;
                case ThreadScheduleState.Running:
                    if (!allowRunning)
                    {
                        throw RbxError.BadArgument(
                            operation + " cannot resume the running thread",
                            "use task.defer(thread) or task.delay(seconds, thread) to resume it after it yields");
                    }

                    return record;
                case ThreadScheduleState.Deferred:
                    record.DeferredArguments = null;
                    record.DeferredSequence = 0;
                    return record;
                case ThreadScheduleState.Delayed:
                    AbandonTimedEntry(record);
                    return record;
                default:
                    throw RbxError.BadArgument(
                        operation + " cannot reschedule a thread suspended in a scheduler wait (state "
                        + record.State + ")",
                        "let its task.wait, signal:Wait or RemoteFunction call finish, or task.cancel it first");
            }
        }

        /// <summary>
        /// Pools a record whose thread died inside the resume that started it. That thread never left
        /// the Running state, so no wait/delay/timeout heap, deferred queue, or completion entry can
        /// reference the record; any other ending (yielded, faulted, killed) keeps today's GC lifetime.
        /// </summary>
        private void TryReleaseRecord(ThreadRecord record)
        {
            if (record.State != ThreadScheduleState.Running
                || record.CompletionWait != null
                || record.DeferredArguments != null
                || record.Thread == null
                || _records.ContainsKey(record.Thread))
            {
                return;
            }

            record.Clear();
            if (_recordPool.Count < MaxPooledRecords)
            {
                _recordPool.Push(record);
            }
        }

        /// <summary>
        /// Creates a thread for the next deferred resumption point. A live thread this scheduler already
        /// owns (a task handle) moves to the back of the deferred queue instead, with
        /// <paramref name="args"/> as its resume values (M2-14); the running thread may defer itself
        /// and then yield. A new thread is charged as <see cref="Spawn"/> charges one.
        /// </summary>
        public IRbxScriptThread Defer(string ownerModId, object callable, object[] args)
        {
            ThreadRecord record = callable is IRbxScriptThread existing
                ? TakeForReschedule(ownerModId, existing, "task.defer", true)
                : CreateScheduledRecord(ownerModId, callable, "task.defer");
            record.State = ThreadScheduleState.Deferred;
            record.DeferredArguments = CopyArguments(args);
            EnqueueDeferred(record);
            return record.Thread;
        }

        /// <summary>
        /// Creates a thread for the next eligible delayed slot. A duration of positive infinity
        /// (<c>task.delay(math.huge, f)</c>) parks the thread: it never resumes, stays cancellable, and
        /// is killed with its owner (M2-17). A live thread this scheduler already owns (a task handle)
        /// is re-armed for the new duration with <paramref name="args"/> instead (M2-14). A new thread
        /// is charged as <see cref="Spawn"/> charges one.
        /// </summary>
        public IRbxScriptThread Delay(string ownerModId, double seconds, object callable, object[] args)
        {
            double duration = ValidateAndNormalizeDuration(seconds, "Delay");
            ThreadRecord record = callable is IRbxScriptThread existing
                ? TakeForReschedule(ownerModId, existing, "task.delay", true)
                : CreateScheduledRecord(ownerModId, callable, "task.delay");
            record.State = ThreadScheduleState.Delayed;
            if (double.IsPositiveInfinity(duration))
            {
                return record.Thread;
            }

            DelayEntry entry = new(record, CopyArguments(args), CurrentTime + duration,
                GetEarliestTimerFrame(), NextSequence());
            _delayHeap.Add(entry);
            record.HasTimedEntry = true;
            record.TimedSequence = entry.Sequence;
            return record.Thread;
        }

        private void EnqueueDeferred(ThreadRecord record)
        {
            long sequence = NextSequence();
            record.DeferredSequence = sequence;
            _deferredQueue.Enqueue(new DeferredEntry(record, sequence));
        }

        private bool IsLiveDeferredEntry(DeferredEntry entry)
        {
            return entry.Record.DeferredSequence == entry.Sequence
                   && IsLiveInState(entry.Record, ThreadScheduleState.Deferred);
        }

        /// <summary>
        /// Schedules an ownerless host callback for the next eligible delayed slot, serviced in the
        /// delayed-threads slot before Heartbeat (R4.2) alongside task.wait/task.delay resumptions.
        /// Ownerless by design: the entry carries no mod id, is never counted against
        /// <see cref="MaxThreadsPerActor"/>, and <see cref="KillOwnedBy"/> never touches it, so a
        /// scheduled host timer (Debris destruction) survives the scheduling mod's unload (S5.1).
        /// A callback that throws is dropped after its single attempt and reported through
        /// <see cref="HostFaulted"/>; later entries of the same slot still run in the same frame.
        /// A duration of positive infinity never fires, so nothing is scheduled.
        /// </summary>
        public void ScheduleHostCallback(double seconds, Action callback)
        {
            if (callback == null)
            {
                throw RbxError.BadArgument(
                    "ScheduleHostCallback requires a callback",
                    "pass the host action to run when the scaled delay elapses");
            }

            double duration = ValidateAndNormalizeDuration(seconds, "ScheduleHostCallback");
            if (double.IsPositiveInfinity(duration))
            {
                return;
            }

            HostCallbackEntry entry = new(callback, CurrentTime + duration,
                GetEarliestTimerFrame(), NextSequence());
            _hostHeap.Add(entry);
        }

        /// <summary>
        /// Schedules an existing caller to resume on the first eligible future delayed slot with its
        /// actual scaled elapsed time as the sole argument. A duration of positive infinity
        /// (<c>task.wait(math.huge)</c>) parks the caller until it is cancelled or its owner is torn
        /// down (M2-17).
        /// </summary>
        public void ScheduleWait(IRbxScriptThread caller, double seconds = 0d)
        {
            double duration = ValidateAndNormalizeDuration(seconds, "ScheduleWait");
            ThreadRecord record = GetSchedulableRecord(caller, "ScheduleWait", true);
            double scheduledAt = CurrentTime;
            record.State = ThreadScheduleState.Waiting;
            if (double.IsPositiveInfinity(duration))
            {
                return;
            }

            WaitEntry entry = new(record, scheduledAt, scheduledAt + duration,
                GetEarliestTimerFrame(), NextSequence());
            _waitHeap.Add(entry);
            record.HasTimedEntry = true;
            record.TimedSequence = entry.Sequence;
        }

        /// <summary>Marks a running scheduler thread as yielded until its signal's first delivery.</summary>
        internal void ScheduleSignalWait(IRbxScriptThread caller)
        {
            ThreadRecord record = GetSchedulableRecord(caller, "signal:Wait", true);
            record.SignalWaitGeneration++;
            record.State = ThreadScheduleState.WaitingForSignal;
        }

        /// <summary>Marks a signal waiter yielded and schedules its owning-thread timeout.</summary>
        internal void ScheduleSignalWait(IRbxScriptThread caller, double seconds,
            Func<object[]> timeoutResumeArguments)
        {
            if (timeoutResumeArguments == null)
            {
                throw RbxError.BadArgument(
                    "signal:Wait timeout requires resume arguments",
                    "provide the timeout result factory from the active Lua binding");
            }

            double duration = ValidateAndNormalizeDuration(seconds, "signal:Wait timeout");
            ThreadRecord record = GetSchedulableRecord(caller, "signal:Wait", true);
            record.SignalWaitGeneration++;
            record.State = ThreadScheduleState.WaitingForSignal;
            if (double.IsPositiveInfinity(duration))
            {
                return;
            }

            SignalWaitTimeoutEntry entry = new(record, record.SignalWaitGeneration,
                timeoutResumeArguments, CurrentTime + duration, GetEarliestTimerFrame(),
                NextSequence());
            _signalWaitTimeoutHeap.Add(entry);
            record.HasTimedEntry = true;
            record.TimedSequence = entry.Sequence;
        }

        /// <summary>Resumes one signal waiter with the arguments captured at fire time.</summary>
        internal void ResumeSignalWait(IRbxScriptThread caller, object[] arguments)
        {
            if (caller == null || !_records.TryGetValue(caller, out ThreadRecord record)
                || record.State != ThreadScheduleState.WaitingForSignal)
            {
                return;
            }

            AbandonTimedEntry(record);
            record.ReadableTombstone = _currentSignalTombstone;
            ResumeThread(record, CopyArguments(arguments));
        }

        /// <summary>
        /// Queues one connection invocation for the deferred signal drain. An invocation past the
        /// generation cap (cascade depth), the queue ceiling, or its owner's per-resumption-point budget
        /// (fan-out width) is dropped, and the responsible owner is faulted once per resumption point at
        /// the next safe point of the drain instead of throwing out of the frame (M2-02, M2-12).
        /// </summary>
        internal void EnqueueSignalInvocation(RbxScriptConnection connection, object[] arguments,
            RbxInstance readableTombstone)
        {
            if (connection == null || !ReferenceEquals(connection.Scheduler, this))
            {
                throw RbxError.BadArgument(
                    "signal invocation belongs to another scheduler",
                    "queue each RBXScriptConnection through its owning ModScheduler");
            }

            int generation = _drainingSignals ? _currentSignalGeneration + 1 : 1;
            if (generation > MaxSignalGenerations)
            {
                connection.DropQueuedInvocation();
                // WHY the firing owner and not the listener: every listener of a looping signal gets
                // its generation-11 invocation dropped, but only the code that fired at generation 10
                // keeps the loop alive. Faulting a mod that merely listens would quarantine a victim.
                string firingOwner = _runningOwnerModId ?? _currentInvocationOwnerModId
                                     ?? connection.OwnerModId;
                if (ShouldReportSignalOverload(firingOwner))
                {
                    NoteSignalOverload(firingOwner, new RbxError(
                        RbxErrorCode.SignalCascade,
                        "signal cascade exceeded " + MaxSignalGenerations + " generations: "
                        + string.Join(" -> ", BuildSignalChain(connection.SignalName)),
                        "break the signal cycle or defer the next mutation to a later frame"));
                }

                return;
            }

            string listenerOwner = connection.OwnerModId;
            if (_signalQueue.Count >= MaxQueuedSignalInvocations)
            {
                connection.DropQueuedInvocation();
                if (ShouldReportSignalOverload(listenerOwner))
                {
                    NoteSignalOverload(listenerOwner, new RbxError(
                        RbxErrorCode.BudgetExceeded,
                        "signal queue is full (" + MaxQueuedSignalInvocations
                        + " queued invocations); dropped " + connection.SignalName + " invocations",
                        "fire fewer signals per frame or connect fewer handlers to busy signals"));
                }

                return;
            }

            bool hostOwned = string.IsNullOrWhiteSpace(listenerOwner);
            if (!TryChargeSignalInvocation(hostOwned ? HostSignalChargeKey : listenerOwner))
            {
                connection.DropQueuedInvocation();
                if (ShouldReportSignalOverload(listenerOwner))
                {
                    NoteSignalOverload(listenerOwner, new RbxError(
                        RbxErrorCode.BudgetExceeded,
                        (hostOwned ? "connections owned by no mod" : "mod '" + listenerOwner + "'")
                        + " exceeded " + MaxSignalInvocationsPerOwner
                        + " signal handler invocations in one resumption point; dropped "
                        + connection.SignalName + " invocations",
                        "stop handlers from re-firing signals they listen to, or spread the work over frames"));
                }

                return;
            }

            string[] chain = BuildSignalChain(connection.SignalName);
            _signalQueue.Enqueue(new SignalInvocation(
                connection, CopyArguments(arguments), readableTombstone, generation, chain,
                _enqueueQuotaActorId));
        }

        /// <summary>
        /// Resumes an existing caller at the next deferred drain after completion. Callers captured in
        /// one ready snapshot are promoted in registration order. Across snapshots, order follows
        /// signal readiness. The host must call <see cref="SignalCompletion"/> after registering and
        /// completing the token.
        /// </summary>
        public void ScheduleWaitUntil(IRbxScriptThread caller, RbxSchedulerCompletion completion)
        {
            if (completion == null)
            {
                throw RbxError.BadArgument(
                    "ScheduleWaitUntil requires a completion token",
                    "pass a distinct RbxSchedulerCompletion for the host operation");
            }

            ThreadRecord record = GetSchedulableRecord(caller, "ScheduleWaitUntil", true);
            lock (_completionGate)
            {
                if (_completionRegistrations.ContainsKey(completion))
                {
                    throw RbxError.BadArgument(
                        "ScheduleWaitUntil requires a distinct completion token",
                        "create one RbxSchedulerCompletion for each scheduler wait");
                }

                CompletionWaitEntry entry = new(record, completion, NextSequence());
                record.State = ThreadScheduleState.WaitingForCompletion;
                record.CompletionWait = entry;
                _completionRegistrations.Add(completion, entry);
            }
        }

        /// <summary>
        /// Publishes a terminal completion to the scheduler. Host callbacks may call this off the
        /// main thread after completing the token; late teardown signals are discarded.
        /// </summary>
        public void SignalCompletion(RbxSchedulerCompletion completion)
        {
            if (completion == null)
            {
                throw RbxError.BadArgument(
                    "SignalCompletion requires a completion token",
                    "signal the token passed to ScheduleWaitUntil");
            }

            lock (_completionGate)
            {
                if (!_completionRegistrations.TryGetValue(completion,
                        out CompletionWaitEntry entry))
                {
                    return;
                }

                if (!completion.IsCompleted)
                {
                    throw RbxError.BadArgument(
                        "SignalCompletion requires a terminal completion token",
                        "complete, fail, or cancel the token before signalling it");
                }

                if (entry.IsSignaled)
                {
                    return;
                }

                entry.IsSignaled = true;
                _readyCompletions.Add(entry.Sequence, entry);
            }
        }

        /// <summary>
        /// Cancels one live scheduler-owned thread and removes all pending work. Cancelling a thread
        /// that already finished is a no-op (M2-13): Roblox's task.cancel closes the thread, and closing
        /// a dead coroutine is not an error, so the <c>task.cancel(self._t)</c> cleanup idiom works after
        /// the task ran. Only the currently running thread cannot be cancelled.
        /// </summary>
        public void Cancel(IRbxScriptThread thread)
        {
            if (thread == null)
            {
                throw RbxError.BadArgument(
                    "task.cancel requires a thread",
                    "pass the live thread returned by task.spawn, task.defer, or task.delay");
            }

            if (thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead)
            {
                // WHY the record is still dropped: a thread finished outside the scheduler (a native
                // coroutine.close) keeps its record until something resumes it; cancelling it is the
                // author's request to forget it, so its queued work goes without a fault report.
                if (_records.TryGetValue(thread, out ThreadRecord deadRecord))
                {
                    KillRecord(deadRecord);
                }

                return;
            }

            if (!_records.TryGetValue(thread, out ThreadRecord record))
            {
                throw RbxError.BadArgument(
                    "task.cancel received a thread not owned by this scheduler",
                    "cancel the thread through the scheduler that created it");
            }

            if (record.State == ThreadScheduleState.Running
                || thread.Status == RbxScriptThreadStatus.Running)
            {
                throw RbxError.BadArgument(
                    "task.cancel cannot cancel the currently running thread",
                    "cancel it from a different scheduled thread or after it yields");
            }

            KillRecord(record);
        }

        /// <summary>Kills every live thread owned by one mod without touching other owners.</summary>
        public int KillOwnedBy(string ownerModId)
        {
            ValidateOwnerModId(ownerModId);
            List<ThreadRecord> owned = new();
            foreach (KeyValuePair<IRbxScriptThread, ThreadRecord> pair in _records)
            {
                if (string.Equals(pair.Value.OwnerModId, ownerModId, StringComparison.Ordinal))
                {
                    owned.Add(pair.Value);
                }
            }

            for (int index = 0; index < owned.Count; index++)
            {
                KillRecord(owned[index]);
            }

            return owned.Count;
        }

        /// <summary>
        /// Advances scaled time and executes one complete logical frame. Per-callback failures are
        /// contained and reported (see the type summary); the first failure of the frame that no
        /// subscriber observed is rethrown after every phase of the frame has run.
        /// </summary>
        public void Advance(double deltaSeconds)
        {
            ValidateDelta(deltaSeconds);
            if (_advancing)
            {
                throw RbxError.BadArgument(
                    "ModScheduler.Advance cannot be called reentrantly",
                    "let the current frame drain finish before advancing again");
            }

            _advancing = true;
            _heldFault = null;
            Exception unobservedFault = null;
            try
            {
                double previousTime = CurrentTime;
                _timeSource.Advance(deltaSeconds);
                ValidateClock(CurrentTime, "advanced");
                if (CurrentTime < previousTime)
                {
                    throw RbxError.BadArgument(
                        "IRbxTimeSource moved backward from " + previousTime + " to " + CurrentTime,
                        "provide a monotonic scaled-time source");
                }

                _frameIndex++;
                _delayedBatchStarted = false;
                for (int index = 0; index < Pipeline.Length; index++)
                {
                    _currentStage = Pipeline[index];
                    if (_currentStage == PipelineStage.ResumeDelayed)
                    {
                        _delayedBatchStarted = true;
                    }

                    RunPipelineStage(_currentStage.Value, deltaSeconds);
                }
            }
            finally
            {
                _currentStage = null;
                _delayedBatchStarted = false;
                _advancing = false;
                unobservedFault = _heldFault;
                _heldFault = null;
            }

            if (unobservedFault != null)
            {
                ExceptionDispatchInfo.Capture(unobservedFault).Throw();
            }
        }

        private void RunPipelineStage(PipelineStage stage, double deltaSeconds)
        {
            switch (stage)
            {
                case PipelineStage.ResumptionPoint:
                    RunResumptionPoint();
                    return;
                case PipelineStage.PreAnimation:
                    ReachPhase(SchedulerPhase.PreAnimation, deltaSeconds);
                    return;
                case PipelineStage.PreSimulation:
                    ReachPhase(SchedulerPhase.PreSimulation, deltaSeconds);
                    return;
                case PipelineStage.PostSimulation:
                    ReachPhase(SchedulerPhase.PostSimulation, deltaSeconds);
                    return;
                case PipelineStage.ResumeDelayed:
                    ResumeDelayedThreads();
                    return;
                case PipelineStage.Heartbeat:
                    ReachPhase(SchedulerPhase.Heartbeat, deltaSeconds);
                    return;
                case PipelineStage.InputProcessing:
                    ReachPhase(SchedulerPhase.InputProcessing, deltaSeconds);
                    return;
                case PipelineStage.PreRender:
                    ReachPhase(SchedulerPhase.PreRender, deltaSeconds);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
            }
        }

        private void ReachPhase(SchedulerPhase phase, double deltaSeconds)
        {
            Action<SchedulerPhase, double>[] subscribers = _phaseReachedSubscribers;
            for (int index = 0; index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](phase, deltaSeconds);
                }
                catch (Exception exception)
                {
                    ReportHostFault(PhaseSubscriberSources[(int)phase], exception);
                }
            }
        }

        /// <summary>
        /// One resumption point (R4.8, R5.4): deferred threads, then queued signal handlers, repeated
        /// while either queue still holds work, up to <see cref="MaxDrainRoundsPerResumptionPoint"/>
        /// rounds. A <c>task.defer</c> made inside a handler therefore runs before the pipeline moves
        /// on (M2-11): a PreRender handler's deferral in the same frame, a PostSimulation handler's
        /// before the delayed threads.
        /// </summary>
        private void RunResumptionPoint()
        {
            try
            {
                for (int round = 0; round < MaxDrainRoundsPerResumptionPoint; round++)
                {
                    DrainDeferred();
                    DrainSignals();
                    if (_deferredQueue.Count == 0 && _signalQueue.Count == 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                FlushPendingSignalFaults();
                _signalChargeByOwner.Clear();
                _signalOverloadReportedOwners.Clear();
                _signalOverloadReportedForHost = false;
            }
        }

        private void DrainDeferred()
        {
            PromoteCompletedWaits();
            _drainBuffer.Clear();
            int count = _deferredQueue.Count;
            for (int index = 0; index < count; index++)
            {
                _drainBuffer.Add(_deferredQueue.Dequeue());
            }

            int nextIndex = 0;
            try
            {
                for (; nextIndex < _drainBuffer.Count; nextIndex++)
                {
                    DeferredEntry entry = _drainBuffer[nextIndex];
                    if (!IsLiveDeferredEntry(entry))
                    {
                        continue;
                    }

                    ThreadRecord record = entry.Record;
                    object[] arguments = record.DeferredArguments;
                    record.DeferredArguments = null;
                    record.DeferredSequence = 0;
                    ResumeThread(record, arguments ?? EmptyArguments);
                }
            }
            catch
            {
                RestoreDeferredBatch(nextIndex + 1);
                throw;
            }
            finally
            {
                _drainBuffer.Clear();
            }
        }

        private void RestoreDeferredBatch(int startIndex)
        {
            List<DeferredEntry> newlyDeferred = new(_deferredQueue.Count);
            while (_deferredQueue.Count > 0)
            {
                newlyDeferred.Add(_deferredQueue.Dequeue());
            }

            for (int index = startIndex; index < _drainBuffer.Count; index++)
            {
                DeferredEntry entry = _drainBuffer[index];
                if (IsLiveDeferredEntry(entry))
                {
                    _deferredQueue.Enqueue(entry);
                }
            }

            for (int index = 0; index < newlyDeferred.Count; index++)
            {
                _deferredQueue.Enqueue(newlyDeferred[index]);
            }
        }

        private void DrainSignals()
        {
            if (_signalQueue.Count == 0)
            {
                return;
            }

            _drainingSignals = true;
            try
            {
                while (_signalQueue.Count > 0)
                {
                    int generation = _signalQueue.Peek().Generation;
                    _currentSignalGeneration = generation;
                    _signalDrainBuffer.Clear();
                    while (_signalQueue.Count > 0
                           && _signalQueue.Peek().Generation == generation)
                    {
                        _signalDrainBuffer.Add(_signalQueue.Dequeue());
                    }

                    for (int index = 0; index < _signalDrainBuffer.Count; index++)
                    {
                        SignalInvocation invocation = _signalDrainBuffer[index];
                        _currentSignalChain = invocation.Chain;
                        _currentSignalTombstone = invocation.ReadableTombstone;
                        _currentInvocationOwnerModId = invocation.Connection.OwnerModId;
                        _currentInvocationQuotaActorId = invocation.QuotaActorId;
                        try
                        {
                            invocation.Connection.InvokePending(invocation.Arguments);
                        }
                        catch (Exception exception)
                        {
                            ReportSignalHandlerFailure(invocation.Connection, exception);
                        }
                        finally
                        {
                            _currentInvocationQuotaActorId = null;
                        }
                    }

                    _currentInvocationOwnerModId = null;
                    FlushPendingSignalFaults();
                }
            }
            finally
            {
                _signalDrainBuffer.Clear();
                _currentSignalGeneration = 0;
                _currentSignalChain = null;
                _currentSignalTombstone = null;
                _currentInvocationOwnerModId = null;
                _currentInvocationQuotaActorId = null;
                _drainingSignals = false;
            }
        }

        private void ReportSignalHandlerFailure(RbxScriptConnection connection, Exception exception)
        {
            string ownerModId = connection.OwnerModId;
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                ReportHostFault(connection.SignalName + " handler", exception);
                return;
            }

            ReportThreadFault(ownerModId, ToRbxError(exception, connection.SignalName + " handler"));
        }

        private bool TryChargeSignalInvocation(string ownerModId)
        {
            _signalChargeByOwner.TryGetValue(ownerModId, out int charged);
            charged++;
            _signalChargeByOwner[ownerModId] = charged;
            return charged <= MaxSignalInvocationsPerOwner;
        }

        private bool ShouldReportSignalOverload(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                if (_signalOverloadReportedForHost)
                {
                    return false;
                }

                _signalOverloadReportedForHost = true;
                return true;
            }

            return _signalOverloadReportedOwners.Add(ownerModId);
        }

        private void NoteSignalOverload(string ownerModId, RbxError error)
        {
            // WHY queued instead of reported here: this runs inside a mod's own Fire call, in the middle
            // of that mod's resume. A subscriber that quarantines the mod would kill the very thread that
            // is still executing; the report waits for the drain's next safe point instead.
            _pendingSignalFaults.Add(new PendingSignalFault(ownerModId, error));
        }

        private void FlushPendingSignalFaults()
        {
            if (_pendingSignalFaults.Count == 0 || _flushingSignalFaults)
            {
                return;
            }

            _flushingSignalFaults = true;
            try
            {
                for (int index = 0; index < _pendingSignalFaults.Count; index++)
                {
                    PendingSignalFault fault = _pendingSignalFaults[index];
                    if (string.IsNullOrWhiteSpace(fault.OwnerModId))
                    {
                        ReportHostFault("signal dispatch", fault.Error);
                    }
                    else
                    {
                        ReportThreadFault(fault.OwnerModId, fault.Error);
                    }
                }
            }
            finally
            {
                _pendingSignalFaults.Clear();
                _flushingSignalFaults = false;
            }
        }

        private string[] BuildSignalChain(string signalName)
        {
            if (!_drainingSignals || _currentSignalChain == null)
            {
                return new[] { signalName };
            }

            string[] chain = new string[_currentSignalChain.Length + 1];
            Array.Copy(_currentSignalChain, chain, _currentSignalChain.Length);
            chain[chain.Length - 1] = signalName;
            return chain;
        }

        private void PromoteCompletedWaits()
        {
            _completionBuffer.Clear();
            lock (_completionGate)
            {
                foreach (KeyValuePair<long, CompletionWaitEntry> pair in _readyCompletions)
                {
                    _completionBuffer.Add(pair.Value);
                }

                _readyCompletions.Clear();
            }

            int nextIndex = -1;
            try
            {
                Action snapshotHandler = CompletionSnapshotCaptured;
                snapshotHandler?.Invoke();
                _promotingCompletions = true;
                for (nextIndex = 0; nextIndex < _completionBuffer.Count; nextIndex++)
                {
                    CompletionPromotionTouchCount++;
                    CompletionWaitEntry entry = _completionBuffer[nextIndex];
                    ThreadRecord record = entry.Record;
                    if (!TryConsumeCompletionEntry(entry))
                    {
                        continue;
                    }

                    switch (entry.Completion.Status)
                    {
                        case RbxSchedulerCompletionStatus.Succeeded:
                            record.State = ThreadScheduleState.Deferred;
                            record.DeferredArguments = CopyArguments(entry.Completion.ResumeArguments);
                            EnqueueDeferred(record);
                            break;
                        case RbxSchedulerCompletionStatus.Faulted:
                            FinalizeFault(record, entry.Completion.Error);
                            break;
                        case RbxSchedulerCompletionStatus.Canceled:
                            FinalizeKill(record);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch
            {
                RestoreCompletionBatch(nextIndex < 0 ? 0 : nextIndex + 1);
                throw;
            }
            finally
            {
                _promotingCompletions = false;
                _completionBuffer.Clear();
            }
        }

        private void RestoreCompletionBatch(int startIndex)
        {
            lock (_completionGate)
            {
                for (int index = startIndex; index < _completionBuffer.Count; index++)
                {
                    CompletionWaitEntry entry = _completionBuffer[index];
                    if (entry.Record.State == ThreadScheduleState.WaitingForCompletion
                        && _records.ContainsKey(entry.Record.Thread)
                        && ReferenceEquals(entry.Record.CompletionWait, entry)
                        && _completionRegistrations.TryGetValue(entry.Completion,
                            out CompletionWaitEntry registered)
                        && ReferenceEquals(registered, entry))
                    {
                        _readyCompletions[entry.Sequence] = entry;
                    }
                }
            }
        }

        private bool TryConsumeCompletionEntry(CompletionWaitEntry entry)
        {
            ThreadRecord record = entry.Record;
            lock (_completionGate)
            {
                if (!_completionRegistrations.TryGetValue(entry.Completion,
                        out CompletionWaitEntry registered)
                    || !ReferenceEquals(registered, entry))
                {
                    return false;
                }

                if (record.State != ThreadScheduleState.WaitingForCompletion
                    || !_records.ContainsKey(record.Thread)
                    || !ReferenceEquals(record.CompletionWait, entry))
                {
                    RemoveCompletionRegistration(entry);
                    return false;
                }

                RemoveCompletionRegistration(entry);
                return true;
            }
        }

        private void ResumeDelayedThreads()
        {
            _delayedBatchBuffer.Clear();
            while (true)
            {
                WaitEntry wait = PeekEligibleWait();
                DelayEntry delay = PeekEligibleDelay();
                SignalWaitTimeoutEntry signalTimeout = PeekEligibleSignalWaitTimeout();
                HostCallbackEntry hostCallback = PeekEligibleHostCallback();
                if (wait == null && delay == null && signalTimeout == null && hostCallback == null)
                {
                    break;
                }

                TimedEntry earliest = wait;
                if (earliest == null
                    || delay != null && CompareTimedEntries(delay, earliest) < 0)
                {
                    earliest = delay;
                }

                if (earliest == null
                    || signalTimeout != null
                    && CompareTimedEntries(signalTimeout, earliest) < 0)
                {
                    earliest = signalTimeout;
                }

                if (earliest == null
                    || hostCallback != null
                    && CompareTimedEntries(hostCallback, earliest) < 0)
                {
                    earliest = hostCallback;
                }

                if (ReferenceEquals(earliest, wait))
                {
                    _delayedBatchBuffer.Add(_waitHeap.Pop());
                }
                else if (ReferenceEquals(earliest, delay))
                {
                    _delayedBatchBuffer.Add(_delayHeap.Pop());
                }
                else if (ReferenceEquals(earliest, signalTimeout))
                {
                    _delayedBatchBuffer.Add(_signalWaitTimeoutHeap.Pop());
                }
                else
                {
                    _delayedBatchBuffer.Add(_hostHeap.Pop());
                }

                ThreadRecord poppedRecord = earliest.Record;
                if (poppedRecord != null && IsLiveTimedEntry(earliest))
                {
                    poppedRecord.HasTimedEntry = false;
                }
            }

            int nextIndex = 0;
            try
            {
                for (; nextIndex < _delayedBatchBuffer.Count; nextIndex++)
                {
                    TimedEntry entry = _delayedBatchBuffer[nextIndex];
                    HostCallbackEntry hostCallback = entry as HostCallbackEntry;
                    if (hostCallback != null)
                    {
                        try
                        {
                            hostCallback.Callback();
                        }
                        catch (Exception exception)
                        {
                            ReportHostFault("host callback", exception);
                        }

                        continue;
                    }

                    WaitEntry wait = entry as WaitEntry;
                    if (wait != null)
                    {
                        if (!_isStaleWait(wait))
                        {
                            double elapsed = CurrentTime - wait.ScheduledAt;
                            ResumeThread(wait.Record, new object[] { elapsed });
                        }

                        continue;
                    }

                    DelayEntry delay = entry as DelayEntry;
                    if (delay != null)
                    {
                        if (!_isStaleDelay(delay))
                        {
                            ResumeThread(delay.Record, delay.Arguments);
                        }

                        continue;
                    }

                    SignalWaitTimeoutEntry signalTimeout =
                        (SignalWaitTimeoutEntry)entry;
                    if (!_isStaleSignalTimeout(signalTimeout))
                    {
                        object[] timeoutArguments;
                        try
                        {
                            timeoutArguments = CopyArguments(signalTimeout.ResumeArguments());
                        }
                        catch (Exception exception)
                        {
                            HandleFault(signalTimeout.Record,
                                ToRbxError(exception, "signal:Wait timeout"));
                            continue;
                        }

                        ResumeThread(signalTimeout.Record, timeoutArguments);
                    }
                }
            }
            catch
            {
                RestoreDelayedBatch(nextIndex + 1);
                throw;
            }
            finally
            {
                _delayedBatchBuffer.Clear();
            }
        }

        private void RestoreDelayedBatch(int startIndex)
        {
            for (int index = startIndex; index < _delayedBatchBuffer.Count; index++)
            {
                TimedEntry entry = _delayedBatchBuffer[index];
                HostCallbackEntry hostCallback = entry as HostCallbackEntry;
                if (hostCallback != null)
                {
                    _hostHeap.Add(hostCallback);
                    continue;
                }

                WaitEntry wait = entry as WaitEntry;
                if (wait != null)
                {
                    if (!_isStaleWait(wait))
                    {
                        _waitHeap.Add(wait);
                        wait.Record.HasTimedEntry = true;
                    }

                    continue;
                }

                DelayEntry delay = entry as DelayEntry;
                if (delay != null)
                {
                    if (!_isStaleDelay(delay))
                    {
                        _delayHeap.Add(delay);
                        delay.Record.HasTimedEntry = true;
                    }

                    continue;
                }

                SignalWaitTimeoutEntry signalTimeout =
                    (SignalWaitTimeoutEntry)entry;
                if (!_isStaleSignalTimeout(signalTimeout))
                {
                    _signalWaitTimeoutHeap.Add(signalTimeout);
                    signalTimeout.Record.HasTimedEntry = true;
                }
            }
        }

        private long GetEarliestTimerFrame()
        {
            if (!_advancing || !_currentStage.HasValue || _delayedBatchStarted)
            {
                return _frameIndex + 1;
            }

            return _frameIndex;
        }

        private WaitEntry PeekEligibleWait()
        {
            if (_waitHeap.Count == 0)
            {
                return null;
            }

            WaitEntry entry = _waitHeap.Peek();
            return IsEligible(entry) ? entry : null;
        }

        private DelayEntry PeekEligibleDelay()
        {
            if (_delayHeap.Count == 0)
            {
                return null;
            }

            DelayEntry entry = _delayHeap.Peek();
            return IsEligible(entry) ? entry : null;
        }

        private SignalWaitTimeoutEntry PeekEligibleSignalWaitTimeout()
        {
            if (_signalWaitTimeoutHeap.Count == 0)
            {
                return null;
            }

            SignalWaitTimeoutEntry entry = _signalWaitTimeoutHeap.Peek();
            return IsEligible(entry) ? entry : null;
        }

        private HostCallbackEntry PeekEligibleHostCallback()
        {
            if (_hostHeap.Count == 0)
            {
                return null;
            }

            HostCallbackEntry entry = _hostHeap.Peek();
            return IsEligible(entry) ? entry : null;
        }

        private bool IsEligible(TimedEntry entry)
        {
            return entry.EarliestFrame <= _frameIndex && entry.Deadline <= CurrentTime;
        }

        private void ResumeThread(ThreadRecord record, object[] arguments)
        {
            if (!_records.ContainsKey(record.Thread))
            {
                return;
            }

            if (record.Thread.IsDead || record.Thread.Status == RbxScriptThreadStatus.Dead)
            {
                // WHY a fault and not a throw: the thread was finished outside the scheduler (a native
                // coroutine.resume or coroutine.close of a task thread). Throwing here aborted the rest
                // of the frame for every mod, and any mod could repeat it every frame on purpose.
                HandleFault(record, RbxError.BadArgument(
                    "scheduler attempted to resume a dead thread owned by mod " + record.OwnerModId,
                    "do not finish or kill a thread outside its owning scheduler; "
                    + "never coroutine.resume or coroutine.close a task thread"));
                return;
            }

            record.State = ThreadScheduleState.Running;
            RbxInstance previousTombstone =
                RbxScriptSignal.EnterTombstoneScope(record.ReadableTombstone);
            string previousRunningOwner = _runningOwnerModId;
            _runningOwnerModId = record.OwnerModId;
            ThreadRecord previousRunningRecord = _runningRecord;
            _runningRecord = record;
            // WHY cleared for the resume: the invocation's quota actor marks the one handler the
            // invocation starts. The threads that handler's code starts in turn inherit the charge
            // through the running record instead (see CreateScheduledRecord).
            string previousQuotaActor = _currentInvocationQuotaActorId;
            _currentInvocationQuotaActorId = null;
            RbxScriptThreadResumeResult result;
            try
            {
                result = record.Thread.Resume(arguments ?? EmptyArguments);
            }
            catch (Exception exception)
            {
                result = RbxScriptThreadResumeResult.Failure(
                    ToRbxError(exception, "scheduler thread resume"));
            }
            finally
            {
                _currentInvocationQuotaActorId = previousQuotaActor;
                _runningRecord = previousRunningRecord;
                _runningOwnerModId = previousRunningOwner;
                RbxScriptSignal.ExitTombstoneScope(previousTombstone);
            }

            if (!result.Succeeded)
            {
                RbxError error = result.Error ?? RbxError.BadArgument(
                    "thread adapter returned a failed resume without an RbxError",
                    "return RbxScriptThreadResumeResult.Failure with a structured error");
                if (DiedOfInducedRefusal(record, error))
                {
                    // WHY killed and not faulted: the refusal was counted and reported against the
                    // sender where it was raised; reporting it to the owner too made one client's
                    // flood quarantine the host's handler mod (A4-01).
                    KillRecord(record);
                    return;
                }

                HandleFault(record, error);
                return;
            }

            if (record.State == ThreadScheduleState.Canceled)
            {
                RetireRecord(record);
                return;
            }

            if (record.Thread.IsDead || record.Thread.Status == RbxScriptThreadStatus.Dead)
            {
                RbxError terminalFault = record.Thread is IRbxScriptThreadTerminalFault faultSource
                    ? faultSource.TerminalFault
                    : null;
                if (terminalFault != null)
                {
                    if (DiedOfInducedRefusal(record, terminalFault))
                    {
                        KillRecord(record);
                        return;
                    }

                    HandleFault(record, terminalFault);
                    return;
                }

                RetireRecord(record);
                RaiseThreadResumeSucceeded(record.OwnerModId, true);
                return;
            }

            if (record.State == ThreadScheduleState.Running)
            {
                record.State = ThreadScheduleState.Idle;
            }

            RaiseThreadResumeSucceeded(record.OwnerModId, false);
        }

        private void RaiseThreadResumeSucceeded(string ownerModId, bool completed)
        {
            Action<string, bool>[] subscribers = _threadResumeSucceededSubscribers;
            for (int index = 0; index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](ownerModId, completed);
                }
                catch (Exception exception)
                {
                    ReportHostFault("ThreadResumeSucceeded subscriber", exception);
                }
            }
        }

        private void HandleFault(ThreadRecord record, RbxError error)
        {
            RemoveQueuedWork(record);
            FinalizeFault(record, error);
        }

        private void FinalizeFault(ThreadRecord record, RbxError error)
        {
            if (!record.Thread.IsDead && record.Thread.Status != RbxScriptThreadStatus.Dead)
            {
                record.Thread.Kill();
            }

            record.DeferredArguments = null;
            record.State = ThreadScheduleState.Canceled;
            RetireRecord(record);
            ReportThreadFault(record.OwnerModId, error);
        }

        private void ReportThreadFault(string ownerModId, RbxError error)
        {
            Action<string, RbxError>[] subscribers = _threadFaultedSubscribers;
            if (subscribers.Length == 0)
            {
                HoldOrThrow(error);
                return;
            }

            for (int index = 0; index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](ownerModId, error);
                }
                catch (Exception exception)
                {
                    ReportHostFault("ThreadFaulted subscriber", exception);
                }
            }
        }

        private void ReportHostFault(string source, Exception exception)
        {
            Action<string, Exception>[] subscribers = _hostFaultedSubscribers;
            if (subscribers.Length == 0)
            {
                HoldOrThrow(exception);
                return;
            }

            Exception subscriberFailure = null;
            for (int index = 0; index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](source, exception);
                }
                catch (Exception failure)
                {
                    subscriberFailure ??= failure;
                }
            }

            if (subscriberFailure != null)
            {
                HoldOrThrow(subscriberFailure);
            }
        }

        /// <summary>
        /// Surfaces a failure nobody subscribed to observe. Outside <see cref="Advance"/> it is thrown
        /// to the caller as before; inside a frame the first one is held and rethrown once every phase
        /// has run, so one unobserved failure can no longer cut the frame short for every other mod.
        /// </summary>
        private void HoldOrThrow(Exception exception)
        {
            if (!_advancing)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
                return;
            }

            _heldFault ??= exception;
        }

        private static RbxError ToRbxError(Exception exception, string source)
        {
            if (exception is RbxError error)
            {
                return error;
            }

            return RbxError.BadArgument(
                source + " failed: " + exception.GetType().Name + ": " + exception.Message,
                "fix the failing code; the scheduler dropped only this callback and kept the frame running");
        }

        private static TDelegate[] ToSubscriberArray<TDelegate>(TDelegate combined)
            where TDelegate : Delegate
        {
            if (combined == null)
            {
                return Array.Empty<TDelegate>();
            }

            Delegate[] invocationList = combined.GetInvocationList();
            TDelegate[] subscribers = new TDelegate[invocationList.Length];
            for (int index = 0; index < invocationList.Length; index++)
            {
                subscribers[index] = (TDelegate)invocationList[index];
            }

            return subscribers;
        }

        private void KillRecord(ThreadRecord record)
        {
            RemoveQueuedWork(record);
            FinalizeKill(record);
        }

        private void FinalizeKill(ThreadRecord record)
        {
            if (!record.Thread.IsDead && record.Thread.Status != RbxScriptThreadStatus.Dead)
            {
                record.Thread.Kill();
            }

            record.DeferredArguments = null;
            record.State = ThreadScheduleState.Canceled;
            RetireRecord(record);
        }

        /// <summary>
        /// The single exit of a record from the live set: releases its induced-thread charge and raises
        /// <see cref="ThreadRetired"/> exactly once, whichever path ended the thread.
        /// </summary>
        private void RetireRecord(ThreadRecord record)
        {
            IRbxScriptThread thread = record.Thread;
            if (thread == null || !_records.TryGetValue(thread, out ThreadRecord live)
                || !ReferenceEquals(live, record))
            {
                return;
            }

            _records.Remove(thread);
            string quotaActorId = record.QuotaActorId;
            if (quotaActorId != null
                && _inducedThreadsByQuotaActor.TryGetValue(quotaActorId, out int induced))
            {
                if (induced <= 1)
                {
                    _inducedThreadsByQuotaActor.Remove(quotaActorId);
                }
                else
                {
                    _inducedThreadsByQuotaActor[quotaActorId] = induced - 1;
                }
            }

            Action<IRbxScriptThread>[] subscribers = _threadRetiredSubscribers;
            for (int index = 0; index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](thread);
                }
                catch (Exception exception)
                {
                    ReportHostFault("ThreadRetired subscriber", exception);
                }
            }
        }

        /// <summary>
        /// Detaches a record from its queued work before it is killed. The deferred queue and the timed
        /// heaps are cleaned lazily (M2-26): each of their consumers already skips an entry whose record
        /// is no longer live in the state that entry expects, so a cancel costs O(1) instead of a scan of
        /// every heap and queue. Stale heap entries are counted and compacted in amortized O(1).
        /// </summary>
        private void RemoveQueuedWork(ThreadRecord record)
        {
            lock (_completionGate)
            {
                CompletionWaitEntry completionWait = record.CompletionWait;
                if (completionWait != null)
                {
                    RemoveCompletionRegistration(completionWait);
                }
            }

            record.DeferredArguments = null;
            record.DeferredSequence = 0;
            AbandonTimedEntry(record);
        }

        private void AbandonTimedEntry(ThreadRecord record)
        {
            record.TimedSequence = 0;
            if (!record.HasTimedEntry)
            {
                return;
            }

            record.HasTimedEntry = false;
            _staleTimedEntries++;
            if (_staleTimedEntries <= MinStaleTimedEntriesBeforeCompaction
                || _staleTimedEntries * 2 <= TimedEntryCount)
            {
                return;
            }

            // WHY the state checks are re-applied here instead of trusting the counter: an entry can
            // go stale in ways the counter never sees (a killed record's entry already popped into a
            // batch), so compaction removes exactly the entries no consumer would resume.
            int touchedCount = _waitHeap.RemoveWhere(_isStaleWait);
            touchedCount += _delayHeap.RemoveWhere(_isStaleDelay);
            touchedCount += _signalWaitTimeoutHeap.RemoveWhere(_isStaleSignalTimeout);
            _staleTimedEntries = 0;
            QueuedWorkScanCount += touchedCount;
            if (_promotingCompletions)
            {
                CompletionPromotionTouchCount += touchedCount;
            }
        }

        private bool IsLiveInState(ThreadRecord record, ThreadScheduleState state)
        {
            return record.State == state
                   && record.Thread != null
                   && _records.TryGetValue(record.Thread, out ThreadRecord live)
                   && ReferenceEquals(live, record);
        }

        private bool IsLiveTimedEntry(TimedEntry entry)
        {
            switch (entry)
            {
                case WaitEntry wait:
                    return !_isStaleWait(wait);
                case DelayEntry delay:
                    return !_isStaleDelay(delay);
                case SignalWaitTimeoutEntry signalTimeout:
                    return !_isStaleSignalTimeout(signalTimeout);
                default:
                    return false;
            }
        }

        private void RemoveCompletionRegistration(CompletionWaitEntry entry)
        {
            if (_completionRegistrations.TryGetValue(entry.Completion,
                    out CompletionWaitEntry registered)
                && ReferenceEquals(registered, entry))
            {
                _completionRegistrations.Remove(entry.Completion);
            }

            _readyCompletions.Remove(entry.Sequence);
            if (ReferenceEquals(entry.Record.CompletionWait, entry))
            {
                entry.Record.CompletionWait = null;
            }
        }

        private ThreadRecord CreateRecord(string ownerModId, object callable,
            string quotaActorId = null, int quotaLimit = 0)
        {
            ValidateOwnerModId(ownerModId);
            if (callable == null)
            {
                throw RbxError.BadArgument(
                    "scheduler callable cannot be nil",
                    "pass a function or resumable thread");
            }

            string actorId = ResolveActorId(ownerModId);
            if (_records.Count >= EmergencyMaxThreads)
            {
                throw new RbxError(
                    RbxErrorCode.ThreadCap,
                    "actor '" + actorId + "' cannot create a scheduler thread for mod '"
                    + ownerModId + "': emergency live scheduler threads ceiling reached ("
                    + EmergencyMaxThreads + ")",
                    "finish or cancel live threads before scheduling more work");
            }

            if (quotaActorId == null && CountThreadsForActor(actorId) >= MaxThreadsPerActor)
            {
                // WHY the parked count is named: a thread suspended by a native coroutine.yield holds
                // its quota slot until something resumes or cancels it, and nothing else in the
                // refusal explains why an actor with no visible work is out of threads (M2-20).
                int parked = CountParkedThreadsForActor(actorId);
                throw new RbxError(
                    RbxErrorCode.ThreadCap,
                    "actor '" + actorId + "' cannot create a scheduler thread for mod '"
                    + ownerModId + "': live scheduler threads quota reached (limit "
                    + MaxThreadsPerActor + ")"
                    + (parked > 0
                        ? "; " + parked + " of them are parked outside the scheduler (suspended by "
                          + "coroutine.yield) and hold their slot until resumed or cancelled"
                        : string.Empty),
                    parked > 0
                        ? "resume parked threads with task.spawn(thread) or release them with task.cancel(thread)"
                        : "finish or cancel live threads before scheduling more work");
            }

            IRbxScriptThread thread = _threadFactory.Create(ownerModId, callable);
            if (thread == null)
            {
                throw RbxError.BadArgument(
                    "IRbxScriptThreadFactory returned nil for mod " + ownerModId,
                    "return a suspended IRbxScriptThread for every valid callable");
            }

            if (thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead)
            {
                throw RbxError.BadArgument(
                    "IRbxScriptThreadFactory returned a dead thread for mod " + ownerModId,
                    "return a new suspended thread");
            }

            if (_records.ContainsKey(thread))
            {
                throw RbxError.BadArgument(
                    "IRbxScriptThreadFactory returned a thread already owned by this scheduler",
                    "create a distinct thread for each scheduling call");
            }

            ThreadRecord record;
            if (_recordPool.Count > 0)
            {
                record = _recordPool.Pop();
                record.Reset(thread, ownerModId);
            }
            else
            {
                record = new ThreadRecord(thread, ownerModId);
            }

            _records.Add(thread, record);
            if (quotaActorId != null)
            {
                record.QuotaActorId = quotaActorId;
                record.QuotaLimit = quotaLimit;
                _inducedThreadsByQuotaActor.TryGetValue(quotaActorId, out int induced);
                _inducedThreadsByQuotaActor[quotaActorId] = induced + 1;
            }

            return record;
        }

        private int CountThreadsForActor(string actorId)
        {
            int count = 0;
            foreach (ThreadRecord record in _records.Values)
            {
                if (record.QuotaActorId == null
                    && string.Equals(ResolveActorId(record.OwnerModId), actorId,
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private int CountParkedThreadsForActor(string actorId)
        {
            int count = 0;
            foreach (ThreadRecord record in _records.Values)
            {
                if (record.QuotaActorId == null
                    && record.State == ThreadScheduleState.Idle
                    && string.Equals(ResolveActorId(record.OwnerModId), actorId,
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private string ResolveActorId(string ownerModId)
        {
            string actorId = _actorIdResolver?.Invoke(ownerModId);
            return string.IsNullOrWhiteSpace(actorId) ? "host/system" : actorId.Trim();
        }

        private ThreadRecord GetSchedulableRecord(IRbxScriptThread thread, string operation,
            bool allowRunning)
        {
            if (thread == null)
            {
                throw RbxError.BadArgument(
                    operation + " requires a caller thread",
                    "pass the live scheduler-owned thread that yielded");
            }

            if (!_records.TryGetValue(thread, out ThreadRecord record))
            {
                throw RbxError.BadArgument(
                    operation + " received a thread not owned by this scheduler",
                    "schedule the wait through the scheduler that created the thread");
            }

            if (thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead)
            {
                throw RbxError.BadArgument(
                    operation + " cannot schedule a dead thread",
                    "schedule the wait before the thread completes");
            }

            bool validState = record.State == ThreadScheduleState.Idle
                              || allowRunning && record.State == ThreadScheduleState.Running;
            if (!validState)
            {
                throw RbxError.BadArgument(
                    operation + " cannot schedule a thread already in state " + record.State,
                    "resume or cancel the existing scheduled operation first");
            }

            return record;
        }

        private static int CompareTimedEntries(TimedEntry left, TimedEntry right)
        {
            int deadline = left.Deadline.CompareTo(right.Deadline);
            if (deadline != 0)
            {
                return deadline;
            }

            int frame = left.EarliestFrame.CompareTo(right.EarliestFrame);
            return frame != 0 ? frame : left.Sequence.CompareTo(right.Sequence);
        }

        private static object[] CopyArguments(object[] args)
        {
            return args == null || args.Length == 0 ? EmptyArguments : (object[])args.Clone();
        }

        /// <summary>
        /// Normalizes a scaled duration: negative values (negative infinity included) mean zero and
        /// positive infinity is returned as is, which the callers treat as "never" (M2-17). Only NaN is
        /// refused, because it names no point in time at all.
        /// </summary>
        private static double ValidateAndNormalizeDuration(double seconds, string operation)
        {
            if (double.IsNaN(seconds))
            {
                throw RbxError.BadArgument(
                    operation + " duration must be a number, not NaN",
                    "pass a duration in scaled seconds; math.huge waits until the thread is cancelled");
            }

            return seconds < 0d ? 0d : seconds;
        }

        private static void ValidateDelta(double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0d)
            {
                throw RbxError.BadArgument(
                    "ModScheduler.Advance deltaSeconds must be finite and non-negative",
                    "pass the scaled non-negative frame delta");
            }
        }

        private static void ValidateClock(double time, string stage)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                throw RbxError.BadArgument(
                    "IRbxTimeSource returned a non-finite " + stage + " time",
                    "provide a finite monotonic scaled-time source");
            }
        }

        private static void ValidateOwnerModId(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw RbxError.BadArgument(
                    "scheduler owner mod id cannot be empty",
                    "pass the stable id of the mod that owns the thread");
            }
        }

        private long NextSequence()
        {
            _sequence++;
            return _sequence;
        }
    }
}
