using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для <see cref="QueuedAiOrchestrator"/>:
    /// приоритет очереди, CancellationScope и MaxConcurrent.
    /// </summary>
    public sealed class QueuedAiOrchestratorEditModeTests
    {
        #region Helpers

        /// <summary>
        /// Stub-оркестратор, записывающий порядок выполнения задач.
        /// Каждая задача ждёт <see cref="TaskCompletionSource"/> перед завершением, что позволяет
        /// управлять моментом завершения извне.
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
                using var reg = cancellationToken.Register(() => gate.TrySetCanceled());
                return await gate.Task;
            }
        }

        /// <summary>
        /// Stub-оркестратор для немедленного завершения (для тестов приоритета).
        /// </summary>
        private sealed class ImmediateRecordingOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            public List<string> ExecutionLog { get; } = new();

            /// <summary>Delay перед завершением, чтобы Queue успел набрать элементы.</summary>
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

            // Первая задача должна получить CancellationToken cancel
            // Мы проверяем это через то, что first бросит OperationCanceledException
            // Ждём немного, чтобы cancel пропагировался
            await Task.Delay(50);

            // Первая задача должна быть отменена (её CancellationToken сработал через gate.TrySetCanceled)
            // Assert: first должен быть Canceled
            Assert.IsTrue(first.IsCanceled || first.IsCompleted,
                "Первая задача с тем же CancellationScope должна быть отменена или завершена");

            // Cleanup: завершаем вторую
            inner.Gates[1].TrySetResult(null);
            await Task.Delay(50);
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
    }
}
