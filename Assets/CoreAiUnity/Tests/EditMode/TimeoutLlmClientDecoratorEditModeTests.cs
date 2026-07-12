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
    /// EditMode coverage for <see cref="TimeoutLlmClientDecorator"/>: a library timeout surfaces as
    /// <see cref="LlmOperationTimeoutException"/> (non-streaming) / a terminal <see cref="LlmErrorCode.Timeout"/>
    /// chunk (streaming), a genuine caller cancellation propagates as a plain
    /// <see cref="OperationCanceledException"/>, and a non-positive timeout disables the bound entirely.
    /// </summary>
    public sealed class TimeoutLlmClientDecoratorEditModeTests
    {
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

        [Test]
        public async Task Streaming_InnerCancelledTerminalFromDecoratorToken_RewritesOnlyErrorCode()
        {
            CancelledResultClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 0.03f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[0].ErrorCode);
            Assert.AreEqual("inner cancelled", chunks[0].Error);
            Assert.AreEqual("partial", chunks[0].Text);
            Assert.AreEqual("test-model", chunks[0].Model);
            Assert.AreEqual(1, chunks[0].ExecutedToolCalls.Count);
        }

        // ---- helpers ----

        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
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
                new LlmToolCallTrace("mutator", true, 1d, "native", "done")
            };

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
    }
}
#endif
