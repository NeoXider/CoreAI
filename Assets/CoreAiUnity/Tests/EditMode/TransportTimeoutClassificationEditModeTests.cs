#if COREAI_LLM
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
                new(MEAI.ChatRole.User, "hi")
            };
        }

        [Test]
        [Timeout(20_000)]
        public async Task NonStreaming_TransportTimeoutWithLiveCallerToken_SurfacesAsTypedTimeout()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);

            LlmClientException ex = null;
            try
            {
                await client.GetResponseAsync(Messages());
            }
            catch (LlmClientException caught)
            {
                ex = caught;
            }

            Assert.IsNotNull(ex, "An internal transport cancellation must surface as LlmClientException.");
            Assert.AreEqual(LlmErrorCode.Timeout, ex.ErrorCode,
                "An internal transport cancellation with a live caller token is a timeout");
            Assert.AreEqual(1, transport.PostCalls);
        }

        [Test]
        [Timeout(20_000)]
        public async Task Streaming_HeaderTimeoutWithLiveCallerToken_SurfacesAsTypedTimeout()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);

            LlmClientException ex = null;
            try
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages()))
                {
                    _ = update;
                }
            }
            catch (LlmClientException caught)
            {
                ex = caught;
            }

            Assert.IsNotNull(ex, "A header timeout must surface as LlmClientException.");
            Assert.AreEqual(LlmErrorCode.Timeout, ex.ErrorCode,
                "A headerless backend must fail as Timeout so the fallback decorator can try the secondary");
            Assert.AreEqual(1, transport.OpenCalls);
        }

        [Test]
        [Timeout(20_000)]
        public async Task NonStreaming_CallerCancellation_StillPropagatesAsOperationCanceled()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);
            using CancellationTokenSource cts = new();
            cts.Cancel();

            OperationCanceledException exception = null;
            try
            {
                await client.GetResponseAsync(Messages(), null, cts.Token);
            }
            catch (OperationCanceledException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, "Caller cancellation must remain an OperationCanceledException.");
        }

        [Test]
        [Timeout(20_000)]
        public async Task Streaming_CallerCancellation_StillPropagatesAsOperationCanceled()
        {
            CancellationThrowingTransport transport = new();
            MeaiOpenAiChatClient client = new(new StubHttpSettings(), transport, NullLog.Instance);
            using CancellationTokenSource cts = new();
            cts.Cancel();

            OperationCanceledException exception = null;
            try
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   Messages(), null, cts.Token))
                {
                    _ = update;
                }
            }
            catch (OperationCanceledException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, "Caller cancellation must remain an OperationCanceledException.");
        }
    }
}
#endif
