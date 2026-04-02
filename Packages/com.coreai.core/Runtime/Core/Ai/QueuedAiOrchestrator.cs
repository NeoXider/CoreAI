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
        private readonly object _queueLock = new object();
        private readonly List<WorkItem> _pending = new List<WorkItem>();
        private readonly object _scopeLock = new object();
        private readonly Dictionary<string, CancellationTokenSource> _scopeTokens =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private int _inFlight;

        /// <param name="inner">Фактический оркестратор (обычно <see cref="AiOrchestrator"/>).</param>
        /// <param name="options">Лимит параллелизма и пр.</param>
        public QueuedAiOrchestrator(IAiOrchestrationService inner, AiOrchestrationQueueOptions options)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            var max = options?.MaxConcurrent ?? 2;
            _maxConcurrent = max < 1 ? 1 : max;
        }

        /// <inheritdoc />
        public Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<object>();
            var work = new WorkItem
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
                var w = _pending[0];
                _pending.RemoveAt(0);
                _inFlight++;
                _ = RunOneAsync(w);
            }
        }

        private async Task RunOneAsync(WorkItem w)
        {
            var scopeKey = w.Task.CancellationScope?.Trim();
            CancellationTokenSource scopeLinked = null;
            try
            {
                CancellationToken token = w.OuterCt;
                if (!string.IsNullOrEmpty(scopeKey))
                {
                    lock (_scopeLock)
                    {
                        if (_scopeTokens.TryGetValue(scopeKey, out var prev))
                            prev.Cancel();

                        scopeLinked = CancellationTokenSource.CreateLinkedTokenSource(w.OuterCt);
                        _scopeTokens[scopeKey] = scopeLinked;
                    }

                    token = scopeLinked.Token;
                }

                await _inner.RunTaskAsync(w.Task, token).ConfigureAwait(false);
                w.Tcs.TrySetResult(null);
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
                        if (_scopeTokens.TryGetValue(scopeKey, out var cur) && ReferenceEquals(cur, scopeLinked))
                            _scopeTokens.Remove(scopeKey);
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
            public TaskCompletionSource<object> Tcs;
            public int Priority;
        }
    }
}
