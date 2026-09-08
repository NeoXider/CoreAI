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
                Assert.ThrowsAsync<LlmOperationTimeoutException>(async () => await call);
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
        /// Продление дедлайна на каждый чанк раньше создавало новый источник отмены, отменяло предыдущий и
        /// запускало новое ожидание — по исключению и по нескольким аллокациям на КАЖДЫЙ токен. Теперь у
        /// потока одно сторожевое ожидание: хост получает ровно один запрос задержки на окно, сколько бы
        /// чанков ни пришло.
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
                "Каждый чанк — отметка прогресса, а не новый таймер: ожидание у хоста запрашивается один раз на окно.");
            Assert.LessOrEqual(marshaler.CancelledDelays, 1,
                "Разрешена одна отмена таймера при завершении запроса; отдельной отмены на каждый чанк быть не должно.");
            marshaler.ReleaseAll();
        }

        /// <summary>
        /// Сбой таймера — не истечение таймера: если задержка хоста упала, запрос НЕ отменяется и не
        /// репортится как таймаут, а сбой отдаётся в обработчик.
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
                "Упавший таймер раньше трактовался как истёкший, и здоровый запрос отменялся с кодом Timeout.");
            Assert.IsTrue(chunks.Exists(c => c.Text == "answer"));
            Assert.IsInstanceOf<InvalidOperationException>(reported, "Сбой таймера должен быть виден хосту.");
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

        [Test]
        public async Task Streaming_ProgressWithinWindow_KeepsStreamAlive_ThenStallTimesOut()
        {
            // Три чанка с паузой в 60 мс при окне 150 мс: ни одна пауза не превышает окно — поток жив;
            // затем застой на 400 мс — таймаут.
            StallingAfterChunksClient inner = new() { ChunkGapMs = 60, StallMs = 400 };
            TimeoutLlmClientDecorator sut = new(inner, () => 0.15f);

            List<LlmStreamChunk> chunks = await Drain(sut.CompleteStreamingAsync(Req()));

            Assert.AreEqual(3, chunks.FindAll(c => c.Text == "t").Count, "Все три чанка до застоя должны дойти.");
            Assert.AreEqual(LlmErrorCode.Timeout, chunks[chunks.Count - 1].ErrorCode, "Застой дольше окна — таймаут.");
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

        /// <summary>Задержка хоста, которая никогда не истекает сама, но считает запросы и отмены.</summary>
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
