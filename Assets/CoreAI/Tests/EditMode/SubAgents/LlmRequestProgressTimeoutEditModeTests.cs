using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// Integration change I5: a request that reports progress (its tools, a sub-agent it waits for) is not cut
    /// off by the total request ceiling of <see cref="TimeoutLlmClientDecorator"/>, on both the buffered and the
    /// streaming path; a silent request still times out at the configured deadline (the negative twins).
    /// </summary>
    [TestFixture]
    public sealed class LlmRequestProgressTimeoutEditModeTests
    {
        private const float WindowSeconds = 0.6f;
        private const int SilentWorkMs = 1800;
        private const int ReportEveryMs = 40;

        [Test]
        public void Report_CallsEveryListener_UntilItsHandleIsDisposed()
        {
            LlmRequestProgress progress = new();
            int first = 0;
            int second = 0;
            IDisposable a = progress.Subscribe(() => first++);
            IDisposable b = progress.Subscribe(() => second++);

            progress.Report();
            a.Dispose();
            a.Dispose();
            progress.Report();
            b.Dispose();
            progress.Report();

            Assert.AreEqual(1, first);
            Assert.AreEqual(2, second);
            Assert.AreEqual(3, progress.Reports);
        }

        [Test]
        public void Report_IgnoresAThrowingListener_AndStillReachesTheOthers()
        {
            LlmRequestProgress progress = new();
            int reached = 0;
            progress.Subscribe(() => throw new InvalidOperationException("listener failed"));
            progress.Subscribe(() => reached++);

            Assert.DoesNotThrow(progress.Report);
            Assert.AreEqual(1, reached);
        }

        [Test]
        public async Task BufferedRequest_ThatReportsProgress_OutlivesTheWindow()
        {
            SlowClient inner = new(SilentWorkMs);
            TimeoutLlmClientDecorator decorator = new(inner, () => WindowSeconds);
            LlmCompletionRequest request = new() { Progress = new LlmRequestProgress() };
            using CancellationTokenSource reporting = new();
            Task reporter = ReportUntilCancelledAsync(request.Progress, reporting.Token);

            LlmCompletionResult result = await decorator.CompleteAsync(request);
            reporting.Cancel();
            await reporter;

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("finished", result.Content);
        }

        // WHY: Assert.ThrowsAsync blocks the calling thread; under the editor's synchronization context the awaited
        // continuation needs that same thread, so the test never returns. These tests await instead.
        [Test]
        public async Task BufferedRequest_WithoutProgress_StillTimesOutAtTheWindow()
        {
            SlowClient inner = new(SilentWorkMs);
            TimeoutLlmClientDecorator decorator = new(inner, () => WindowSeconds);
            Stopwatch clock = Stopwatch.StartNew();

            await SubAgentTestSupport.CaptureExceptionAsync<LlmOperationTimeoutException>(
                () => decorator.CompleteAsync(new LlmCompletionRequest()));

            Assert.Less(clock.ElapsedMilliseconds, SilentWorkMs,
                "A silent request is cut off at the window, not when its work ends.");
        }

        [Test]
        public async Task BufferedRequest_WhoseProgressNobodyReports_StillTimesOut()
        {
            SlowClient inner = new(SilentWorkMs);
            TimeoutLlmClientDecorator decorator = new(inner, () => WindowSeconds);

            await SubAgentTestSupport.CaptureExceptionAsync<LlmOperationTimeoutException>(
                () => decorator.CompleteAsync(new LlmCompletionRequest { Progress = new LlmRequestProgress() }));
        }

        [Test]
        public async Task StreamingRequest_SilentBetweenChunks_SurvivesWhileProgressIsReported()
        {
            SlowClient inner = new(SilentWorkMs);
            TimeoutLlmClientDecorator decorator = new(inner, () => WindowSeconds);
            LlmCompletionRequest request = new() { Progress = new LlmRequestProgress() };
            using CancellationTokenSource reporting = new();
            Task reporter = ReportUntilCancelledAsync(request.Progress, reporting.Token);

            List<LlmStreamChunk> chunks = await Collect(decorator.CompleteStreamingAsync(request));
            reporting.Cancel();
            await reporter;

            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(LlmErrorCode.None, chunks[1].ErrorCode);
            Assert.AreEqual("finished", chunks[1].Text);
        }

        [Test]
        public async Task StreamingRequest_SilentBetweenChunks_WithoutProgress_TimesOut()
        {
            SlowClient inner = new(SilentWorkMs);
            TimeoutLlmClientDecorator decorator = new(inner, () => WindowSeconds);

            List<LlmStreamChunk> chunks = await Collect(decorator.CompleteStreamingAsync(new LlmCompletionRequest()));

            Assert.AreEqual(LlmErrorCode.Timeout, chunks[chunks.Count - 1].ErrorCode);
        }

        [Test]
        public void Progress_IsCarriedFromTheTaskRequestByClone()
        {
            LlmRequestProgress progress = new();

            AiTaskRequest clone = new AiTaskRequest { Progress = progress, ConversationId = "chat-2" }.Clone();

            Assert.AreSame(progress, clone.Progress);
            Assert.AreEqual("chat-2", clone.ConversationId);
            Assert.AreEqual("", new AiTaskRequest().ConversationId);
            Assert.IsNull(new AiTaskRequest().Progress);
        }

        private static async Task ReportUntilCancelledAsync(LlmRequestProgress progress, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                progress.Report();
                try
                {
                    await Task.Delay(ReportEveryMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static async Task<List<LlmStreamChunk>> Collect(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in stream)
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        /// <summary>Works silently for a fixed time; the streaming form sends one chunk first.</summary>
        private sealed class SlowClient : ILlmClient
        {
            private readonly int _workMs;

            public SlowClient(int workMs)
            {
                _workMs = workMs;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(_workMs, cancellationToken);
                return new LlmCompletionResult { Ok = true, Content = "finished" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = "started" };
                await Task.Delay(_workMs, cancellationToken);
                yield return new LlmStreamChunk { Text = "finished", IsDone = true };
            }
        }
    }
}
