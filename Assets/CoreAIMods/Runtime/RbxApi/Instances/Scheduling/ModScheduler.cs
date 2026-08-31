using System;
using System.Collections.Generic;
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
    /// Deterministic, engine-free scheduler for immediate, deferred, waiting, delayed, and
    /// host-completion-backed script threads. One <see cref="Advance"/> call executes one complete
    /// logical frame in the canonical R4.2 order.
    /// </summary>
    public sealed class ModScheduler
    {
        public const int DefaultMaxThreadsPerActor = 256;
        public const int EmergencyMaxThreads = 4096;
        public const int MaxSignalGenerations = 10;

        private enum PipelineStage
        {
            DrainDeferred,
            PreAnimation,
            PreSimulation,
            PostSimulation,
            ResumeDelayed,
            DrainSignals,
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

        private sealed class ThreadRecord
        {
            public ThreadRecord(IRbxScriptThread thread, string ownerModId)
            {
                Thread = thread;
                OwnerModId = ownerModId;
            }

            public IRbxScriptThread Thread { get; }

            public string OwnerModId { get; }

            public ThreadScheduleState State { get; set; }

            public object[] DeferredArguments { get; set; }

            public CompletionWaitEntry CompletionWait { get; set; }

            public RbxInstance ReadableTombstone { get; set; }

            public long SignalWaitGeneration { get; set; }
        }

        private sealed class SignalInvocation
        {
            public SignalInvocation(RbxScriptConnection connection, object[] arguments,
                RbxInstance readableTombstone, int generation, string[] chain)
            {
                Connection = connection;
                Arguments = arguments;
                ReadableTombstone = readableTombstone;
                Generation = generation;
                Chain = chain;
            }

            public RbxScriptConnection Connection { get; }

            public object[] Arguments { get; }

            public RbxInstance ReadableTombstone { get; }

            public int Generation { get; }

            public string[] Chain { get; }
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
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.PreAnimation,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.PreSimulation,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.PostSimulation,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.ResumeDelayed,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.Heartbeat,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.InputProcessing,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals,
            PipelineStage.PreRender,
            PipelineStage.DrainDeferred,
            PipelineStage.DrainSignals
        };

        private readonly IRbxScriptThreadFactory _threadFactory;
        private readonly IRbxTimeSource _timeSource;
        private readonly Dictionary<IRbxScriptThread, ThreadRecord> _records =
            new(ThreadReferenceComparer.Instance);
        private readonly Queue<ThreadRecord> _deferredQueue = new();
        private readonly List<ThreadRecord> _drainBuffer = new();
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
        private Func<string, string> _actorIdResolver;

        private long _frameIndex;
        private long _sequence;
        private bool _advancing;
        private bool _delayedBatchStarted;
        private bool _promotingCompletions;
        private bool _drainingSignals;
        private int _currentSignalGeneration;
        private string[] _currentSignalChain;
        private string[] _signalCascadeChain;
        private RbxInstance _currentSignalTombstone;
        private PipelineStage? _currentStage;

        /// <summary>Configured per-actor live-thread quota.</summary>
        public int MaxThreadsPerActor { get; private set; } = DefaultMaxThreadsPerActor;

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
        }

        /// <summary>Configures actor attribution and the per-actor live-thread quota.</summary>
        public void ConfigureActorQuota(int maxThreadsPerActor, Func<string, string> actorIdResolver)
        {
            MaxThreadsPerActor = Math.Max(1, maxThreadsPerActor);
            _actorIdResolver = actorIdResolver;
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

        internal int LiveThreadCount => _records.Count;

        /// <summary>Raised at each observable phase boundary in canonical pipeline order.</summary>
        public event Action<SchedulerPhase, double> PhaseReached;

        /// <summary>
        /// Raised after a failed thread has been killed and unregistered. If no subscriber exists,
        /// the structured error is thrown so the fault cannot disappear silently.
        /// </summary>
        public event Action<string, RbxError> ThreadFaulted;

        /// <summary>Creates and immediately resumes a thread to its first yield or completion.</summary>
        public IRbxScriptThread Spawn(string ownerModId, object callable, object[] args)
        {
            ThreadRecord record = CreateRecord(ownerModId, callable);
            ResumeThread(record, CopyArguments(args));
            return record.Thread;
        }

        /// <summary>Creates a scheduler-owned signal callback with its destruction tombstone scope.</summary>
        internal IRbxScriptThread SpawnSignal(string ownerModId, object callable, object[] args)
        {
            ThreadRecord record = CreateRecord(ownerModId, callable);
            record.ReadableTombstone = _currentSignalTombstone;
            ResumeThread(record, CopyArguments(args));
            return record.Thread;
        }

        /// <summary>Creates a thread for the next deferred resumption point.</summary>
        public IRbxScriptThread Defer(string ownerModId, object callable, object[] args)
        {
            ThreadRecord record = CreateRecord(ownerModId, callable);
            record.State = ThreadScheduleState.Deferred;
            record.DeferredArguments = CopyArguments(args);
            _deferredQueue.Enqueue(record);
            return record.Thread;
        }

        /// <summary>Creates a thread for the next eligible delayed slot.</summary>
        public IRbxScriptThread Delay(string ownerModId, double seconds, object callable, object[] args)
        {
            double duration = ValidateAndNormalizeDuration(seconds, "Delay");
            ThreadRecord record = CreateRecord(ownerModId, callable);
            record.State = ThreadScheduleState.Delayed;
            DelayEntry entry = new(record, CopyArguments(args), CurrentTime + duration,
                GetEarliestTimerFrame(), NextSequence());
            _delayHeap.Add(entry);
            return record.Thread;
        }

        /// <summary>
        /// Schedules an existing caller to resume on the first eligible future delayed slot with its
        /// actual scaled elapsed time as the sole argument.
        /// </summary>
        public void ScheduleWait(IRbxScriptThread caller, double seconds = 0d)
        {
            double duration = ValidateAndNormalizeDuration(seconds, "ScheduleWait");
            ThreadRecord record = GetSchedulableRecord(caller, "ScheduleWait", true);
            double scheduledAt = CurrentTime;
            record.State = ThreadScheduleState.Waiting;
            WaitEntry entry = new(record, scheduledAt, scheduledAt + duration,
                GetEarliestTimerFrame(), NextSequence());
            _waitHeap.Add(entry);
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
            SignalWaitTimeoutEntry entry = new(record, record.SignalWaitGeneration,
                timeoutResumeArguments, CurrentTime + duration, GetEarliestTimerFrame(),
                NextSequence());
            _signalWaitTimeoutHeap.Add(entry);
        }

        /// <summary>Resumes one signal waiter with the arguments captured at fire time.</summary>
        internal void ResumeSignalWait(IRbxScriptThread caller, object[] arguments)
        {
            if (caller == null || !_records.TryGetValue(caller, out ThreadRecord record)
                || record.State != ThreadScheduleState.WaitingForSignal)
            {
                return;
            }

            record.ReadableTombstone = _currentSignalTombstone;
            ResumeThread(record, CopyArguments(arguments));
        }

        /// <summary>Queues one connection invocation for the deferred signal drain.</summary>
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
            string[] chain = BuildSignalChain(connection.SignalName);
            if (generation > MaxSignalGenerations)
            {
                _signalCascadeChain ??= chain;
                connection.DropQueuedInvocation();
                return;
            }

            _signalQueue.Enqueue(new SignalInvocation(
                connection, CopyArguments(arguments), readableTombstone, generation, chain));
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

        /// <summary>Cancels one live scheduler-owned thread and removes all pending work.</summary>
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
                throw RbxError.BadArgument(
                    "task.cancel cannot cancel a dead thread",
                    "retain and cancel the thread before it completes");
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

        /// <summary>Advances scaled time and executes one complete logical frame.</summary>
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
            }
        }

        private void RunPipelineStage(PipelineStage stage, double deltaSeconds)
        {
            switch (stage)
            {
                case PipelineStage.DrainDeferred:
                    DrainDeferred();
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
                case PipelineStage.DrainSignals:
                    DrainSignals();
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
            Action<SchedulerPhase, double> handler = PhaseReached;
            handler?.Invoke(phase, deltaSeconds);
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
                    ThreadRecord record = _drainBuffer[nextIndex];
                    if (record.State != ThreadScheduleState.Deferred
                        || !_records.ContainsKey(record.Thread))
                    {
                        continue;
                    }

                    object[] arguments = record.DeferredArguments;
                    record.DeferredArguments = null;
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
            List<ThreadRecord> newlyDeferred = new(_deferredQueue.Count);
            while (_deferredQueue.Count > 0)
            {
                newlyDeferred.Add(_deferredQueue.Dequeue());
            }

            for (int index = startIndex; index < _drainBuffer.Count; index++)
            {
                ThreadRecord record = _drainBuffer[index];
                if (record.State == ThreadScheduleState.Deferred
                    && _records.ContainsKey(record.Thread))
                {
                    _deferredQueue.Enqueue(record);
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
            _signalCascadeChain = null;
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
                        invocation.Connection.InvokePending(invocation.Arguments);
                    }

                    if (_signalCascadeChain != null)
                    {
                        _signalQueue.Clear();
                        throw new RbxError(
                            RbxErrorCode.SignalCascade,
                            "signal cascade exceeded " + MaxSignalGenerations
                            + " generations: " + string.Join(" -> ", _signalCascadeChain),
                            "break the signal cycle or defer the next mutation to a later frame");
                    }
                }
            }
            finally
            {
                _signalDrainBuffer.Clear();
                _currentSignalGeneration = 0;
                _currentSignalChain = null;
                _currentSignalTombstone = null;
                _signalCascadeChain = null;
                _drainingSignals = false;
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
                            _deferredQueue.Enqueue(record);
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
                if (wait == null && delay == null && signalTimeout == null)
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

                if (ReferenceEquals(earliest, wait))
                {
                    _delayedBatchBuffer.Add(_waitHeap.Pop());
                }
                else if (ReferenceEquals(earliest, delay))
                {
                    _delayedBatchBuffer.Add(_delayHeap.Pop());
                }
                else
                {
                    _delayedBatchBuffer.Add(_signalWaitTimeoutHeap.Pop());
                }
            }

            int nextIndex = 0;
            try
            {
                for (; nextIndex < _delayedBatchBuffer.Count; nextIndex++)
                {
                    TimedEntry entry = _delayedBatchBuffer[nextIndex];
                    WaitEntry wait = entry as WaitEntry;
                    if (wait != null)
                    {
                        if (wait.Record.State == ThreadScheduleState.Waiting
                            && _records.ContainsKey(wait.Record.Thread))
                        {
                            double elapsed = CurrentTime - wait.ScheduledAt;
                            ResumeThread(wait.Record, new object[] { elapsed });
                        }

                        continue;
                    }

                    DelayEntry delay = entry as DelayEntry;
                    if (delay != null)
                    {
                        if (delay.Record.State == ThreadScheduleState.Delayed
                            && _records.ContainsKey(delay.Record.Thread))
                        {
                            ResumeThread(delay.Record, delay.Arguments);
                        }

                        continue;
                    }

                    SignalWaitTimeoutEntry signalTimeout =
                        (SignalWaitTimeoutEntry)entry;
                    if (signalTimeout.Record.State == ThreadScheduleState.WaitingForSignal
                        && signalTimeout.Record.SignalWaitGeneration == signalTimeout.Generation
                        && _records.ContainsKey(signalTimeout.Record.Thread))
                    {
                        ResumeThread(signalTimeout.Record,
                            CopyArguments(signalTimeout.ResumeArguments()));
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
                WaitEntry wait = entry as WaitEntry;
                if (wait != null)
                {
                    if (wait.Record.State == ThreadScheduleState.Waiting
                        && _records.ContainsKey(wait.Record.Thread))
                    {
                        _waitHeap.Add(wait);
                    }

                    continue;
                }

                DelayEntry delay = entry as DelayEntry;
                if (delay != null)
                {
                    if (delay.Record.State == ThreadScheduleState.Delayed
                        && _records.ContainsKey(delay.Record.Thread))
                    {
                        _delayHeap.Add(delay);
                    }

                    continue;
                }

                SignalWaitTimeoutEntry signalTimeout =
                    (SignalWaitTimeoutEntry)entry;
                if (signalTimeout.Record.State == ThreadScheduleState.WaitingForSignal
                    && signalTimeout.Record.SignalWaitGeneration == signalTimeout.Generation
                    && _records.ContainsKey(signalTimeout.Record.Thread))
                {
                    _signalWaitTimeoutHeap.Add(signalTimeout);
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
                _records.Remove(record.Thread);
                throw RbxError.BadArgument(
                    "scheduler attempted to resume a dead thread owned by mod " + record.OwnerModId,
                    "do not finish or kill a thread outside its owning scheduler");
            }

            record.State = ThreadScheduleState.Running;
            RbxInstance previousTombstone =
                RbxScriptSignal.EnterTombstoneScope(record.ReadableTombstone);
            RbxScriptThreadResumeResult result;
            try
            {
                result = record.Thread.Resume(arguments ?? EmptyArguments);
            }
            finally
            {
                RbxScriptSignal.ExitTombstoneScope(previousTombstone);
            }

            if (!result.Succeeded)
            {
                RbxError error = result.Error ?? RbxError.BadArgument(
                    "thread adapter returned a failed resume without an RbxError",
                    "return RbxScriptThreadResumeResult.Failure with a structured error");
                HandleFault(record, error);
                return;
            }

            if (record.Thread.IsDead || record.Thread.Status == RbxScriptThreadStatus.Dead)
            {
                _records.Remove(record.Thread);
                return;
            }

            if (record.State == ThreadScheduleState.Running)
            {
                record.State = ThreadScheduleState.Idle;
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
            _records.Remove(record.Thread);
            Action<string, RbxError> handler = ThreadFaulted;
            if (handler == null)
            {
                throw error;
            }

            handler(record.OwnerModId, error);
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
            _records.Remove(record.Thread);
        }

        private void RemoveQueuedWork(ThreadRecord record)
        {
            int touchedCount = _waitHeap.RemoveWhere(
                entry => ReferenceEquals(entry.Record, record));
            touchedCount += _delayHeap.RemoveWhere(
                entry => ReferenceEquals(entry.Record, record));
            touchedCount += _signalWaitTimeoutHeap.RemoveWhere(
                entry => ReferenceEquals(entry.Record, record));
            lock (_completionGate)
            {
                CompletionWaitEntry completionWait = record.CompletionWait;
                if (completionWait != null)
                {
                    RemoveCompletionRegistration(completionWait);
                }
            }

            int deferredCount = _deferredQueue.Count;
            touchedCount += deferredCount;
            for (int index = 0; index < deferredCount; index++)
            {
                ThreadRecord candidate = _deferredQueue.Dequeue();
                if (!ReferenceEquals(candidate, record))
                {
                    _deferredQueue.Enqueue(candidate);
                }
            }

            record.DeferredArguments = null;
            if (_promotingCompletions)
            {
                CompletionPromotionTouchCount += touchedCount;
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

        private ThreadRecord CreateRecord(string ownerModId, object callable)
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

            if (CountThreadsForActor(actorId) >= MaxThreadsPerActor)
            {
                throw new RbxError(
                    RbxErrorCode.ThreadCap,
                    "actor '" + actorId + "' cannot create a scheduler thread for mod '"
                    + ownerModId + "': live scheduler threads quota reached (limit "
                    + MaxThreadsPerActor + ")",
                    "finish or cancel live threads before scheduling more work");
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

            ThreadRecord record = new(thread, ownerModId);
            _records.Add(thread, record);
            return record;
        }

        private int CountThreadsForActor(string actorId)
        {
            int count = 0;
            foreach (ThreadRecord record in _records.Values)
            {
                if (string.Equals(ResolveActorId(record.OwnerModId), actorId,
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

        private static double ValidateAndNormalizeDuration(double seconds, string operation)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                throw RbxError.BadArgument(
                    operation + " duration must be finite",
                    "pass a finite duration in scaled seconds");
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
