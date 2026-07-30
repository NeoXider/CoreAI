using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


namespace CoreAI.Ai
{
    /// <summary>
    /// Adds priority queueing, concurrency limits, and cancellation scopes around an orchestrator.
    /// </summary>
    public sealed class QueuedAiOrchestrator : IAiOrchestrationService, IDisposable
    {
        private readonly IAiOrchestrationService _inner;
        private readonly int _maxConcurrent;
        private readonly int _maxPending;

        // WHY: BUG-1 fix: single lock for both queue and scope operations to eliminate deadlock risk.
        // Previously _scopeLock was nested inside _queueLock in some paths but taken independently
        // in ReleaseScopeToken, creating an inconsistent lock ordering hazard.
        private readonly object _lock = new();
        private readonly List<WorkItem> _pending = new();
        private readonly List<StreamWorkItem> _streamPending = new();
        private readonly Dictionary<string, CancellationTokenSource> _scopeTokens = new(StringComparer.Ordinal);

        // WHY: F-10: cancelled on Dispose() so in-flight work observes teardown even though its own
        // caller/scope token was never cancelled. Never disposed (only Cancel()'d): work already
        // reading its .Token racing with a Dispose()-time Dispose() call would risk ObjectDisposedException;
        // an unreferenced, timer-less CTS is reclaimed by GC once this orchestrator is collected.
        private readonly CancellationTokenSource _lifetimeCts = new();

        private static readonly IComparer<WorkItem> WorkItemComparer = Comparer<WorkItem>.Create(CompareWorkItems);

        private static readonly IComparer<StreamWorkItem> StreamWorkItemComparer =
            Comparer<StreamWorkItem>.Create(CompareStreamWorkItems);

        private int _inFlight;
        private long _nextSequence;
        private bool _disposed;

        /// <param name="inner">The inner value.</param>
        /// <param name="options">The options value.</param>
        public QueuedAiOrchestrator(IAiOrchestrationService inner, AiOrchestrationQueueOptions options)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            int max = options?.MaxConcurrent ?? 2;
            _maxConcurrent = max < 1 ? 1 : max;
            int maxPending = options?.MaxPending ?? 64;
            _maxPending = maxPending < 1 ? 1 : maxPending;
        }

        /// <inheritdoc />
        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QueuedAiOrchestrator));
            }

            TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkItem work = new()
            {
                Task = task ?? new AiTaskRequest(),
                OuterCt = cancellationToken,
                Tcs = tcs,
                Priority = task?.Priority ?? 0,
                Sequence = NextSequence()
            };

            if (cancellationToken.IsCancellationRequested)
            {
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

            StreamWorkItem work = new()
            {
                Task = task ?? new AiTaskRequest(),
                OuterCt = cancellationToken,
                Queue = queue,
                ConsumerCancellation = consumerCancellation,
                Priority = task?.Priority ?? 0,
                Sequence = NextSequence()
            };

            if (cancellationToken.IsCancellationRequested)
            {
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
                    bool hasTask = _pending.Count > 0;
                    bool hasStream = _streamPending.Count > 0;
                    if (hasTask && (!hasStream || ComesBefore(_pending[0], _streamPending[0])))
                    {
                        WorkItem w = _pending[0];
                        _pending.RemoveAt(0);
                        _inFlight++;
                        readyTasks ??= new List<WorkItem>();
                        readyTasks.Add(w);
                        continue;
                    }

                    if (hasStream)
                    {
                        StreamWorkItem sw = _streamPending[0];
                        _streamPending.RemoveAt(0);
                        _inFlight++;
                        readyStreams ??= new List<StreamWorkItem>();
                        readyStreams.Add(sw);
                        continue;
                    }

                    break;
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

        private async Task RunOneAsync(WorkItem w)
        {
            // WHY: This blocks until a concurrently running CancelPending callback returns, and that
            // callback waits for _lock - so this method must never be started while _lock is held.
            // See Pump().
            w.PendingCancellation.Dispose();
            CancellationTokenSource linkedCts = null;
            try
            {
                CancellationToken baseToken = w.ScopeCancellation?.Token ?? w.OuterCt;
                // WHY: Link the orchestrator's lifetime signal so Dispose() cancels in-flight work even
                // when neither the caller nor the cancellation scope token was ever cancelled.
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken, _lifetimeCts.Token);
                CancellationToken token = linkedCts.Token;
                token.ThrowIfCancellationRequested();
                // WHY: WebGL player: keep continuation on Unity SynchronizationContext.
                // ConfigureAwait(false) on single-threaded IL2CPP queues to TaskScheduler.Default
#if UNITY_WEBGL && !UNITY_EDITOR
                string result = await _inner.RunTaskAsync(w.Task, token);
#else
                string result = await _inner.RunTaskAsync(w.Task, token).ConfigureAwait(false);
#endif
                w.Tcs.TrySetResult(result);
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
                    _inFlight--;
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
                CancellationToken baseToken = w.ScopeCancellation?.Token ?? w.OuterCt;
                // WHY: Always link the lifetime signal (Dispose() teardown) and, when present, the
                // consumer-abandonment signal so breaking enumeration without cancelling the caller
                // token still stops the inner stream.
                linkedCts = w.ConsumerCancellation != null
                    ? CancellationTokenSource.CreateLinkedTokenSource(
                        baseToken, w.ConsumerCancellation.Token, _lifetimeCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(baseToken, _lifetimeCts.Token);
                CancellationToken token = linkedCts.Token;

                token.ThrowIfCancellationRequested();
                await foreach (LlmStreamChunk chunk in _inner.RunStreamingAsync(w.Task, token))
                {
                    w.Queue.Write(chunk);
                }

                w.Queue.Complete();
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
                    _inFlight--;
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

            // WHY: Cancelled by the public RunStreamingAsync iterator's finally when the consumer stops
            // enumerating (including an early break that does not cancel its own token). The producer
            // links its inner-stream token to this so it stops draining instead of running off-screen.
            public CancellationTokenSource ConsumerCancellation;
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope)
        {
            if (string.IsNullOrWhiteSpace(cancellationScope))
            {
                return;
            }

            string scopeKey = cancellationScope.Trim();
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            CancellationTokenSource activeToCancel = null;

            lock (_lock)
            {
                removedPending = _pending.FindAll(w =>
                    string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                removedStreamPending = _streamPending.FindAll(w =>
                    string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                _pending.RemoveAll(w =>
                    string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                _streamPending.RemoveAll(w =>
                    string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));

                if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                {
                    activeToCancel = prev;
                    _scopeTokens.Remove(scopeKey);
                }
            }

            // WHY: BUG-2 fix: Cancel outside lock, guarded against concurrent Dispose from ReleaseScopeToken.
            SafeCancel(activeToCancel);

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

        private void Enqueue(WorkItem work)
        {
            if (work.OuterCt.IsCancellationRequested)
            {
                work.PendingCancellation.Dispose();
                work.Tcs.TrySetCanceled(work.OuterCt);
                return;
            }

            string scopeKey = work.Task.CancellationScope?.Trim();
            CancellationTokenSource activeToCancel = null;
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            bool rejected = false;

            lock (_lock)
            {
                // WHY: F-10: bounded admission instead of an unbounded queue - reject fast rather than
                // growing _pending/_streamPending without limit under sustained offline/slow-LLM load.
                if (_pending.Count + _streamPending.Count >= _maxPending)
                {
                    rejected = true;
                }
                else
                {
                    InsertSorted(_pending, work, WorkItemComparer);
                    if (!string.IsNullOrEmpty(scopeKey))
                    {
                        work.ScopeKey = scopeKey;
                        work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            activeToCancel = prev;
                        }

                        _scopeTokens[scopeKey] = work.ScopeCancellation;

                        removedPending = _pending.FindAll(w =>
                            !ReferenceEquals(w, work) &&
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                        removedStreamPending = _streamPending.FindAll(w =>
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));

                        _pending.RemoveAll(w =>
                            !ReferenceEquals(w, work) &&
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                        _streamPending.RemoveAll(w =>
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                    }
                }
            }

            if (rejected)
            {
                work.PendingCancellation.Dispose();
                work.Tcs.TrySetException(new AiOrchestrationQueueFullException(_maxPending));
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
                work.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                work.Queue.Complete();
                return;
            }

            string scopeKey = work.Task.CancellationScope?.Trim();
            CancellationTokenSource activeToCancel = null;
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            bool rejected = false;

            lock (_lock)
            {
                if (_pending.Count + _streamPending.Count >= _maxPending)
                {
                    rejected = true;
                }
                else
                {
                    InsertSorted(_streamPending, work, StreamWorkItemComparer);
                    if (!string.IsNullOrEmpty(scopeKey))
                    {
                        work.ScopeKey = scopeKey;
                        work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            activeToCancel = prev;
                        }

                        _scopeTokens[scopeKey] = work.ScopeCancellation;

                        removedPending = _pending.FindAll(w =>
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                        removedStreamPending = _streamPending.FindAll(w =>
                            !ReferenceEquals(w, work) &&
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));

                        _pending.RemoveAll(w =>
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                        _streamPending.RemoveAll(w =>
                            !ReferenceEquals(w, work) &&
                            string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                    }
                }
            }

            if (rejected)
            {
                work.PendingCancellation.Dispose();
                work.Queue.Write(new LlmStreamChunk
                {
                    IsDone = true,
                    Error = $"AI orchestration queue is full (MaxPending={_maxPending}).",
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

        private void CancelPending(WorkItem work)
        {
            bool removed;
            lock (_lock)
            {
                removed = _pending.Remove(work);
            }

            if (removed)
            {
                ReleaseScopeToken(work.ScopeKey, work.ScopeCancellation);
                work.Tcs.TrySetCanceled(work.OuterCt);
            }
        }

        private void CancelPending(StreamWorkItem work)
        {
            bool removed;
            lock (_lock)
            {
                removed = _streamPending.Remove(work);
            }

            if (removed)
            {
                ReleaseScopeToken(work.ScopeKey, work.ScopeCancellation);
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
                    w.Tcs.TrySetCanceled();
                }
            }

            if (removedStreamPending != null)
            {
                foreach (StreamWorkItem w in removedStreamPending)
                {
                    w.PendingCancellation.Dispose();
                    ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);
                    w.Queue.Write(new LlmStreamChunk { IsDone = true, Error = "cancelled" });
                    w.Queue.Complete();
                }
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
                if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource cur) &&
                    ReferenceEquals(cur, scopeCancellation))
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
                scopeTokens = new List<CancellationTokenSource>(_scopeTokens.Values);
                _scopeTokens.Clear();

                drainedPending = new List<WorkItem>(_pending);
                _pending.Clear();

                drainedStreamPending = new List<StreamWorkItem>(_streamPending);
                _streamPending.Clear();
            }

            // WHY: Pending work never got a chance to run: resolve it now instead of leaving it forever
            // un-awaited. Scope CTS ownership already moved into `scopeTokens` above.
            foreach (WorkItem w in drainedPending)
            {
                w.PendingCancellation.Dispose();
                w.Tcs.TrySetException(new ObjectDisposedException(nameof(QueuedAiOrchestrator)));
            }

            foreach (StreamWorkItem w in drainedStreamPending)
            {
                w.PendingCancellation.Dispose();
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
