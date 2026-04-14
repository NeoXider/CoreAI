using System;
using System.Collections.Generic;
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

        private void TryPumpLocked()
        {
            while (_inFlight < _maxConcurrent && _pending.Count > 0)
            {
                WorkItem w = _pending[0];
                _pending.RemoveAt(0);
                _inFlight++;
                _ = RunOneAsync(w);
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

        private sealed class WorkItem
        {
            public AiTaskRequest Task;
            public CancellationToken OuterCt;
            public TaskCompletionSource<string> Tcs;
            public int Priority;
        }
    }
}