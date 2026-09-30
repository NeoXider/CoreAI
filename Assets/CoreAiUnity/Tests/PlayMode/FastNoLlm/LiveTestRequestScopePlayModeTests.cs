using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Pins, without a model backend, the machinery the live fixtures rely on to survive a framework timeout:
    /// the <see cref="LiveTestRequestScope"/> budget cap, cancel-and-drain, source disposal, and the
    /// <see cref="InFlightMeter"/> behind the parallel-agents concurrency check. Stub requests are held open on
    /// <see cref="TaskCompletionSource{TResult}"/> gates so every overlap is deterministic.
    /// </summary>
    public sealed class LiveTestRequestScopePlayModeTests
    {
        private const int RoomyTimeoutMs = 60000;
        private const float StubWaitSeconds = 5f;

        private readonly List<TaskCompletionSource<bool>> _gates = new();

        [TearDown]
        public void ReleaseGates()
        {
            // WHY: a failed assertion must not leave a stub request parked on its gate for the rest of the run.
            foreach (TaskCompletionSource<bool> gate in _gates)
            {
                gate.TrySetResult(true);
            }

            _gates.Clear();
        }

        [Test]
        public void Cap_NeverExceedsSecondsLeft_AndIsNeverNegative()
        {
            LiveTestRequestScope roomy = new(RoomyTimeoutMs);
            float left = roomy.SecondsLeft;
            Assert.That(left, Is.GreaterThan(0f).And.LessThanOrEqualTo(
                RoomyTimeoutMs / 1000f - LiveTestRequestScope.ReserveSeconds));
            Assert.AreEqual(1f, roomy.Cap(1f), "A wait shorter than the budget is left unchanged.");
            float capped = roomy.Cap(100000f);
            Assert.That(capped, Is.GreaterThan(0f).And.LessThanOrEqualTo(left),
                "A wait longer than the budget is clamped to what is left.");

            // WHY: a framework timeout shorter than the reserve leaves no budget at all, the state a slow model
            // load produces; every wait must then collapse to zero rather than go negative.
            LiveTestRequestScope spent = new(1000);
            Assert.AreEqual(0f, spent.SecondsLeft);
            Assert.AreEqual(0f, spent.Cap(30f));
            Assert.AreEqual(0f, spent.Cap(-5f), "A negative wait is clamped to zero.");
            Assert.AreEqual(0f, roomy.Cap(-5f), "A negative wait is clamped to zero even with budget left.");
        }

        [UnityTest]
        public IEnumerator CancelAllAndDrain_CancelsTheSource_AndTheTrackedRequestEnds()
        {
            LiveTestRequestScope scope = new(RoomyTimeoutMs);
            CancellationTokenSource source = scope.CreateCancellation();
            CancellationToken token = source.Token;
            Task request = scope.Track(HonourCancellationAsync(token, 0));
            yield return null;
            Assert.IsFalse(request.IsCompleted, "The stub request waits for cancellation.");

            yield return scope.CancelAllAndDrain();

            Assert.IsTrue(token.IsCancellationRequested, "The drain must cancel every source the scope created.");
            Assert.IsTrue(request.IsCompleted, "The drain must return only after the tracked request ended.");
            Assert.IsFalse(request.IsFaulted, request.Exception?.GetBaseException().Message);
        }

        [UnityTest]
        public IEnumerator CancelAllAndDrain_ReturnsWhenTrackedRequestsFinish_WithoutWaitingTheFullBound()
        {
            const int unwindMilliseconds = 200;
            LiveTestRequestScope scope = new(RoomyTimeoutMs);
            CancellationTokenSource source = scope.CreateCancellation();
            Task request = scope.Track(HonourCancellationAsync(source.Token, unwindMilliseconds));
            yield return null;

            float started = Time.realtimeSinceStartup;
            yield return scope.CancelAllAndDrain();
            float elapsed = Time.realtimeSinceStartup - started;

            Assert.IsTrue(request.IsCompleted);
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(unwindMilliseconds / 1000f * 0.75f),
                "The drain must wait for a request that unwinds on later frames.");
            Assert.That(elapsed, Is.LessThan(LiveTestRequestScope.DrainSeconds - 2f),
                "The drain must return as soon as the tracked requests finished, not after its full bound.");
        }

        [UnityTest]
        public IEnumerator CancelAllAndDrain_RequestIgnoringCancellation_StopsAtMaxSecondsAndOnlyWarns()
        {
            const float maxSeconds = 0.3f;
            LiveTestRequestScope scope = new(RoomyTimeoutMs);
            scope.CreateCancellation();
            TaskCompletionSource<bool> stuck = OpenGate();
            scope.Track(stuck.Task);
            LogAssert.Expect(LogType.Warning, new Regex(@"\[LiveTestRequestScope\].*still running after"));

            float started = Time.realtimeSinceStartup;
            yield return scope.CancelAllAndDrain(maxSeconds);
            float elapsed = Time.realtimeSinceStartup - started;

            Assert.IsFalse(stuck.Task.IsCompleted, "The stub ignores cancellation, so it must still be running.");
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(maxSeconds).And.LessThan(maxSeconds + 2f),
                "The drain must give up at maxSeconds instead of hanging the teardown.");
        }

        [UnityTest]
        public IEnumerator CancelAllAndDrain_DisposesTheSources()
        {
            LiveTestRequestScope scope = new(RoomyTimeoutMs);
            CancellationTokenSource source = scope.CreateCancellation();
            CancellationToken token = source.Token;

            yield return scope.CancelAllAndDrain();

            Assert.Throws<ObjectDisposedException>(() => { _ = token.WaitHandle; },
                "A disposed source throws when its wait handle is requested.");
            Assert.Throws<ObjectDisposedException>(() => source.CancelAfter(1),
                "A disposed source rejects a new cancellation timer.");
        }

        [UnityTest]
        public IEnumerator CancelAll_Twice_IsSafe_AndLeavesTheDrainSafe()
        {
            LiveTestRequestScope scope = new(RoomyTimeoutMs);
            CancellationTokenSource source = scope.CreateCancellation();
            CancellationToken token = source.Token;
            Task request = scope.Track(HonourCancellationAsync(token, 0));

            Assert.DoesNotThrow(scope.CancelAll);
            Assert.DoesNotThrow(scope.CancelAll);
            Assert.IsTrue(token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => { _ = token.WaitHandle; },
                "CancelAll disposes the sources it cancelled.");

            yield return scope.CancelAllAndDrain();
            Assert.IsTrue(request.IsCompleted);
        }

        [UnityTest]
        public IEnumerator InFlightMeter_TwoConcurrentCompletions_PeakAtTwo()
        {
            InFlightMeter meter = new();
            TaskCompletionSource<bool> gate = OpenGate();
            InFlightCountingLlmClient first = new(new GatedLlmClient(gate.Task), meter);
            InFlightCountingLlmClient second = new(new GatedLlmClient(gate.Task), meter);

            Task<LlmCompletionResult> a = first.CompleteAsync(Request());
            Task<LlmCompletionResult> b = second.CompleteAsync(Request());
            Assert.AreEqual(2, meter.Current, "Both calls are inside the client boundary while the gate is shut.");

            gate.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(Task.WhenAll(a, b), StubWaitSeconds, "gated completions");

            Assert.AreEqual(2, meter.Peak);
            Assert.AreEqual(0, meter.Current, "Each call leaves the meter when its completion returns.");
        }

        [UnityTest]
        public IEnumerator InFlightMeter_SequentialCompletions_PeakAtOne()
        {
            InFlightMeter meter = new();
            TaskCompletionSource<bool> firstGate = OpenGate();
            TaskCompletionSource<bool> secondGate = OpenGate();
            InFlightCountingLlmClient first = new(new GatedLlmClient(firstGate.Task), meter);
            InFlightCountingLlmClient second = new(new GatedLlmClient(secondGate.Task), meter);

            Task<LlmCompletionResult> a = first.CompleteAsync(Request());
            Assert.AreEqual(1, meter.Current);
            firstGate.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(a, StubWaitSeconds, "first completion");
            Assert.AreEqual(0, meter.Current);

            Task<LlmCompletionResult> b = second.CompleteAsync(Request());
            Assert.AreEqual(1, meter.Current);
            secondGate.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(b, StubWaitSeconds, "second completion");

            Assert.AreEqual(1, meter.Peak);
            Assert.AreEqual(0, meter.Current);
        }

        [UnityTest]
        public IEnumerator InFlightMeter_TwoConcurrentStreams_PeakAtTwo_CountedFromFirstMoveNext()
        {
            InFlightMeter meter = new();
            TaskCompletionSource<bool> gate = OpenGate();
            InFlightCountingLlmClient first = new(new GatedLlmClient(gate.Task), meter);
            InFlightCountingLlmClient second = new(new GatedLlmClient(gate.Task), meter);

            IAsyncEnumerator<LlmStreamChunk> a = first.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
            IAsyncEnumerator<LlmStreamChunk> b = second.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
            Assert.AreEqual(0, meter.Current, "An async iterator runs no code before its first MoveNextAsync.");

            Task<bool> firstChunkA = a.MoveNextAsync().AsTask();
            Task<bool> firstChunkB = b.MoveNextAsync().AsTask();
            Assert.AreEqual(2, meter.Current, "Both streams entered the client boundary on their first MoveNextAsync.");

            gate.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(Task.WhenAll(firstChunkA, firstChunkB), StubWaitSeconds,
                "first stream chunks");
            Assert.IsTrue(firstChunkA.Result && firstChunkB.Result, "Each stub stream yields one chunk.");
            Assert.AreEqual(2, meter.Current, "A stream stays in flight until the inner stream ends.");

            Task<bool> endA = a.MoveNextAsync().AsTask();
            Task<bool> endB = b.MoveNextAsync().AsTask();
            yield return PlayModeTestAwait.WaitTask(Task.WhenAll(endA, endB), StubWaitSeconds, "stream ends");
            Assert.IsFalse(endA.Result || endB.Result);
            yield return PlayModeTestAwait.WaitTask(
                Task.WhenAll(a.DisposeAsync().AsTask(), b.DisposeAsync().AsTask()), StubWaitSeconds, "stream disposal");

            Assert.AreEqual(2, meter.Peak);
            Assert.AreEqual(0, meter.Current);
        }

        [UnityTest]
        public IEnumerator InFlightMeter_SequentialStreams_PeakAtOne_AndDisposeLeavesTheMeter()
        {
            InFlightMeter meter = new();
            TaskCompletionSource<bool> gate = OpenGate();
            gate.SetResult(true);
            InFlightCountingLlmClient client = new(new GatedLlmClient(gate.Task), meter);

            IAsyncEnumerator<LlmStreamChunk> a = client.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
            Task<bool> firstChunk = a.MoveNextAsync().AsTask();
            yield return PlayModeTestAwait.WaitTask(firstChunk, StubWaitSeconds, "first stream chunk");
            Assert.IsTrue(firstChunk.Result);
            Assert.AreEqual(1, meter.Current);

            // WHY: an abandoned stream is disposed without being read to the end; the meter must still see it leave.
            yield return PlayModeTestAwait.WaitTask(a.DisposeAsync().AsTask(), StubWaitSeconds, "abandoned stream");
            Assert.AreEqual(0, meter.Current);

            IAsyncEnumerator<LlmStreamChunk> b = client.CompleteStreamingAsync(Request()).GetAsyncEnumerator();
            Task<bool> secondChunk = b.MoveNextAsync().AsTask();
            yield return PlayModeTestAwait.WaitTask(secondChunk, StubWaitSeconds, "second stream chunk");
            Task<bool> secondEnd = b.MoveNextAsync().AsTask();
            yield return PlayModeTestAwait.WaitTask(secondEnd, StubWaitSeconds, "second stream end");
            Assert.IsFalse(secondEnd.Result);
            yield return PlayModeTestAwait.WaitTask(b.DisposeAsync().AsTask(), StubWaitSeconds, "second stream disposal");

            Assert.AreEqual(1, meter.Peak);
            Assert.AreEqual(0, meter.Current);
        }

        private TaskCompletionSource<bool> OpenGate()
        {
            TaskCompletionSource<bool> gate = new();
            _gates.Add(gate);
            return gate;
        }

        private static LlmCompletionRequest Request()
        {
            return new LlmCompletionRequest
            {
                AgentRoleId = "InFlightProbe",
                SystemPrompt = "stub",
                UserPayload = "probe"
            };
        }

        /// <summary>Stub request that waits for cancellation, then takes <paramref name="unwindMilliseconds"/> to end.</summary>
        private static async Task HonourCancellationAsync(CancellationToken token, int unwindMilliseconds)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            if (unwindMilliseconds > 0)
            {
                await Task.Delay(unwindMilliseconds);
            }
        }

        /// <summary>Stub client whose completion and stream both wait on one gate before answering.</summary>
        private sealed class GatedLlmClient : ILlmClient
        {
            private readonly Task _gate;

            public GatedLlmClient(Task gate)
            {
                _gate = gate;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                await _gate;
                return new LlmCompletionResult { Ok = true, Content = "done" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await _gate;
                yield return new LlmStreamChunk { Text = "done", IsDone = true };
            }
        }
    }
}
