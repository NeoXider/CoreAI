using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Tests the orchestrator streaming path, including the interface default fallback
    /// and transparent chunk forwarding through <see cref="QueuedAiOrchestrator"/>.
    /// </summary>
    public sealed class AiOrchestratorStreamingEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: a test here waits on a Task from the calling thread. Under
        /// Unity's SynchronizationContext that task's continuation is posted back to the very thread
        /// the wait is blocking, and the whole EditMode run hangs with no results file.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreflight_StreamingWaitsForConfirmationBeforeProviderAndEviction(bool failConfirmation)
        {
            AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario scenario = new();
            scenario.Summary.SaveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.Summary.FailSave = failConfirmation;
            Task<List<LlmStreamChunk>> turn = CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request));
            try
            {
                await AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario.AwaitEntered(scenario.Summary.SaveEntered.Task);
                Assert.IsFalse(turn.IsCompleted);
                scenario.AssertOldSourceRetained();
            }
            finally { scenario.Summary.SaveGate.TrySetResult(true); }
            if (failConfirmation)
            {
                Assert.That(await AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario.CaptureFailure(turn),
                    Is.TypeOf<IOException>());
                scenario.AssertOldSourceRetained();
                scenario.Summary.FailSave = false;
                await CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request));
            }
            else { await turn; }
            scenario.AssertPublishedOnce();
        }

        [Test]
        public async Task SummaryPreflight_StreamingFailedLoadPreservesSourceAndCanRetry()
        {
            AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario scenario = new();
            scenario.Summary.FailLoad = true;
            Assert.That(await AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario.CaptureFailure(
                CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request))), Is.TypeOf<IOException>());
            scenario.AssertOldSourceRetained();
            scenario.Summary.FailLoad = false;
            await CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request));
            scenario.AssertPublishedOnce();
        }

        [Test]
        public async Task SummaryPreflight_StreamingCancellationDuringLoadDoesNotAppendFromFinally()
        {
            AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario scenario = new();
            scenario.Summary.LoadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource cancellation = new();
            Task<List<LlmStreamChunk>> turn = CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request, cancellation.Token));
            await AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario.AwaitEntered(scenario.Summary.LoadEntered.Task);
            cancellation.Cancel();
            Assert.That(await AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario.CaptureFailure(turn),
                Is.InstanceOf<OperationCanceledException>());
            scenario.AssertOldSourceRetained();
        }

        [Test]
        public async Task SummaryPreflight_StreamingProviderFailureAfterConfirmationRetainsUserIntentOnce()
        {
            AiOrchestratorRefactorEditModeTests.SummaryPreflightScenario scenario = new();
            scenario.Provider.Fail = true;
            await CollectAsync(scenario.Orchestrator.RunStreamingAsync(scenario.Request));
            Assert.AreEqual(1, scenario.Provider.Calls);
            Assert.AreEqual(0, scenario.Publications);
            Assert.AreEqual(1, scenario.Memory.Appends.Count);
            Assert.AreEqual(scenario.Request.Hint, scenario.Memory.Appends[0].Content);
            StringAssert.Contains(scenario.Memory.Original[0].Content, scenario.Summary.Stored);
        }

        /// <summary>
        /// Orchestrator stub that implements only <see cref="IAiOrchestrationService.RunTaskAsync"/>
        /// and relies on the interface default <c>RunStreamingAsync</c> implementation.
        /// Used to verify that the fallback emits text and completion chunks correctly.
        /// </summary>
        private sealed class FallbackOnlyOrchestrator : IAiOrchestrationService
        {
            private readonly string _result;

            public FallbackOnlyOrchestrator(string result)
            {
                _result = result;
            }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        /// <summary>
        /// Orchestrator stub with an explicit streaming implementation that emits configured delta chunks.
        /// Used to verify that <see cref="QueuedAiOrchestrator"/> forwards queued chunks without buffering.
        /// </summary>
        private sealed class StreamingOrchestrator : IAiOrchestrationService
        {
            private readonly string[] _parts;
            public int StreamCalls { get; private set; }
            public int RunTaskCalls { get; private set; }

            public StreamingOrchestrator(params string[] parts)
            {
                _parts = parts;
            }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                RunTaskCalls++;
                return Task.FromResult(string.Concat(_parts));
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                foreach (string part in _parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new LlmStreamChunk { Text = part };
                    await Task.Yield();
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class MixedQueueOrchestrator : IAiOrchestrationService
        {
            private readonly object _lock = new();
            public List<string> ExecutionLog { get; } = new();
            public List<TaskCompletionSource<string>> Gates { get; } = new();

            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock)
                {
                    ExecutionLog.Add("task:" + (task?.Hint ?? ""));
                    Gates.Add(gate);
                }

                using CancellationTokenRegistration reg = cancellationToken.Register(() => gate.TrySetCanceled());
                return await gate.Task;
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock)
                {
                    ExecutionLog.Add("stream:" + (task?.Hint ?? ""));
                    Gates.Add(gate);
                }

                yield return new LlmStreamChunk { Text = "stream-start" };
                using CancellationTokenRegistration reg = cancellationToken.Register(() => gate.TrySetCanceled());
                await gate.Task;
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        [Test]
        public async Task DefaultFallback_EmitsSingleTextChunkThenDone()
        {
            IAiOrchestrationService svc = new FallbackOnlyOrchestrator("full result");

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(2, chunks.Count, "default fallback -> 1 text + 1 terminal");
            Assert.AreEqual("full result", chunks[0].Text);
            Assert.IsFalse(chunks[0].IsDone);
            Assert.IsTrue(chunks[1].IsDone);
        }

        [Test]
        public async Task DefaultFallback_EmptyResult_EmitsErrorChunk()
        {
            IAiOrchestrationService svc = new FallbackOnlyOrchestrator(null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual("empty result", chunks[0].Error);
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_DelegatesRealChunks()
        {
            // If QueuedAiOrchestrator did not override RunStreamingAsync, the default fallback
            // would glue the whole answer into 1 chunk through RunTaskAsync. This test pins the contract.
            StreamingOrchestrator inner = new("Hel", "lo,", " wo", "rld!");
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in queued.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "Tester", Hint = "go" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, inner.StreamCalls, "streaming must be called, not the sync path");
            Assert.AreEqual(0, inner.RunTaskCalls, "RunTaskAsync must not be called");

            // 4 text chunks + 1 terminal
            Assert.AreEqual(5, chunks.Count);
            Assert.AreEqual("Hel", chunks[0].Text);
            Assert.AreEqual("lo,", chunks[1].Text);
            Assert.AreEqual(" wo", chunks[2].Text);
            Assert.AreEqual("rld!", chunks[3].Text);
            Assert.IsTrue(chunks[4].IsDone);
        }

        private static async Task AssertEventually(
            Func<bool> condition,
            string message,
            int timeoutMs = 5000,
            int pollMs = 20)
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

            Assert.IsTrue(condition(), message);
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_RespectsMaxConcurrent()
        {
            // Two streams in parallel with MaxConcurrent=1: second stream must wait until first finishes.
            StreamingOrchestrator inner = new("a", "b", "c");
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task<List<LlmStreamChunk>> stream1 = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "T1", Hint = "first" }));
            Task<List<LlmStreamChunk>> stream2 = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "T2", Hint = "second" }));

            await Task.WhenAll(stream1, stream2);

            Assert.AreEqual(4, stream1.Result.Count, "stream1: 3 text chunks + 1 terminal");
            Assert.AreEqual(4, stream2.Result.Count, "stream2: 3 text chunks + 1 terminal");
            Assert.AreEqual(2, inner.StreamCalls, "both streams ran");
        }

        [Test]
        public async Task QueuedAiOrchestrator_StreamingAndTask_UseSharedPriorityQueue()
        {
            MixedQueueOrchestrator inner = new();
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queued.RunTaskAsync(new AiTaskRequest { Hint = "blocker", Priority = 0 });
            Task lowTask = queued.RunTaskAsync(new AiTaskRequest { Hint = "low-task", Priority = 1 });
            Task<List<LlmStreamChunk>> highStream = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { Hint = "high-stream", Priority = 10 }));

            await AssertEventually(
                () => inner.ExecutionLog.Count == 1 && inner.ExecutionLog[0] == "task:blocker",
                "Only the blocking task should run while MaxConcurrent slots are full.");

            inner.Gates[0].TrySetResult(null);

            await AssertEventually(
                () => inner.ExecutionLog.Count >= 2 && inner.ExecutionLog[1] == "stream:high-stream",
                "A higher-priority stream should run before a lower-priority non-stream task.");

            inner.Gates[1].TrySetResult(null);
            await highStream;

            await AssertEventually(
                () => inner.ExecutionLog.Count >= 3 && inner.ExecutionLog[2] == "task:low-task",
                "Lower-priority task should run after the high-priority stream.");

            inner.Gates[2].TrySetResult(null);
            await Task.WhenAll(blocker, lowTask);
        }

        [Test]
        public async Task QueuedAiOrchestrator_StreamingCancellationScope_PendingLatestWins()
        {
            MixedQueueOrchestrator inner = new();
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queued.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task<List<LlmStreamChunk>> oldStream = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { Hint = "old-stream", CancellationScope = "npc" }));
            Task<List<LlmStreamChunk>> latestStream = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { Hint = "latest-stream", CancellationScope = "npc" }));

            await AssertEventually(
                () => oldStream.IsCompleted,
                "Older pending stream should complete immediately when superseded.");

            Assert.AreEqual(1, inner.ExecutionLog.Count, "Only blocker should be active.");
            AssertHasCancelledTerminal(oldStream.Result);

            inner.Gates[0].TrySetResult(null);

            await AssertEventually(
                () => inner.ExecutionLog.Count >= 2 && inner.ExecutionLog[1] == "stream:latest-stream",
                "After blocker, the latest stream in the same CancellationScope should run.");

            inner.Gates[1].TrySetResult(null);
            await latestStream;
            await blocker;
        }

        [Test]
        public async Task QueuedAiOrchestrator_StreamingCancelTasks_CancelsPendingStream()
        {
            MixedQueueOrchestrator inner = new();
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task blocker = queued.RunTaskAsync(new AiTaskRequest { Hint = "blocker" });
            Task<List<LlmStreamChunk>> pendingStream = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { Hint = "pending-stream", CancellationScope = "npc" }));

            await AssertEventually(
                () => inner.ExecutionLog.Count == 1,
                "Blocker must be running before we cancel the pending stream.");

            queued.CancelTasks("npc");
            await AssertEventually(
                () => pendingStream.IsCompleted,
                "Pending stream should complete when its scope is cancelled.");

            AssertHasCancelledTerminal(pendingStream.Result);
            Assert.AreEqual(1, inner.ExecutionLog.Count, "Cancelled pending stream must not start later.");

            inner.Gates[0].TrySetResult(null);
            await blocker;
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_ExternalCancellation_EmitsCancelledTerminal()
        {
            // A user cancellation (the cancellationToken parameter) during a stream
            // must lead to a terminal chunk with Error="cancelled", not to an
            // unhandled OperationCanceledException in the reader.
            SlowStreamingOrchestrator inner = new();
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            using CancellationTokenSource cts = new();
            List<LlmStreamChunk> collected = new();

            // Cancel after 80 ms, when the stream has already started emitting chunks.
            _ = Task.Run(async () =>
            {
                await Task.Delay(80);
                cts.Cancel();
            });

            await foreach (LlmStreamChunk chunk in queued.RunStreamingAsync(
                               new AiTaskRequest { RoleId = "T", Hint = "first" }, cts.Token))
            {
                collected.Add(chunk);
                if (chunk.IsDone)
                {
                    break;
                }
            }

            // There must be at least one terminal chunk with Error="cancelled".
            bool gotCancelled = false;
            foreach (LlmStreamChunk chunk in collected)
            {
                if (chunk.IsDone && chunk.Error == "cancelled")
                {
                    gotCancelled = true;
                    break;
                }
            }

            Assert.IsTrue(gotCancelled,
                $"QueuedAiOrchestrator must emit a terminal chunk with Error=\"cancelled\" on cancellation. " +
                $"Chunks received: {collected.Count}");
        }

        private static void AssertHasCancelledTerminal(IReadOnlyList<LlmStreamChunk> chunks)
        {
            bool gotCancelled = false;
            foreach (LlmStreamChunk chunk in chunks)
            {
                if (chunk.IsDone && chunk.Error == "cancelled")
                {
                    gotCancelled = true;
                    break;
                }
            }

            Assert.IsTrue(gotCancelled, "Expected a terminal stream chunk with Error=\"cancelled\".");
        }

        private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
        {
            List<T> list = new();
            await foreach (T item in source)
            {
                list.Add(item);
            }

            return list;
        }

        /// <summary>
        /// Emits the first chunk immediately, then waits for either cancellation or its own gate.
        /// </summary>
        private sealed class SlowStreamingOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("sync");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = "first-chunk" };

                // Simulate a long generation while still reacting to cancellation.
                try
                {
                    await Task.Delay(10000, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }
    }
}
