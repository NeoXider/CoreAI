using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Ограничение параллелизма, приоритет очереди и отмена предыдущей задачи с тем же <see cref="AiTaskRequest.CancellationScope"/>.
    /// </summary>
    public sealed class QueuedAiOrchestrator : IAiOrchestrationService
    {
        private readonly IAiOrchestrationService _inner;
        private readonly int _maxConcurrent;
        private readonly object _queueLock = new();
        private readonly List<WorkItem> _pending = new();
        private readonly List<StreamWorkItem> _streamPending = new();
        private readonly object _scopeLock = new();
        private readonly Dictionary<string, CancellationTokenSource> _scopeTokens = new(StringComparer.Ordinal);
        private int _inFlight;
        private long _nextSequence;

        /// <param name="inner">Фактический оркестратор (обычно <see cref="AiOrchestrator"/>).</param>
        /// <param name="options">Лимит параллелизма и пр.</param>
        public QueuedAiOrchestrator(IAiOrchestrationService inner, AiOrchestrationQueueOptions options)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            int max = options?.MaxConcurrent ?? 2;
            _maxConcurrent = max < 1 ? 1 : max;
        }

        /// <inheritdoc />
        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // AsyncChunkQueue — портативная producer/consumer очередь, работает без
            // System.Threading.Channels (который недоступен в Unity-сборке CoreAI).
            // Worker (RunOneStreamingAsync) пишет чанки, а здесь мы их вычитываем
            // по мере поступления. Очередь уважает MaxConcurrent и CancellationScope
            // так же, как и RunTaskAsync — через общую _inFlight логику.
            AsyncChunkQueue queue = new();

            StreamWorkItem work = new()
            {
                Task = task ?? new AiTaskRequest(),
                OuterCt = cancellationToken,
                Queue = queue,
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

            // ВАЖНО: читаем без cancellationToken. Отмена уже распространяется на
            // _inner.RunStreamingAsync через w.OuterCt; worker (RunOneStreamingAsync)
            // сам запишет терминальный чанк { Error="cancelled" } и вызовет Complete(),
            // что корректно завершит это чтение. Если бы мы передавали cancellationToken
            // в TryTakeAsync, reader бросил бы OperationCanceledException ДО того, как
            // worker успевал записать терминальный chunk — вызывающий код потерял бы
            // финальный статус стрима.
            await foreach (LlmStreamChunk chunk in ReadStreamingQueue(queue))
            {
                yield return chunk;
            }
        }

        private void TryPumpLocked()
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
                    _ = RunOneAsync(w);
                    continue;
                }

                if (hasStream)
                {
                    StreamWorkItem sw = _streamPending[0];
                    _streamPending.RemoveAt(0);
                    _inFlight++;
                    _ = RunOneStreamingAsync(sw);
                    continue;
                }

                break;
            }
        }

        private async Task RunOneAsync(WorkItem w)
        {
            w.PendingCancellation.Dispose();
            try
            {
                CancellationToken token = w.ScopeCancellation?.Token ?? w.OuterCt;
                token.ThrowIfCancellationRequested();
                string result = await _inner.RunTaskAsync(w.Task, token);
                w.Tcs.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                w.Tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                w.Tcs.TrySetException(ex);
            }
            finally
            {
                ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);

                lock (_queueLock)
                {
                    _inFlight--;
                    TryPumpLocked();
                }
            }
        }

        private async Task RunOneStreamingAsync(StreamWorkItem w)
        {
            w.PendingCancellation.Dispose();
            try
            {
                CancellationToken token = w.ScopeCancellation?.Token ?? w.OuterCt;
                token.ThrowIfCancellationRequested();
                await foreach (LlmStreamChunk chunk in _inner.RunStreamingAsync(w.Task, token))
                {
                    w.Queue.Write(chunk);
                }

                w.Queue.Complete();
            }
            catch (OperationCanceledException)
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
                ReleaseScopeToken(w.ScopeKey, w.ScopeCancellation);

                lock (_queueLock)
                {
                    _inFlight--;
                    TryPumpLocked();
                }
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
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope)
        {
            if (string.IsNullOrWhiteSpace(cancellationScope)) return;
            string scopeKey = cancellationScope.Trim();
            List<WorkItem> removedPending = null;
            List<StreamWorkItem> removedStreamPending = null;
            CancellationTokenSource activeToCancel = null;

            lock (_queueLock)
            {
                removedPending = _pending.FindAll(w => string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                removedStreamPending = _streamPending.FindAll(w => string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                _pending.RemoveAll(w => string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));
                _streamPending.RemoveAll(w => string.Equals(w.Task.CancellationScope?.Trim(), scopeKey, StringComparison.Ordinal));

                lock (_scopeLock)
                {
                    if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                    {
                        activeToCancel = prev;
                        _scopeTokens.Remove(scopeKey);
                    }
                }
            }

            activeToCancel?.Cancel();

            // 3. Завершаем удалённые pending-задачи, чтобы вызывающий не висел в ожидании.
            CancelRemovedPending(removedPending, removedStreamPending);
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

            lock (_queueLock)
            {
                _pending.Add(work);
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    work.ScopeKey = scopeKey;
                    work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            activeToCancel = prev;
                        }

                        _scopeTokens[scopeKey] = work.ScopeCancellation;
                    }

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

                _pending.Sort(CompareWorkItems);
            }

            activeToCancel?.Cancel();
            CancelRemovedPending(removedPending, removedStreamPending);

            lock (_queueLock)
            {
                TryPumpLocked();
            }
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

            lock (_queueLock)
            {
                _streamPending.Add(work);
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    work.ScopeKey = scopeKey;
                    work.ScopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.OuterCt);

                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            activeToCancel = prev;
                        }

                        _scopeTokens[scopeKey] = work.ScopeCancellation;
                    }

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

                _streamPending.Sort(CompareStreamWorkItems);
            }

            activeToCancel?.Cancel();
            CancelRemovedPending(removedPending, removedStreamPending);

            lock (_queueLock)
            {
                TryPumpLocked();
            }
        }

        private void CancelPending(WorkItem work)
        {
            bool removed;
            lock (_queueLock)
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
            lock (_queueLock)
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

        private void ReleaseScopeToken(string scopeKey, CancellationTokenSource scopeCancellation)
        {
            if (string.IsNullOrEmpty(scopeKey) || scopeCancellation == null)
            {
                return;
            }

            lock (_scopeLock)
            {
                if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource cur) &&
                    ReferenceEquals(cur, scopeCancellation))
                {
                    _scopeTokens.Remove(scopeKey);
                }
            }

            scopeCancellation.Dispose();
        }

        private static async IAsyncEnumerable<LlmStreamChunk> ReadStreamingQueue(AsyncChunkQueue queue)
        {
            while (true)
            {
                (bool hasValue, LlmStreamChunk chunk) = await queue.TryTakeAsync(CancellationToken.None).ConfigureAwait(false);
                if (!hasValue)
                {
                    yield break;
                }

                yield return chunk;
            }
        }

        /// <summary>
        /// Портативная async producer/consumer-очередь чанков. Работает без
        /// <c>System.Threading.Channels</c>. После <see cref="Complete"/> семафор
        /// освобождается «бесконечно», чтобы все ожидающие читатели получили
        /// <c>hasValue=false</c> и корректно вышли из цикла.
        /// </summary>
        private sealed class AsyncChunkQueue
        {
            private readonly ConcurrentQueue<LlmStreamChunk> _queue = new();
            private readonly SemaphoreSlim _signal = new(0);
            private volatile bool _completed;

            public void Write(LlmStreamChunk chunk)
            {
                _queue.Enqueue(chunk);
                _signal.Release();
            }

            public void Complete()
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                // Пробуждаем всех возможных ожидающих читателей.
                _signal.Release();
            }

            public async Task<(bool hasValue, LlmStreamChunk chunk)> TryTakeAsync(CancellationToken ct)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                if (_queue.TryDequeue(out LlmStreamChunk chunk))
                {
                    return (true, chunk);
                }

                // Очередь пуста и Complete() подал сигнал — отпускаем следующий
                // сигнал на случай, если есть ещё ожидающие читатели.
                if (_completed)
                {
                    _signal.Release();
                }

                return (false, default);
            }
        }
    }
}