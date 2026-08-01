#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Permanent vs transient provider failures. A provider refusal that no replay can change
    /// (HTTP 402 payment required above all) used to be classified as the catch-all
    /// <see cref="LlmErrorCode.ProviderError"/>, which both the streaming retry decorator and the
    /// fallback decorator treat as transient - so an answer that was impossible from the first
    /// millisecond was requested three more times, and the player waited for every one of them.
    /// </summary>
    [TestFixture]
    public sealed class PermanentLlmFailureEditModeTests
    {
        // ==================== HTTP status classification ====================

        [Test]
        public void MapHttpStatus_PaymentRequired_IsNotTheTransientCatchAll()
        {
            Assert.AreEqual(
                LlmErrorCode.PaymentRequired,
                MeaiOpenAiChatClient.MapHttpStatusForTests(
                    402, "{\"error\":{\"message\":\"Insufficient credits\"}}", ""),
                "HTTP 402 must not land in ProviderError, which the retry/fallback decorators replay.");
        }

        [Test]
        public void MapHttpStatus_UnclassifiedClientError_IsPermanent()
        {
            Assert.AreEqual(
                LlmErrorCode.PermanentProviderError,
                MeaiOpenAiChatClient.MapHttpStatusForTests(404, "{\"error\":{\"message\":\"no such model\"}}", ""));
            Assert.AreEqual(
                LlmErrorCode.PermanentProviderError,
                MeaiOpenAiChatClient.MapHttpStatusForTests(405, "", ""));
        }

        [Test]
        public void MapHttpStatus_RequestTimeout_StaysRetryable()
        {
            Assert.AreEqual(
                LlmErrorCode.Timeout,
                MeaiOpenAiChatClient.MapHttpStatusForTests(408, "", ""),
                "408 is the one 4xx a replay can clear; the permanent-4xx sweep must not swallow it.");
        }

        [Test]
        public void MapHttpStatus_TransientStatuses_KeepTheirClassification()
        {
            Assert.AreEqual(LlmErrorCode.RateLimited, MeaiOpenAiChatClient.MapHttpStatusForTests(429, "", ""));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, MeaiOpenAiChatClient.MapHttpStatusForTests(500, "", ""));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, MeaiOpenAiChatClient.MapHttpStatusForTests(503, "", ""));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, MeaiOpenAiChatClient.MapHttpStatusForTests(0, "", ""));
        }

        [Test]
        public void BuildHttpException_PaymentRequired_CarriesCodeAndStatus()
        {
            LlmClientException ex = MeaiOpenAiChatClient.BuildHttpExceptionForTests(
                402, "{\"error\":{\"message\":\"Insufficient credits\"}}");

            Assert.AreEqual(LlmErrorCode.PaymentRequired, ex.ErrorCode);
            Assert.AreEqual(402, ex.HttpStatus);
        }

        // ==================== FallbackLlmClientDecorator ====================

        [Test]
        public async Task Fallback_Streaming_PaymentRequired_DoesNotSwitchBackends()
        {
            CountingFailingClient primary = new(
                new LlmClientException("HTTP error 402: out of credit", LlmErrorCode.PaymentRequired, 402));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator sut = new(primary, secondary, NullLog.Instance);

            LlmClientException thrown = null;
            try
            {
                await DrainAsync(sut);
            }
            catch (LlmClientException ex)
            {
                thrown = ex;
            }

            Assert.IsNotNull(thrown, "The permanent refusal must reach the caller, not be swallowed.");
            Assert.AreEqual(LlmErrorCode.PaymentRequired, thrown.ErrorCode);
            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(
                0, secondary.Calls,
                "The secondary backend cannot pay the primary's bill, so it must never be called.");
            Assert.AreEqual(0, sut.FallbackCount);
        }

        [Test]
        public async Task Fallback_Streaming_TransientFailure_StillSwitchesBackends()
        {
            CountingFailingClient primary = new(
                new LlmClientException("primary down", LlmErrorCode.BackendUnavailable, 503));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator sut = new(primary, secondary, NullLog.Instance);

            List<LlmStreamChunk> chunks = await DrainAsync(sut);

            Assert.AreEqual(1, secondary.Calls, "A transient primary failure must still fall back.");
            Assert.AreEqual(1, sut.FallbackCount);
            Assert.IsTrue(chunks.Exists(c => c.Text == "secondary answer"));
        }

        [Test]
        public async Task Fallback_NonStreaming_PaymentRequired_DoesNotSwitchBackends()
        {
            CountingFailingClient primary = new(
                new LlmClientException("HTTP error 402: out of credit", LlmErrorCode.PaymentRequired, 402));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator sut = new(primary, secondary, NullLog.Instance);

            LlmClientException thrown = null;
            try
            {
                await sut.CompleteAsync(new LlmCompletionRequest());
            }
            catch (LlmClientException ex)
            {
                thrown = ex;
            }

            Assert.IsNotNull(thrown);
            Assert.AreEqual(LlmErrorCode.PaymentRequired, thrown.ErrorCode);
            Assert.AreEqual(0, secondary.Calls);
            Assert.AreEqual(0, sut.FallbackCount);
        }

        [Test]
        public async Task Fallback_NonStreaming_TransientFailure_StillSwitchesBackends()
        {
            CountingFailingClient primary = new(
                new LlmClientException("primary down", LlmErrorCode.BackendUnavailable, 503));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator sut = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await sut.CompleteAsync(new LlmCompletionRequest());

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, secondary.Calls);
            Assert.AreEqual(1, sut.FallbackCount);
        }

        // ==================== Whole pipeline (retry over fallback) ====================

        [Test]
        public async Task Pipeline_PaymentRequired_CostsExactlyOneUpstreamCall()
        {
            CountingFailingClient primary = new(
                new LlmClientException("HTTP error 402: out of credit", LlmErrorCode.PaymentRequired, 402));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator fallback = new(primary, secondary, NullLog.Instance);
            List<int> scheduledBackoffs = new();
            RetryingStreamingLlmClientDecorator sut = new(
                fallback,
                3,
                attempt =>
                {
                    scheduledBackoffs.Add(attempt);
                    return TimeSpan.FromSeconds(5);
                });

            List<LlmStreamChunk> chunks = await DrainAsync(sut);

            Assert.AreEqual(
                1, primary.Calls,
                "One 402 request, one upstream call - retries and fallbacks must not multiply on it.");
            Assert.AreEqual(0, secondary.Calls);
            Assert.AreEqual(0, sut.RetryCount);
            CollectionAssert.IsEmpty(
                scheduledBackoffs,
                "No backoff wait may be scheduled: that wait is what the player sits through.");
            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.PaymentRequired, chunks[0].ErrorCode);
            Assert.AreEqual(402, chunks[0].HttpStatus);
        }

        [Test]
        public async Task Pipeline_TransientFailure_StillRetriesAndFallsBack()
        {
            CountingFailingClient primary = new(
                new LlmClientException("primary down", LlmErrorCode.BackendUnavailable, 503));
            CountingOkClient secondary = new();
            FallbackLlmClientDecorator fallback = new(primary, secondary, NullLog.Instance);
            RetryingStreamingLlmClientDecorator sut = new(fallback, 3, _ => TimeSpan.Zero);

            List<LlmStreamChunk> chunks = await DrainAsync(sut);

            Assert.AreEqual(1, primary.Calls);
            Assert.AreEqual(1, secondary.Calls, "A transient outage must still reach the secondary backend.");
            Assert.IsTrue(chunks.Exists(c => c.Text == "secondary answer"));
        }

        // ==================== Player-facing text ====================

        [Test]
        public void UserMessage_PaymentRequired_StaysHumanAndKeepsAGatewayAuthoredSentence()
        {
            LlmClientException bare = new("HTTP error 402: Insufficient credits", LlmErrorCode.PaymentRequired, 402);
            string builtIn = LlmErrorPresentation.ToUserMessage(bare);
            Assert.IsNotEmpty(builtIn);
            Assert.IsFalse(builtIn.StartsWith("{", StringComparison.Ordinal));

            LlmClientException authored = new(
                "HTTP error 402: nope",
                LlmErrorCode.PaymentRequired,
                402,
                null,
                "{\"error\":{\"message\":\"The teacher is unavailable right now - tell your instructor.\"}}");

            Assert.AreEqual(
                "The teacher is unavailable right now - tell your instructor.",
                LlmErrorPresentation.ToUserMessage(authored),
                "A gateway-authored sentence must still win over the built-in phrase for the new code.");
        }

        // ==================== Helpers ====================

        private static async Task<List<LlmStreamChunk>> DrainAsync(ILlmClient client)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        private sealed class CountingFailingClient : ILlmClient
        {
            private readonly Exception _failure;

            public CountingFailingClient(Exception failure)
            {
                _failure = failure;
            }

            public int Calls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw _failure;
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Calls++;
                await Task.Yield();
                throw _failure;
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }
        }

        private sealed class CountingOkClient : ILlmClient
        {
            public int Calls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "secondary answer" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Calls++;
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "secondary answer" };
                yield return new LlmStreamChunk { IsDone = true, Text = "" };
            }
        }
    }
}
#endif
