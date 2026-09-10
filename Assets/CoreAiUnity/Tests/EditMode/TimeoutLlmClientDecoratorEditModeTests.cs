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
    /// EditMode coverage for <see cref="TimeoutLlmClientDecorator"/>: a library timeout surfaces as
    /// <see cref="LlmOperationTimeoutException"/> (non-streaming) / a terminal <see cref="LlmErrorCode.Timeout"/>
    /// chunk (streaming), a genuine caller cancellation propagates as a plain
    /// <see cref="OperationCanceledException"/>, and a non-positive timeout disables the bound entirely.
    /// </summary>
    public sealed class TimeoutLlmClientDecoratorEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: it asserts through Assert.ThrowsAsync/CatchAsync, which BLOCK
        /// the calling thread until the awaited delegate finishes — being inside an async test does
        /// not change that. Under Unity's SynchronizationContext the delegate's continuation is
        /// posted back to that same blocked thread, and the editor deadlocks with no results file.
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

        /// <summary>
        /// Observes an ALREADY-COMPLETED task and returns the exception it carries.
        /// </summary>
        /// <remarks>
        /// WHY not Assert.ThrowsAsync: that helper runs the await inside an async lambda, and the compiler
        /// puts an async method's own task into the Canceled state whenever its body throws an
        /// OperationCanceledException-derived exception — which LlmOperationTimeoutException is. The wrapper
        /// therefore reports TaskCanceledException no matter what the subject produced, measuring the
        /// wrapper instead of the decorator. Reading the finished task directly is what a real caller that
        /// re-observes a completed request does, and it is the shape this file's Faulted-not-Canceled
        /// design exists to serve. Only ever called on a task already proven complete, so it cannot block.
        /// </remarks>
        private static TException ObserveCompleted<TException>(Task task)
            where TException : Exception
        {
            Assert.IsTrue(task.IsCompleted, "ObserveCompleted must only read a finished task.");
            return Assert.Throws<TException>(() => task.GetAwaiter().GetResult());
        }

        [Test]
        public async Task NonCooperativeCompletion_DeadlineReturnsBeforeInnerFinishes()
        {
            IgnoringCancellationClient inner = new();
            PendingDelayMarshaler clock = new();
            TimeoutLlmClientDecorator client = new(inner, () => 60f, clock);
            Task<LlmCompletionResult> call = client.CompleteAsync(Req());
            clock.ReleaseAll();
            try
            {
                Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)),
                    "Deadline completion must not depend on a backend observing its cancellation token.");
                ObserveCompleted<LlmOperationTimeoutException>(call);
                Assert.IsFalse(inner.Completion.Task.IsCompleted);
            }
            finally
            {
                inner.Completion.TrySetException(new InvalidOperationException("late backend failure"));
            }
        }

        [Test]
        public async Task NonCooperativeStream_DeadlineReturnsAndDefersDisposalUntilMoveCompletes()
        {
            IgnoringCancellationClient inner = new() { FailDisposal = true };
            PendingDelayMarshaler clock = new();
            TimeoutLlmClientDecorator client = new(inner, () => 60f, clock);
            Task<List<LlmStreamChunk>> call = Drain(client.CompleteStreamingAsync(Req()));
            clock.ReleaseAll();
            try
            {
                Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)));
                List<LlmStreamChunk> chunks = await call;
                Assert.AreEqual(1, chunks.Count);
                Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
                Assert.AreEqual(0, inner.DisposeCount, "An active MoveNext must not overlap DisposeAsync.");
            }
            finally
            {
                inner.Move.TrySetException(new InvalidOperationException("late stream failure"));
            }
            Assert.AreSame(inner.Disposed.Task, await Task.WhenAny(inner.Disposed.Task, Task.Delay(3000)));
            Assert.AreEqual(1, inner.DisposeCount);
            Assert.AreEqual(0, inner.OverlappingDisposals);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NonCooperativeInner_CallerCancellationReturnsWithoutBecomingTimeout(bool streaming)
        {
            IgnoringCancellationClient inner = new();
            PendingDelayMarshaler clock = new();
            TimeoutLlmClientDecorator client = new(inner, () => 60f, clock);
            using CancellationTokenSource caller = new();
            Task call = streaming ? Drain(client.CompleteStreamingAsync(Req(), caller.Token))
                : client.CompleteAsync(Req(), caller.Token);
            caller.Cancel();
            try
            {
                Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)));
                try
                {
                    await call;
                    Assert.Fail("Caller cancellation must propagate.");
                }
                catch (OperationCanceledException error)
                {
                    Assert.IsNotInstanceOf<LlmOperationTimeoutException>(error);
                }
            }
            finally
            {
                inner.Completion.TrySetResult(new LlmCompletionResult { Ok = true });
                inner.Move.TrySetResult(false);
            }
        }

        private sealed class IgnoringCancellationClient : ILlmClient, IAsyncEnumerable<LlmStreamChunk>, IAsyncEnumerator<LlmStreamChunk>
        {
            public readonly TaskCompletionSource<LlmCompletionResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Move = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _moving;
            public int DisposeCount;
            public int OverlappingDisposals;
            public bool FailDisposal;
            public LlmStreamChunk Current => new() { Text = "late" };
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) => Completion.Task;
            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) => this;
            public IAsyncEnumerator<LlmStreamChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() => new(MoveAsync());
            private async Task<bool> MoveAsync()
            {
                _moving = true;
                try { return await Move.Task; }
                finally { _moving = false; }
            }
            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                if (_moving) { OverlappingDisposals++; }
                Disposed.TrySetResult(true);
                return FailDisposal
                    ? new ValueTask(Task.FromException(new InvalidOperationException("late cleanup failure")))
                    : default;
            }
        }

        [Test]
        public async Task CompleteAsync_LibraryTimeout_ThrowsLlmOperationTimeoutException()
        {
            SlowClient inner = new() { DelayMs = 2000 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.05f);

            bool timedOut = false;
            try
            {
                await sut.CompleteAsync(Req());
            }
            catch (LlmOperationTimeoutException)
            {
                timedOut = true;
            }

            Assert.IsTrue(timedOut, "A slow inner call must time out as LlmOperationTimeoutException.");
        }

        /// <summary>
        /// Pins the class's own documented reason for using a <see cref="TaskCompletionSource{T}"/> plus
        /// <c>TrySetException</c> instead of letting the async method's Task go Canceled: re-observing an
        /// already-completed task (as <c>Assert.ThrowsAsync</c> does after racing it, and as a caller that
        /// checks a cached task twice would) must still surface <see cref="LlmOperationTimeoutException"/>
        /// and a Faulted status, never degrade to a bare <see cref="TaskCanceledException"/>.
        /// </summary>
        [Test]
        public async Task CompleteAsync_LibraryTimeout_SurvivesSecondObservation_AsFaultedException()
        {
            SlowClient inner = new() { DelayMs = 2000 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.05f);

            Task<LlmCompletionResult> call = sut.CompleteAsync(Req());
            Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)));
            Assert.AreEqual(TaskStatus.Faulted, call.Status,
                "A library timeout must fault the task, not cancel it, so the exact exception replays.");

            LlmOperationTimeoutException first = ObserveCompleted<LlmOperationTimeoutException>(call);
            LlmOperationTimeoutException second = ObserveCompleted<LlmOperationTimeoutException>(call);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
        }

        /// <summary>
        /// Regression for the nested-decorator classification bug: <see cref="LlmOperationTimeoutException"/>
        /// derives from <see cref="OperationCanceledException"/>, so when an inner decorator's own (shorter)
        /// deadline fires first, the outer decorator must recognise the exception it receives as already
        /// being that type — neither its own deadline nor the caller's token fired — instead of folding it
        /// into the generic "inner cancelled for its own reasons" branch and discarding it via TrySetCanceled.
        /// </summary>
        [Test]
        public async Task CompleteAsync_NestedTimeouts_InnerDeadlineFires_OuterSurfacesLlmOperationTimeoutException()
        {
            SlowClient innermost = new() { DelayMs = 2000 };
            TimeoutLlmClientDecorator innerDecorator = new(innermost, () => 0.05f);
            TimeoutLlmClientDecorator outerDecorator = new(innerDecorator, () => 60f);

            Task<LlmCompletionResult> call = outerDecorator.CompleteAsync(Req());
            Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)),
                "The outer decorator must complete once the inner deadline fires.");
            LlmOperationTimeoutException error = ObserveCompleted<LlmOperationTimeoutException>(call);
            Assert.IsNotNull(error);
        }

        /// <summary>
        /// Companion to the regression above: nesting must not make the outer decorator over-eager about
        /// classifying things as timeouts. A genuine caller cancellation of the OUTER call must still win
        /// over the new "inner timeout" branch — it is checked first in
        /// <c>TimeoutLlmClientDecorator.RunCompleteWithDeadlineAsync</c> for exactly this reason.
        /// </summary>
        [Test]
        public async Task CompleteAsync_NestedTimeouts_CallerCancelsOuter_StaysPlainCancellation_NotTimeout()
        {
            SlowClient innermost = new() { DelayMs = 5000 };
            TimeoutLlmClientDecorator innerDecorator = new(innermost, () => 60f);
            TimeoutLlmClientDecorator outerDecorator = new(innerDecorator, () => 60f);
            using CancellationTokenSource caller = new();
            caller.CancelAfter(30);

            bool caughtPlainCancel = false;
            try
            {
                await outerDecorator.CompleteAsync(Req(), caller.Token);
                Assert.Fail("The caller's own cancellation must propagate.");
            }
            catch (LlmOperationTimeoutException)
            {
                Assert.Fail("A genuine caller cancellation must never be reported as a library timeout, "
                    + "nested decorators included.");
            }
            catch (OperationCanceledException)
            {
                caughtPlainCancel = true;
            }

            Assert.IsTrue(caughtPlainCancel);
        }

        [Test]
        public async Task CompleteAsync_Deadline_UsesInjectedHostDelay()
        {
            SlowClient inner = new() { DelayMs = 2000 };
            RecordingDelayMarshaler marshaler = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 60f, marshaler);

            bool timedOut = false;
            try
            {
                await sut.CompleteAsync(Req());
            }
            catch (LlmOperationTimeoutException)
            {
                timedOut = true;
            }

            Assert.IsTrue(timedOut);
            Assert.AreEqual(1, marshaler.DelayCallCount);
            Assert.AreEqual(60000, marshaler.LastDelayMilliseconds);
        }

        [Test]
        public async Task CompleteAsync_CallerCancellation_PropagatesAsOperationCanceled_NotTimeout()
        {
            SlowClient inner = new() { DelayMs = 2000 };
            TimeoutLlmClientDecorator sut = new(inner, () => 60f);
            using CancellationTokenSource cts = new();
            cts.CancelAfter(30);

            bool caughtPlainCancel = false;
            try
            {
                await sut.CompleteAsync(Req(), cts.Token);
            }
            catch (LlmOperationTimeoutException)
            {
                Assert.Fail("A caller cancellation must not be reported as a library timeout.");
            }
            catch (OperationCanceledException)
            {
                caughtPlainCancel = true;
            }

            Assert.IsTrue(caughtPlainCancel);
        }

        /// <summary>
        /// Neither the caller's token nor the decorator's own deadline fired — the inner client cancelled
        /// on its own. This must still classify as a plain cancellation (TrySetCanceled), never as a fault
        /// via the generic `catch (Exception)` branch, and never as <see cref="LlmOperationTimeoutException"/>.
        /// </summary>
        [Test]
        public async Task CompleteAsync_InnerCancelsForOwnReasons_SurfacesAsCancellation_NotFault()
        {
            SelfCancellingClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 60f);

            bool caughtPlainCancel = false;
            try
            {
                await sut.CompleteAsync(Req());
                Assert.Fail("The inner client's own cancellation must propagate.");
            }
            catch (LlmOperationTimeoutException)
            {
                Assert.Fail("An inner-originated cancellation must not be reported as a library timeout.");
            }
            catch (OperationCanceledException)
            {
                caughtPlainCancel = true;
            }

            Assert.IsTrue(caughtPlainCancel);
        }

        [Test]
        public async Task CompleteAsync_TimeoutDisabled_DelegatesDirectly()
        {
            SlowClient inner = new() { DelayMs = 0, Result = new LlmCompletionResult { Ok = true, Content = "fast" } };
            TimeoutLlmClientDecorator sut = new(inner, () => 0f);

            LlmCompletionResult result = await sut.CompleteAsync(Req());

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("fast", result.Content);
        }

        [Test]
        public async Task CompleteAsync_InnerCancelledResultFromDecoratorToken_RewritesOnlyErrorCode()
        {
            CancelledResultClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 0.03f);

            LlmCompletionResult result = await sut.CompleteAsync(Req());

            Assert.AreEqual(LlmErrorCode.Timeout, result.ErrorCode);
            Assert.AreEqual("inner cancelled", result.Error);
            Assert.AreEqual("partial", result.Content);
            Assert.AreEqual("test-model", result.Model);
            Assert.AreEqual(1, result.ExecutedToolCalls.Count);
        }

        /// <summary>
        /// The grace window (see AwaitOperationAsync) gives a cooperative inner operation real time to
        /// finish its own unwind AFTER the deadline fires, before its result is discarded as a bare
        /// timeout. An inner client that reacts to cancellation and returns a genuine result well inside
        /// that window must have that result delivered, not overwritten by a timeout.
        /// </summary>
        [Test]
        public async Task CompleteAsync_CooperativeInnerUnwindsWithinGraceWindow_StillDeliversResult()
        {
            CooperativeRecoveryClient inner = new() { DelayAfterCancelMs = 5 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.03f);

            LlmCompletionResult result = await sut.CompleteAsync(Req());

            Assert.IsTrue(result.Ok, "A cooperative result produced within the grace window must survive.");
            Assert.AreEqual("recovered-after-deadline", result.Content);
        }

        [Test]
        public async Task Streaming_LibraryTimeout_YieldsTerminalTimeoutChunk()
        {
            SlowClient inner = new() { DelayMs = 2000 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.05f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
        }

        [Test]
        public async Task Streaming_FastInner_PassesThrough()
        {
            SlowClient inner = new() { DelayMs = 0, StreamText = "streamed" };
            TimeoutLlmClientDecorator sut = new(inner, () => 5f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            System.Text.StringBuilder sb = new();
            foreach (LlmStreamChunk c in chunks)
            {
                sb.Append(c.Text);
            }

            Assert.AreEqual("streamed", sb.ToString());
            Assert.IsFalse(chunks.Exists(c => c.ErrorCode == LlmErrorCode.Timeout));
        }

        /// <summary>
        /// The decorator rewrites BOTH the code and the text. Rewriting only the code left the message
        /// reading "cancelled", which the UI presents as if the user had pressed Stop — the payload of the
        /// stream (text, model, executed tool calls) is still preserved untouched.
        /// </summary>
        [Test]
        public async Task Streaming_InnerCancelledTerminalFromDecoratorToken_RewritesCodeAndMessage()
        {
            CancelledResultClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 0.03f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
            Assert.AreEqual("LLM request timed out.", chunks[0].Error);
            Assert.AreEqual("partial", chunks[0].Text);
            Assert.AreEqual("test-model", chunks[0].Model);
            Assert.AreEqual(1, chunks[0].ExecutedToolCalls.Count);
        }

        /// <summary>
        /// Extending the deadline on every chunk used to create a new cancellation source, cancel the previous one
        /// and start a new wait, costing an exception and several allocations on EVERY token. Now a stream has one
        /// watchdog wait: the host receives exactly one delay request per window, no matter how many chunks
        /// arrive.
        /// </summary>
        [Test]
        public async Task Streaming_ManyChunks_DoNotRearmTheHostDelayPerChunk()
        {
            const int chunkCount = 500;
            ChattyClient inner = new() { ChunkCount = chunkCount };
            PendingDelayMarshaler marshaler = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 30f, marshaler);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(chunkCount + 1, chunks.Count);
            Assert.AreEqual(1, marshaler.DelayCallCount,
                "Every chunk is a progress mark, not a new timer: the host is asked to wait once per window.");
            Assert.LessOrEqual(marshaler.CancelledDelays, 1,
                "One timer cancellation when the request completes is allowed; there must be no separate cancellation per chunk.");
            marshaler.ReleaseAll();
        }

        /// <summary>
        /// A timer failure is not a timer expiry: if the host delay faults, the request is NOT cancelled and is not
        /// reported as a timeout; the failure is handed to the error handler instead.
        /// </summary>
        [Test]
        public async Task Streaming_HostDelayFaults_RequestIsNotReportedAsTimeout()
        {
            SlowClient inner = new() { DelayMs = 100, StreamText = "answer" };
            FaultingDelayMarshaler marshaler = new();
            Exception reported = null;
            TimeoutLlmClientDecorator sut = new(inner, () => 30f, marshaler, ex => reported = ex);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.IsFalse(chunks.Exists(c => c.ErrorCode == LlmErrorCode.Timeout),
                "A faulted timer used to be read as an expired one, and a healthy request was cancelled with code Timeout.");
            Assert.IsTrue(chunks.Exists(c => c.Text == "answer"));
            Assert.IsInstanceOf<InvalidOperationException>(reported, "A timer failure must be visible to the host.");
        }

        [Test]
        public async Task CompleteAsync_HostDelayFaults_ResultIsDelivered_NotTimeout()
        {
            SlowClient inner = new() { DelayMs = 100, Result = new LlmCompletionResult { Ok = true, Content = "late" } };
            FaultingDelayMarshaler marshaler = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 30f, marshaler);

            LlmCompletionResult result = await sut.CompleteAsync(Req());

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("late", result.Content);
        }

        /// <summary>
        /// IdleDeadline's constructor kicks off its watch (`_ = WatchAsync()`) synchronously, and that
        /// watch calls straight into the injected marshaler. A marshaler whose <c>DelayAsync</c> throws
        /// SYNCHRONOUSLY (as opposed to returning a faulted <see cref="Task"/>, already covered by
        /// <see cref="FaultingDelayMarshaler"/>) must not escape resource construction/setup and leave the
        /// TaskCompletionSource in RunCompleteWithDeadlineAsync uncompleted — the request must still
        /// resolve with the inner client's real result.
        /// </summary>
        [Test]
        public async Task CompleteAsync_DelayRegistrationThrowsSynchronously_StillCompletesReturnedTask()
        {
            SlowClient inner = new() { DelayMs = 0, Result = new LlmCompletionResult { Ok = true, Content = "fast" } };
            SynchronouslyThrowingDelayMarshaler marshaler = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 60f, marshaler);

            Task<LlmCompletionResult> call = sut.CompleteAsync(Req());
            Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)),
                "A marshaler that throws synchronously while registering its delay must not hang the caller.");
            LlmCompletionResult result = await call;

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("fast", result.Content);
        }

        /// <summary>
        /// Finding B: if disposing deadline/signal/timeoutCts throws (e.g. a registered host-delay
        /// callback throwing out of <c>CancellationTokenSource.Cancel()</c> as an AggregateException), the
        /// TaskCompletionSource must still be completed. Before the fix the exception escaped the detached
        /// `RunCompleteWithDeadlineAsync` call as an unobserved task exception and the caller's `tcs.Task`
        /// hung forever.
        /// </summary>
        [Test]
        public async Task CompleteAsync_DisposalThrows_StillCompletesReturnedTask()
        {
            SlowClient inner = new() { DelayMs = 0, Result = new LlmCompletionResult { Ok = true, Content = "fast" } };
            ThrowingDisposalDelayMarshaler marshaler = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 60f, marshaler);

            Task<LlmCompletionResult> call = sut.CompleteAsync(Req());
            Assert.AreSame(call, await Task.WhenAny(call, Task.Delay(3000)),
                "A throw while disposing deadline/signal/timeoutCts must not leave the returned task incomplete.");
            Assert.CatchAsync<Exception>(async () => await call,
                "Disposal failure must surface to the caller, not be silently swallowed.");
        }

        [Test]
        public async Task Streaming_ProgressWithinWindow_KeepsStreamAlive_ThenStallTimesOut()
        {
            // Three chunks 60 ms apart with a 150 ms window: no single gap exceeds the window, so the stream stays alive;
            // then a 400 ms stall - timeout.
            StallingAfterChunksClient inner = new() { ChunkGapMs = 60, StallMs = 400 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.15f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(3, chunks.FindAll(c => c.Text == "t").Count, "All three chunks before the stall must arrive.");
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[chunks.Count - 1].ErrorCode, "A stall longer than the window is a timeout.");
        }

        // ---- helpers ----

        [Test]
        public async Task Streaming_Rewrite_DoesNotMutateInnerChunk()
        {
            CachingCancelledClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 0.03f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.AreNotSame(inner.CachedChunk, chunks[0]);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
            Assert.AreEqual("LLM request timed out.", chunks[0].Error);
            Assert.AreEqual(LlmErrorCode.Cancelled, inner.CachedChunk.ErrorCode);
            Assert.AreEqual("inner cancelled", inner.CachedChunk.Error);
        }

        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
        }

        private static async Task InlineCancellationAsync(CancellationToken token)
        {
            // Model a response produced synchronously by cancellation. This checks metadata that is
            // available at the deadline; a later response cannot hold a hard deadline open indefinitely.
            TaskCompletionSource<bool> stopped = new();
            using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetCanceled(token));
            await stopped.Task.ConfigureAwait(false);
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

        private sealed class RecordingDelayMarshaler : ILlmAsyncMarshaler
        {
            public int DelayCallCount;
            public int LastDelayMilliseconds;

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DelayCallCount++;
                LastDelayMilliseconds = milliseconds;
                return Task.CompletedTask;
            }
        }

        /// <summary>A host delay that never expires on its own, but counts requests and cancellations.</summary>
        private sealed class PendingDelayMarshaler : ILlmAsyncMarshaler
        {
            private readonly List<TaskCompletionSource<bool>> _pending = new();
            public int DelayCallCount;
            public int CancelledDelays;

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                DelayCallCount++;

                // WHY CancellationToken.None short-circuits: TimeoutLlmClientDecorator.AwaitOperationAsync
                // requests the GRACE-window delay on CancellationToken.None (see its own comment — a
                // delay on None is the grace window, not the deadline). That call happens only AFTER the
                // deadline delay above has already been released, i.e. after any ReleaseAll() a test made
                // has already run and returned — a delay registered afterward would sit in `_pending`
                // forever, waiting for a second release that never comes. The deadline delay itself never
                // uses None (IdleDeadline always passes its own real `_stopToken`), so this cannot mask a
                // deadline wait a test still means to hold open.
                if (cancellationToken == CancellationToken.None)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    CancelledDelays++;
                    tcs.TrySetCanceled(cancellationToken);
                });
                lock (_pending)
                {
                    _pending.Add(tcs);
                }

                return tcs.Task;
            }

            public void ReleaseAll()
            {
                lock (_pending)
                {
                    foreach (TaskCompletionSource<bool> tcs in _pending)
                    {
                        tcs.TrySetResult(true);
                    }
                }
            }
        }

        private sealed class FaultingDelayMarshaler : ILlmAsyncMarshaler
        {
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                return Task.FromException(new InvalidOperationException("player loop is not running"));
            }
        }

        /// <summary>Throws SYNCHRONOUSLY out of the call itself, unlike <see cref="FaultingDelayMarshaler"/>
        /// which returns an already-faulted <see cref="Task"/>.</summary>
        private sealed class SynchronouslyThrowingDelayMarshaler : ILlmAsyncMarshaler
        {
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("delay registration failed");
            }
        }

        /// <summary>
        /// Registers a cancellation callback that throws, so cancelling the token it was given — exactly
        /// what <see cref="TimeoutLlmClientDecorator"/>'s IdleDeadline.Dispose() does via `_stop.Cancel()`
        /// — surfaces an AggregateException out of that Cancel() call.
        /// </summary>
        private sealed class ThrowingDisposalDelayMarshaler : ILlmAsyncMarshaler
        {
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => throw new InvalidOperationException("callback boom"));
                return tcs.Task;
            }
        }

        private sealed class ChattyClient : ILlmClient
        {
            public int ChunkCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    await Task.Yield();
                    yield return new LlmStreamChunk { Text = "t" };
                }

                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class StallingAfterChunksClient : ILlmClient
        {
            public int ChunkGapMs;
            public int StallMs;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(ChunkGapMs, cancellationToken).ConfigureAwait(false);
                    yield return new LlmStreamChunk { Text = "t" };
                }

                await Task.Delay(StallMs, cancellationToken).ConfigureAwait(false);
                yield return new LlmStreamChunk { IsDone = true, Text = "never" };
            }
        }

        /// <summary>
        /// Observes cancellation of the token it was given, then finishes cooperatively a short real-time
        /// interval later with a genuine (non-cancelled) result — modelling a backend that reacts to the
        /// decorator's deadline instead of ignoring it, but needs a few milliseconds to unwind.
        /// </summary>
        private sealed class CooperativeRecoveryClient : ILlmClient
        {
            public int DelayAfterCancelMs;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<bool> cancelledSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => cancelledSignal.TrySetResult(true));
                await cancelledSignal.Task.ConfigureAwait(false);
                await Task.Delay(DelayAfterCancelMs).ConfigureAwait(false);
                return new LlmCompletionResult { Ok = true, Content = "recovered-after-deadline" };
            }
        }

        /// <summary>Cancels immediately with a token that is neither the caller's nor the decorator's.
        /// WHY not disposed: TrySetCanceled only needs the token's value, and keeping the source alive
        /// avoids any question of whether inspecting a disposed CancellationToken later is safe.</summary>
        private sealed class SelfCancellingClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CancellationTokenSource ownReasons = new();
                ownReasons.Cancel();
                throw new OperationCanceledException("inner client cancelled for its own reasons", null,
                    ownReasons.Token);
            }
        }

        private sealed class SlowClient : ILlmClient
        {
            public int DelayMs;
            public LlmCompletionResult Result = new() { Ok = true, Content = "ok" };
            public string StreamText = "ok";

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                if (DelayMs > 0)
                {
                    await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
                }

                return Result;
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                if (DelayMs > 0)
                {
                    await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
                }

                yield return new LlmStreamChunk { Text = StreamText };
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class CancelledResultClient : ILlmClient
        {
            private static readonly LlmToolCallTrace[] Traces =
            {
                new("mutator", true, 1d, "native", "done")
            };

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                try
                {
                    await InlineCancellationAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return new LlmCompletionResult
                {
                    Ok = false,
                    Content = "partial",
                    Error = "inner cancelled",
                    ErrorCode = LlmErrorCode.Cancelled,
                    Model = "test-model",
                    ExecutedToolCalls = Traces
                };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                try
                {
                    await InlineCancellationAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                yield return new LlmStreamChunk
                {
                    Text = "partial",
                    IsDone = true,
                    Error = "inner cancelled",
                    ErrorCode = LlmErrorCode.Cancelled,
                    Model = "test-model",
                    ExecutedToolCalls = Traces
                };
            }
        }

        private sealed class CachingCancelledClient : ILlmClient
        {
            public readonly LlmStreamChunk CachedChunk = new()
            {
                Text = "partial",
                IsDone = true,
                Error = "inner cancelled",
                ErrorCode = LlmErrorCode.Cancelled,
                Model = "test-model"
            };

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Streaming-only stub.");
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                try
                {
                    await InlineCancellationAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                yield return CachedChunk;
            }
        }
    }
}
#endif
