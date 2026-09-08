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
