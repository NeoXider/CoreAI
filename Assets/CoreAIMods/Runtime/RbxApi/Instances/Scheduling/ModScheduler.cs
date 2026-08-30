using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CoreAI.Mods.Rbx.Instances.Scheduling
{
    /// <summary>Roblox frame phases exposed by the engine-free scheduler pipeline.</summary>
    public enum SchedulerPhase
    {
        PreAnimation,
        PreSimulation,
        PostSimulation,
        Heartbeat,
        PreRender
    }

    /// <summary>
    /// Deterministic, engine-free scheduler for immediate, deferred, waiting, delayed, and
    /// host-completion-backed script threads. One <see cref="Advance"/> call executes one complete
    /// logical frame in the canonical R4.2 order.
    /// </summary>
    public sealed class ModScheduler
    {
        private enum PipelineStage
        {
            DrainDeferred,
            PreAnimation,
            PreSimulation,
            PostSimulation,
            ResumeDelayed,
            Heartbeat,
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

            public void RemoveWhere(Predicate<T> predicate)
            {
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
                    return;
                }

                _items.RemoveRange(writeIndex, _items.Count - writeIndex);
                for (int index = (_items.Count / 2) - 1; index >= 0; index--)
                {
                    SiftDown(index);
                }
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
            PipelineStage.PreAnimation,
            PipelineStage.DrainDeferred,
            PipelineStage.PreSimulation,
            PipelineStage.DrainDeferred,
            PipelineStage.PostSimulation,
            PipelineStage.DrainDeferred,
            PipelineStage.ResumeDelayed,
            PipelineStage.DrainDeferred,
            PipelineStage.Heartbeat,
            PipelineStage.DrainDeferred,
            PipelineStage.PreRender,
            PipelineStage.DrainDeferred
        };

        private readonly IRbxScriptThreadFactory _threadFactory;
        private readonly IRbxTimeSource _timeSource;
        private readonly Dictionary<IRbxScriptThread, ThreadRecord> _records =
            new(ThreadReferenceComparer.Instance);
        private readonly Queue<ThreadRecord> _deferredQueue = new();
        private readonly List<ThreadRecord> _drainBuffer = new();
        private readonly List<TimedEntry> _delayedBatchBuffer = new();
        private readonly List<CompletionWaitEntry> _completionWaits = new();
        private readonly List<CompletionWaitEntry> _completionBuffer = new();
        private readonly MinHeap<WaitEntry> _waitHeap;
        private readonly MinHeap<DelayEntry> _delayHeap;

        private long _frameIndex;
        private long _sequence;
        private bool _advancing;
        private bool _delayedBatchStarted;
        private PipelineStage? _currentStage;

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
        }

        /// <summary>Current logical frame number; the first <see cref="Advance"/> enters frame one.</summary>
        public long FrameIndex => _frameIndex;

        /// <summary>Current injected scaled scheduler time.</summary>
        public double CurrentTime => _timeSource.CurrentTime;

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

        /// <summary>Resumes an existing caller at the next deferred drain after completion.</summary>
        public void ScheduleWaitUntil(IRbxScriptThread caller, RbxSchedulerCompletion completion)
        {
            if (completion == null)
            {
                throw RbxError.BadArgument(
                    "ScheduleWaitUntil requires a completion token",
                    "pass a distinct RbxSchedulerCompletion for the host operation");
            }

            ThreadRecord record = GetSchedulableRecord(caller, "ScheduleWaitUntil", true);
            record.State = ThreadScheduleState.WaitingForCompletion;
            _completionWaits.Add(new CompletionWaitEntry(record, completion, NextSequence()));
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
                case PipelineStage.Heartbeat:
                    ReachPhase(SchedulerPhase.Heartbeat, deltaSeconds);
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

        private void PromoteCompletedWaits()
        {
            _completionBuffer.Clear();
            for (int index = _completionWaits.Count - 1; index >= 0; index--)
            {
                CompletionWaitEntry entry = _completionWaits[index];
                if (!entry.Completion.IsCompleted)
                {
                    continue;
                }

                _completionWaits.RemoveAt(index);
                _completionBuffer.Add(entry);
            }

            _completionBuffer.Reverse();
            int nextIndex = 0;
            try
            {
                for (; nextIndex < _completionBuffer.Count; nextIndex++)
                {
                    CompletionWaitEntry entry = _completionBuffer[nextIndex];
                    ThreadRecord record = entry.Record;
                    if (record.State != ThreadScheduleState.WaitingForCompletion
                        || !_records.ContainsKey(record.Thread))
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
                            HandleFault(record, entry.Completion.Error);
                            break;
                        case RbxSchedulerCompletionStatus.Canceled:
                            KillRecord(record);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch
            {
                RestoreCompletionBatch(nextIndex + 1);
                throw;
            }
            finally
            {
                _completionBuffer.Clear();
            }
        }

        private void RestoreCompletionBatch(int startIndex)
        {
            for (int index = startIndex; index < _completionBuffer.Count; index++)
            {
                CompletionWaitEntry entry = _completionBuffer[index];
                if (entry.Record.State == ThreadScheduleState.WaitingForCompletion
                    && _records.ContainsKey(entry.Record.Thread))
                {
                    _completionWaits.Add(entry);
                }
            }

            _completionWaits.Sort((CompletionWaitEntry left, CompletionWaitEntry right) =>
                left.Sequence.CompareTo(right.Sequence));
        }

        private void ResumeDelayedThreads()
        {
            _delayedBatchBuffer.Clear();
            while (true)
            {
                WaitEntry wait = PeekEligibleWait();
                DelayEntry delay = PeekEligibleDelay();
                if (wait == null && delay == null)
                {
                    break;
                }

                if (delay == null || wait != null && CompareTimedEntries(wait, delay) <= 0)
                {
                    _delayedBatchBuffer.Add(_waitHeap.Pop());
                }
                else
                {
                    _delayedBatchBuffer.Add(_delayHeap.Pop());
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

                    DelayEntry delay = (DelayEntry)entry;
                    if (delay.Record.State == ThreadScheduleState.Delayed
                        && _records.ContainsKey(delay.Record.Thread))
                    {
                        ResumeThread(delay.Record, delay.Arguments);
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

                DelayEntry delay = (DelayEntry)entry;
                if (delay.Record.State == ThreadScheduleState.Delayed
                    && _records.ContainsKey(delay.Record.Thread))
                {
                    _delayHeap.Add(delay);
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
            RbxScriptThreadResumeResult result = record.Thread.Resume(arguments ?? EmptyArguments);
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
            if (!record.Thread.IsDead && record.Thread.Status != RbxScriptThreadStatus.Dead)
            {
                record.Thread.Kill();
            }

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
            if (!record.Thread.IsDead && record.Thread.Status != RbxScriptThreadStatus.Dead)
            {
                record.Thread.Kill();
            }

            record.State = ThreadScheduleState.Canceled;
            _records.Remove(record.Thread);
        }

        private void RemoveQueuedWork(ThreadRecord record)
        {
            _waitHeap.RemoveWhere(entry => ReferenceEquals(entry.Record, record));
            _delayHeap.RemoveWhere(entry => ReferenceEquals(entry.Record, record));
            for (int index = _completionWaits.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(_completionWaits[index].Record, record))
                {
                    _completionWaits.RemoveAt(index);
                }
            }

            int deferredCount = _deferredQueue.Count;
            for (int index = 0; index < deferredCount; index++)
            {
                ThreadRecord candidate = _deferredQueue.Dequeue();
                if (!ReferenceEquals(candidate, record))
                {
                    _deferredQueue.Enqueue(candidate);
                }
            }

            record.DeferredArguments = null;
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
