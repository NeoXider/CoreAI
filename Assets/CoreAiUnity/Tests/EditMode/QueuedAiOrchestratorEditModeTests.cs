using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
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
        private sealed class RecordingOrchestrator : IAiOrchestrationService, IUnstartedAiTurnRecorder
        {
            private readonly object _lock = new();
            public List<string> ExecutionLog { get; } = new();
            public List<TaskCompletionSource<string>> Gates { get; } = new();
            public List<(string RoleId, string Hint)> UnstartedTurns { get; } = new();
            public bool ThrowWhenRecordingUnstartedTurn { get; set; }

            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                string hint = task?.Hint ?? "";

                TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

            void IUnstartedAiTurnRecorder.RecordUnstartedUserTurn(AiTaskRequest task)
            {
                if (ThrowWhenRecordingUnstartedTurn)
                {
                    throw new InvalidOperationException("history store failed");
                }

                lock (_lock)
                {
                    UnstartedTurns.Add((task?.RoleId, task?.Hint));
                }
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

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public string UserId { get; set; } = "";

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("school", UserId, "lesson", "");
            }
        }

        private sealed class ScopedPersistenceOrchestrator : IAiOrchestrationService, IUnstartedAiTurnRecorder
        {
            private readonly object _lock = new();
            private readonly IAgentMemoryStore _memory;

            public ScopedPersistenceOrchestrator(IAgentMemoryScopeProvider scopeProvider)
            {
                _memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), scopeProvider);
            }

            public List<TaskCompletionSource<string>> Gates { get; } = new();

            public async Task<string> RunTaskAsync(
                AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                _memory.AppendChatMessage(task.RoleId, "user", "started:" + task.Hint, false);
                TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock)
                {
                    Gates.Add(gate);
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                    gate.TrySetCanceled(cancellationToken));
                return await gate.Task;
            }

            public void CancelTasks(string cancellationScope)
            {
            }

            public ChatMessage[] GetHistory(string roleId)
            {
                return _memory.GetChatHistory(roleId);
            }

            public ChatMessage[] GetHistory(ActorContext actorContext, string roleId)
            {
                using (AgentMemoryScopeExecutionContext.Push(actorContext))
                {
                    return _memory.GetChatHistory(roleId);
                }
            }

            void IUnstartedAiTurnRecorder.RecordUnstartedUserTurn(AiTaskRequest task)
            {
                _memory.AppendChatMessage(task.RoleId, "user", "unstarted:" + task.Hint, false);
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

            // Wait for the observable queue state instead of assuming a loaded Unity Editor
            // schedules the worker continuation inside an arbitrary 50 ms window.
            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should start while the remaining work is queued.");

            // Act: завершаем blocker → очередь начинает pump
            Assert.AreEqual(1, inner.Gates.Count, "Только blocker должен был начать выполнение");
            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 2,
                "After blocker the highest-priority task must start.");

            // high (priority=10) должен выполниться следующим
            Assert.GreaterOrEqual(inner.Gates.Count, 2, "После blocker должна начаться следующая задача");
            Assert.AreEqual("high", inner.ExecutionLog[1], "Задача с наивысшим приоритетом должна идти следующей");

            // Завершаем high → mid должен быть следующим
            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 3,
                "After high-priority work the middle-priority task must start.");

            Assert.GreaterOrEqual(inner.Gates.Count, 3);
            Assert.AreEqual("mid", inner.ExecutionLog[2], "Средний приоритет после высокого");

            // Завершаем mid → low
            inner.Gates[2].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 4,
                "After middle-priority work the low-priority task must start.");

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

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should start while equal-priority work is queued.");
            Assert.AreEqual("blocker", inner.ExecutionLog[0]);

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 2,
                "First equal-priority task must start after blocker.");
            Assert.AreEqual("first", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 3,
                "Second equal-priority task must start after first.");
            Assert.AreEqual("second", inner.ExecutionLog[2]);

            inner.Gates[2].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 4,
                "Third equal-priority task must start after second.");
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

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should be active before the latest scoped task is released.");
            Assert.AreEqual(1, inner.Gates.Count, "Первая задача должна запуститься");

            // Вторая задача с тем же scope — должна отменить первую
            Task second = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "second",
                CancellationScope = "crafting"
            });

            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Second same-scope turn must start when a concurrent slot is available.");
            Assert.AreEqual(2, inner.Gates.Count, "Вторая задача тоже должна запуститься (MaxConcurrent=2)");

            await WaitUntilAsync(() => first.IsCanceled,
                "The previous same-scope turn must observe cancellation.");

            Assert.IsTrue(first.IsCanceled,
                "Previous active task with the same CancellationScope must be cancelled, not merely completed.");

            // Cleanup: завершаем вторую
            inner.Gates[1].TrySetResult(null);
            await second;
        }

        [Test]
        public async Task ProductionAdmission_SameRoleActors_IsolateMemoryAndCancellation()
        {
            AgentMemoryScope sharedLegacyScope = new("school", "shared-user", "shared-session", "");
            ActorContext studentAContext = new LocalActorIdentityProvider(
                    "student-a",
                    "connection-a",
                    "world",
                    ActorGrantSet.None,
                    sharedLegacyScope)
                .GetActorContext("Teacher");
            ActorContext studentBContext = new LocalActorIdentityProvider(
                    "student-b",
                    "connection-b",
                    "world",
                    ActorGrantSet.None,
                    sharedLegacyScope)
                .GetActorContext("Teacher");
            DefaultAgentMemoryScopeProvider scopeProvider = new();
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 2 },
                scopeProvider);

            Task<string> studentA = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "student-a",
                ActorContext = studentAContext,
                CancellationScope = studentAContext.SessionId
            });
            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Student A turn must enter the production queue path.");

            Task<string> studentB = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "student-b",
                ActorContext = studentBContext,
                CancellationScope = studentBContext.SessionId
            });
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Student B turn must start without cancelling student A's session.");

            Assert.AreEqual(2, inner.Gates.Count);
            Assert.IsFalse(studentA.IsCanceled,
                "The same role from another actor must not cancel an active turn.");
            CollectionAssert.AreEqual(
                new[] { "started:student-a" },
                Array.ConvertAll(
                    inner.GetHistory(studentAContext, "Teacher"),
                    message => message.Content));
            CollectionAssert.AreEqual(
                new[] { "started:student-b" },
                Array.ConvertAll(
                    inner.GetHistory(studentBContext, "Teacher"),
                    message => message.Content));

            ((IScopedAiTaskCancellation)queue).CancelTasks(studentBContext.SessionId, "Teacher");
            Task cancelled = await Task.WhenAny(studentB, Task.Delay(2000));
            Assert.AreSame(studentB, cancelled,
                "Scoped cancellation must reach the active inner turn promptly.");
            Assert.IsTrue(studentB.IsCanceled,
                "Session cancellation must stop only student B's Teacher turn.");
            Assert.IsFalse(studentA.IsCompleted,
                "Cancelling student B must leave student A's concurrent Teacher turn running.");

            inner.Gates[0].TrySetResult("student-a-complete");
            Assert.AreEqual("student-a-complete", await studentA);
            queue.Dispose();
        }

        [Test]
        public async Task ProductionAdmission_ReconnectResumesActorMemoryAcrossSessions()
        {
            ActorContext firstConnection = new LocalActorIdentityProvider(
                    "durable-student",
                    "connection-1",
                    "world",
                    ActorGrantSet.None,
                    new AgentMemoryScope("school", "legacy-user-1", "legacy-session-1", "topic-1"))
                .GetActorContext("Teacher");
            ActorContext secondConnection = new LocalActorIdentityProvider(
                    "durable-student",
                    "connection-2",
                    "world",
                    ActorGrantSet.None,
                    new AgentMemoryScope("other-school", "legacy-user-2", "legacy-session-2", "topic-2"))
                .GetActorContext("Teacher");
            DefaultAgentMemoryScopeProvider scopeProvider = new();
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 },
                scopeProvider);

            Task<string> first = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "first-connection",
                ActorContext = firstConnection,
                CancellationScope = firstConnection.SessionId
            });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "First connection must start.");
            inner.Gates[0].TrySetResult("first-complete");
            await first;

            Task<string> second = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "second-connection",
                ActorContext = secondConnection,
                CancellationScope = secondConnection.SessionId
            });
            await WaitUntilAsync(() => inner.Gates.Count == 2, "Reconnected actor must start.");

            string[] expected = { "started:first-connection", "started:second-connection" };
            CollectionAssert.AreEqual(
                expected,
                Array.ConvertAll(
                    inner.GetHistory(firstConnection, "Teacher"),
                    message => message.Content));
            CollectionAssert.AreEqual(
                expected,
                Array.ConvertAll(
                    inner.GetHistory(secondConnection, "Teacher"),
                    message => message.Content));

            inner.Gates[1].TrySetResult("second-complete");
            await second;
            queue.Dispose();
        }

        [Test]
        public async Task ProductionAdmission_DefaultLocalActorPreservesRoleMemory()
        {
            ActorContext localActor = new LocalActorIdentityProvider().GetActorContext("Teacher");
            DefaultAgentMemoryScopeProvider scopeProvider = new();
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 },
                scopeProvider);

            Task<string> task = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "local",
                ActorContext = localActor,
                CancellationScope = localActor.SessionId
            });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Local actor turn must start.");

            CollectionAssert.AreEqual(
                new[] { "started:local" },
                Array.ConvertAll(inner.GetHistory("Teacher"), message => message.Content));

            inner.Gates[0].TrySetResult("local-complete");
            await task;
            queue.Dispose();
        }

        [Test]
        public async Task CancelTasks_CustomScopeDifferentFromRole_CancelsOnlyCurrentStudent()
        {
            RecordingOrchestrator inner = new();
            MutableScopeProvider scopeProvider = new();
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 2 },
                scopeProvider);

            scopeProvider.UserId = "student-a";
            Task<string> studentA = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "student-a",
                CancellationScope = "lesson-panel"
            });
            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Student A turn must start before switching the learner scope.");

            scopeProvider.UserId = "student-b";
            Task<string> studentB = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "student-b",
                CancellationScope = "lesson-panel"
            });
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Student B turn must start without cancelling student A.");

            queue.CancelTasks("lesson-panel");

            Task cancelled = await Task.WhenAny(studentB, Task.Delay(2000));
            Assert.AreSame(studentB, cancelled,
                "The legacy one-argument API must find a custom scope when RoleId differs from CancellationScope.");
            Assert.IsTrue(studentB.IsCanceled);
            Assert.IsFalse(studentA.IsCompleted,
                "One-argument cancellation must remain isolated to the current learner scope.");

            inner.Gates[0].TrySetResult("student-a-complete");
            Assert.AreEqual("student-a-complete", await studentA);
            queue.Dispose();
        }

        [Test]
        public async Task ScopeSnapshot_PendingTaskRunsAndPersistsUnderEnqueueTimeStudent()
        {
            MutableScopeProvider scopeProvider = new() { UserId = "blocker-student" };
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 },
                scopeProvider);

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            scopeProvider.UserId = "student-a";
            Task studentA = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "student-a" });
            scopeProvider.UserId = "student-b";
            inner.Gates[0].TrySetResult("blocker-complete");
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "The pending student A task must start after the blocker.");

            scopeProvider.UserId = "student-a";
            CollectionAssert.AreEqual(
                new[] { "started:student-a" },
                Array.ConvertAll(inner.GetHistory("Teacher"), message => message.Content));
            scopeProvider.UserId = "student-b";
            Assert.IsEmpty(inner.GetHistory("Teacher"),
                "Switching the host identity before Pump must not move student A history into student B.");

            inner.Gates[1].TrySetResult("student-a-complete");
            await Task.WhenAll(blocker, studentA);
            queue.Dispose();
        }

        [Test]
        public async Task ScopeSnapshot_PendingCancellationRecordsOnlyEnqueueTimeStudent()
        {
            MutableScopeProvider scopeProvider = new() { UserId = "blocker-student" };
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 },
                scopeProvider);
            using CancellationTokenSource cancellation = new();

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            scopeProvider.UserId = "student-a";
            Task studentA = queue.RunTaskAsync(
                new AiTaskRequest { RoleId = "Teacher", Hint = "cancelled-a" },
                cancellation.Token);
            scopeProvider.UserId = "student-b";
            cancellation.Cancel();
            await CaptureExceptionAsync<OperationCanceledException>(() => studentA);

            scopeProvider.UserId = "student-a";
            CollectionAssert.AreEqual(
                new[] { "unstarted:cancelled-a" },
                Array.ConvertAll(inner.GetHistory("Teacher"), message => message.Content));
            scopeProvider.UserId = "student-b";
            Assert.IsEmpty(inner.GetHistory("Teacher"),
                "Pending cancellation must not persist student A's raw turn under student B.");

            inner.Gates[0].TrySetResult("blocker-complete");
            await blocker;
            queue.Dispose();
        }

        [Test]
        public async Task ScopeSnapshot_PendingStreamRunsAndPersistsUnderEnqueueTimeStudent()
        {
            MutableScopeProvider scopeProvider = new() { UserId = "blocker-student" };
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 },
                scopeProvider);

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            scopeProvider.UserId = "student-a";
            IAsyncEnumerator<LlmStreamChunk> stream = queue.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Teacher", Hint = "stream-a" }).GetAsyncEnumerator();
            ValueTask<bool> firstChunk = stream.MoveNextAsync();
            scopeProvider.UserId = "student-b";
            inner.Gates[0].TrySetResult("blocker-complete");
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "The pending student A stream must enter the inner orchestrator.");

            scopeProvider.UserId = "student-a";
            CollectionAssert.AreEqual(
                new[] { "started:stream-a" },
                Array.ConvertAll(inner.GetHistory("Teacher"), message => message.Content));
            scopeProvider.UserId = "student-b";
            Assert.IsEmpty(inner.GetHistory("Teacher"));

            inner.Gates[1].TrySetResult("stream-complete");
            Assert.IsTrue(await firstChunk);
            while (await stream.MoveNextAsync())
            {
            }

            await stream.DisposeAsync();
            await blocker;
            queue.Dispose();
        }

        [Test]
        public async Task CancellationScope_PendingSameScope_LatestTaskWinsBeforeStart()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task oldPending = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "old",
                CancellationScope = "npc"
            });
            Task latest = queue.RunTaskAsync(new AiTaskRequest { Hint = "latest", CancellationScope = "npc" });

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should be active while the latest scoped task is pending.");

            Assert.AreEqual(1, inner.Gates.Count, "Only blocker should be active.");
            Assert.IsTrue(oldPending.IsCanceled,
                "Older pending task with the same CancellationScope should be cancelled immediately.");
            AssertSingleUnstartedTurn(inner, "Teacher", "old");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Latest scoped task must start after blocker finishes.");

            Assert.AreEqual(2, inner.Gates.Count, "Latest task should start after blocker finishes.");
            Assert.AreEqual("latest", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(blocker, latest);
        }

        [Test]
        public async Task CancellationScope_PendingStreamReplacedByLatestTask_RecordsRawStreamTurnOnce()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            IAsyncEnumerator<LlmStreamChunk> oldStream = queue.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "old stream",
                CancellationScope = "npc"
            }).GetAsyncEnumerator();
            ValueTask<bool> oldMoveNext = oldStream.MoveNextAsync();

            Task latest = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "latest",
                CancellationScope = "npc"
            });

            Assert.IsTrue(await oldMoveNext);
            Assert.IsTrue(oldStream.Current.IsDone);
            Assert.AreEqual("cancelled", oldStream.Current.Error);
            Assert.IsFalse(await oldStream.MoveNextAsync());
            await oldStream.DisposeAsync();
            AssertSingleUnstartedTurn(inner, "Teacher", "old stream");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "Latest task must start after blocker.");
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
            Task pending = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "pending"
            }, cts.Token);

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Blocker must occupy the queue slot before pending cancellation.");
            cts.Cancel();
            await WaitUntilAsync(() => pending.IsCanceled,
                "Pending task must observe external cancellation promptly.");

            Assert.IsTrue(pending.IsCanceled,
                "Pending task should observe external cancellation without waiting for a free slot.");
            AssertSingleUnstartedTurn(inner, "Teacher", "pending");

            inner.Gates[0].TrySetResult(null);
            await blocker;

            Assert.AreEqual(1, inner.ExecutionLog.Count, "Cancelled pending task must not start later.");
        }

        [Test]
        public async Task ExternalCancellation_PreCancelledTask_RecordsRawTurnBeforeReturningCancellation()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cts = new();
            cts.Cancel();

            Task turn = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "raw pre-cancelled question"
            }, cts.Token);

            await CaptureExceptionAsync<OperationCanceledException>(() => turn);
            Assert.IsEmpty(inner.ExecutionLog, "A pre-cancelled item must never enter the inner orchestrator.");
            AssertSingleUnstartedTurn(inner, "Teacher", "raw pre-cancelled question");
        }

        [Test]
        public async Task ExternalCancellation_PreCancelledStream_RecordsRawTurnBeforeTerminalChunk()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cts = new();
            cts.Cancel();

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in queue.RunStreamingAsync(new AiTaskRequest
                           {
                               RoleId = "Teacher",
                               SourceTag = "Chat",
                               Hint = "raw pre-cancelled stream"
                           }, cts.Token))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual("cancelled", chunks[0].Error);
            Assert.IsEmpty(inner.ExecutionLog);
            AssertSingleUnstartedTurn(inner, "Teacher", "raw pre-cancelled stream");
        }

        [Test]
        public async Task ExternalCancellation_PendingStream_RecordsBeforeInnerStarts()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            using CancellationTokenSource cts = new();
            IAsyncEnumerator<LlmStreamChunk> enumerator = queue.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "pending stream raw"
            }, cts.Token).GetAsyncEnumerator();
            ValueTask<bool> moveNext = enumerator.MoveNextAsync();

            cts.Cancel();

            Assert.IsTrue(await moveNext);
            Assert.IsTrue(enumerator.Current.IsDone);
            Assert.AreEqual("cancelled", enumerator.Current.Error);
            Assert.IsFalse(await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
            AssertSingleUnstartedTurn(inner, "Teacher", "pending stream raw");
            Assert.AreEqual(1, inner.ExecutionLog.Count, "Cancelled pending stream must not enter inner.");

            inner.Gates[0].TrySetResult(null);
            await blocker;
        }

        [Test]
        public async Task ExternalCancellation_TaskClaimedBeforeInner_RecordsExactlyOnceWithoutStartingInner()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            using CancellationTokenSource cts = new();
            Task claimed = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "claimed task raw"
            }, cts.Token);

            using ManualResetEventSlim callbackEntered = new(false);
            using ManualResetEventSlim releaseCancellation = new(false);
            using CancellationTokenRegistration holdCancellation = cts.Token.Register(() =>
            {
                callbackEntered.Set();
                releaseCancellation.Wait();
            });
            Task cancellation = Task.Run(() => cts.Cancel());

            try
            {
                Assert.IsTrue(callbackEntered.Wait(TimeSpan.FromSeconds(5)),
                    "The test cancellation callback must hold cancellation before the queue callback runs.");
                inner.Gates[0].TrySetResult(null);
                await WaitUntilAsync(
                    () => GetPendingCount(queue, "_pending") == 0,
                    "Pump must claim the item while the earlier cancellation callback is still held.");
                Assert.AreEqual(1, inner.ExecutionLog.Count,
                    "Claimed cancellation must be resolved by the queue without entering the inner orchestrator.");
            }
            finally
            {
                releaseCancellation.Set();
            }

            await cancellation;
            await CaptureExceptionAsync<OperationCanceledException>(() => claimed);
            await blocker;
            Assert.AreEqual(1, inner.ExecutionLog.Count,
                "A claimed-but-not-started cancelled item must not enter the inner orchestrator.");
            AssertSingleUnstartedTurn(inner, "Teacher", "claimed task raw");
        }

        [Test]
        public async Task ExternalCancellation_StreamClaimedBeforeInner_RecordsExactlyOnceWithoutStartingInner()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            using CancellationTokenSource cts = new();
            IAsyncEnumerator<LlmStreamChunk> enumerator = queue.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "claimed stream raw"
            }, cts.Token).GetAsyncEnumerator();
            ValueTask<bool> moveNext = enumerator.MoveNextAsync();

            using ManualResetEventSlim callbackEntered = new(false);
            using ManualResetEventSlim releaseCancellation = new(false);
            using CancellationTokenRegistration holdCancellation = cts.Token.Register(() =>
            {
                callbackEntered.Set();
                releaseCancellation.Wait();
            });
            Task cancellation = Task.Run(() => cts.Cancel());

            try
            {
                Assert.IsTrue(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
                inner.Gates[0].TrySetResult(null);
                await WaitUntilAsync(
                    () => GetPendingCount(queue, "_streamPending") == 0,
                    "Pump must claim the stream while the earlier cancellation callback is still held.");
                Assert.AreEqual(1, inner.ExecutionLog.Count,
                    "Claimed stream cancellation must be resolved without entering the inner orchestrator.");
            }
            finally
            {
                releaseCancellation.Set();
            }

            await cancellation;
            Assert.IsTrue(await moveNext);
            Assert.IsTrue(enumerator.Current.IsDone);
            Assert.AreEqual("cancelled", enumerator.Current.Error);
            Assert.IsFalse(await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
            await blocker;
            Assert.AreEqual(1, inner.ExecutionLog.Count);
            AssertSingleUnstartedTurn(inner, "Teacher", "claimed stream raw");
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

            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Exactly two tasks must start at MaxConcurrent=2.");

            // Assert: только 2 задачи должны начать выполнение
            Assert.AreEqual(2, inner.Gates.Count,
                "MaxConcurrent=2: только 2 задачи должны начать выполняться одновременно");
            Assert.AreEqual("t1", inner.ExecutionLog[0]);
            Assert.AreEqual("t2", inner.ExecutionLog[1]);

            // Завершаем первую — третья должна начаться
            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 3,
                "Third task must start when the first slot is released.");

            Assert.AreEqual(3, inner.Gates.Count,
                "После завершения первой задачи третья должна начаться");
            Assert.AreEqual("t3", inner.ExecutionLog[2]);

            // Завершаем вторую — четвёртая должна начаться
            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 4,
                "Fourth task must start when the second slot is released.");

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

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only NPC1 should be active before scoped cancellation.");

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
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "NPC2 must start after cancelling active NPC1.");
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
            Task cancelled = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "cancelled",
                CancellationScope = "NPC1"
            });
            Task survivor = queue.RunTaskAsync(new AiTaskRequest { Hint = "survivor", CancellationScope = "NPC2" });

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should be active before cancelling pending NPC1.");
            Assert.AreEqual(1, inner.Gates.Count, "Only blocker should be active.");

            queue.CancelTasks("NPC1");
            await WaitUntilAsync(() => cancelled.IsCanceled,
                "Pending NPC1 task must observe scoped cancellation.");

            Assert.IsTrue(cancelled.IsCanceled, "Pending NPC1 task should be cancelled without starting.");
            Assert.AreEqual(1, inner.Gates.Count, "Cancelled pending task must not start.");
            AssertSingleUnstartedTurn(inner, "Teacher", "cancelled");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Different-scope survivor must start after blocker finishes.");

            Assert.AreEqual(2, inner.Gates.Count, "Different scope task should start after blocker finishes.");
            Assert.AreEqual("survivor", inner.ExecutionLog[1]);

            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(blocker, survivor);
        }

        [Test]
        public async Task CancelTasks_SpecificScope_CancelsPendingStreamAndRecordsRawTurnOnce()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");

            IAsyncEnumerator<LlmStreamChunk> cancelledStream = queue.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "cancel stream by scope",
                CancellationScope = "NPC1"
            }).GetAsyncEnumerator();
            ValueTask<bool> moveNext = cancelledStream.MoveNextAsync();

            queue.CancelTasks("NPC1");

            Assert.IsTrue(await moveNext);
            Assert.IsTrue(cancelledStream.Current.IsDone);
            Assert.AreEqual("cancelled", cancelledStream.Current.Error);
            Assert.IsFalse(await cancelledStream.MoveNextAsync());
            await cancelledStream.DisposeAsync();
            AssertSingleUnstartedTurn(inner, "Teacher", "cancel stream by scope");
            Assert.AreEqual(1, inner.ExecutionLog.Count);

            inner.Gates[0].TrySetResult(null);
            await blocker;
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
            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Scoped task must start before disposal exercises active-token teardown.");

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

        private static int GetPendingCount(QueuedAiOrchestrator queue, string fieldName)
        {
            FieldInfo field = typeof(QueuedAiOrchestrator).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Queue field '{fieldName}' must exist for the race regression.");
            return ((ICollection)field.GetValue(queue)).Count;
        }

        private static void AssertSingleUnstartedTurn(
            RecordingOrchestrator inner,
            string expectedRoleId,
            string expectedHint)
        {
            Assert.AreEqual(1, inner.UnstartedTurns.Count,
                "One queue item may transfer its unstarted history ownership only once.");
            Assert.AreEqual(expectedRoleId, inner.UnstartedTurns[0].RoleId);
            Assert.AreEqual(expectedHint, inner.UnstartedTurns[0].Hint,
                "Queue lifecycle persistence must receive the raw Hint, not a composed prompt.");
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
            Task overflow = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "overflow"
            });

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
            AssertSingleUnstartedTurn(inner, "Teacher", "overflow");

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
                               new AiTaskRequest
                               {
                                   RoleId = "Teacher",
                                   SourceTag = "Chat",
                                   Hint = "overflow-stream"
                               }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count,
                "A streaming request rejected by admission control gets exactly one terminal chunk.");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.That(chunks[0].Error, Does.Contain("MaxPending"));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
            AssertSingleUnstartedTurn(inner, "Teacher", "overflow-stream");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "p1 should start once blocker finishes.");
            inner.Gates[1].TrySetResult(null);

            await Task.WhenAll(blocker, p1);
        }

        [Test]
        public async Task MaxPending_UnstartedPersistenceFailure_DoesNotReplaceQueueFullException()
        {
            RecordingOrchestrator inner = new() { ThrowWhenRecordingUnstartedTurn = true };
            QueuedAiOrchestrator queue = new(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1, MaxPending = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");
            Task pending = queue.RunTaskAsync(new AiTaskRequest { Hint = "pending" });

            Task overflow = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "overflow with broken history"
            });

            AiOrchestrationQueueFullException thrown =
                await CaptureExceptionAsync<AiOrchestrationQueueFullException>(() => overflow);
            Assert.AreEqual(typeof(AiOrchestrationQueueFullException), thrown.GetType(),
                "History persistence failure must not replace the admission-control outcome.");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "Pending task must start after blocker.");
            inner.Gates[1].TrySetResult(null);
            await Task.WhenAll(blocker, pending);
        }

        [Test]
        public async Task Dispose_CompletesPendingTask_InsteadOfHangingForever()
        {
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker should occupy the only concurrent slot.");

            Task pending = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "pending"
            });

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
            AssertSingleUnstartedTurn(inner, "Teacher", "pending");

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
                new AiTaskRequest
                {
                    RoleId = "Teacher",
                    SourceTag = "Chat",
                    Hint = "pending-stream"
                }).GetAsyncEnumerator();

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
            AssertSingleUnstartedTurn(inner, "Teacher", "pending-stream");

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
        public async Task Dispose_UnstartedPersistenceFailure_DoesNotReplacePendingObjectDisposedException()
        {
            RecordingOrchestrator inner = new() { ThrowWhenRecordingUnstartedTurn = true };
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            await WaitUntilAsync(() => inner.Gates.Count == 1, "Blocker must occupy the queue slot.");
            Task pending = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "disposed with broken history"
            });

            queue.Dispose();

            ObjectDisposedException thrown = await CaptureExceptionAsync<ObjectDisposedException>(() => pending);
            Assert.AreEqual(typeof(ObjectDisposedException), thrown.GetType(),
                "History persistence failure must not replace queue disposal.");
            try
            {
                await blocker;
            }
            catch (OperationCanceledException)
            {
                /* expected: Dispose cancels active work */
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

        private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException ex)
            {
                return ex;
            }

            Assert.Fail($"Expected {typeof(TException).Name}, but the operation completed successfully.");
            return null;
        }
    }
}
