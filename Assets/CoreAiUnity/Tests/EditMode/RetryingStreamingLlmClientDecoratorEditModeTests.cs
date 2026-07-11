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
        public async Task EmptyStream_IsTreatedAsTransient_AndRetried()
        {
            StubStreamingClient inner = new();
            inner.NextStreams.Enqueue(Array.Empty<LlmStreamChunk>()); // ends with no content
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
            // A single stream that commits text and THEN emits a (would-be retryable) error.
            inner.NextStreams.Enqueue(new[] { Text("partial"), ErrChunk(LlmErrorCode.BackendUnavailable) });

            RetryingStreamingLlmClientDecorator sut = new(inner, 3, null);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(0, sut.RetryCount, "A committed stream must never be retried.");
            Assert.AreEqual(1, inner.StreamCallCount);
            Assert.AreEqual("partial", Concat(chunks));
            Assert.IsTrue(HasError(chunks), "The post-commit error must propagate unchanged.");
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

        // ---- helpers ----

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

        private sealed class StubStreamingClient : ILlmClient
        {
            public readonly Queue<LlmStreamChunk[]> NextStreams = new();
            public LlmCompletionResult NextResult = new() { Ok = true };
            public int StreamCallCount;
            public int CompleteCallCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                return Task.FromResult(NextResult);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                StreamCallCount++;
                LlmStreamChunk[] chunks = NextStreams.Count > 0
                    ? NextStreams.Dequeue()
                    : new[] { new LlmStreamChunk { Text = "ok", IsDone = true } };

                foreach (LlmStreamChunk c in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return c;
                }
            }
        }
    }
}
#endif