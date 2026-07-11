#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CircuitBreakerLlmClientDecorator"/>: trip-open on consecutive
    /// transient failures, short-circuit while open, half-open probe recovery and re-open, and the rule
    /// that caller-caused failures never trip the breaker. Time is a manual clock so tests are deterministic.
    /// </summary>
    public sealed class CircuitBreakerLlmClientDecoratorEditModeTests
    {
        [Test]
        public async Task TripsOpen_AfterThresholdConsecutiveTransientFailures_ThenShortCircuits()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 3, 1000, clock.NowMs);

            // 3 transient failures trip the breaker.
            inner.NextResults.Enqueue(Fail(LlmErrorCode.BackendUnavailable));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.ProviderError));

            for (int i = 0; i < 3; i++)
            {
                await breaker.CompleteAsync(Req());
            }

            Assert.AreEqual("Open", breaker.StateName, "3 consecutive transient failures must open the breaker.");
            Assert.AreEqual(3, inner.CallCount, "All 3 real calls should have reached the inner client.");

            // While open, the next call is short-circuited without touching the inner client.
            LlmCompletionResult shortCircuited = await breaker.CompleteAsync(Req());
            Assert.IsFalse(shortCircuited.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, shortCircuited.ErrorCode);
            Assert.AreEqual(3, inner.CallCount, "An open breaker must NOT invoke the inner client.");
        }

        [Test]
        public async Task HalfOpenProbe_Succeeds_ClosesBreaker()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            await breaker.CompleteAsync(Req());
            await breaker.CompleteAsync(Req());
            Assert.AreEqual("Open", breaker.StateName);

            // Still open before the cooldown elapses.
            clock.Advance(999);
            LlmCompletionResult stillOpen = await breaker.CompleteAsync(Req());
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, stillOpen.ErrorCode);
            Assert.AreEqual(2, inner.CallCount, "Before cooldown the breaker stays open and short-circuits.");

            // After cooldown, one probe is admitted; it succeeds and closes the breaker.
            clock.Advance(1);
            inner.NextResults.Enqueue(Success());
            LlmCompletionResult probe = await breaker.CompleteAsync(Req());
            Assert.IsTrue(probe.Ok, "The half-open probe should reach the inner client and succeed.");
            Assert.AreEqual("Closed", breaker.StateName, "A successful probe closes the breaker.");
            Assert.AreEqual(3, inner.CallCount);
        }

        [Test]
        public async Task HalfOpenProbe_Fails_ReopensBreaker()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 500, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.BackendUnavailable));
            await breaker.CompleteAsync(Req());
            Assert.AreEqual("Open", breaker.StateName);

            clock.Advance(500);
            inner.NextResults.Enqueue(Fail(LlmErrorCode.BackendUnavailable)); // probe fails
            await breaker.CompleteAsync(Req());
            Assert.AreEqual("Open", breaker.StateName, "A failed half-open probe re-opens the breaker.");

            // And it short-circuits again immediately after re-opening.
            int callsBefore = inner.CallCount;
            await breaker.CompleteAsync(Req());
            Assert.AreEqual(callsBefore, inner.CallCount, "Re-opened breaker short-circuits without calling inner.");
        }

        [Test]
        public async Task CallerCausedFailures_DoNotTripBreaker()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            // Auth + invalid-request + context-length are the caller's problem, not backend health.
            inner.NextResults.Enqueue(Fail(LlmErrorCode.AuthExpired));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.InvalidRequest));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.ContextLengthExceeded));

            for (int i = 0; i < 3; i++)
            {
                await breaker.CompleteAsync(Req());
            }

            Assert.AreEqual("Closed", breaker.StateName,
                "Caller-caused failures must never trip the breaker (retrying would not help).");
            Assert.AreEqual(3, inner.CallCount);
        }

        [Test]
        public async Task InterleavedSuccess_ResetsConsecutiveFailureCount()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 3, 1000, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextResults.Enqueue(Success()); // resets the counter
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));

            for (int i = 0; i < 5; i++)
            {
                await breaker.CompleteAsync(Req());
            }

            Assert.AreEqual("Closed", breaker.StateName,
                "A success between failures resets the streak, so 2+2 (not 4) must not trip a threshold of 3.");
        }

        [Test]
        public async Task Streaming_ErrorChunk_CountsAsFailure_AndCanTripOpen()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.BackendUnavailable) });
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.BackendUnavailable) });

            await Drain(breaker.CompleteStreamingAsync(Req()));
            await Drain(breaker.CompleteStreamingAsync(Req()));

            Assert.AreEqual("Open", breaker.StateName, "Two failing streams must trip the breaker.");

            // Open breaker short-circuits the stream with a single terminal error chunk.
            List<LlmStreamChunk> chunks = await Drain(breaker.CompleteStreamingAsync(Req()));
            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
            Assert.AreEqual(2, inner.StreamCallCount, "Open breaker must not start a new inner stream.");
        }

        [Test]
        public async Task HalfOpen_ConcurrentCalls_OnlyOneProbeReachesInner()
        {
            GatedLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            Task<LlmCompletionResult> trip = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Timeout));
            await trip;
            Assert.AreEqual("Open", breaker.StateName);

            clock.Advance(1000);
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req());
            Assert.AreEqual(2, inner.CallCount, "The half-open probe must reach the inner client.");

            // While the probe is still in flight, concurrent calls must be short-circuited.
            LlmCompletionResult concurrent = await breaker.CompleteAsync(Req());
            Assert.IsFalse(concurrent.Ok);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, concurrent.ErrorCode);
            Assert.AreEqual(2, inner.CallCount,
                "Half-open must admit exactly ONE probe; concurrent calls must NOT burst onto the backend.");

            inner.Pending.Dequeue().SetResult(Success());
            LlmCompletionResult probeResult = await probe;
            Assert.IsTrue(probeResult.Ok);
            Assert.AreEqual("Closed", breaker.StateName, "A successful probe closes the breaker.");
        }

        [Test]
        public async Task HalfOpen_CancelledProbe_ReleasesProbeSlot_WithoutCountingFailure()
        {
            GatedLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            Task<LlmCompletionResult> trip = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Timeout));
            await trip;
            Assert.AreEqual("Open", breaker.StateName);

            clock.Advance(1000);
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetCanceled();
            try
            {
                await probe;
                Assert.Fail("A cancelled probe must rethrow the cancellation.");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual("HalfOpen", breaker.StateName,
                "Cancellation is caller intent: it must neither re-open (failure) nor close (success) the breaker.");

            // The probe slot must be free again: the next call becomes the new probe.
            Task<LlmCompletionResult> retry = breaker.CompleteAsync(Req());
            Assert.AreEqual(3, inner.CallCount,
                "After a cancelled probe the slot must be released so the next call can probe.");
            inner.Pending.Dequeue().SetResult(Success());
            Assert.IsTrue((await retry).Ok);
            Assert.AreEqual("Closed", breaker.StateName);
        }

        [Test]
        public async Task Streaming_ConsumerAbandonsHealthyStream_NotCountedAsFailure()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            // Abandon several healthy streams mid-way (user pressed stop) — with threshold 2, any
            // misclassification of abandonment as failure would trip the breaker open.
            for (int i = 0; i < 4; i++)
            {
                inner.NextStreams.Enqueue(new[]
                {
                    new LlmStreamChunk { Text = "chunk-1" },
                    new LlmStreamChunk { Text = "chunk-2", IsDone = true }
                });
                await foreach (LlmStreamChunk _ in breaker.CompleteStreamingAsync(Req()))
                {
                    break; // consumer abandons after the first chunk
                }
            }

            Assert.AreEqual("Closed", breaker.StateName,
                "Abandoned healthy streams carry no health verdict and must never trip the breaker.");
        }

        [Test]
        public async Task Streaming_HalfOpenProbeAbandoned_ReleasesProbeSlot_ThenNextProbeCloses()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.BackendUnavailable));
            await breaker.CompleteAsync(Req());
            Assert.AreEqual("Open", breaker.StateName);

            clock.Advance(1000);
            inner.NextStreams.Enqueue(new[]
            {
                new LlmStreamChunk { Text = "chunk-1" },
                new LlmStreamChunk { Text = "chunk-2", IsDone = true }
            });
            await foreach (LlmStreamChunk _ in breaker.CompleteStreamingAsync(Req()))
            {
                break; // probe abandoned before the stream ends
            }

            Assert.AreEqual("HalfOpen", breaker.StateName,
                "An abandoned probe stream is no verdict: stay half-open, do not re-open or close.");

            // The released probe slot lets the next (drained) stream act as the new probe and close.
            inner.NextStreams.Enqueue(new[] { new LlmStreamChunk { Text = "ok", IsDone = true } });
            await Drain(breaker.CompleteStreamingAsync(Req()));
            Assert.AreEqual("Closed", breaker.StateName,
                "A clean probe stream after the abandoned one must close the breaker.");
            Assert.AreEqual(2, inner.StreamCallCount);
        }

        [Test]
        public async Task Streaming_AbandonedAfterTerminalErrorChunk_StillCountsAsFailure()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            // The error chunk is followed by more chunks the consumer never reads: the failure
            // verdict already exists and must be recorded even though the stream was abandoned.
            inner.NextStreams.Enqueue(new[]
            {
                ErrChunk(LlmErrorCode.BackendUnavailable),
                new LlmStreamChunk { Text = "never-read", IsDone = true }
            });
            await foreach (LlmStreamChunk c in breaker.CompleteStreamingAsync(Req()))
            {
                if (!string.IsNullOrEmpty(c.Error))
                {
                    break;
                }
            }

            Assert.AreEqual("Open", breaker.StateName,
                "A terminal error chunk seen before abandonment is a real failure and must trip a threshold of 1.");
        }

        // ---- helpers ----

        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
        }

        private static LlmCompletionResult Success()
        {
            return new LlmCompletionResult { Ok = true, Content = "ok" };
        }

        private static LlmCompletionResult Fail(LlmErrorCode code)
        {
            return new LlmCompletionResult { Ok = false, Error = code.ToString(), ErrorCode = code };
        }

        private static LlmStreamChunk ErrChunk(LlmErrorCode code)
        {
            return new LlmStreamChunk { IsDone = true, Error = code.ToString(), ErrorCode = code };
        }

        private static async Task<List<LlmStreamChunk>> Drain(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in stream)
            {
                chunks.Add(c);
            }

            return chunks;
        }

        private sealed class ManualClock
        {
            private long _ms;

            public long NowMs()
            {
                return _ms;
            }

            public void Advance(long ms)
            {
                _ms += ms;
            }
        }

        /// <summary>
        /// Inner client whose calls stay in flight until the test completes them explicitly —
        /// lets tests hold a half-open probe open while issuing concurrent calls deterministically.
        /// </summary>
        private sealed class GatedLlmClient : ILlmClient
        {
            public readonly Queue<TaskCompletionSource<LlmCompletionResult>> Pending = new();
            public int CallCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CallCount++;
                TaskCompletionSource<LlmCompletionResult> tcs =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending.Enqueue(tcs);
                return tcs.Task;
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "ok", IsDone = true };
            }
        }

        private sealed class ProgrammableLlmClient : ILlmClient
        {
            public readonly Queue<LlmCompletionResult> NextResults = new();
            public readonly Queue<LlmStreamChunk[]> NextStreams = new();
            public int CallCount;
            public int StreamCallCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CallCount++;
                LlmCompletionResult r = NextResults.Count > 0
                    ? NextResults.Dequeue()
                    : new LlmCompletionResult { Ok = true };
                return Task.FromResult(r);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCallCount++;
                LlmStreamChunk[] chunks = NextStreams.Count > 0
                    ? NextStreams.Dequeue()
                    : new[] { new LlmStreamChunk { Text = "ok", IsDone = true } };
                foreach (LlmStreamChunk c in chunks)
                {
                    await Task.Yield();
                    yield return c;
                }
            }
        }
    }
}
#endif
