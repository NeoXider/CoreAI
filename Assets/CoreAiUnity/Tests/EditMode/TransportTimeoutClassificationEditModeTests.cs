#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A transport-level timeout (linked CTS fired while the caller's token is still live) must
    /// surface as a typed <see cref="LlmClientException"/> with <see cref="LlmErrorCode.Timeout"/>,
    /// not as a raw <see cref="OperationCanceledException"/> that downstream maps to Cancelled
    /// (non-retryable, not fallback-eligible). Genuine caller cancellation must keep propagating
    /// as <see cref="OperationCanceledException"/>.
    /// </summary>
    public sealed class TransportTimeoutClassificationEditModeTests
    {
        private sealed class CancellationThrowingTransport : IOpenAiHttpTransport
        {
            public string DebugLabel => "TestOceTransport";
            public bool SupportsSseStreaming => true;
            public int PostCalls;
            public int OpenCalls;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(
                OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                PostCalls++;
                throw new TaskCanceledException("response headers never arrived");
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(
                OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenCalls++;
                throw new TaskCanceledException("response headers never arrived");
            }
        }

        private sealed class StubHttpSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 1;
            public int MaxTokens => 64;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider HeaderProvider => null;
        }

        private static List<MEAI.ChatMessage> Messages()
        {
            return new List<MEAI.ChatMessage>
            {
                new MEAI.ChatMessage(MEAI.ChatRole.User, "hi")
            };
        }

        [Test]
        [Timeout(20_000)]
        public void NonStreaming_TransportTimeoutWithLiveCallerToken_SurfacesAsTypedTimeout()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);

            LlmClientException ex = Assert.ThrowsAsync<LlmClientException>(async () =>
                await client.GetResponseAsync(Messages()));

            Assert.AreEqual(LlmErrorCode.Timeout, ex.ErrorCode,
                "An internal transport cancellation with a live caller token is a timeout");
            Assert.AreEqual(1, transport.PostCalls);
        }

        [Test]
        [Timeout(20_000)]
        public void Streaming_HeaderTimeoutWithLiveCallerToken_SurfacesAsTypedTimeout()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);

            LlmClientException ex = Assert.ThrowsAsync<LlmClientException>(async () =>
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages()))
                {
                    _ = update;
                }
            });

            Assert.AreEqual(LlmErrorCode.Timeout, ex.ErrorCode,
                "A headerless backend must fail as Timeout so the fallback decorator can try the secondary");
            Assert.AreEqual(1, transport.OpenCalls);
        }

        [Test]
        [Timeout(20_000)]
        public void NonStreaming_CallerCancellation_StillPropagatesAsOperationCanceled()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);
            using CancellationTokenSource cts = new();
            cts.Cancel();

            // WHY: CatchAsync (not ThrowsAsync): TaskCanceledException derives from
            // OperationCanceledException and both shapes are valid caller-cancellation surfaces.
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await client.GetResponseAsync(Messages(), null, cts.Token));
        }

        [Test]
        [Timeout(20_000)]
        public void Streaming_CallerCancellation_StillPropagatesAsOperationCanceled()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);
            using CancellationTokenSource cts = new();
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   Messages(), null, cts.Token))
                {
                    _ = update;
                }
            });
        }
    }
}
#endif
