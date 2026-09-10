using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Session;
using CoreAI.Messaging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="QueuedAiOrchestrator"/> queue priority,
    /// cancellation scopes, and maximum concurrency.
    /// </summary>
    public sealed class QueuedAiOrchestratorEditModeTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task TypedTask_RealOrchestratorFailureRetainsPartialToolsAndUsage(bool thrownFailure)
        {
            ReceiptLlmClient provider = new() { ThrowFailure = thrownFailure };
            AiOrchestrator core = BuildReceiptOrchestrator(provider);
            using QueuedAiOrchestrator queue = new(core, new AiOrchestrationQueueOptions());
            LlmCompletionResult result = await queue.RunTaskResultAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "help", SourceTag = "Chat" });
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.RateLimited, result.ErrorCode);
            Assert.AreEqual("partial", result.Content);
            Assert.AreEqual("receipt-model", result.Model);
            Assert.AreEqual(429, result.HttpStatus);
            Assert.AreEqual(9, result.RetryAfterSeconds);
            Assert.AreEqual(37, result.TotalTokens);
            Assert.AreEqual(1, result.ExecutedToolCalls.Count);
            Assert.AreEqual(1, provider.Calls, "An accepted tool turn must not replay.");
        }

        [TestCase(false, "provider rejected")]
        [TestCase(true, "provider rejected")]
        [TestCase(false, "")]
        [TestCase(true, "")]
        public async Task TypedTask_ContextOverflowAfterToolExecutionNeverReplays(bool uiStream, string errorMessage)
        {
            ReceiptLlmClient provider = new() { ErrorCode = LlmErrorCode.ContextLengthExceeded, Text = "", ErrorMessage = errorMessage };
            AiOrchestrator core = BuildReceiptOrchestrator(provider);
            AiTaskRequest request = new() { RoleId = "Teacher", Hint = "help", SourceTag = "Chat" };
            if (uiStream)
            {
                LlmStreamChunk terminal = null;
                await foreach (LlmStreamChunk chunk in core.RunStreamingAsync(request)) if (chunk.IsDone) terminal = chunk;
                Assert.IsNotNull(terminal);
                Assert.AreEqual(LlmErrorCode.ContextLengthExceeded, terminal.ErrorCode);
                Assert.AreEqual(1, terminal.ExecutedToolCalls.Count);
                Assert.AreEqual(37, terminal.TotalTokens);
            }
            else
            {
                LlmCompletionResult result = await core.RunTaskResultAsync(request);
                Assert.IsFalse(result.Ok);
                Assert.AreEqual(LlmErrorCode.ContextLengthExceeded, result.ErrorCode);
                Assert.AreEqual(1, result.ExecutedToolCalls.Count);
            }
            Assert.AreEqual(1, provider.Calls, "A provider's error must not re-execute its completed tool.");
            Assert.AreEqual(0, provider.Sink.Publications, "A typed failure must not publish a successful command.");
        }

        [Test]
        public async Task TypedTask_SummaryPreflightFailureDoesNotEnterProviderOrEvictHistory()
        {
            AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario scenario = new();
            scenario.Summary.FailSave = true;
            LlmCompletionResult result = await scenario.Orchestrator.RunTaskResultAsync(scenario.Request);
            Assert.IsFalse(result.Ok);
            scenario.AssertOldSourceRetained();
            scenario.Summary.FailSave = false;
            Assert.IsTrue((await scenario.Orchestrator.RunTaskResultAsync(scenario.Request)).Ok);
            scenario.AssertPublishedOnce();
        }

        private static AiOrchestrator BuildReceiptOrchestrator(ReceiptLlmClient provider) => new(
            new ReceiptAuthority(), provider, provider.Sink, new ReceiptTelemetry(),
            new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider(), null),
            null, new AgentMemoryPolicy(), null, null, new CoreAISettingsOptions { EnableStreaming = true },
            new LocalActorIdentityProvider("typed-receipt-test"));
        private sealed class ReceiptAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }
        private sealed class ReceiptSink : IAiGameCommandSink { public int Publications; public void Publish(ApplyAiGameCommand command) { Publications++; } }
        private sealed class ReceiptTelemetry : ISessionTelemetryProvider
        { public GameSessionSnapshot BuildSnapshot() => new(); }
        private sealed class ReceiptLlmClient : ILlmClient
        {
            public bool ThrowFailure;
            public readonly ReceiptSink Sink = new();
            public string ErrorMessage = "provider rejected";
            public string Text = "partial";
            public LlmErrorCode ErrorCode = LlmErrorCode.RateLimited;
            public int Calls;
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken token = default) =>
                throw new InvalidOperationException("The configured streaming provider must run exactly once.");
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
            {
                Calls++;
                yield return new LlmStreamChunk { Text = Text, Model = "receipt-model", TotalTokens = 37,
                    ExecutedToolCalls = new[] { new LlmToolCallTrace("lesson_tool", true, 1, "test") } };
                if (ThrowFailure) throw new LlmClientException("provider rejected", ErrorCode, 429, 9);
                yield return new LlmStreamChunk { IsDone = true, Error = ErrorMessage, ErrorCode = ErrorCode,
                    HttpStatus = 429, RetryAfterSeconds = 9 };
                await Task.CompletedTask;
            }
        }

        [Test]
        public async Task TypedTask_FailurePreservesMetadataWithoutExecutingLegacyPath()
        {
            LlmCompletionResult failure = new() { Ok = false, Content = "partial output", Error = "rate limited",
                ErrorCode = LlmErrorCode.RateLimited, HttpStatus = 429, RetryAfterSeconds = 3,
                Model = "test-model", TotalTokens = 17, CacheReadTokens = 8 };
            TypedResultOrchestrator inner = new(failure);
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            LlmCompletionResult result = await queue.RunTaskResultAsync(new AiTaskRequest { Hint = "typed" });
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.RateLimited, result.ErrorCode);
            Assert.AreEqual(429, result.HttpStatus);
            Assert.AreEqual(3, result.RetryAfterSeconds);
            Assert.AreEqual("partial output", result.Content);
            Assert.AreEqual(17, result.TotalTokens);
            Assert.AreEqual(8, result.CacheReadTokens);
            Assert.AreEqual(1, inner.TypedCalls);
            Assert.AreEqual(0, inner.LegacyCalls);
        }

        [Test]
        public void TypedTask_LegacyDecoratorCannotAdvertiseOrInventSuccess()
        {
            using QueuedAiOrchestrator inner = new(new RecordingOrchestrator(), new AiOrchestrationQueueOptions());
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            Assert.IsFalse(((IAiTaskResultService)queue).SupportsTaskResults);
            Assert.Throws<NotSupportedException>(() => queue.RunTaskResultAsync(new AiTaskRequest()));
        }

        [Test]
        public async Task TypedTask_LibraryTimeoutRemainsFaultAndReleasesQueue()
        {
            TypedResultOrchestrator inner = new(new LlmCompletionResult { Ok = true, Content = "next" });
            inner.Failure = new LlmOperationTimeoutException();
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            Task<LlmCompletionResult> first = queue.RunTaskResultAsync(new AiTaskRequest());
            try { await first; Assert.Fail("A timed out execution must fail."); }
            catch (LlmOperationTimeoutException) { }
            Assert.IsTrue(first.IsFaulted, "A library timeout is not caller cancellation.");
            inner.Failure = null;
            Assert.IsTrue((await queue.RunTaskResultAsync(new AiTaskRequest())).Ok);
        }

        [Test]
        public async Task TypedTask_PreCancelledNeverExecutesProvider()
        {
            TypedResultOrchestrator inner = new(new LlmCompletionResult { Ok = true, Content = "unexpected" });
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Task<LlmCompletionResult> turn = queue.RunTaskResultAsync(new AiTaskRequest(), cancellation.Token);
            try { await turn; Assert.Fail("Cancellation must not be converted into a completion."); }
            catch (OperationCanceledException) { }
            Assert.IsTrue(turn.IsCanceled);
            Assert.AreEqual(0, inner.TypedCalls);
            Assert.AreEqual(0, inner.LegacyCalls);
        }

        private sealed class TypedResultOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            private readonly LlmCompletionResult _result;
            public int TypedCalls;
            public int LegacyCalls;
            public Exception Failure;
            public TypedResultOrchestrator(LlmCompletionResult result) { _result = result; }
            public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                TypedCalls++;
                return Failure == null ? Task.FromResult(_result) : Task.FromException<LlmCompletionResult>(Failure);
            }
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                LegacyCalls++;
                return Task.FromResult("legacy text is not a receipt");
            }
            public void CancelTasks(string scope) { }
        }

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

                // Wait until the test "opens the gate" or the CancellationToken fires
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
                // Wait for the start signal (only the first time, or every time - depends on the test)
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

        private sealed class ScriptedAdmissionOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            private readonly List<string> _started = new();
            private readonly List<TaskCompletionSource<string>> _gates = new();
            private int _cancelledCount;
            private int _completedCount;

            public int StartedCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _started.Count;
                    }
                }
            }

            public int CancelledCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _cancelledCount;
                    }
                }
            }

            public int CompletedCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _completedCount;
                    }
                }
            }

            public async Task<string> RunTaskAsync(
                AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<string> gate =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock)
                {
                    _started.Add(task?.Hint ?? "");
                    _gates.Add(gate);
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        _cancelledCount++;
                    }

                    gate.TrySetCanceled(cancellationToken);
                });
                return await gate.Task;
            }

            public void CancelTasks(string cancellationScope)
            {
            }

            public string[] StartedSnapshot()
            {
                lock (_lock)
                {
                    return _started.ToArray();
                }
            }

            public void CompleteNext()
            {
                TaskCompletionSource<string> gate;
                lock (_lock)
                {
                    if (_completedCount >= _gates.Count)
                    {
                        throw new InvalidOperationException("No started scripted request is waiting for completion.");
                    }

                    gate = _gates[_completedCount];
                    _completedCount++;
                }

                gate.TrySetResult("ok");
            }
        }

        /// <summary>Releases every provider call in a wave from one shared completion signal.</summary>
        private sealed class ConcurrentWaveAdmissionOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            private readonly List<string> _started = new();
            private TaskCompletionSource<string> _waveGate =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _completedCount;
            private int _currentWaveCount;
            private int _inFlight;
            private int _maxInFlight;

            public int StartedCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _started.Count;
                    }
                }
            }

            public int CompletedCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _completedCount;
                    }
                }
            }

            public int MaxInFlight
            {
                get
                {
                    lock (_lock)
                    {
                        return _maxInFlight;
                    }
                }
            }

            public async Task<string> RunTaskAsync(
                AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<string> waveGate;
                lock (_lock)
                {
                    _started.Add(task?.Hint ?? "");
                    _currentWaveCount++;
                    _inFlight++;
                    _maxInFlight = Math.Max(_maxInFlight, _inFlight);
                    waveGate = _waveGate;
                }

                try
                {
                    return await waveGate.Task;
                }
                finally
                {
                    lock (_lock)
                    {
                        _completedCount++;
                        _inFlight--;
                    }
                }
            }

            public void CancelTasks(string cancellationScope)
            {
            }

            public int ReleaseStartedWave()
            {
                TaskCompletionSource<string> waveGate;
                int waveCount;
                lock (_lock)
                {
                    if (_currentWaveCount == 0)
                    {
                        throw new InvalidOperationException("No concurrent admission wave is waiting.");
                    }

                    waveGate = _waveGate;
                    waveCount = _currentWaveCount;
                    _currentWaveCount = 0;
                    _waveGate = new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                waveGate.TrySetResult("ok");
                return waveCount;
            }

            public string[] StartedSnapshot()
            {
                lock (_lock)
                {
                    return _started.ToArray();
                }
            }
        }

        /// <summary>
        /// An inner orchestrator that fails with a given exception, either immediately or (with
        /// <see cref="WaitForCancellation"/>) only after its token has been cancelled.
        /// </summary>
        private sealed class FaultingOrchestrator : IAiOrchestrationService
        {
            private readonly Func<Exception> _failure;

            public FaultingOrchestrator(Func<Exception> failure)
            {
                _failure = failure;
            }

            public bool WaitForCancellation { get; set; }

            public bool Started { get; private set; }

            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Started = true;
                await Task.Yield();
                await WaitIfRequestedAsync(cancellationToken);
                throw _failure();
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Started = true;
                await Task.Yield();
                await WaitIfRequestedAsync(cancellationToken);
                throw _failure();
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }

            public void CancelTasks(string cancellationScope)
            {
            }

            private async Task WaitIfRequestedAsync(CancellationToken cancellationToken)
            {
                if (!WaitForCancellation)
                {
                    return;
                }

                // WHY: wait for the cancellation WITHOUT throwing OperationCanceledException: what is under test is
                // how the queue classifies exactly the exception the inner layer throws on top of the cancellation.
                TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => cancelled.TrySetResult(true));
                await cancelled.Task;
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
                using (AgentMemoryActorScope.Enter(actorContext))
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
        // Test 1: priority - a task with a high Priority runs earlier
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task Priority_HigherPriorityTask_ExecutesFirst()
        {
            // Arrange: MaxConcurrent = 1 so the queue builds up
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            // The first task takes the only slot
            Task blocker = queue.RunTaskAsync(new AiTaskRequest { Hint = "blocker", Priority = 0 });

            // While the blocker runs, add 3 tasks with different priorities
            Task low = queue.RunTaskAsync(new AiTaskRequest { Hint = "low", Priority = 1 });
            Task high = queue.RunTaskAsync(new AiTaskRequest { Hint = "high", Priority = 10 });
            Task mid = queue.RunTaskAsync(new AiTaskRequest { Hint = "mid", Priority = 5 });

            // Wait for the observable queue state instead of assuming a loaded Unity Editor
            // schedules the worker continuation inside an arbitrary 50 ms window.
            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should start while the remaining work is queued.");

            // Act: finish the blocker so the queue starts pumping
            Assert.AreEqual(1, inner.Gates.Count, "Only the blocker should have started running");
            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 2,
                "After blocker the highest-priority task must start.");

            // high (priority=10) must run next
            Assert.GreaterOrEqual(inner.Gates.Count, 2, "The next task must start after the blocker");
            Assert.AreEqual("high", inner.ExecutionLog[1], "The highest-priority task must go next");

            // Finish high, so mid must be next
            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 3,
                "After high-priority work the middle-priority task must start.");

            Assert.GreaterOrEqual(inner.Gates.Count, 3);
            Assert.AreEqual("mid", inner.ExecutionLog[2], "Medium priority after high");

            // Finish mid, then low
            inner.Gates[2].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count >= 4,
                "After middle-priority work the low-priority task must start.");

            Assert.AreEqual(4, inner.ExecutionLog.Count);
            Assert.AreEqual("low", inner.ExecutionLog[3], "Low priority last");

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
        // Test 2: CancellationScope - a new task with the same scope cancels the previous one
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task CancellationScope_SameScope_CancelsPreviousTask()
        {
            // Arrange: MaxConcurrent = 2 so both tasks start
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            // The first task with scope "crafting"
            Task first = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "first",
                CancellationScope = "crafting"
            });

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only blocker should be active before the latest scoped task is released.");
            Assert.AreEqual(1, inner.Gates.Count, "The first task must start");

            // The second task with the same scope must cancel the first one
            Task second = queue.RunTaskAsync(new AiTaskRequest
            {
                Hint = "second",
                CancellationScope = "crafting"
            });

            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Second same-scope turn must start when a concurrent slot is available.");
            Assert.AreEqual(2, inner.Gates.Count, "The second task must start as well (MaxConcurrent=2)");

            await WaitUntilAsync(() => first.IsCanceled,
                "The previous same-scope turn must observe cancellation.");

            Assert.IsTrue(first.IsCanceled,
                "Previous active task with the same CancellationScope must be cancelled, not merely completed.");

            // Cleanup: finish the second one
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

        /// <summary>
        /// WHY the scope is varied one field at a time: a named actor's durable memory key is
        /// (actor, tenant, user, session, topic, role), so what survives a reconnect is the SCOPE the
        /// host declares, not the transport connection. An earlier version of this test reconnected
        /// under a different tenant AND user and still expected one shared history — that only passed
        /// because the store pinned the first scope it saw for an actor, which merged two tenants into
        /// one file. The pin is gone, and every field of the key is now pinned separately: change any
        /// one of them and the histories must part.
        /// </summary>
        [TestCase("school", "student-memory", "lesson-session", "topic-1", true)]
        [TestCase("other-school", "student-memory", "lesson-session", "topic-1", false)]
        [TestCase("school", "other-student", "lesson-session", "topic-1", false)]
        [TestCase("school", "student-memory", "other-lesson-session", "topic-1", false)]
        [TestCase("school", "student-memory", "lesson-session", "topic-2", false)]
        public async Task ProductionAdmission_ReconnectResumesMemoryOnlyWithinTheSameDurableScope(
            string tenantId,
            string userId,
            string memorySessionId,
            string topicId,
            bool sharesHistory)
        {
            AgentMemoryScope durableScope = new("school", "student-memory", "lesson-session", "topic-1");
            ActorContext firstConnection = new LocalActorIdentityProvider(
                    "durable-student",
                    "connection-1",
                    "world",
                    ActorGrantSet.None,
                    durableScope)
                .GetActorContext("Teacher");
            ActorContext secondConnection = new LocalActorIdentityProvider(
                    "durable-student",
                    "connection-2",
                    "world",
                    ActorGrantSet.None,
                    new AgentMemoryScope(tenantId, userId, memorySessionId, topicId))
                .GetActorContext("Teacher");
            ActorContext otherTenant = new LocalActorIdentityProvider(
                    "durable-student",
                    "connection-3",
                    "world",
                    ActorGrantSet.None,
                    new AgentMemoryScope("other-school", "learner-1", "term-1", "topic-1"))
                .GetActorContext("Teacher");
            DefaultAgentMemoryScopeProvider scopeProvider = new();
            ScopedPersistenceOrchestrator inner = new(scopeProvider);
            using QueuedAiOrchestrator queue = new(
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

            string[] firstHistory = sharesHistory
                ? new[] { "started:first-connection", "started:second-connection" }
                : new[] { "started:first-connection" };
            string[] secondHistory = sharesHistory
                ? firstHistory
                : new[] { "started:second-connection" };
            CollectionAssert.AreEqual(
                firstHistory,
                Array.ConvertAll(
                    inner.GetHistory(firstConnection, "Teacher"),
                    message => message.Content));
            CollectionAssert.AreEqual(
                secondHistory,
                Array.ConvertAll(
                    inner.GetHistory(secondConnection, "Teacher"),
                    message => message.Content));
            CollectionAssert.IsEmpty(
                inner.GetHistory(otherTenant, "Teacher"),
                "The same actor id under a different tenant must not read that tenant's history.");

            inner.Gates[1].TrySetResult("second-complete");
            await second;
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
        public async Task ProductionAdmission_TwentyActorsFortyRequests_UsesBoundedFairRounds()
        {
            const int actorCount = 20;
            const int requestsPerActor = 2;
            const int blockerCount = 2;
            ScriptedAdmissionOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 2, MaxPending = 64 });
            List<Task> tasks = new();

            for (int blockerIndex = 0; blockerIndex < blockerCount; blockerIndex++)
            {
                string actorId = "blocker-" + blockerIndex;
                ActorContext actorContext = CreateActorContext(actorId, actorId + "-session");
                tasks.Add(queue.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "Teacher",
                    Hint = actorId,
                    ActorContext = actorContext,
                    CancellationScope = actorContext.SessionId
                }));
            }

            await WaitUntilAsync(() => inner.StartedCount == blockerCount,
                "Both scripted blockers must occupy the configured provider slots.");

            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                string actorId = "actor-" + actorIndex.ToString("D2");
                for (int requestIndex = 0; requestIndex < requestsPerActor; requestIndex++)
                {
                    string sessionId = actorId + "-session-" + requestIndex;
                    ActorContext actorContext = CreateActorContext(actorId, sessionId);
                    tasks.Add(queue.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = "Teacher",
                        Hint = actorId,
                        ActorContext = actorContext,
                        CancellationScope = "Teacher"
                    }));
                }
            }

            await CompleteAllScriptedAsync(inner, blockerCount + actorCount * requestsPerActor);
            await Task.WhenAll(tasks);

            Assert.AreEqual(0, inner.CancelledCount,
                "Actor-aware admission must not cancel another actor sharing the same role.");
            Assert.AreEqual(65L, queue.MaximumActorBypasses,
                "The configured starvation bound must remain visible and measurable.");

            string[] started = inner.StartedSnapshot();
            Dictionary<string, int> lastStartByActor = new(StringComparer.Ordinal);
            // WHY: Least-last-dispatch order serves each of the other 20 - 1 actors once before this actor.
            for (int subjectIndex = 0; subjectIndex < actorCount * requestsPerActor; subjectIndex++)
            {
                string actorId = started[blockerCount + subjectIndex];
                if (lastStartByActor.TryGetValue(actorId, out int lastStart))
                {
                    Assert.LessOrEqual(subjectIndex - lastStart - 1, actorCount - 1,
                        "A waiting actor exceeded the measurable per-round bypass bound.");
                }
                else
                {
                    Assert.LessOrEqual(subjectIndex, actorCount - 1,
                        "An actor's first request was starved beyond one fair round.");
                }

                lastStartByActor[actorId] = subjectIndex;
            }

            for (int round = 0; round < requestsPerActor; round++)
            {
                HashSet<string> actorsInRound = new(StringComparer.Ordinal);
                int roundStart = blockerCount + round * actorCount;
                for (int offset = 0; offset < actorCount; offset++)
                {
                    Assert.IsTrue(actorsInRound.Add(started[roundStart + offset]),
                        "An actor was served twice before every waiting actor received its turn.");
                }

                Assert.AreEqual(actorCount, actorsInRound.Count);
            }

            queue.Dispose();
        }

        [Test]
        public async Task ProductionAdmission_ConcurrentTurnover_ClaimsRemainCompleteAndFair()
        {
            const int actorCount = 100;
            const int requestsPerActor = 2;
            const int maxConcurrent = 16;
            const int blockerCount = maxConcurrent;
            const int actorRequestCount = actorCount * requestsPerActor;
            const int expectedTotal = blockerCount + actorRequestCount;
            ConcurrentWaveAdmissionOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions
                {
                    MaxConcurrent = maxConcurrent,
                    MaxPending = actorRequestCount
                });
            queue.EnableAdmissionClaimTrackingForTests();
            List<Task> tasks = new();
            HashSet<string> expectedRequestIds = new(StringComparer.Ordinal);
            HashSet<string> expectedActorIds = new(StringComparer.Ordinal);

            for (int blockerIndex = 0; blockerIndex < blockerCount; blockerIndex++)
            {
                string requestId = "blocker-" + blockerIndex.ToString("D2");
                ActorContext actorContext = CreateActorContext(requestId, requestId + "-session");
                expectedRequestIds.Add(requestId);
                tasks.Add(queue.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "Teacher",
                    Hint = requestId,
                    ActorContext = actorContext,
                    CancellationScope = actorContext.SessionId
                }));
            }

            await WaitUntilAsync(
                () => inner.StartedCount == blockerCount,
                "All blockers must occupy provider slots before the actor backlog is offered.");

            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                string actorId = "actor-" + actorIndex.ToString("D3");
                expectedActorIds.Add(actorId);
                for (int requestIndex = 0; requestIndex < requestsPerActor; requestIndex++)
                {
                    string requestId = actorId + "-request-" + requestIndex.ToString("D2");
                    ActorContext actorContext = CreateActorContext(actorId, requestId + "-session");
                    expectedRequestIds.Add(requestId);
                    tasks.Add(queue.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = "Teacher",
                        Hint = requestId,
                        ActorContext = actorContext,
                        CancellationScope = actorContext.SessionId
                    }));
                }
            }

            int fullWaveCount = 0;
            while (inner.CompletedCount < expectedTotal)
            {
                int startedBeforeWave = inner.StartedCount;
                int waveSize = inner.ReleaseStartedWave();
                Assert.LessOrEqual(waveSize, maxConcurrent,
                    "A completion wave exceeded the configured provider concurrency.");
                if (waveSize == maxConcurrent)
                {
                    fullWaveCount++;
                }

                int expectedStarted = Math.Min(expectedTotal, startedBeforeWave + waveSize);
                await WaitUntilAsync(
                    () => inner.CompletedCount >= startedBeforeWave &&
                          inner.StartedCount >= expectedStarted,
                    "Concurrent completion did not refill every available provider slot.");
                Assert.AreEqual(startedBeforeWave, inner.CompletedCount,
                    "A replacement wave completed before its shared gate was released.");
                Assert.AreEqual(expectedStarted, inner.StartedCount,
                    "Concurrent admission started an unexpected number of requests.");
            }

            await Task.WhenAll(tasks);
            Assert.GreaterOrEqual(fullWaveCount, 2,
                "The scenario must release multiple full provider waves concurrently.");
            Assert.AreEqual(maxConcurrent, inner.MaxInFlight,
                "The measured scenario must occupy every configured provider slot.");

            string[] started = inner.StartedSnapshot();
            Assert.AreEqual(expectedTotal, started.Length,
                "Concurrent admission lost or duplicated a provider start.");
            HashSet<string> seenRequestIds = new(StringComparer.Ordinal);
            foreach (string requestId in started)
            {
                Assert.IsTrue(expectedRequestIds.Contains(requestId),
                    "Concurrent admission started an unknown request.");
                Assert.IsTrue(seenRequestIds.Add(requestId),
                    "Concurrent admission started one request more than once.");
            }

            CollectionAssert.AreEquivalent(expectedRequestIds, seenRequestIds,
                "Concurrent admission did not start every queued request exactly once.");

            (long ClaimOrdinal, string ActorId)[] claims =
                queue.GetAdmissionClaimSnapshotForTests();
            Assert.AreEqual(expectedTotal, claims.Length,
                "The authoritative admission trace must contain exactly one claim per request.");
            for (int claimIndex = 0; claimIndex < claims.Length; claimIndex++)
            {
                Assert.AreEqual(claimIndex + 1L, claims[claimIndex].ClaimOrdinal,
                    "Admission claim ordinals must be gap-free and strictly monotonic.");
            }

            Dictionary<string, int> lastClaimByActor = new(StringComparer.Ordinal);
            for (int subjectIndex = 0; subjectIndex < actorRequestCount; subjectIndex++)
            {
                string actorId = claims[blockerCount + subjectIndex].ActorId;
                Assert.IsTrue(expectedActorIds.Contains(actorId),
                    "The actor claim trace contained an unexpected actor.");
                if (lastClaimByActor.TryGetValue(actorId, out int lastClaim))
                {
                    int bypasses = subjectIndex - lastClaim - 1;
                    Assert.LessOrEqual((long)bypasses, queue.MaximumActorBypasses,
                        "An actor exceeded the configured production bypass bound.");
                    Assert.LessOrEqual(bypasses, actorCount - 1,
                        "An actor exceeded one fair round in authoritative claim order.");
                }
                else
                {
                    Assert.LessOrEqual((long)subjectIndex, queue.MaximumActorBypasses,
                        "An actor's first claim exceeded the configured production bypass bound.");
                    Assert.LessOrEqual(subjectIndex, actorCount - 1,
                        "An actor's first claim was delayed beyond one fair round.");
                }

                lastClaimByActor[actorId] = subjectIndex;
            }

            Assert.AreEqual(actorCount, lastClaimByActor.Count,
                "Every actor must appear in the authoritative claim trace.");
            for (int round = 0; round < requestsPerActor; round++)
            {
                HashSet<string> actorsInRound = new(StringComparer.Ordinal);
                int roundStart = blockerCount + round * actorCount;
                for (int offset = 0; offset < actorCount; offset++)
                {
                    Assert.IsTrue(actorsInRound.Add(claims[roundStart + offset].ActorId),
                        "An actor was claimed twice before all waiting actors received a claim.");
                }

                CollectionAssert.AreEquivalent(expectedActorIds, actorsInRound,
                    "Each claim round must contain every waiting actor exactly once.");
            }

            queue.Dispose();
        }

        [Test]
        public async Task ProductionAdmission_LargeBacklog_DoesNotDelayAnotherActorsFirstRequest()
        {
            const int backlogCount = 8;
            ScriptedAdmissionOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(
                inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1, MaxPending = 16 });
            ActorContext busyActor = CreateActorContext("busy-actor", "busy-session");
            ActorContext secondActor = CreateActorContext("second-actor", "second-session");
            List<Task> tasks = new()
            {
                queue.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "Teacher",
                    Hint = "busy-active",
                    ActorContext = busyActor
                })
            };

            await WaitUntilAsync(() => inner.StartedCount == 1,
                "The busy actor must occupy the provider slot before its backlog is offered.");

            for (int requestIndex = 0; requestIndex < backlogCount; requestIndex++)
            {
                tasks.Add(queue.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "Teacher",
                    Hint = "busy-backlog-" + requestIndex,
                    ActorContext = busyActor
                }));
            }

            tasks.Add(queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "second-first",
                ActorContext = secondActor
            }));

            inner.CompleteNext();
            await WaitUntilAsync(() => inner.StartedCount == 2,
                "A pending request must start after the active scripted request completes.");
            Assert.AreEqual("second-first", inner.StartedSnapshot()[1],
                "A newly waiting actor must be admitted before the already-served actor's backlog.");

            await CompleteAllScriptedAsync(inner, backlogCount + 2);
            await Task.WhenAll(tasks);
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
        // Test 3: MaxConcurrent - no more than N tasks at a time
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task MaxConcurrent_LimitsParallelExecution()
        {
            // Arrange: MaxConcurrent = 2
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            // Start 4 tasks
            Task t1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t1" });
            Task t2 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t2" });
            Task t3 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t3" });
            Task t4 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t4" });

            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "Exactly two tasks must start at MaxConcurrent=2.");

            // Assert: only 2 tasks may start running
            Assert.AreEqual(2, inner.Gates.Count,
                "MaxConcurrent=2: only 2 tasks may start running at the same time");
            Assert.AreEqual("t1", inner.ExecutionLog[0]);
            Assert.AreEqual("t2", inner.ExecutionLog[1]);

            // Finish the first one, so the third must start
            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 3,
                "Third task must start when the first slot is released.");

            Assert.AreEqual(3, inner.Gates.Count,
                "The third task must start once the first one has finished");
            Assert.AreEqual("t3", inner.ExecutionLog[2]);

            // Finish the second one, so the fourth must start
            inner.Gates[1].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 4,
                "Fourth task must start when the second slot is released.");

            Assert.AreEqual(4, inner.Gates.Count,
                "The fourth task must start once the second one has finished");
            Assert.AreEqual("t4", inner.ExecutionLog[3]);

            // Cleanup
            inner.Gates[2].TrySetResult(null);
            inner.Gates[3].TrySetResult(null);
            await Task.WhenAll(t1, t2, t3, t4);
        }

        // ──────────────────────────────────────────────────────────
        // Test 4: CancelTasks - cancels the running tasks of the given scope and drops that scope from the queue
        // ──────────────────────────────────────────────────────────

        [Test]
        public async Task CancelTasks_SpecificScope_CancelsActiveTask()
        {
            // Arrange: MaxConcurrent = 1
            RecordingOrchestrator inner = new();
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            // Task 1 (active)
            Task t1 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t1", CancellationScope = "NPC1" });

            // Task 2 (pending, a different scope)
            Task t2 = queue.RunTaskAsync(new AiTaskRequest { Hint = "t2", CancellationScope = "NPC2" });

            await WaitUntilAsync(() => inner.Gates.Count == 1,
                "Only NPC1 should be active before scoped cancellation.");

            // Assert: only t1 started
            Assert.AreEqual(1, inner.Gates.Count);

            // Act: cancel every task for NPC1
            queue.CancelTasks("NPC1");
            using (CancellationTokenSource wait = new(TimeSpan.FromSeconds(10)))
            {
                while (!t1.IsCompleted && !wait.IsCancellationRequested)
                {
                    await Task.Yield();
                }
            }

            Assert.IsTrue(t1.IsCompleted,
                $"t1 must complete after CancelTasks (status={t1.Status}).");
            // t1 must be cancelled (IsCanceled)
            Assert.IsTrue(t1.IsCanceled,
                $"t1 (active) must be cancelled (status={t1.Status}, fault={(t1.IsFaulted ? t1.Exception?.GetBaseException().Message : null)}).");

            // t2 (NPC2) must have started running, because a slot became free.
            await WaitUntilAsync(() => inner.Gates.Count == 2,
                "NPC2 must start after cancelling active NPC1.");
            Assert.AreEqual(2, inner.Gates.Count, "t2 (NPC2) must start once NPC1 has been cancelled");
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

        private static ActorContext CreateActorContext(string actorId, string sessionId)
        {
            return new LocalActorIdentityProvider(
                    actorId,
                    sessionId,
                    "world",
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty)
                .GetActorContext("Teacher");
        }

        private static async Task CompleteAllScriptedAsync(
            ScriptedAdmissionOrchestrator inner,
            int expectedCount)
        {
            while (inner.CompletedCount < expectedCount)
            {
                await WaitUntilAsync(
                    () => inner.StartedCount > inner.CompletedCount,
                    "The fair queue did not start the next scripted request.");
                int startedBeforeCompletion = inner.StartedCount;
                inner.CompleteNext();
                if (startedBeforeCompletion < expectedCount)
                {
                    // WHY: Claims are locked but starts are not; one replacement at a time preserves admission order.
                    await WaitUntilAsync(
                        () => inner.StartedCount > startedBeforeCompletion,
                        "The fair queue did not replace the completed scripted request.");
                }
            }
        }

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
            ActorContext overflowActor = CreateActorContext("overflow-actor", "overflow-session");
            Task overflow = queue.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                SourceTag = "Chat",
                Hint = "overflow",
                ActorContext = overflowActor,
                CancellationScope = overflowActor.SessionId
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
            Assert.AreEqual("overflow-actor", caught.ActorId);
            Assert.AreEqual(2, caught.MaxPending);
            Assert.That(caught.Message, Does.Contain("overflow-actor"));
            Assert.That(caught.Message, Does.Contain("MaxPending=2"));
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

            ActorContext overflowActor = CreateActorContext("stream-overflow-actor", "stream-overflow-session");
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in queue.RunStreamingAsync(
                               new AiTaskRequest
                               {
                                    RoleId = "Teacher",
                                    SourceTag = "Chat",
                                    Hint = "overflow-stream",
                                    ActorContext = overflowActor,
                                    CancellationScope = overflowActor.SessionId
                                }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count,
                "A streaming request rejected by admission control gets exactly one terminal chunk.");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
            // WHY: the chunk text is shown in the chat bubble as-is. The internal actorId and MaxPending are for the
            // log, not for the learner.
            Assert.IsNotEmpty(chunks[0].Error);
            Assert.That(chunks[0].Error, Does.Not.Contain("MaxPending"));
            Assert.That(chunks[0].Error, Does.Not.Contain("stream-overflow-actor"));
            AssertSingleUnstartedTurn(inner, "Teacher", "overflow-stream");

            inner.Gates[0].TrySetResult(null);
            await WaitUntilAsync(() => inner.Gates.Count == 2, "p1 should start once blocker finishes.");
            inner.Gates[1].TrySetResult(null);

            await Task.WhenAll(blocker, p1);
        }

        // ──────────────────────────────────────────────────────────
        // Classifying inner-layer failures: a timeout is not a cancellation, and foreign wording never reaches the chat
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Defect: LlmOperationTimeoutException derives from OperationCanceledException, and the queue collapsed it
        /// into TrySetCanceled(). The caller got a bare TaskCanceledException, and presentation told the child "the
        /// request was stopped", as if they had interrupted the teacher's answer themselves.
        /// </summary>
        [Test]
        public async Task LibraryTimeout_RunTaskAsync_SurfacesAsTimeout_NotAsUserStop()
        {
            FaultingOrchestrator inner = new(() => new LlmOperationTimeoutException());
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());

            Task<string> turn = queue.RunTaskAsync(new AiTaskRequest { Hint = "slow teacher" });

            await CaptureExceptionAsync<LlmOperationTimeoutException>(() => turn);
            Assert.IsTrue(turn.IsFaulted, "A library timeout is a fault with a typed exception, not a cancellation.");
            Assert.IsFalse(turn.IsCanceled);
            queue.Dispose();
        }

        [Test]
        public async Task LibraryTimeout_RunStreamingAsync_EmitsTimeoutChunk_NotCancellation()
        {
            FaultingOrchestrator inner = new(() => new LlmOperationTimeoutException());
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());

            List<LlmStreamChunk> chunks = await DrainAsync(
                queue.RunStreamingAsync(new AiTaskRequest { Hint = "slow stream" }));

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode,
                "The queue must keep the timeout typed; Cancelled would tell the learner they stopped the answer.");
            queue.Dispose();
        }

        /// <summary>
        /// Defect: "a cancellation" meant any exception with an OperationCanceledException somewhere in its
        /// InnerException chain. A transport error with a nested TaskCanceledException, while the token was still
        /// alive, turned into "the learner stopped the answer".
        /// </summary>
        [Test]
        public async Task TransportFaultWrappingCancellation_WithLiveToken_IsAFault_NotCancellation()
        {
            FaultingOrchestrator inner = new(
                () => new InvalidOperationException("socket closed", new TaskCanceledException()));
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());

            Task<string> turn = queue.RunTaskAsync(new AiTaskRequest { Hint = "flaky transport" });

            InvalidOperationException thrown = await CaptureExceptionAsync<InvalidOperationException>(() => turn);
            Assert.AreEqual("socket closed", thrown.Message);
            Assert.IsFalse(turn.IsCanceled, "Nobody asked to stop this turn, so it must not read as a cancellation.");
            queue.Dispose();
        }

        [Test]
        public async Task CallerCancellation_WithFaultWrappingCancellation_StaysCancellation()
        {
            FaultingOrchestrator inner = new(
                () => new InvalidOperationException("torn down", new TaskCanceledException()))
            {
                WaitForCancellation = true
            };
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cts = new();

            Task<string> turn = queue.RunTaskAsync(new AiTaskRequest { Hint = "stopped by learner" }, cts.Token);
            await WaitUntilAsync(() => inner.Started, "The inner turn must start before the learner stops it.");
            cts.Cancel();

            await CaptureExceptionAsync<OperationCanceledException>(() => turn);
            Assert.IsTrue(turn.IsCanceled,
                "The learner stopped the turn: whatever the inner layer threw on top of that is still a cancellation.");
            queue.Dispose();
        }

        [Test]
        public async Task LibraryTimeoutRacingCallerCancellation_StaysCancellation()
        {
            FaultingOrchestrator inner = new(() => new LlmOperationTimeoutException())
            {
                WaitForCancellation = true
            };
            QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());
            using CancellationTokenSource cts = new();

            Task<string> turn = queue.RunTaskAsync(new AiTaskRequest { Hint = "stopped then timed out" }, cts.Token);
            await WaitUntilAsync(() => inner.Started, "The inner turn must start before the learner stops it.");
            cts.Cancel();

            await CaptureExceptionAsync<OperationCanceledException>(() => turn);
            Assert.IsTrue(turn.IsCanceled, "A cancelled token wins the race against the library timer.");
            queue.Dispose();
        }

        /// <summary>
        /// Defect: a stream producer failure went into the chat as Error = ex.Message with ErrorCode = None. The
        /// consumer picks the banner by the code, and None belongs to no category at all, so the child saw the text
        /// of an internal exception as the teacher's reply.
        /// </summary>
        [Test]
        public async Task StreamProducerFault_CarriesTypedCode_AndKeepsInternalMessageOutOfChat()
        {
            const string internalDetail = "actor 'student-42' state pointer is null";
            CoreAI.Logging.ILog savedLog = CoreAI.Logging.Log.Instance;
            CoreAI.Logging.Log.Instance = CoreAI.Logging.NullLog.Instance;
            try
            {
                FaultingOrchestrator inner = new(() => new InvalidOperationException(internalDetail));
                QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());

                List<LlmStreamChunk> chunks = await DrainAsync(
                    queue.RunStreamingAsync(new AiTaskRequest { Hint = "broken producer" }));

                Assert.AreEqual(1, chunks.Count);
                Assert.IsTrue(chunks[0].IsDone);
                Assert.AreEqual(LlmErrorCode.ProviderError, chunks[0].ErrorCode,
                    "None is not an availability failure for consumers; the chunk must carry a real category.");
                Assert.IsNotEmpty(chunks[0].Error);
                StringAssert.DoesNotContain("student-42", chunks[0].Error,
                    "Internal identifiers belong in the log, not in the learner's chat bubble.");
                queue.Dispose();
            }
            finally
            {
                CoreAI.Logging.Log.Instance = savedLog;
            }
        }

        [Test]
        public async Task StreamProducerLlmFault_KeepsAdapterClassification()
        {
            CoreAI.Logging.ILog savedLog = CoreAI.Logging.Log.Instance;
            CoreAI.Logging.Log.Instance = CoreAI.Logging.NullLog.Instance;
            try
            {
                FaultingOrchestrator inner = new(
                    () => new LlmClientException("HTTP error 429: slow down", LlmErrorCode.RateLimited, 429, 7));
                QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions());

                List<LlmStreamChunk> chunks = await DrainAsync(
                    queue.RunStreamingAsync(new AiTaskRequest { Hint = "rate limited" }));

                Assert.AreEqual(1, chunks.Count);
                Assert.AreEqual(LlmErrorCode.RateLimited, chunks[0].ErrorCode);
                Assert.AreEqual(429, chunks[0].HttpStatus);
                Assert.AreEqual(7, chunks[0].RetryAfterSeconds);
                // WHY: the adapter's Message is a string for the log ("HTTP error 429: ..."); what goes into the chat
                // bubble is a phrase for the player, without the transport prefix.
                Assert.IsNotEmpty(chunks[0].Error);
                Assert.That(chunks[0].Error, Does.Not.StartWith("HTTP error"));
                queue.Dispose();
            }
            finally
            {
                CoreAI.Logging.Log.Instance = savedLog;
            }
        }

        private static async Task<List<LlmStreamChunk>> DrainAsync(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in stream)
            {
                chunks.Add(chunk);
            }

            return chunks;
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
