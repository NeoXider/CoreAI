#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Regression tests for correctness bugs in the orchestration/resilience layer: the queue pump
    /// deadlocking against a concurrent cancellation callback, a foreign-token cancellation being retried
    /// as a provider fault, a leaked circuit-breaker half-open probe slot, and the null-request asymmetry
    /// between the streaming and non-streaming orchestrator entry points.
    /// </summary>
    public sealed class OrchestrationResilienceEditModeTests
    {
        private const int RaceRounds = 200;
        private const int RaceBatch = 32;

        [Test]
        public void QueuedOrchestrator_CancelRacingThePump_DoesNotDeadlock()
        {
            Task scenario = Task.Run(RunCancelPumpRace);

            Assert.IsTrue(
                scenario.Wait(TimeSpan.FromSeconds(30)),
                "QueuedAiOrchestrator deadlocked: the pump waited for a cancellation callback " +
                "while holding the lock that callback needs.");

            scenario.GetAwaiter().GetResult();
        }

        [Test]
        public async Task RetryingStreamingDecorator_ForeignTokenCancellation_PropagatesWithoutRetry()
        {
            ForeignCancelStreamingClient inner = new();
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);

            bool cancelled = false;
            try
            {
                await DrainAsync(sut);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(
                cancelled,
                "A cancellation carried by a non-caller token must propagate, not become a retryable " +
                "provider error.");
            Assert.AreEqual(1, inner.StreamOpens, "A cancelled stream must never be re-opened.");
        }

        [Test]
        public async Task CircuitBreaker_HalfOpenProbeThrowingSynchronously_ReleasesTheProbeSlot()
        {
            ProbeStreamingClient inner = new();
            long nowMs = 0;
            CircuitBreakerLlmClientDecorator sut = new(inner, 1, 10, () => nowMs);

            inner.Mode = ProbeMode.TransientError;
            await DrainAsync(sut);
            Assert.AreEqual("Open", sut.StateName);

            nowMs = 100;
            inner.Mode = ProbeMode.ThrowBeforeEnumerating;
            try
            {
                await DrainAsync(sut);
                Assert.Fail("The half-open probe was expected to throw.");
            }
            catch (InvalidOperationException)
            {
            }

            inner.Mode = ProbeMode.Success;
            List<LlmStreamChunk> chunks = await DrainAsync(sut);

            Assert.AreEqual(
                "Closed", sut.StateName,
                "The failed probe leaked its slot, so the breaker rejects every later call forever.");
            Assert.IsTrue(chunks.Exists(c => c.Text == "ok"));
        }

        [Test]
        public async Task AiOrchestrator_RunTaskAsync_NullTask_MatchesStreamingTwin()
        {
            AiOrchestrator sut = new(
                new SoloAuthorityHost(), null, null, null, null, null, null, null, null,
                new CoreAISettingsOptions());

            string result = await sut.RunTaskAsync(null);
            Assert.IsNull(result);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.RunStreamingAsync(null))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
        }

        private static void RunCancelPumpRace()
        {
            using QueuedAiOrchestrator queue = new(
                new ImmediateOrchestrator(),
                new AiOrchestrationQueueOptions { MaxConcurrent = 2, MaxPending = 512 });

            for (int round = 0; round < RaceRounds; round++)
            {
                List<Task> started = new(RaceBatch);
                List<CancellationTokenSource> sources = new(RaceBatch);

                for (int i = 0; i < RaceBatch; i++)
                {
                    CancellationTokenSource cts = new();
                    sources.Add(cts);

                    // The cancellation callback races the pump that is starting this very work item.
                    Task canceller = Task.Run(() => cts.Cancel());
                    started.Add(canceller);
                    started.Add(Swallow(queue.RunTaskAsync(new AiTaskRequest(), cts.Token)));
                }

                Task.WaitAll(started.ToArray(), TimeSpan.FromSeconds(20));

                foreach (CancellationTokenSource cts in sources)
                {
                    cts.Dispose();
                }
            }
        }

        private static async Task Swallow(Task<string> task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        private static async Task<List<LlmStreamChunk>> DrainAsync(ILlmClient client)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        private sealed class ImmediateOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("done");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new LlmStreamChunk { IsDone = true, Text = "done" };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class ForeignCancelStreamingClient : ILlmClient
        {
            public int StreamOpens;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                await Task.Yield();

                // Stands in for the timeout decorator's linked CTS / a per-read idle timer: cancellation
                // carried by a token the caller never sees.
                using (CancellationTokenSource foreign = new())
                {
                    foreign.Cancel();
                    foreign.Token.ThrowIfCancellationRequested();
                }

                yield return new LlmStreamChunk { IsDone = true, Text = "never reached" };
            }
        }

        private enum ProbeMode
        {
            Success,
            TransientError,
            ThrowBeforeEnumerating
        }

        private sealed class ProbeStreamingClient : ILlmClient
        {
            public ProbeMode Mode = ProbeMode.Success;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (Mode == ProbeMode.ThrowBeforeEnumerating)
                {
                    throw new InvalidOperationException("connection refused");
                }

                return Iterate(Mode);
            }

            private static async IAsyncEnumerable<LlmStreamChunk> Iterate(ProbeMode mode)
            {
                await Task.Yield();
                if (mode == ProbeMode.TransientError)
                {
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "backend exploded",
                        ErrorCode = LlmErrorCode.ProviderError
                    };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "ok" };
                yield return new LlmStreamChunk { IsDone = true, Text = "" };
            }
        }
    }
}
#endif
