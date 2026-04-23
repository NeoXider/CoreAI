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
            TaskCompletionSource<string> tcs = new();
            WorkItem work = new()
            {
                Task = task ?? new AiTaskRequest(),
                OuterCt = cancellationToken,
                Tcs = tcs,
                Priority = task?.Priority ?? 0
            };
            lock (_queueLock)
            {
                _pending.Add(work);
                _pending.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                TryPumpLocked();
            }

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
                Priority = task?.Priority ?? 0
            };

            lock (_queueLock)
            {
                _streamPending.Add(work);
                _streamPending.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                TryPumpLocked();
            }

            // ВАЖНО: читаем без cancellationToken. Отмена уже распространяется на
            // _inner.RunStreamingAsync через w.OuterCt; worker (RunOneStreamingAsync)
            // сам запишет терминальный чанк { Error="cancelled" } и вызовет Complete(),
            // что корректно завершит это чтение. Если бы мы передавали cancellationToken
            // в TryTakeAsync, reader бросил бы OperationCanceledException ДО того, как
            // worker успевал записать терминальный chunk — вызывающий код потерял бы
            // финальный статус стрима.
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

        private void TryPumpLocked()
        {
            while (_inFlight < _maxConcurrent)
            {
                if (_pending.Count > 0)
                {
                    WorkItem w = _pending[0];
                    _pending.RemoveAt(0);
                    _inFlight++;
                    _ = RunOneAsync(w);
                    continue;
                }

                if (_streamPending.Count > 0)
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
            string scopeKey = w.Task.CancellationScope?.Trim();
            CancellationTokenSource scopeLinked = null;
            try
            {
                CancellationToken token = w.OuterCt;
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            prev.Cancel();
                        }

                        scopeLinked = CancellationTokenSource.CreateLinkedTokenSource(w.OuterCt);
                        _scopeTokens[scopeKey] = scopeLinked;
                    }

                    token = scopeLinked.Token;
                }

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
                if (!string.IsNullOrEmpty(scopeKey) && scopeLinked != null)
                {
                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource cur) &&
                            ReferenceEquals(cur, scopeLinked))
                        {
                            _scopeTokens.Remove(scopeKey);
                        }
                    }

                    scopeLinked.Dispose();
                }

                lock (_queueLock)
                {
                    _inFlight--;
                    TryPumpLocked();
                }
            }
        }

        private async Task RunOneStreamingAsync(StreamWorkItem w)
        {
            string scopeKey = w.Task.CancellationScope?.Trim();
            CancellationTokenSource scopeLinked = null;
            try
            {
                CancellationToken token = w.OuterCt;
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource prev))
                        {
                            prev.Cancel();
                        }

                        scopeLinked = CancellationTokenSource.CreateLinkedTokenSource(w.OuterCt);
                        _scopeTokens[scopeKey] = scopeLinked;
                    }

                    token = scopeLinked.Token;
                }

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
                if (!string.IsNullOrEmpty(scopeKey) && scopeLinked != null)
                {
                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out CancellationTokenSource cur) &&
                            ReferenceEquals(cur, scopeLinked))
                        {
                            _scopeTokens.Remove(scopeKey);
                        }
                    }

                    scopeLinked.Dispose();
                }

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
        }

        private sealed class StreamWorkItem
        {
            public AiTaskRequest Task;
            public CancellationToken OuterCt;
            public AsyncChunkQueue Queue;
            public int Priority;
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