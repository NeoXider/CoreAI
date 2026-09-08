#if COREAI_LLM
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
        public async Task CallerCancelledResult_ReleasesProbeWithoutClaimingBackendRecovery()
        {
            GatedLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            Task<LlmCompletionResult> trip = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Timeout));
            await trip;
            clock.Advance(1000);
            using CancellationTokenSource caller = new();
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req(), caller.Token);
            caller.Cancel();
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Cancelled));
            await probe;
            Assert.AreEqual("HalfOpen", breaker.StateName);
            Task<LlmCompletionResult> retry = breaker.CompleteAsync(Req());
            Assert.AreEqual(3, inner.CallCount);
            inner.Pending.Dequeue().SetResult(Success());
            await retry;
        }

        [Test]
        public async Task CallerCancelledStreamResult_ReleasesProbeWithoutClaimingBackendRecovery()
        {
            ProgrammableLlmClient inner = new();
            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.Cancelled) });
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            await breaker.CompleteAsync(Req());
            clock.Advance(1000);
            using CancellationTokenSource caller = new();
            IAsyncEnumerator<LlmStreamChunk> iterator = breaker.CompleteStreamingAsync(Req(), caller.Token).GetAsyncEnumerator();
            Assert.IsTrue(await iterator.MoveNextAsync());
            caller.Cancel();
            await iterator.DisposeAsync();
            Assert.AreEqual("HalfOpen", breaker.StateName);
            Assert.IsTrue((await breaker.CompleteAsync(Req())).Ok);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LibraryTimeout_IsBackendFailureEvenThoughItInheritsCancellation(bool streaming)
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            if (streaming)
            {
                inner.NextStreamExceptions.Enqueue(new LlmOperationTimeoutException());
                List<LlmStreamChunk> chunks = await Drain(breaker.CompleteStreamingAsync(Req()));
                Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
            }
            else
            {
                inner.NextExceptions.Enqueue(new LlmOperationTimeoutException());
                await CaptureExceptionAsync<LlmOperationTimeoutException>(() => breaker.CompleteAsync(Req()));
            }
            Assert.AreEqual("Open", breaker.StateName);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, (await breaker.CompleteAsync(Req())).ErrorCode);
        }

        [Test]
        public async Task Streaming_ErrorWithoutTypedCode_TripsBreaker()
        {
            ProgrammableLlmClient inner = new();
            inner.NextStreams.Enqueue(new[] { new LlmStreamChunk { IsDone = true, Error = "backend failed" } });
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, () => 0);
            await Drain(breaker.CompleteStreamingAsync(Req()));
            Assert.AreEqual("Open", breaker.StateName);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OlderCompletion_CannotCloseOrReleaseAnotherHalfOpenProbe(bool cancelOld)
        {
            GatedLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            using CancellationTokenSource oldCaller = new();
            Task<LlmCompletionResult> old = breaker.CompleteAsync(Req(), oldCaller.Token);
            TaskCompletionSource<LlmCompletionResult> oldResult = inner.Pending.Dequeue();
            Task<LlmCompletionResult> trip = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Timeout));
            await trip;
            clock.Advance(1000);
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req());
            TaskCompletionSource<LlmCompletionResult> probeResult = inner.Pending.Dequeue();

            if (cancelOld)
            {
                oldCaller.Cancel();
                oldResult.SetCanceled();
                try { await old; } catch (OperationCanceledException) { }
            }
            else
            {
                oldResult.SetResult(Success());
                await old;
            }
            Assert.AreEqual("HalfOpen", breaker.StateName);
            Task<LlmCompletionResult> rejected = breaker.CompleteAsync(Req());
            if (inner.Pending.Count > 0)
            {
                // Make an incorrectly admitted call finish too, so the regression fails instead of hanging.
                inner.Pending.Dequeue().SetResult(Success());
            }
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, (await rejected).ErrorCode);
            Assert.AreEqual(3, inner.CallCount);
            probeResult.SetResult(Success());
            Assert.IsTrue((await probe).Ok);
        }

        [Test]
        public async Task OlderFailure_CannotReopenRecoveredCircuit()
        {
            GatedLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            Task<LlmCompletionResult> old = breaker.CompleteAsync(Req());
            TaskCompletionSource<LlmCompletionResult> oldResult = inner.Pending.Dequeue();
            Task<LlmCompletionResult> trip = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Fail(LlmErrorCode.Timeout));
            await trip;
            clock.Advance(1000);
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req());
            inner.Pending.Dequeue().SetResult(Success());
            await probe;
            oldResult.SetResult(Fail(LlmErrorCode.Timeout));
            await old;
            Assert.AreEqual("Closed", breaker.StateName);
        }

        [Test]
        public async Task AbandonedProbe_DisposeThrows_DoesNotStrandHalfOpenSlot()
        {
            DisposeFaultLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);
            await breaker.CompleteAsync(Req());
            clock.Advance(1000);
            IAsyncEnumerator<LlmStreamChunk> iterator = breaker.CompleteStreamingAsync(Req()).GetAsyncEnumerator();
            Assert.IsTrue(await iterator.MoveNextAsync());
            await CaptureExceptionAsync<InvalidOperationException>(async () => await iterator.DisposeAsync());
            Assert.IsTrue((await breaker.CompleteAsync(Req())).Ok);
            Assert.AreEqual("Closed", breaker.StateName);
        }

        private sealed class DisposeFaultLlmClient : ILlmClient, IAsyncEnumerable<LlmStreamChunk>, IAsyncEnumerator<LlmStreamChunk>
        {
            private bool _first = true;
            public LlmStreamChunk Current => new() { Text = "partial" };
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                bool first = _first;
                _first = false;
                return Task.FromResult(first ? Fail(LlmErrorCode.Timeout) : Success());
            }
            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) => this;
            public IAsyncEnumerator<LlmStreamChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() => new(true);
            public ValueTask DisposeAsync() => new(Task.FromException(new InvalidOperationException("dispose failed")));
        }

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
            using CancellationTokenSource caller = new();
            Task<LlmCompletionResult> probe = breaker.CompleteAsync(Req(), caller.Token);
            caller.Cancel();
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

        // ---- Сбои по вине вызывающего, пришедшие ИСКЛЮЧЕНИЕМ (док обещал: никогда не размыкают) ----

        [Test]
        public async Task ThrownAuthFailures_DoNotTripBreaker_AndPropagateUnchanged()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 3, 1000, clock.NowMs);

            for (int i = 0; i < 3; i++)
            {
                inner.NextExceptions.Enqueue(new LlmClientException("HTTP error 401: expired", LlmErrorCode.AuthExpired, 401));
                LlmClientException thrown = await CaptureExceptionAsync<LlmClientException>(() => breaker.CompleteAsync(Req()));
                Assert.AreEqual(LlmErrorCode.AuthExpired, thrown.ErrorCode, "Классификация адаптера должна пережить предохранитель.");
                Assert.AreEqual(401, thrown.HttpStatus, "HTTP-статус должен дойти до вызывающего.");
            }

            Assert.AreEqual("Closed", breaker.StateName,
                "Три 401 подряд — проблема вызывающего, не здоровья бэкенда: предохранитель обязан остаться замкнутым.");
            Assert.AreEqual(3, inner.CallCount);
        }

        [Test]
        public async Task ThrownPaymentRequired_KeepsItsCodeAndStatus_ForOuterDecorators()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextExceptions.Enqueue(new LlmClientException("HTTP error 402: out of credit", LlmErrorCode.PaymentRequired, 402));
            LlmClientException thrown = await CaptureExceptionAsync<LlmClientException>(() => breaker.CompleteAsync(Req()));

            Assert.AreEqual(LlmErrorCode.PaymentRequired, thrown.ErrorCode,
                "402 нельзя перебрасывать как ProviderError: внешние retry/fallback сочтут его транзиентным и будут повторять.");
            Assert.AreEqual(402, thrown.HttpStatus);
            Assert.AreEqual("Closed", breaker.StateName, "Требуется оплата — не сбой бэкенда, порог 1 не должен сработать.");
        }

        [Test]
        public async Task ThrownTransientTypedFailure_CountsTowardTripping()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            inner.NextExceptions.Enqueue(new LlmClientException("503", LlmErrorCode.BackendUnavailable, 503));
            inner.NextExceptions.Enqueue(new LlmClientException("timeout", LlmErrorCode.Timeout));
            await CaptureExceptionAsync<LlmClientException>(() => breaker.CompleteAsync(Req()));
            await CaptureExceptionAsync<LlmClientException>(() => breaker.CompleteAsync(Req()));

            Assert.AreEqual("Open", breaker.StateName, "Транзиентные сбои исключением считаются так же, как результатом.");
        }

        [Test]
        public async Task ThrownUntypedFailure_CountsAsTransient_AndPropagatesTheOriginalException()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextExceptions.Enqueue(new InvalidOperationException("socket reset"));
            InvalidOperationException thrown =
                await CaptureExceptionAsync<InvalidOperationException>(() => breaker.CompleteAsync(Req()));

            Assert.AreEqual("socket reset", thrown.Message, "Исходное исключение не должно подменяться обёрткой.");
            Assert.AreEqual("Open", breaker.StateName);
        }

        [Test]
        public async Task HalfOpenProbe_ThrowingCallerCausedFailure_ClosesBreaker_BackendIsReachable()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.BackendUnavailable));
            await breaker.CompleteAsync(Req());
            Assert.AreEqual("Open", breaker.StateName);

            clock.Advance(1000);
            inner.NextExceptions.Enqueue(new LlmClientException("HTTP error 400: bad request", LlmErrorCode.InvalidRequest, 400));
            await CaptureExceptionAsync<LlmClientException>(() => breaker.CompleteAsync(Req()));

            Assert.AreEqual("Closed", breaker.StateName,
                "Пробный запрос дошёл до бэкенда и получил ответ по вине вызывающего: бэкенд достижим, предохранитель замыкается.");
        }

        // ---- Поток ----

        [Test]
        public async Task Streaming_EmptyStream_IsAFailure_NotASuccess()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            inner.NextStreams.Enqueue(Array.Empty<LlmStreamChunk>());
            inner.NextStreams.Enqueue(Array.Empty<LlmStreamChunk>());
            await Drain(breaker.CompleteStreamingAsync(Req()));
            await Drain(breaker.CompleteStreamingAsync(Req()));

            Assert.AreEqual("Open", breaker.StateName,
                "Поток без единого чанка — бэкенд не ответил ничего; раньше это записывалось УСПЕХОМ.");
        }

        [Test]
        public async Task Streaming_EmptyStream_DoesNotResetTheFailureStreak()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 2, 1000, clock.NowMs);

            inner.NextResults.Enqueue(Fail(LlmErrorCode.Timeout));
            await breaker.CompleteAsync(Req());
            inner.NextStreams.Enqueue(Array.Empty<LlmStreamChunk>());
            await Drain(breaker.CompleteStreamingAsync(Req()));

            Assert.AreEqual("Open", breaker.StateName, "Пустой поток после таймаута — второй сбой подряд, не сброс серии.");
        }

        [Test]
        public async Task Streaming_ThrownTypedFailure_KeepsCodeAndStatusInTerminalChunk_AndDoesNotTripOnCallerFault()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextStreamExceptions.Enqueue(new LlmClientException("HTTP error 402: out of credit", LlmErrorCode.PaymentRequired, 402));
            List<LlmStreamChunk> chunks = await Drain(breaker.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.PaymentRequired, chunks[0].ErrorCode);
            Assert.AreEqual(402, chunks[0].HttpStatus);
            Assert.AreEqual("Closed", breaker.StateName, "402 в потоке — не сбой бэкенда.");
        }

        [Test]
        public async Task Streaming_ThrownTransientFailure_TripsBreaker()
        {
            ProgrammableLlmClient inner = new();
            ManualClock clock = new();
            CircuitBreakerLlmClientDecorator breaker = new(inner, 1, 1000, clock.NowMs);

            inner.NextStreamExceptions.Enqueue(new LlmClientException("503", LlmErrorCode.BackendUnavailable, 503));
            List<LlmStreamChunk> chunks = await Drain(breaker.CompleteStreamingAsync(Req()));

            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
            Assert.AreEqual(503, chunks[0].HttpStatus);
            Assert.AreEqual("Open", breaker.StateName);
        }

        // ---- helpers ----

        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
        }

        private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            // WHY: Unity must keep pumping continuations while a test awaits an expected failure.
            // NUnit's synchronous ThrowsAsync helper cannot pump UnitySynchronizationContext.
            try { await action(); }
            catch (TException exception) { return exception; }
            Assert.Fail("Expected exception was not thrown.");
            return null;
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
            public readonly Queue<Exception> NextExceptions = new();
            public readonly Queue<Exception> NextStreamExceptions = new();
            public int CallCount;
            public int StreamCallCount;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CallCount++;
                await Task.Yield();
                if (NextExceptions.Count > 0)
                {
                    throw NextExceptions.Dequeue();
                }

                return NextResults.Count > 0
                    ? NextResults.Dequeue()
                    : new LlmCompletionResult { Ok = true };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCallCount++;
                await Task.Yield();
                if (NextStreamExceptions.Count > 0)
                {
                    throw NextStreamExceptions.Dequeue();
                }

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
