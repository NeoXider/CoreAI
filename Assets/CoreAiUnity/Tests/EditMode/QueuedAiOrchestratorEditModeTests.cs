using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="QueuedAiOrchestrator"/> queue priority,
    /// cancellation scopes, and maximum concurrency.
    /// </summary>
    public sealed class QueuedAiOrchestratorEditModeTests
    {
        #region Helpers

        /// <summary>
        /// Stub orchestrator that records execution order and waits on externally
        /// controlled gates before completing each task.
        /// </summary>
        private sealed class RecordingOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            public List<string> ExecutionLog { get; } = new();
            public List<TaskCompletionSource<string>> Gates { get; } = new();

            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                string hint = task?.Hint ?? "";

                TaskCompletionSource<string> gate = new();
                lock (_lock)
                {
                    ExecutionLog.Add(hint);
                    Gates.Add(gate);
                }

                // Ждём, пока тест "откроет ворота" или CancellationToken сработает
                using CancellationTokenRegistration reg = cancellationToken.Register(() =>
                {
                    try
                    {
                        gate.TrySetCanceled(cancellationToken);
                    }
                    catch
                    {
                        gate.TrySetCanceled();
                    }
                });
                return await gate.Task;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        /// <summary>
        /// Stub orchestrator that completes immediately for priority-order tests.
        /// </summary>
        private sealed class ImmediateRecordingOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            public List<string> ExecutionLog { get; } = new();

            /// <summary>Delay before completion so the queue can accumulate pending items.</summary>
            public TaskCompletionSource<string> StartGate { get; } = new();

            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                // Ждём стартовый сигнал (только первый раз или всегда — зависит от теста)
                await StartGate.Task;

                cancellationToken.ThrowIfCancellationRequested();

                lock (_lock)
                {
                    ExecutionLog.Add(task?.Hint ?? "");
                }

                return null;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        #endregion

        // ──────────────────────────────────────────────────────────
        // Тест 1: Приоритет — задача с высоким Priority выполняется раньше
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task Priority_HigherPriorityTask_ExecutesFirst()
        {
            // Arrange: MaxConcurrent = 1, чтобы очередь накапливалась
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            // Первая задача — занимает единственный слот
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker", Priority = 0 });

            // Пока blocker выполняется, добавляем 3 задачи с разными приоритетами
            Task low = queue.RunTaskAsync(new AiTaskRequest { Hint = "low", Priority = 1 });
            Task high = queue.RunTaskAsync(new AiTaskRequest { Hint = "high", Priority = 10 });
            Task mid = queue.RunTaskAsync(new AiTaskRequest { Hint = "mid", Priority = 5 });

            // Даём время на помещение в очередь
            await Task.Delay(50);

            // Act: завершаем blocker → очередь начинает pump
            Assert.AreEqual(1, inner.Gates.Count, "Только blocker должен был начать выполнение");
            inner.Gates[0].TrySetResult(null);
            await Task.Delay(50);

            // high (priority=10) должен выполниться следующим
            Assert.GreaterOrEqual(inner.Gates.Count, 2, "После blocker должна начаться следующая задача");
            Assert.AreEqual("high", inner.ExecutionLog[1], "Задача с наивысшим приоритетом должна идти следующей");

            // Завершаем high → mid должен быть следующим
            inner.Gates[1].TrySetResult(null);
            await Task.Delay(50);

            Assert.GreaterOrEqual(inner.Gates.Count, 3);
            Assert.AreEqual("mid", inner.ExecutionLog[2], "Средний приоритет после высокого");

            // Завершаем mid → low
            inner.Gates[2].TrySetResult(null);
            await Task.Delay(50);

            Assert.AreEqual(4, inner.ExecutionLog.Count);
            Assert.AreEqual("low", inner.ExecutionLog[3], "Низкий приоритет последним");

            // Cleanup
            inner.Gates[3].TrySetResult(null);
            await Task.WhenAll(blocker, low, high, mid);
        }

        [Test]
        public async Task Priority_EqualPriority_ExecutesFifo()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker", Priority = 0 });
            Task first = queue.RunTaskAsync(new AiTaskRequest { Hint = "first", Priority = 5 });
            Task second = queue.RunTaskAsync(new AiTaskRequest { Hint = "second", Priority = 5 });
            Task third = queue.RunTaskAsync(new AiTaskRequest { Hint = "third", Priority = 5 });

            await Task.Delay(50);
            Assert.AreEqual("blocker", inner.ExecutionLog[0]);

            inner.Gates[0].TrySetResult(null);
            await Task.Delay(50);
            Assert.AreEqual("first", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await Task.Delay(50);
            Assert.AreEqual("second", inner.ExecutionLog[2]);

            inner.Gates[2].TrySetResult(null);
            await Task.Delay(50);
            Assert.AreEqual("third", inner.ExecutionLog[3]);

            inner.Gates[3].TrySetResult(null);
            await Task.WhenAll(blocker, first, second, third);
        }

        // ──────────────────────────────────────────────────────────
        // Тест 2: CancellationScope — новая задача с тем же scope отменяет предыдущую
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task CancellationScope_SameScope_CancelsPreviousTask()
        {
            // Arrange: MaxConcurrent = 2, чтобы обе задачи запустились
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            // Первая задача со scope "crafting"
            Task first = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "first",
                CancellationScope = "crafting"
            });

            await Task.Delay(50);
            Assert.AreEqual(1, inner.Gates.Count, "Первая задача должна запуститься");

            // Вторая задача с тем же scope — должна отменить первую
            Task second = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "second",
                CancellationScope = "crafting"
            });

            await Task.Delay(50);
            Assert.AreEqual(2, inner.Gates.Count, "Вторая задача тоже должна запуститься (MaxConcurrent=2)");

            await Task.Delay(50);

            Assert.IsTrue(first.IsCanceled,
                "Previous active task with the same CancellationScope must be cancelled, not merely completed.");

            // Cleanup: завершаем вторую
            inner.Gates[1].TrySetResult(null);
            await second;
        }

        [Test]
        public async Task CancellationScope_PendingSameScope_LatestTaskWinsBeforeStart()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task oldPending = queue.RunTaskAsync(new AiTaskRequest { Hint = "old", CancellationScope = "npc" });
            Task latest = queue.RunTaskAsync(new AiTaskRequest { Hint = "latest", CancellationScope = "npc" });

            await Task.Delay(50);

            Assert.AreEqual(1, inner.Gates.Count, "Only blocker should be active.");
            Assert.IsTrue(oldPending.IsCanceled,
                "Older pending task with the same CancellationScope should be cancelled immediately.");

            inner.Gates[0].TrySetResult(null);
            await Task.Delay(50);

            Assert.AreEqual(2, inner.Gates.Count, "Latest task should start after blocker finishes.");
            Assert.AreEqual("latest", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(blocker, latest);
        }

        [Test]
        public async Task ExternalCancellation_PendingTask_CancelsBeforeStart()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            using CancellationTokenSource cts = new();

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task pending = queue.RunTaskAsync(new AiTaskRequest { Hint = "pending" }, cts.Token);

            await Task.Delay(50);
            cts.Cancel();
            await Task.Delay(50);

            Assert.IsTrue(pending.IsCanceled,
                "Pending task should observe external cancellation without waiting for a free slot.");

            inner.Gates[0].TrySetResult(null);
            await Task.Delay(50);

            Assert.AreEqual(1, inner.ExecutionLog.Count, "Cancelled pending task must not start later.");
            await blocker;
        }

        // ──────────────────────────────────────────────────────────
        // Тест 3: MaxConcurrent — не более N задач одновременно
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task MaxConcurrent_LimitsParallelExecution()
        {
            // Arrange: MaxConcurrent = 2
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            // Запускаем 4 задачи
            Task t1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t1" });
            Task t2 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t2" });
            Task t3 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t3" });
            Task t4 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t4" });

            await Task.Delay(100);

            // Assert: только 2 задачи должны начать выполнение
            Assert.AreEqual(2, inner.Gates.Count,
                "MaxConcurrent=2: только 2 задачи должны начать выполняться одновременно");
            Assert.AreEqual("t1", inner.ExecutionLog[0]);
            Assert.AreEqual("t2", inner.ExecutionLog[1]);

            // Завершаем первую — третья должна начаться
            inner.Gates[0].TrySetResult(null);
            await Task.Delay(100);

            Assert.AreEqual(3, inner.Gates.Count,
                "После завершения первой задачи третья должна начаться");
            Assert.AreEqual("t3", inner.ExecutionLog[2]);

            // Завершаем вторую — четвёртая должна начаться
            inner.Gates[1].TrySetResult(null);
            await Task.Delay(100);

            Assert.AreEqual(4, inner.Gates.Count,
                "После завершения второй задачи четвёртая должна начаться");
            Assert.AreEqual("t4", inner.ExecutionLog[3]);

            // Cleanup
            inner.Gates[2].TrySetResult(null);
            inner.Gates[3].TrySetResult(null);
            await Task.WhenAll(t1, t2, t3, t4);
        }

        // ──────────────────────────────────────────────────────────
        // Тест 4: CancelTasks — отменяет текущие и удаляет из очереди задачи указанного scope
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task CancelTasks_SpecificScope_CancelsActiveTask()
        {
            // Arrange: MaxConcurrent = 1
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            // Задача 1 (active)
            Task t1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t1", CancellationScope = "NPC1" });

            // Задача 2 (pending, другой scope)
            Task t2 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t2", CancellationScope = "NPC2" });

            await Task.Delay(100);

            // Assert: только t1 стартовала
            Assert.AreEqual(1, inner.Gates.Count);

            // Act: Отменяем все задачи для NPC1
            queue.CancelTasks("NPC1");
            using (CancellationTokenSource wait = new(TimeSpan.FromSeconds(10)))
            {
                while (!t1.IsCompleted && !wait.IsCancellationRequested)
                {
                    await Task.Yield();
                }
            }

            Assert.IsTrue(t1.IsCompleted,
                $"t1 должна завершиться после CancelTasks (status={t1.Status}).");
            // t1 должна быть отменена (IsCanceled)
            Assert.IsTrue(t1.IsCanceled,
                $"t1 (active) должна быть отменена (status={t1.Status}, fault={(t1.IsFaulted ? t1.Exception?.GetBaseException().Message : null)}).");

            // t2 (NPC2) должна была начать выполняться, так как слот освободился.
            Assert.AreEqual(2, inner.Gates.Count, "t2 (NPC2) должна стартовать после отмены NPC1");
            Assert.AreEqual("t2", inner.ExecutionLog[1]);

            // Cleanup
            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(t2);
        }

        [Test]
        public async Task CancelTasks_SpecificScope_CancelsPendingTask()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task cancelled = queue.RunTaskAsync(new AiTaskRequest { Hint = "cancelled", CancellationScope = "NPC1" });
            Task survivor = queue.RunTaskAsync(new AiTaskRequest { Hint = "survivor", CancellationScope = "NPC2" });

            await Task.Delay(100);
            Assert.AreEqual(1, inner.Gates.Count, "Only blocker should be active.");

            queue.CancelTasks("NPC1");
            await Task.Delay(100);

            Assert.IsTrue(cancelled.IsCanceled, "Pending NPC1 task should be cancelled without starting.");
            Assert.AreEqual(1, inner.Gates.Count, "Cancelled pending task must not start.");

            inner.Gates[0].TrySetResult(null);
            await Task.Delay(100);

            Assert.AreEqual(2, inner.Gates.Count, "Different scope task should start after blocker finishes.");
            Assert.AreEqual("survivor", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(blocker, survivor);
        }
        // ──────────────────────────────────────────────────────────
        // v1.5.4: IDisposable (ARCH-5)
        // ──────────────────────────────────────────────────────────

        [Test]
        public void Dispose_CleansUpScopeTokens()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            // Enqueue a scoped task to create a CTS in _scopeTokens
            _ = queue.RunTaskAsync(new AiTaskRequest { Hint = "scoped", CancellationScope = "test" });

            // Should not throw
            queue.Dispose();
        }

        [Test]
        public void Dispose_IsSafeToCallTwice()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            queue.Dispose();
            Assert.DoesNotThrow(() => queue.Dispose(), "Double dispose must not throw.");
        }

        [Test]
        public async Task CancelTasks_AfterDispose_DoesNotThrow()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            Task t = queue.RunTaskAsync(new AiTaskRequest { Hint = "t", CancellationScope = "s" });
            await Task.Delay(50);

            // Simulate ReleaseScopeToken disposing the CTS before CancelTasks runs
            queue.Dispose();
            Assert.DoesNotThrow(() => queue.CancelTasks("s"),
                "CancelTasks after Dispose must not throw ObjectDisposedException.");

            // Cleanup
            if (inner.Gates.Count > 0)
            {
                inner.Gates[0].TrySetResult(null);
            }

            try
            {
                await t;
            }
            catch
            {
                /* expected cancellation */
            }
        }

        // ──────────────────────────────────────────────────────────
        // F-10: bounded pending queue (MaxPending) and full Dispose contract
        // ──────────────────────────────────────────────────────────

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            string message,
            int timeoutMs = 5000,
            int pollMs = 10)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(pollMs);
            }

            Assert.Fail(message);
        }

        [Test]
        public async Task MaxPending_Exceeded_TaskEnqueue_FailsFastWithStructuredError()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1, MaxPending = 2 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker should occupy the only concurrent slot.");

            // Fill the 2-item pending cap.
            Task p1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "p1" });
            Task p2 = queue.RunTaskAsync(new AiTaskRequest { Hint = "p2" });

            // A third pending request must be rejected immediately instead of growing the queue further.
            Task overflow = queue.RunTaskAsync(new AiTaskRequest { Hint = "overflow" });

            AiOrchestrationQueueFullException caught = null;
            try
            {
                await overflow;
            }
            catch (AiOrchestrationQueueFullException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught,
                "Enqueuing beyond MaxPending must fail fast with AiOrchestrationQueueFullException.");
            Assert.AreEqual(2, caught.MaxPending);

            // Cleanup: release blocker and both accepted pending tasks so nothing is left running.
            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "p1 or p2 should start after blocker.");
            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 3, "The other of p1/p2 should start.");
            inner.Gates[2].TrySetResult(null);

            await Task.WhenAll(blocker, p1, p2);
        }

        [Test]
        public async Task MaxPending_Exceeded_StreamingEnqueue_EmitsTerminalErrorChunk()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1, MaxPending = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker should occupy the only concurrent slot.");

            // Fills the single pending slot.
            Task p1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "p1" });

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in queue.RunStreamingAsync(
                               new AiTaskRequest { Hint = "overflow-stream" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count,
                "A streaming request rejected by admission control gets exactly one terminal chunk.");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.That(chunks[0].Error, Does.Contain("MaxPending"));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "p1 should start once blocker finishes.");
            inner.Gates[1].TrySetResult(null);

            await Task.WhenAll(blocker, p1);
        }

        [Test]
        public async Task Dispose_CompletesPendingTask_InsteadOfHangingForever()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker should occupy the only concurrent slot.");

            Task pending = queue.RunTaskAsync(new AiTaskRequest { Hint = "pending" });

            queue.Dispose();

            ObjectDisposedException caught = null;
            try
            {
                await pending;
            }
            catch (ObjectDisposedException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught,
                "A still-pending (never started) task must resolve instead of awaiting forever once disposed.");

            // Cleanup: the in-flight blocker is cancelled via Dispose()'s lifetime CTS.
            inner.Gates[0].TrySetResult(null);
            try
            {
                await blocker;
            }
            catch (OperationCanceledException)
            {
                /* expected: Dispose() cancels in-flight work via the lifetime CTS */
            }
        }

        [Test]
        public async Task Dispose_CompletesPendingStream_WithTerminalChunk_InsteadOfHangingForever()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker should occupy the only concurrent slot.");

            IAsyncEnumerator<LlmStreamChunk> enumerator = queue.RunStreamingAsync(
                new AiTaskRequest { Hint = "pending-stream" }).GetAsyncEnumerator();

            // An async-iterator's MoveNextAsync runs synchronously up to its first real suspension point.
            // Enqueue(work) is plain synchronous code, so by the time this call returns an incomplete
            // ValueTask (suspended inside the empty/not-yet-completed AsyncChunkQueue read), the streaming
            // request is deterministically already sitting in the pending list - no arbitrary delay needed.
            ValueTask<bool> moveNextTask = enumerator.MoveNextAsync();

            queue.Dispose();

            bool hasNext = await moveNextTask;
            Assert.IsTrue(hasNext, "Dispose() must deliver a terminal chunk instead of completing the stream empty.");
            LlmStreamChunk chunk = enumerator.Current;
            Assert.IsTrue(chunk.IsDone);
            Assert.IsNotEmpty(chunk.Error);

            Assert.IsFalse(await enumerator.MoveNextAsync(), "No further chunks after the terminal one.");
            await enumerator.DisposeAsync();

            inner.Gates[0].TrySetResult(null);
            try
            {
                await blocker;
            }
            catch (OperationCanceledException)
            {
                /* expected: Dispose() cancels in-flight work via the lifetime CTS */
            }
        }

        [Test]
        public void RunTaskAsync_AfterDispose_ThrowsObjectDisposedException()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            queue.Dispose();

            Assert.Throws<ObjectDisposedException>(() =>
                queue.RunTaskAsync(new AiTaskRequest { Hint = "after-dispose" }));
        }

        [Test]
        public async Task RunStreamingAsync_AfterDispose_ThrowsObjectDisposedException()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            queue.Dispose();

            ObjectDisposedException caught = null;
            try
            {
                await foreach (LlmStreamChunk _ in queue.RunStreamingAsync(
                                   new AiTaskRequest { Hint = "after-dispose" }))
                {
                }
            }
            catch (ObjectDisposedException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "RunStreamingAsync must throw ObjectDisposedException after Dispose().");
        }
    }
}