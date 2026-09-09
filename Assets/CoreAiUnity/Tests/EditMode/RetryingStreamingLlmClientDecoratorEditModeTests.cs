#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="RetryingStreamingLlmClientDecorator"/>: retry a stream that fails
    /// BEFORE committing content, never retry after real text/tool output, honour retry exhaustion, leave
    /// non-retryable errors and caller cancellation alone, and pass the non-streaming path straight through.
    /// </summary>
    public sealed class RetryingStreamingLlmClientDecoratorEditModeTests
    {
        [Test]
        public async Task PreCommitTransientError_RetriesAndSucceeds()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.BackendUnavailable) });
            inner.NextStreams.Enqueue(new[] { Text("hello"), Done() });

            RetryingStreamingLlmClientDecorator sut = new(inner, 2, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, sut.RetryCount, "One pre-commit failure must trigger exactly one retry.");
            Assert.AreEqual(2, inner.StreamCallCount, "The stream must be re-opened once.");
            Assert.AreEqual("hello", Concat(chunks));
            Assert.IsFalse(HasError(chunks), "The retried stream succeeded, so no error should surface.");
        }

        [Test]
        public async Task PreCommitRetryBackoff_UsesInjectedHostDelay()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.BackendUnavailable) });
            inner.NextStreams.Enqueue(new[] { Text("hello"), Done() });
            RecordingDelayMarshaler marshaler = new();
            RetryingStreamingLlmClientDecorator sut = new(
                inner, 1, _ => TimeSpan.FromSeconds(2), null, marshaler);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual("hello", Concat(chunks));
            Assert.AreEqual(1, marshaler.DelayCallCount);
            Assert.AreEqual(2000, marshaler.LastDelayMilliseconds);
        }

        [Test]
        public async Task PreCommitRetry_KeepsOneHeaderSnapshotAndLaterInvocationResamples()
        {
            SnapshotAwareStreamingClient inner = new();
            RetryingStreamingLlmClientDecorator sut = new(inner, 1, null);
            LlmCompletionRequest request = Req();

            List<LlmStreamChunk> first = await Drain(sut.CompleteStreamingAsync(request));

            Assert.AreEqual("ok", Concat(first));
            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a" }, inner.SeenLessons);
            Assert.AreEqual(1, inner.ScopeCount);

            inner.CurrentLesson = "lesson-c";
            List<LlmStreamChunk> later = await Drain(sut.CompleteStreamingAsync(request));

            Assert.AreEqual("ok", Concat(later));
            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a", "lesson-c" }, inner.SeenLessons);
            Assert.AreEqual(2, inner.ScopeCount);
        }

        [Test]
        public async Task EmptyStream_IsTreatedAsTransient_AndRetried()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(Array.Empty<LlmStreamChunk>());
            inner.NextStreams.Enqueue(new[] { Text("recovered"), Done() });

            RetryingStreamingLlmClientDecorator sut = new(inner, 1, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, sut.RetryCount);
            Assert.AreEqual("recovered", Concat(chunks));
        }

        [Test]
        public async Task CommittedContent_IsNeverRetried_EvenIfLaterError()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { Text("partial"), ErrChunk(LlmErrorCode.BackendUnavailable) });

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount, "A committed stream must never be retried.");
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual("partial", Concat(chunks));
            Assert.IsTrue(HasError(chunks), "The post-commit error must propagate unchanged.");
        }

        [Test]
        public async Task ErrorChunkAfterToolExecution_IsNeverRetried_EvenWithRetryableCode()
        {
            StubStreamingClient inner = new();
            LlmStreamChunk failedAfterTool = new()
            {
                IsDone = true,
                Error = "HTTP 503 after the tool already ran",
                ErrorCode = LlmErrorCode.BackendUnavailable,
                ExecutedToolCalls = new[] { new LlmToolCallTrace("spawn_quiz", true, 2d, "native") }
            };
            inner.NextStreams.Enqueue(new[] { failedAfterTool });
            inner.NextStreams.Enqueue(new[] { Text("second run"), Done() });

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount, "A tool already ran in this turn: re-opening the stream would run it twice.");
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual(1, chunks.Count);
            Assert.AreSame(failedAfterTool, chunks[0], "The post-tool failure must propagate unchanged.");
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
        }

        [Test]
        public async Task RetriesExhausted_SurfacesTerminalError()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.Timeout) });
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.Timeout) });
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.Timeout) });

            RetryingStreamingLlmClientDecorator sut = new(inner, 2, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(2, sut.RetryCount, "maxRetryAttempts=2 → three opens total, then give up.");
            Assert.AreEqual(3, inner.StreamCallCount);
            Assert.IsTrue(chunks.Count >= 1 && chunks[^1].IsDone && HasError(chunks));
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[^1].ErrorCode);
        }

        [Test]
        public async Task NonRetryableError_IsNotRetried()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.InvalidRequest) });

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount, "A caller-caused error must not be retried.");
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual(LlmErrorCode.InvalidRequest, chunks[^1].ErrorCode);
        }

        [Test]
        public async Task CallerCancellation_IsNotRetried_AndPropagates()
        {
            StubStreamingClient inner = new();
            using CancellationTokenSource cts = new();
            cts.Cancel();

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            bool cancelled = false;
            try
            {
                await Drain(sut.CompleteStreamingAsync(Req(), cts.Token));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled, "Caller cancellation must propagate as OperationCanceledException.");
            Assert.AreEqual(0, sut.RetryCount, "Cancellation is never a retry trigger.");
        }

        [Test]
        public void PreCancelledRequest_DoesNotOpenProvider()
        {
            StubStreamingClient inner = new();
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await Drain(sut.CompleteStreamingAsync(Req(), cancellation.Token)));

            Assert.AreEqual(0, inner.StreamCallCount);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [Test]
        public void CancellationDuringTransientFailure_DoesNotReopenProvider()
        {
            using CancellationTokenSource cancellation = new();
            StubStreamingClient inner = new();
            inner.BeforeYield = cancellation.Cancel;
            inner.NextStreams.Enqueue(new[] { ErrChunk(LlmErrorCode.BackendUnavailable) });
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await Drain(sut.CompleteStreamingAsync(Req(), cancellation.Token)));

            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [Test]
        public async Task SynchronousStreamOpenFailure_IsRetriedBeforeContent()
        {
            OpeningFailureClient inner = new();
            RetryingStreamingLlmClientDecorator sut = new(inner, 1);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual("ok", Concat(chunks));
            Assert.AreEqual(2, inner.OpenCount);
            Assert.AreEqual(1, sut.RetryCount);
        }

        [Test]
        public async Task SynchronousPermanentOpenFailure_PreservesClassificationWithoutRetry()
        {
            OpeningFailureClient inner = new()
            {
                OpeningException = new LlmClientException("Authentication expired.", LlmErrorCode.AuthExpired, 401)
            };
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.AuthExpired, chunks[0].ErrorCode);
            Assert.AreEqual(401, chunks[0].HttpStatus);
            Assert.AreEqual(1, inner.OpenCount);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [Test]
        public async Task NonStreamingPath_DelegatesWithoutRetry()
        {
            StubStreamingClient inner = new();
            inner.NextResult = new LlmCompletionResult { Ok = true, Content = "direct" };

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            LlmCompletionResult result = await sut.CompleteAsync(Req());

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("direct", result.Content);
            Assert.AreEqual(1, inner.CompleteCallCount);
        }

        [TestCase(LlmErrorCode.AuthExpired, 401)]
        [TestCase(LlmErrorCode.PaymentRequired, 402)]
        [TestCase(LlmErrorCode.InvalidRequest, 400)]
        public async Task DefaultStreamingAdapter_CodeOnlyPermanentFailure_IsNotRetried(LlmErrorCode code, int status)
        {
            CompletionOnlyFailureClient inner = new(code, status);
            RetryingStreamingLlmClientDecorator sut = new(inner, 2);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, inner.CompleteCallCount, "Optional error text must not override a permanent failure category.");
            Assert.AreEqual(0, sut.RetryCount);
            Assert.AreEqual(code, chunks[chunks.Count - 1].ErrorCode);
            Assert.AreEqual(status, chunks[chunks.Count - 1].HttpStatus);
        }

        /// <summary>
        /// A failing chunk is defined by "error text OR a non-None code", the same predicate the
        /// orchestrator uses to end a turn. Matching only on the text let a code-only failure ride
        /// through as a benign hint: the provider's category was dropped and the caller was told the
        /// answer was empty instead of rate-limited.
        /// </summary>
        [Test]
        public async Task MidStreamTransientCodeWithoutText_IsRetriedInsteadOfPassedOnAsAHint()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[]
            {
                new LlmStreamChunk { ErrorCode = LlmErrorCode.RateLimited }, Text("after the failure"), Done()
            });
            inner.NextStreams.Enqueue(new[] { Text("recovered"), Done() });
            RetryingStreamingLlmClientDecorator sut = new(inner, 1);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, sut.RetryCount, "A retryable code is a pre-commit failure even with no message.");
            Assert.AreEqual(2, inner.StreamCallCount);
            Assert.AreEqual("recovered", Concat(chunks), "Content produced after a failing chunk must not reach the caller.");
        }

        [Test]
        public async Task MidStreamPermanentCodeWithoutText_EndsTheStreamWithThatCategory()
        {
            StubStreamingClient inner = new();
            LlmStreamChunk refusal = new() { ErrorCode = LlmErrorCode.PaymentRequired, HttpStatus = 402 };
            inner.NextStreams.Enqueue(new[] { refusal, Text("after the refusal"), Done() });
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount, "A permanent refusal answers every replay identically.");
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual(1, chunks.Count, "The refusal is terminal: nothing after it belongs to this turn.");
            Assert.AreSame(refusal, chunks[0], "The provider's own classification must reach the caller.");
        }

        [Test]
        public async Task PreCommitChunkWithoutErrorOrCode_StaysABenignHintAndTheStreamContinues()
        {
            StubStreamingClient inner = new();
            LlmStreamChunk hint = new() { Model = "gpt-test" };
            inner.NextStreams.Enqueue(new[] { hint, Text("answer"), Done() });
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount);
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreSame(hint, chunks[0], "A chunk with no error text and no code carries metadata, not a failure.");
            Assert.AreEqual("answer", Concat(chunks));
        }

        [Test]
        public async Task NullChunkBeforeVisibleAnswer_DoesNotAbortOrReopenTheStream()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new[] { null, Text("answer"), Done() });
            RetryingStreamingLlmClientDecorator sut = new(inner, 2);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual("answer", Concat(chunks));
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [Test]
        public async Task NullOnlyStreams_UseTheBoundedEmptyResponseRetryPolicy()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(new LlmStreamChunk[] { null });
            inner.NextStreams.Enqueue(new LlmStreamChunk[] { null, null });
            RetryingStreamingLlmClientDecorator sut = new(inner, 1);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(2, inner.StreamCallCount, "Null entries are not committed output and do not bypass the retry limit.");
            Assert.AreEqual(1, sut.RetryCount);
            Assert.AreEqual(LlmErrorCode.EmptyResponse, chunks[chunks.Count - 1].ErrorCode);
        }

        [Test]
        public async Task CodeOnlyTransientFailure_AfterRetryBudgetPreservesMetadata()
        {
            StubStreamingClient inner = new();
            LlmStreamChunk terminal = new()
            {
                IsDone = true, ErrorCode = LlmErrorCode.BackendUnavailable, HttpStatus = 503, RetryAfterSeconds = 7
            };
            inner.NextStreams.Enqueue(new[] { terminal });
            inner.NextStreams.Enqueue(new[] { terminal });
            RetryingStreamingLlmClientDecorator sut = new(inner, 1);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreSame(terminal, chunks[chunks.Count - 1], "Retry exhaustion must preserve the provider's original code and metadata.");
            Assert.AreEqual(2, inner.StreamCallCount);
            Assert.AreEqual(1, sut.RetryCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationAndDisposalFailure_PreservesCancellationAndDisposesOnce(bool committed)
        {
            OperationCanceledException primary = new("Caller stopped the request.");
            DisposalFailureClient inner = new(committed, primary);
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);
            Exception observed = null;
            try { await Drain(sut.CompleteStreamingAsync(Req())); }
            catch (Exception exception) { observed = exception; }

            Assert.AreSame(primary, observed);
            Assert.AreSame(inner.CleanupFailure, observed.Data[ClientLimitedLlmClientDecorator.StreamDisposeExceptionDataKey]);
            Assert.AreEqual(1, inner.Opens);
            Assert.AreEqual(1, inner.Disposals);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PermanentFailureAndDisposalFailure_PreservesProviderClassification(bool committed)
        {
            LlmClientException primary = new("Authentication expired.", LlmErrorCode.AuthExpired, 401);
            DisposalFailureClient inner = new(committed, primary);
            RetryingStreamingLlmClientDecorator sut = new(inner, 3, log: _ => throw new IOException("Diagnostic sink failed."));
            List<LlmStreamChunk> chunks = new();
            Exception observed = null;
            try
            {
                await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(Req())) chunks.Add(chunk);
            }
            catch (Exception exception) { observed = exception; }

            if (committed) Assert.AreSame(primary, observed);
            else
            {
                Assert.IsNull(observed);
                Assert.AreEqual(LlmErrorCode.AuthExpired, chunks[0].ErrorCode);
                Assert.AreEqual(401, chunks[0].HttpStatus);
            }
            Assert.AreSame(inner.CleanupFailure, primary.Data[ClientLimitedLlmClientDecorator.StreamDisposeExceptionDataKey]);
            Assert.AreEqual(1, inner.Opens);
            Assert.AreEqual(1, inner.Disposals);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [TestCase(false, LlmErrorCode.AuthExpired, "Provider refused the request.")]
        [TestCase(true, LlmErrorCode.AuthExpired, "Provider refused the request.")]
        [TestCase(false, LlmErrorCode.BackendUnavailable, "Provider refused the request.")]
        [TestCase(true, LlmErrorCode.BackendUnavailable, "Provider refused the request.")]
        [TestCase(false, LlmErrorCode.AuthExpired, "")]
        [TestCase(true, LlmErrorCode.AuthExpired, "")]
        [TestCase(false, LlmErrorCode.BackendUnavailable, "")]
        [TestCase(true, LlmErrorCode.BackendUnavailable, "")]
        public async Task ErrorChunkAndDisposalFailure_PreservesTerminalError(bool committed, LlmErrorCode errorCode, string errorText)
        {
            LlmStreamChunk terminal = new() { Error = errorText, ErrorCode = errorCode, IsDone = true };
            DisposalFailureClient inner = new(committed, null) { Terminal = terminal };
            RetryingStreamingLlmClientDecorator sut = new(inner, 0);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreSame(terminal, chunks[chunks.Count - 1]);
            Assert.AreEqual(1, inner.Opens);
            Assert.AreEqual(1, inner.Disposals);
            Assert.AreEqual(0, sut.RetryCount);
        }

        [Test]
        public async Task SuccessfulStreamAndDisposalFailure_SurfacesCleanupFailure()
        {
            DisposalFailureClient inner = new(true, null);
            RetryingStreamingLlmClientDecorator sut = new(inner, 3);
            Exception observed = null;
            try { await Drain(sut.CompleteStreamingAsync(Req())); }
            catch (Exception exception) { observed = exception; }

            Assert.AreSame(inner.CleanupFailure, observed);
            Assert.AreEqual(1, inner.Opens);
            Assert.AreEqual(1, inner.Disposals);
            Assert.AreEqual(0, sut.RetryCount);
        }


        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
        }

        private static LlmStreamChunk Text(string t)
        {
            return new LlmStreamChunk { Text = t };
        }

        private static LlmStreamChunk Done()
        {
            return new LlmStreamChunk { IsDone = true };
        }

        private static LlmStreamChunk ErrChunk(LlmErrorCode code)
        {
            return new LlmStreamChunk { IsDone = true, Error = code.ToString(), ErrorCode = code };
        }

        private static string Concat(IEnumerable<LlmStreamChunk> chunks)
        {
            System.Text.StringBuilder sb = new();
            foreach (LlmStreamChunk c in chunks)
            {
                sb.Append(c.Text);
            }

            return sb.ToString();
        }

        private static bool HasError(IEnumerable<LlmStreamChunk> chunks)
        {
            foreach (LlmStreamChunk c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Error))
                {
                    return true;
                }
            }

            return false;
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

        private sealed class CompletionOnlyFailureClient : ILlmClient
        {
            private readonly LlmErrorCode _code;
            private readonly int _status;
            public int CompleteCallCount;

            public CompletionOnlyFailureClient(LlmErrorCode code, int status)
            {
                _code = code;
                _status = status;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = false, ErrorCode = _code, HttpStatus = _status });
            }
        }

        private sealed class DisposalFailureClient : ILlmClient, IAsyncEnumerable<LlmStreamChunk>, IAsyncEnumerator<LlmStreamChunk>
        {
            private readonly bool _committed;
            private readonly Exception _primaryFailure;
            private int _moves;
            public readonly IOException CleanupFailure = new("Stream disposal failed.");
            public int Opens;
            public int Disposals;
            public LlmStreamChunk Terminal;
            public LlmStreamChunk Current { get; private set; }

            public DisposalFailureClient(bool committed, Exception primaryFailure)
            {
                _committed = committed;
                _primaryFailure = primaryFailure;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Opens++;
                return this;
            }

            public IAsyncEnumerator<LlmStreamChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync()
            {
                int move = _moves++;
                if (_committed && move == 0)
                {
                    Current = Text("partial");
                    return new ValueTask<bool>(true);
                }
                if (_primaryFailure != null) return new ValueTask<bool>(Task.FromException<bool>(_primaryFailure));
                Current = Terminal;
                return new ValueTask<bool>(Terminal != null && move == (_committed ? 1 : 0));
            }

            public ValueTask DisposeAsync()
            {
                Disposals++;
                return new ValueTask(Task.FromException(CleanupFailure));
            }
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

        private sealed class StubStreamingClient : ILlmClient
        {
            public readonly Queue<LlmStreamChunk[]> NextStreams = new();
            public LlmCompletionResult NextResult = new() { Ok = true };
            public int StreamCallCount;
            public int CompleteCallCount;
            public Action BeforeYield;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                return Task.FromResult(NextResult);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    BeforeYield?.Invoke();
                    yield return c;
                }
            }
        }

        private sealed class OpeningFailureClient : ILlmClient
        {
            public int OpenCount;
            public Exception OpeningException = new InvalidOperationException("Temporary stream initialization failure.");
            private readonly StubStreamingClient _inner = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.CompleteAsync(request, cancellationToken);
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                OpenCount++;
                if (OpenCount == 1)
                {
                    throw OpeningException;
                }

                return _inner.CompleteStreamingAsync(request, cancellationToken);
            }
        }

        private sealed class SnapshotAwareStreamingClient : ILlmClient, ILlmRequestHeaderScope
        {
            private string _activeLesson;
            private int _depth;
            private int _streamCalls;

            public string CurrentLesson { get; set; } = "lesson-a";

            public int ScopeCount { get; private set; }

            public List<string> SeenLessons { get; } = new();

            public IDisposable BeginRequestHeaders(LlmCompletionRequest request)
            {
                string previous = _activeLesson;
                if (_depth == 0)
                {
                    _activeLesson = CurrentLesson;
                    ScopeCount++;
                }

                _depth++;
                return new HeaderScope(this, previous);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                using (BeginRequestHeaders(request))
                {
                    SeenLessons.Add(_activeLesson);
                    _streamCalls++;
                    await Task.Yield();
                    if (_streamCalls == 1)
                    {
                        CurrentLesson = "lesson-b";
                        yield return ErrChunk(LlmErrorCode.BackendUnavailable);
                        yield break;
                    }

                    yield return Text("ok");
                    yield return Done();
                }
            }

            private sealed class HeaderScope : IDisposable
            {
                private readonly SnapshotAwareStreamingClient _owner;
                private readonly string _previous;

                public HeaderScope(SnapshotAwareStreamingClient owner, string previous)
                {
                    _owner = owner;
                    _previous = previous;
                }

                public void Dispose()
                {
                    _owner._depth--;
                    _owner._activeLesson = _previous;
                }
            }
        }
    }
}
#endif
