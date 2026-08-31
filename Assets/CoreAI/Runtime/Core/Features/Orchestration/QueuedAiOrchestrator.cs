using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Logging;


namespace CoreAI.Ai
{
    /// <summary>
    /// Adds per-actor fair queueing, concurrency limits, and cancellation scopes around an orchestrator.
    /// </summary>
    public sealed class QueuedAiOrchestrator : IAiOrchestrationService, IAiActorContextResolver,
        IScopedAiTaskCancellation, IDisposable
    {
        private readonly IAiOrchestrationService _inner;
        private readonly IAiActorContextResolver _actorContextResolver;
        private readonly IUnstartedAiTurnRecorder _unstartedTurnRecorder;
        private readonly IAgentMemoryScopeProvider _scopeProvider;
        private readonly int _maxConcurrent;
        private readonly int _maxPending;

        // WHY: BUG-1 fix: single lock for both queue and scope operations to eliminate deadlock risk.
        // Previously _scopeLock was nested inside _queueLock in some paths but taken independently
        // in ReleaseScopeToken, creating an inconsistent lock ordering hazard.
        private readonly object _lock = new();
        private readonly List<WorkItem> _pending = new();
        private readonly List<StreamWorkItem> _streamPending = new();
        private readonly Dictionary<string, ActorQueueState> _actorQueues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScopeEntry> _scopeTokens = new(StringComparer.Ordinal);

        // WHY: F-10: cancelled on Dispose() so in-flight work observes teardown even though its own
        // caller/scope token was never cancelled. Never disposed (only Cancel()'d): work already
        // reading its .Token racing with a Dispose()-time Dispose() call would risk ObjectDisposedException;
        // an unreferenced, timer-less CTS is reclaimed by GC once this orchestrator is collected.
        private readonly CancellationTokenSource _lifetimeCts = new();

        private static readonly IComparer<WorkItem> WorkItemComparer = Comparer<WorkItem>.Create(CompareWorkItems);

        private static readonly IComparer<StreamWorkItem> StreamWorkItemComparer =
            Comparer<StreamWorkItem>.Create(CompareStreamWorkItems);

        private int _inFlight;
        private long _fairDispatchOrdinal;
        private long _nextSequence;
        private bool _disposed;

        /// <param name="inner">The inner value.</param>
        /// <param name="options">The options value.</param>
        public QueuedAiOrchestrator(
            IAiOrchestrationService inner,
            AiOrchestrationQueueOptions options,
            IAgentMemoryScopeProvider scopeProvider = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _actorContextResolver = inner as IAiActorContextResolver;
            _unstartedTurnRecorder = inner as IUnstartedAiTurnRecorder;
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
            int max = options?.MaxConcurrent ?? 2;
            _maxConcurrent = max < 1 ? 1 : max;
            int maxPending = options?.MaxPending ?? 64;
            _maxPending = maxPending < 1 ? 1 : maxPending;
        }

        /// <summary>The configured maximum number of concurrent provider calls.</summary>
        public int MaxConcurrent => _maxConcurrent;

        /// <summary>The configured maximum number of pending requests.</summary>
        public int MaxPending => _maxPending;

        /// <summary>
        /// Maximum number of other actor admissions that can occur while an admitted actor remains pending.
        /// </summary>
        public long MaximumActorBypasses => (long)_maxPending + _maxConcurrent - 1L;

        /// <inheritdoc />
        public ActorContext ResolveActorContext(AiTaskRequest task)
        {
            ActorContext? actorContext = CaptureActorContext(task);
            if (!actorContext.HasValue)
            {
                throw new InvalidOperationException(
                    "Queued AI orchestration requires an actor-aware inner service or an explicit actor context.");
            }

            return actorContext.Value;
        }

        /// <inheritdoc />
        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QueuedAiOrchestrator));
            }

            TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AiTaskRequest effectiveTask = task ?? new AiTaskRequest();
            ActorContext? actorContext = CaptureActorContext(effectiveTask);
            WorkItem work = new()
            {
                Task = effectiveTask,
                OuterCt = cancellationToken,
                Tcs = tcs,
                Priority = effectiveTask.Priority,
                Sequence = NextSequence(),
                ActorContext = actorContext,
                ActorId = ResolveAdmissionActorId(actorContext),
                MemoryScope = actorContext.HasValue
                    ? actorContext.Value.MemoryScope
                    : CaptureMemoryScope(effectiveTask.RoleId)
            };

            if (cancellationToken.IsCancellationRequested)
            {
                RecordUnstartedTurn(work, "pre-cancelled");
                tcs.TrySetCanceled(cancellationToken);
                return tcs.Task;
            }

            if (cancellationToken.CanBeCanceled)
            {
                work.PendingCancellation = cancellationToken.Register(() => CancelPending(work));
            }

            Enqueue(work);

            return tcs.Task;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            AiTaskRequest task,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QueuedAiOrchestrator));
            }

            AsyncChunkQueue queue = new();
            CancellationTokenSource consumerCancellation = new();
            AiTaskRequest effectiveTask = task ?? new AiTaskRequest();
            ActorContext? actorContext = CaptureActorContext(effectiveTask);

            StreamWorkItem work = new()
            {
                Task = effectiveTask,
                OuterCt = cancellationToken,
                Queue = queue,
                ConsumerCancellation = consumerCancellation,
                Priority = effectiveTask.Priority,
                Sequence = NextSequence(),
                ActorContext = actorContext,
                ActorId = ResolveAdmissionActorId(actorContext),
                MemoryScope = actorContext.HasValue
                    ? actorContext.Value.MemoryScope
                    : CaptureMemoryScope(effectiveTask.RoleId)
            };

            if (cancellationToken.IsCancellationRequested)
            {
                RecordUnstartedTurn(work, "pre-cancelled stream");
                yield return new LlmStreamChunk { IsDone = true, Error = "cancelled" };
                yield break;
            }

            if (cancellationToken.CanBeCanceled)
            {
                work.PendingCancellation = cancellationToken.Register(() => CancelPending(work));
            }

            Enqueue(work);

            if (queue.IsCompleted)
            {
                foreach (LlmStreamChunk chunk in queue.DrainSync())
                {
                    yield return chunk;
                }

                yield break;
            }

            try
            {
                // WHY: Drain with CancellationToken.None on purpose: when the caller cancels, the producer
                // observes it, writes the terminal "cancelled" chunk and completes the queue, and the
                // consumer must deliver that terminal chunk rather than bail out early. Consumer
                // abandonment (break without cancelling) is handled by the finally below.
                await foreach (LlmStreamChunk chunk in ReadStreamingQueue(queue))
                {
                    yield return chunk;
                }
            }
            finally
            {
                // WHY: Consumer finished or abandoned enumeration (e.g. broke out of the await foreach without
                // cancelling its own token). Signal the producer so it stops draining the inner LLM stream
                // into the unbounded queue instead of running silently to completion. Intentionally not
                // disposed: a producer that starts after this point still links to its token, and a CTS
                // with no timer and no allocated WaitHandle is reclaimed by GC once unreferenced. Nothing
                // disposes consumerCancellation, so Cancel() cannot throw ObjectDisposedException here.
                consumerCancellation.Cancel();
                CancelPending(work);
            }
        }

        /// <summary>
        /// Claims free concurrency slots under <see cref="_lock"/>, then starts the claimed work
        /// <b>after</b> the lock is released. Never call this while already holding <see cref="_lock"/>.
        /// </summary>
        /// <remarks>
        /// WHY: Starting work inside the lock ran <see cref="RunOneAsync"/> synchronously up to (and, for an
        /// inner service that completes synchronously, past) its first await while the lock was held. Its
        /// first statement disposes the pending-cancellation registration, which blocks until a concurrently
        /// running <c>CancelPending</c> callback returns - and that callback waits for this very lock, so the
        /// two wedged each other permanently. Slots are claimed (<c>_inFlight++</c>) before the release, so
        /// the concurrency limit still holds while the start is in flight.
        /// </remarks>
        private void Pump()
        {
            List<WorkItem> readyTasks = null;
            List<StreamWorkItem> readyStreams = null;

            lock (_lock)
            {
                while (_inFlight < _maxConcurrent)
                {
                    string actorId = SelectNextActorIdLocked();
                    if (actorId == null)
                    {
                        break;
                    }

                    int taskIndex = FindNextTaskIndexLocked(actorId);
                    int streamIndex = FindNextStreamIndexLocked(actorId);
                    bool hasTask = taskIndex >= 0;
                    bool hasStream = streamIndex >= 0;
                    if (hasTask && (!hasStream || ComesBefore(_pending[taskIndex], _streamPending[streamIndex])))
                    {
                        WorkItem w = _pending[taskIndex];
                        _pending.RemoveAt(taskIndex);
                        MarkActorDispatchedLocked(actorId);
                        readyTasks ??= new List<WorkItem>();
                        readyTasks.Add(w);
                        continue;
                    }

                    StreamWorkItem sw = _streamPending[streamIndex];
                    _streamPending.RemoveAt(streamIndex);
                    MarkActorDispatchedLocked(actorId);
                    readyStreams ??= new List<StreamWorkItem>();
                    readyStreams.Add(sw);
                }
            }

            if (readyTasks != null)
            {
                foreach (WorkItem w in readyTasks)
                {
                    _ = RunOneAsync(w);
                }
            }

            if (readyStreams != null)
            {
                foreach (StreamWorkItem sw in readyStreams)
                {
                    _ = RunOneStreamingAsync(sw);
                }
            }
        }

        private string SelectNextActorIdLocked()
        {
            ActorQueueState selected = null;
            foreach (ActorQueueState candidate in _actorQueues.Values)
            {
                if (candidate.PendingCount == 0 || !ComesBefore(candidate, selected))
                {
                    continue;
                }

                selected = candidate;
            }

            return selected?.ActorId;
        }

        private static bool ComesBefore(ActorQueueState candidate, ActorQueueState selected)
        {
            if (selected == null)
            {
                return true;
            }

            if (candidate.LastDispatchOrdinal != selected.LastDispatchOrdinal)
            {
                return candidate.LastDispatchOrdinal < selected.LastDispatchOrdinal;
            }

            if (candidate.HasBeenDispatched != selected.HasBeenDispatched)
            {
                return !candidate.HasBeenDispatched;
            }

            return candidate.ActivationSequence < selected.ActivationSequence;
        }

        private int FindNextTaskIndexLocked(string actorId)
        {
            for (int index = 0; index < _pending.Count; index++)
            {
                if (string.Equals(_pending[index].ActorId, actorId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private int FindNextStreamIndexLocked(string actorId)
        {
            for (int index = 0; index < _streamPending.Count; index++)
            {
                if (string.Equals(_streamPending[index].ActorId, actorId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private void MarkActorDispatchedLocked(string actorId)
        {
            ActorQueueState state = _actorQueues[actorId];
            state.PendingCount--;
            state.InFlightCount++;
            state.HasBeenDispatched = true;
            state.LastDispatchOrdinal = ++_fairDispatchOrdinal;
            _inFlight++;
        }

        private void ReleaseActorDispatchLocked(string actorId)
        {
            _inFlight--;
            if (!_actorQueues.TryGetValue(actorId, out ActorQueueState state))
            {
                return;
            }

            state.InFlightCount--;
            RemoveIdleActorLocked(state);
        }

        private async Task RunOneAsync(WorkItem w)
        {
            // WHY: This blocks until a concurrently running CancelPending callback returns, and that
            // callback waits for _lock - so this method must never be started while _lock is held.
            // See Pump().
            w.PendingCancellation.Dispose();
            CancellationTokenSource linkedCts = null;
            try
            {
                using (w.ActorContext.HasValue
                           ? AgentMemoryScopeExecutionContext.Push(w.ActorContext.Value)
                           : AgentMemoryScopeExecutionContext.Push(w.MemoryScope))
                {
                    CancellationToken baseToken = w.ScopeCancellation?.Token ?? w.OuterCt;
                    // WHY: Link the orchestrator's lifetime signal so Dispose() cancels in-flight work even
                    // when neither the caller nor the cancellation scope token was ever cancelled.
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken, _lifetimeCts.Token);
                    CancellationToken token = linkedCts.Token;
                    if (token.IsCancellationRequested)
                    {
                        // WHY: Pump already removed the item, so CancelPending can no longer own persistence.
                        // Finish it here before inner starts; if cancellation arrives after this check, inner is
                        // always invoked and owns the normal per-invocation teardown instead.
                        RecordUnstartedTurn(w, "cancelled after queue claim");
                        w.Tcs.TrySetCanceled(token);
                        return;
                    }

                    // WHY: WebGL player: keep continuation on Unity SynchronizationContext.
                    // ConfigureAwait(false) on single-threaded IL2CPP queues to TaskScheduler.Default
#if UNITY_WEBGL && !UNITY_EDITOR
                    string result = await _inner.RunTaskAsync(w.Task, token);
#else
                    string result = await _inner.RunTaskAsync(w.Task, token).ConfigureAwait(false);
#endif
                    w.Tcs.TrySetResult(result);
                }
            }
            catch (Exception ex) when (IsCancellationLike(ex))
            {
                w.Tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                w.Tcs.TrySetException(ex);
            }
            finally
            {
                linkedCts?.Dispose();
                ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);

                lock (_lock)
                {
                    ReleaseActorDispatchLocked(w.ActorId);
                }

                Pump();
            }
        }

        private async Task RunOneStreamingAsync(StreamWorkItem w)
        {
            // WHY: Blocking dispose - see RunOneAsync. This method must never be started under _lock.
            w.PendingCancellation.Dispose();
            CancellationTokenSource linkedCts = null;
            try
            {
                using (w.ActorContext.HasValue
                           ? AgentMemoryScopeExecutionContext.Push(w.ActorContext.Value)
                           : AgentMemoryScopeExecutionContext.Push(w.MemoryScope))
                {
                    CancellationToken baseToken = w.ScopeCancellation?.Token ?? w.OuterCt;
                    // WHY: Always link the lifetime signal (Dispose() teardown) and, when present, the
                    // consumer-abandonment signal so breaking enumeration without cancelling the caller
                    // token still stops the inner stream.
                    linkedCts = w.ConsumerCancellation != null
                        ? CancellationTokenSource.CreateLinkedTokenSource(
                            baseToken, w.ConsumerCancellation.Token, _lifetimeCts.Token)
                        : CancellationTokenSource.CreateLinkedTokenSource(baseToken, _lifetimeCts.Token);
                    CancellationToken token = linkedCts.Token;

                    if (token.IsCancellationRequested)
                    {
                        RecordUnstartedTurn(w, "stream cancelled after queue claim");
                        w.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                        w.Queue.Complete();
                        return;
                    }

                    await foreach (LlmStreamChunk chunk in _inner.RunStreamingAsync(w.Task, token))
                    {
                        w.Queue.Write(chunk);
                    }

                    w.Queue.Complete();
                }
            }
            catch (Exception ex) when (IsCancellationLike(ex))
            {
                w.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                w.Queue.Complete();
            }
            catch (Exception ex)
            {
                w.Queue.Write(new LlmStreamChunk { IsDone = true, Error = ex.Message });
                w.Queue.Complete();
            }
            finally
            {
                linkedCts?.Dispose();
                ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);

                lock (_lock)
                {
                    ReleaseActorDispatchLocked(w.ActorId);
                }

                Pump();
            }
        }

        private sealed class WorkItem
        {
            public AiTaskRequest Task;
            public CancellationToken OuterCt;
            public TaskCompletionSource<string> Tcs;
            public int Priority;
            public long Sequence;
            public CancellationTokenRegistration PendingCancellation;
            public string ScopeKey;
            public CancellationTokenSource ScopeCancellation;
            public ActorContext? ActorContext;
            public string ActorId;
            public AgentMemoryScope MemoryScope;
            public int UnstartedPersistenceAttempted;
        }

        private sealed class StreamWorkItem
        {
            public AiTaskRequest Task;
            public CancellationToken OuterCt;
            public AsyncChunkQueue Queue;
            public int Priority;
            public long Sequence;
            public CancellationTokenRegistration PendingCancellation;
            public string ScopeKey;
            public CancellationTokenSource ScopeCancellation;
            public ActorContext? ActorContext;
            public string ActorId;
            public AgentMemoryScope MemoryScope;
            public int UnstartedPersistenceAttempted;

            // WHY: Cancelled by the public RunStreamingAsync iterator's finally when the consumer stops
            // enumerating (including an early break that does not cancel its own token). The producer
            // links its inner-stream token to this so it stops draining instead of running off-screen.
            public CancellationTokenSource ConsumerCancellation;
        }

        private sealed class ActorQueueState
        {
            public string ActorId;
            public long ActivationSequence;
            public long LastDispatchOrdinal;
            public bool HasBeenDispatched;
            public int PendingCount;
            public int InFlightCount;
        }

        private sealed class ScopeEntry
        {
            public string CancellationScope;
            public string RoleId;
            public ActorContext? ActorContext;
            public CancellationTokenSource Cancellation;
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope)
        {
            if (string.IsNullOrWhiteSpace(cancellationScope))
            {
                return;
            }

            CancelScopeKeys(ResolveCurrentScopeKeys(cancellationScope.Trim(), null));
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope, string roleId)
        {
            if (string.IsNullOrWhiteSpace(cancellationScope))
            {
                return;
            }

            CancelScopeKeys(ResolveCurrentScopeKeys(
                cancellationScope.Trim(),
                NormalizeRoleId(roleId)));
        }

        private List<string> ResolveCurrentScopeKeys(string cancellationScope, string roleId)
        {
            List<KeyValuePair<string, ScopeEntry>> candidates = new();
            lock (_lock)
            {
                foreach (KeyValuePair<string, ScopeEntry> pair in _scopeTokens)
                {
                    if (string.Equals(
                            pair.Value.CancellationScope,
                            cancellationScope,
                            StringComparison.Ordinal) &&
                        (roleId == null || string.Equals(pair.Value.RoleId, roleId, StringComparison.Ordinal)))
                    {
                        candidates.Add(pair);
                    }
                }
            }

            List<string> currentScopeKeys = new();
            foreach (KeyValuePair<string, ScopeEntry> pair in candidates)
            {
                if (pair.Value.ActorContext.HasValue)
                {
                    string actorScopeKey = ResolveActorCancellationScopeKey(
                        pair.Value.ActorContext.Value.SessionId,
                        pair.Value.RoleId);
                    if (string.Equals(
                            pair.Key,
                            actorScopeKey,
                            StringComparison.Ordinal))
                    {
                        currentScopeKeys.Add(pair.Key);
                    }

                    continue;
                }

                AgentMemoryScope currentScope = CaptureMemoryScope(pair.Value.RoleId);
                string currentKey = ResolveLegacyCancellationScopeKey(
                    cancellationScope,
                    pair.Value.RoleId,
                    currentScope);
                if (string.Equals(pair.Key, currentKey, StringComparison.Ordinal))
                {
                    currentScopeKeys.Add(pair.Key);
                }
            }

            return currentScopeKeys;
        }

        private void CancelScopeKeys(IReadOnlyCollection<string> scopeKeys)
        {
            if (scopeKeys == null || scopeKeys.Count == 0)
            {
                return;
            }

            HashSet<string> keySet = new(scopeKeys, StringComparer.Ordinal);
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            List<CancellationTokenSource> activeToCancel = new();

            lock (_lock)
            {
                removedPending = _pending.FindAll(w =>
                    keySet.Contains(w.ScopeKey));
                removedStreamPending = _streamPending.FindAll(w =>
                    keySet.Contains(w.ScopeKey));
                _pending.RemoveAll(w =>
                    keySet.Contains(w.ScopeKey));
                _streamPending.RemoveAll(w =>
                    keySet.Contains(w.ScopeKey));

                foreach (WorkItem removed in removedPending)
                {
                    RemovePendingActorLocked(removed.ActorId);
                }

                foreach (StreamWorkItem removed in removedStreamPending)
                {
                    RemovePendingActorLocked(removed.ActorId);
                }

                foreach (string scopeKey in keySet)
                {
                    if (_scopeTokens.TryGetValue(scopeKey, out ScopeEntry entry))
                    {
                        activeToCancel.Add(entry.Cancellation);
                        _scopeTokens.Remove(scopeKey);
                    }
                }
            }

            foreach (CancellationTokenSource cancellation in activeToCancel)
            {
                SafeCancel(cancellation);
            }

            CancelRemovedPending(removedPending, removedStreamPending);

            Pump();
        }

        private long NextSequence()
        {
            return Interlocked.Increment(ref _nextSequence);
        }

        private static int CompareWorkItems(WorkItem a, WorkItem b)
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
        }

        private static int CompareStreamWorkItems(StreamWorkItem a, StreamWorkItem b)
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
        }

        private static bool ComesBefore(WorkItem task, StreamWorkItem stream)
        {
            if (task.Priority != stream.Priority)
            {
                return task.Priority > stream.Priority;
            }

            return task.Sequence < stream.Sequence;
        }

        /// <summary>Inserts into a list kept sorted by <paramref name="comparer"/> via binary search
        /// instead of re-sorting the whole list on every enqueue.</summary>
        private static void InsertSorted<T>(List<T> list, T item, IComparer<T> comparer)
        {
            int index = list.BinarySearch(item, comparer);
            if (index < 0)
            {
                index = ~index;
            }

            list.Insert(index, item);
        }

        private void AddPendingActorLocked(string actorId, long activationSequence)
        {
            if (!_actorQueues.TryGetValue(actorId, out ActorQueueState state))
            {
                state = new ActorQueueState
                {
                    ActorId = actorId,
                    ActivationSequence = activationSequence,
                    LastDispatchOrdinal = _fairDispatchOrdinal
                };
                _actorQueues.Add(actorId, state);
            }

            state.PendingCount++;
        }

        private void RemovePendingActorLocked(string actorId)
        {
            if (!_actorQueues.TryGetValue(actorId, out ActorQueueState state))
            {
                return;
            }

            state.PendingCount--;
            RemoveIdleActorLocked(state);
        }

        private void RemoveIdleActorLocked(ActorQueueState state)
        {
            if (state.PendingCount == 0 && state.InFlightCount == 0)
            {
                _actorQueues.Remove(state.ActorId);
            }
        }

        private void Enqueue(WorkItem work)
        {
            if (work.OuterCt.IsCancellationRequested)
            {
                work.PendingCancellation.Dispose();
                RecordUnstartedTurn(work, "cancelled before task admission");
                work.Tcs.TrySetCanceled(work.OuterCt);
                return;
            }

            string scopeKey = ResolveCancellationScopeKey(
                work.Task,
                work.ActorContext,
                work.MemoryScope);
            work.ScopeKey = scopeKey;
            CancellationTokenSource activeToCancel = null;
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            bool rejected = false;

            lock (_lock)
            {
                List<WorkItem> replaceablePending = null;
                List<StreamWorkItem> replaceableStreamPending = null;
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    replaceablePending = _pending.FindAll(w =>
                        string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                    replaceableStreamPending = _streamPending.FindAll(w =>
                        string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                }

                int replaceableCount = (replaceablePending?.Count ?? 0) +
                                       (replaceableStreamPending?.Count ?? 0);
                int projectedPending = _pending.Count + _streamPending.Count - replaceableCount;
                if (projectedPending >= _maxPending)
                {
                    rejected = true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(scopeKey))
                    {
                        work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                        if (_scopeTokens.TryGetValue(scopeKey, out ScopeEntry previous))
                        {
                            activeToCancel = previous.Cancellation;
                        }

                        removedPending = replaceablePending;
                        removedStreamPending = replaceableStreamPending;
                        _pending.RemoveAll(w =>
                            string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                        _streamPending.RemoveAll(w =>
                            string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));

                        foreach (WorkItem removed in removedPending)
                        {
                            RemovePendingActorLocked(removed.ActorId);
                        }

                        foreach (StreamWorkItem removed in removedStreamPending)
                        {
                            RemovePendingActorLocked(removed.ActorId);
                        }

                        _scopeTokens[scopeKey] = CreateScopeEntry(
                            work.Task,
                            work.ActorContext,
                            work.ScopeCancellation);
                    }

                    InsertSorted(_pending, work, WorkItemComparer);
                    AddPendingActorLocked(work.ActorId, work.Sequence);
                }
            }

            if (rejected)
            {
                work.PendingCancellation.Dispose();
                RecordUnstartedTurn(work, "task queue full");
                work.Tcs.TrySetException(new AiOrchestrationQueueFullException(work.ActorId, _maxPending));
                return;
            }

            // WHY: BUG-2 fix: Cancel outside lock, guarded against concurrent Dispose.
            SafeCancel(activeToCancel);
            CancelRemovedPending(removedPending, removedStreamPending);

            Pump();
        }

        private void Enqueue(StreamWorkItem work)
        {
            if (work.OuterCt.IsCancellationRequested)
            {
                work.PendingCancellation.Dispose();
                RecordUnstartedTurn(work, "cancelled before stream admission");
                work.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                work.Queue.Complete();
                return;
            }

            string scopeKey = ResolveCancellationScopeKey(
                work.Task,
                work.ActorContext,
                work.MemoryScope);
            work.ScopeKey = scopeKey;
            CancellationTokenSource activeToCancel = null;
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            bool rejected = false;

            lock (_lock)
            {
                List<WorkItem> replaceablePending = null;
                List<StreamWorkItem> replaceableStreamPending = null;
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    replaceablePending = _pending.FindAll(w =>
                        string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                    replaceableStreamPending = _streamPending.FindAll(w =>
                        string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                }

                int replaceableCount = (replaceablePending?.Count ?? 0) +
                                       (replaceableStreamPending?.Count ?? 0);
                int projectedPending = _pending.Count + _streamPending.Count - replaceableCount;
                if (projectedPending >= _maxPending)
                {
                    rejected = true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(scopeKey))
                    {
                        work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                        if (_scopeTokens.TryGetValue(scopeKey, out ScopeEntry previous))
                        {
                            activeToCancel = previous.Cancellation;
                        }

                        removedPending = replaceablePending;
                        removedStreamPending = replaceableStreamPending;
                        _pending.RemoveAll(w =>
                            string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));
                        _streamPending.RemoveAll(w =>
                            string.Equals(w.ScopeKey, scopeKey, StringComparison.Ordinal));

                        foreach (WorkItem removed in removedPending)
                        {
                            RemovePendingActorLocked(removed.ActorId);
                        }

                        foreach (StreamWorkItem removed in removedStreamPending)
                        {
                            RemovePendingActorLocked(removed.ActorId);
                        }

                        _scopeTokens[scopeKey] = CreateScopeEntry(
                            work.Task,
                            work.ActorContext,
                            work.ScopeCancellation);
                    }

                    InsertSorted(_streamPending, work, StreamWorkItemComparer);
                    AddPendingActorLocked(work.ActorId, work.Sequence);
                }
            }

            if (rejected)
            {
                work.PendingCancellation.Dispose();
                RecordUnstartedTurn(work, "stream queue full");
                AiOrchestrationQueueFullException rejection =
                    new(work.ActorId, _maxPending);
                work.Queue.Write(new LlmStreamChunk
                {
                    IsDone = true,
                    Error = rejection.Message,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                });
                work.Queue.Complete();
                return;
            }

            // WHY: BUG-2 fix: Cancel outside lock, guarded against concurrent Dispose.
            SafeCancel(activeToCancel);
            CancelRemovedPending(removedPending, removedStreamPending);

            Pump();
        }

        private static string ResolveCancellationScopeKey(
            AiTaskRequest task,
            ActorContext? actorContext,
            AgentMemoryScope memoryScope)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.CancellationScope))
            {
                return null;
            }

            return actorContext.HasValue
                ? ResolveActorCancellationScopeKey(actorContext.Value.SessionId, task.RoleId)
                : ResolveLegacyCancellationScopeKey(task.CancellationScope, task.RoleId, memoryScope);
        }

        private static string ResolveActorCancellationScopeKey(string sessionId, string roleId)
        {
            string normalizedSessionId = sessionId?.Trim() ?? "";
            string normalizedRoleId = NormalizeRoleId(roleId);
            return normalizedSessionId.Length + ":" + normalizedSessionId + normalizedRoleId;
        }

        private static string ResolveLegacyCancellationScopeKey(
            string cancellationScope,
            string roleId,
            AgentMemoryScope memoryScope)
        {
            if (string.IsNullOrWhiteSpace(cancellationScope))
            {
                return null;
            }

            string logicalScope = cancellationScope.Trim();
            string normalizedRole = string.IsNullOrWhiteSpace(roleId)
                ? BuiltInAgentRoleIds.Creator
                : roleId.Trim();
            string identityKey = AgentMemoryScopeKey.Resolve(memoryScope, normalizedRole);
            if (string.Equals(identityKey, normalizedRole, StringComparison.Ordinal))
            {
                return logicalScope; // empty/default identity scope: exact legacy queue semantics
            }

            return logicalScope + "::" + identityKey;
        }

        private AgentMemoryScope CaptureMemoryScope(string roleId)
        {
            string normalizedRole = NormalizeRoleId(roleId);
            return _scopeProvider.GetScope(normalizedRole);
        }

        private ActorContext? CaptureActorContext(AiTaskRequest task)
        {
            ActorContext? actorContext = task?.ActorContext;
            if (!actorContext.HasValue && task != null && _actorContextResolver != null)
            {
                actorContext = _actorContextResolver.ResolveActorContext(task);
            }

            if (!actorContext.HasValue)
            {
                return null;
            }

            ActorContext trusted = actorContext.Value;
            trusted.AssertTrusted();
            task.ActorContext = trusted;
            return trusted;
        }

        private static string ResolveAdmissionActorId(ActorContext? actorContext)
        {
            return actorContext.HasValue
                ? actorContext.Value.ActorId
                : LocalActorIdentityProvider.DefaultActorId;
        }

        private static ScopeEntry CreateScopeEntry(
            AiTaskRequest task,
            ActorContext? actorContext,
            CancellationTokenSource cancellation)
        {
            return new ScopeEntry
            {
                CancellationScope = actorContext.HasValue
                    ? actorContext.Value.SessionId
                    : task.CancellationScope.Trim(),
                RoleId = NormalizeRoleId(task.RoleId),
                ActorContext = actorContext,
                Cancellation = cancellation
            };
        }

        private static string NormalizeRoleId(string roleId)
        {
            return string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
        }

        private void CancelPending(WorkItem work)
        {
            bool removed;
            lock (_lock)
            {
                removed = _pending.Remove(work);
                if (removed)
                {
                    RemovePendingActorLocked(work.ActorId);
                }
            }

            if (removed)
            {
                ReleaseScopeToken(work.ScopeKey, work.ScopeCancellation);
                RecordUnstartedTurn(work, "pending task cancelled");
                work.Tcs.TrySetCanceled(work.OuterCt);
            }
        }

        private void CancelPending(StreamWorkItem work)
        {
            bool removed;
            lock (_lock)
            {
                removed = _streamPending.Remove(work);
                if (removed)
                {
                    RemovePendingActorLocked(work.ActorId);
                }
            }

            if (removed)
            {
                ReleaseScopeToken(work.ScopeKey, work.ScopeCancellation);
                RecordUnstartedTurn(work, "pending stream cancelled");
                work.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                work.Queue.Complete();
            }
        }

        private void CancelRemovedPending(
            List<WorkItem> removedPending,
            List<StreamWorkItem> removedStreamPending)
        {
            if (removedPending != null)
            {
                foreach (WorkItem w in removedPending)
                {
                    w.PendingCancellation.Dispose();
                    ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);
                    RecordUnstartedTurn(w, "pending scoped task cancelled");
                    w.Tcs.TrySetCanceled();
                }
            }

            if (removedStreamPending != null)
            {
                foreach (StreamWorkItem w in removedStreamPending)
                {
                    w.PendingCancellation.Dispose();
                    ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);
                    RecordUnstartedTurn(w, "pending scoped stream cancelled");
                    w.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                    w.Queue.Complete();
                }
            }
        }

        private void RecordUnstartedTurn(WorkItem work, string outcome)
        {
            if (work == null || Interlocked.Exchange(ref work.UnstartedPersistenceAttempted, 1) != 0)
            {
                return;
            }

            using (work.ActorContext.HasValue
                       ? AgentMemoryScopeExecutionContext.Push(work.ActorContext.Value)
                       : AgentMemoryScopeExecutionContext.Push(work.MemoryScope))
            {
                RecordUnstartedTurn(work.Task, outcome);
            }
        }

        private void RecordUnstartedTurn(StreamWorkItem work, string outcome)
        {
            if (work == null || Interlocked.Exchange(ref work.UnstartedPersistenceAttempted, 1) != 0)
            {
                return;
            }

            using (work.ActorContext.HasValue
                       ? AgentMemoryScopeExecutionContext.Push(work.ActorContext.Value)
                       : AgentMemoryScopeExecutionContext.Push(work.MemoryScope))
            {
                RecordUnstartedTurn(work.Task, outcome);
            }
        }

        private void RecordUnstartedTurn(AiTaskRequest task, string outcome)
        {
            if (_unstartedTurnRecorder == null)
            {
                return;
            }

            try
            {
                _unstartedTurnRecorder.RecordUnstartedUserTurn(task);
            }
            catch (Exception ex)
            {
                // WHY: a best-effort history append must never replace queue cancellation, rejection or dispose.
                Log.Instance.Warn(
                    $"[QueuedAiOrchestrator] Could not persist {outcome} user turn: {ex.Message}",
                    LogTag.Llm);
            }
        }

        /// <summary>
        /// BUG-1 fix: uses the single <see cref="_lock"/> instead of the removed _scopeLock.
        /// </summary>
        private void ReleaseScopeToken(string scopeKey, CancellationTokenSource scopeCancellation)
        {
            if (string.IsNullOrEmpty(scopeKey) || scopeCancellation == null)
            {
                return;
            }

            lock (_lock)
            {
                if (_scopeTokens.TryGetValue(scopeKey, out ScopeEntry current) &&
                    ReferenceEquals(current.Cancellation, scopeCancellation))
                {
                    _scopeTokens.Remove(scopeKey);
                }
            }

            SafeDispose(scopeCancellation);
        }

        /// <summary>
        /// Some runtimes / stacks surface cancellation as <see cref="AggregateException"/> or types that do not
        /// inherit <see cref="OperationCanceledException"/> the way modern .NET does. Map those to a clean cancel
        /// on the public <see cref="Task"/> from <see cref="RunTaskAsync"/> instead of faulting the work item.
        /// </summary>
        private static bool IsCancellationLike(Exception ex)
        {
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is OperationCanceledException)
                {
                    return true;
                }

                if (cur is TaskCanceledException)
                {
                    return true;
                }
            }

            if (ex is AggregateException agg)
            {
                foreach (Exception inner in agg.InnerExceptions)
                {
                    if (IsCancellationLike(inner))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// BUG-2 fix: Cancel a CTS that may have already been disposed by a concurrent ReleaseScopeToken.
        /// </summary>
        private static void SafeCancel(CancellationTokenSource cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Safe dispose that swallows ObjectDisposedException (double-dispose guard).
        /// </summary>
        private static void SafeDispose(CancellationTokenSource cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static async IAsyncEnumerable<LlmStreamChunk> ReadStreamingQueue(AsyncChunkQueue queue)
        {
            while (true)
            {
                // WHY: No ConfigureAwait(false): WebGL has no working ThreadPool, and the
                // continuation must come back through UnitySynchronizationContext.
                // CancellationToken.None on purpose: termination is driven by the producer completing the
                // queue (it writes a terminal "cancelled" chunk on cancellation), so the consumer drains
                // to completion instead of dropping that terminal chunk on the caller's cancel.
                (bool hasValue, LlmStreamChunk chunk) = await queue.TryTakeAsync(CancellationToken.None);
                if (!hasValue)
                {
                    yield break;
                }

                yield return chunk;
            }
        }

        /// <summary>
        /// ARCH-5 / F-10: cancels in-flight work via the lifetime token, completes every still-pending
        /// task/stream (so callers never await forever past scene teardown), and disposes outstanding
        /// scope CancellationTokenSources. Safe to call multiple times. After this call, <see cref="RunTaskAsync"/>
        /// and <see cref="RunStreamingAsync"/> throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // WHY: Unblocks any in-flight RunOneAsync/RunOneStreamingAsync await - both link their token to
            // this, so an inner provider call that ignores its own caller/scope token still observes
            // teardown. Their existing IsCancellationLike catch blocks then complete the work item / write
            // a terminal "cancelled" chunk exactly as they do for a normal cancellation.
            _lifetimeCts.Cancel();

            List<CancellationTokenSource> scopeTokens;
            List<WorkItem> drainedPending;
            List<StreamWorkItem> drainedStreamPending;
            lock (_lock)
            {
                scopeTokens = new List<CancellationTokenSource>();
                foreach (ScopeEntry entry in _scopeTokens.Values)
                {
                    scopeTokens.Add(entry.Cancellation);
                }

                _scopeTokens.Clear();

                drainedPending = new List<WorkItem>(_pending);
                _pending.Clear();

                drainedStreamPending = new List<StreamWorkItem>(_streamPending);
                _streamPending.Clear();
                _actorQueues.Clear();
            }

            // WHY: Pending work never got a chance to run: resolve it now instead of leaving it forever
            // un-awaited. Scope CTS ownership already moved into `scopeTokens` above.
            foreach (WorkItem w in drainedPending)
            {
                w.PendingCancellation.Dispose();
                RecordUnstartedTurn(w, "task queue disposed");
                w.Tcs.TrySetException(new ObjectDisposedException(nameof(QueuedAiOrchestrator)));
            }

            foreach (StreamWorkItem w in drainedStreamPending)
            {
                w.PendingCancellation.Dispose();
                RecordUnstartedTurn(w, "stream queue disposed");
                w.Queue.Write(new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "AI orchestration queue disposed.",
                    ErrorCode = LlmErrorCode.BackendUnavailable
                });
                w.Queue.Complete();
            }

            foreach (CancellationTokenSource cts in scopeTokens)
            {
                SafeDispose(cts);
            }
        }

        /// <summary>
        /// Minimal async queue used to bridge streamed chunks between producer and consumer tasks.
        /// </summary>
        private sealed class AsyncChunkQueue
        {
            private readonly ConcurrentQueue<LlmStreamChunk> _queue = new();
            private readonly object _signalLock = new();

            private TaskCompletionSource<bool> _signalTcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private volatile bool _completed;

            public bool IsCompleted => _completed;

            public void Write(LlmStreamChunk chunk)
            {
                _queue.Enqueue(chunk);
                FireSignal();
            }

            public void Complete()
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                FireSignal();
            }

            public List<LlmStreamChunk> DrainSync()
            {
                List<LlmStreamChunk> result = new();
                while (_queue.TryDequeue(out LlmStreamChunk chunk))
                {
                    result.Add(chunk);
                }

                return result;
            }

            public async Task<(bool hasValue, LlmStreamChunk chunk)> TryTakeAsync(CancellationToken ct)
            {
                while (true)
                {
                    if (_queue.TryDequeue(out LlmStreamChunk chunk))
                    {
                        return (true, chunk);
                    }

                    if (_completed)
                    {
                        return (false, default);
                    }

                    Task waitTask;
                    lock (_signalLock)
                    {
                        // WHY: Re-check inside lock to close the race between Write/Complete and the
                        // we await would be missed and the reader would park forever.
                        if (_queue.TryDequeue(out LlmStreamChunk chunk2))
                        {
                            return (true, chunk2);
                        }

                        if (_completed)
                        {
                            return (false, default);
                        }

                        waitTask = _signalTcs.Task;
                    }

                    if (ct.CanBeCanceled)
                    {
                        Task cancelTask = Task.Delay(Timeout.Infinite, ct);
                        await Task.WhenAny(waitTask, cancelTask);
                        ct.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        await waitTask;
                    }
                }
            }

            private void FireSignal()
            {
                TaskCompletionSource<bool> toFire;
                lock (_signalLock)
                {
                    toFire = _signalTcs;
                    _signalTcs = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                toFire.TrySetResult(true);
            }
        }
    }
}
