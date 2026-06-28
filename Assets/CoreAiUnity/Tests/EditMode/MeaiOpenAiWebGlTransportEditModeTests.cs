#if !COREAI_NO_LLM
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Non-SSE transports (e.g. WebGL <see cref="UnityWebRequestOpenAiTransport"/>) must complete via full JSON
    /// and yield simulated <see cref="MEAI.ChatResponseUpdate"/> sequences.
    /// </summary>
    public sealed class MeaiOpenAiWebGlTransportEditModeTests
    {
        private sealed class NonSseMockTransport : IOpenAiHttpTransport
        {
            public string DebugLabel => "NonSseMock";
            public bool SupportsSseStreaming => false;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                const string body =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"from_full_json\"}}]}";
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = body,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult r = new()
                {
                    StatusCode = 500,
                    ErrorBodyText = "mock transport has no SSE",
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                };
                return Task.FromResult(r);
            }
        }

        /// <summary>
        /// Mimics the WebGL composite transport: advertises SSE support (streaming goes to the fetch bridge)
        /// yet still serves non-streaming completions. Guards the regression where native-streaming WebGL
        /// wiring used a streaming-only transport and non-streaming agents (TeacherLessonFeedback) threw
        /// "Use UnityWebRequestOpenAiTransport for non-streaming in WebGL."
        /// </summary>
        private sealed class StreamingCapableNonStreamServingTransport : IOpenAiHttpTransport
        {
            public string DebugLabel => "CompositeMock";
            public bool SupportsSseStreaming => true;
            public bool NonStreamingWasCalled { get; private set; }

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingWasCalled = true;
                const string body =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"feedback_json\"}}]}";
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = body,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("streaming path not exercised by this test");
        }

        private sealed class DummyHttpSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }

        [Test]
        public async Task GetStreamingResponseAsync_NonSseTransport_YieldsAssistantTextFromFullCompletion()
        {
            MeaiOpenAiChatClient client = new(new DummyHttpSettings(), new NonSseMockTransport());
            List<string> parts = new();
            await foreach (MEAI.ChatResponseUpdate u in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(u.Text))
                {
                    parts.Add(u.Text);
                }
            }

            Assert.AreEqual("from_full_json", string.Concat(parts));
        }

        [Test]
        public async Task GetResponseAsync_SseCapableTransport_StillServesNonStreamingCompletion()
        {
            StreamingCapableNonStreamServingTransport transport = new();
            MeaiOpenAiChatClient client = new(new DummyHttpSettings(), transport);

            MEAI.ChatResponse response = await client.GetResponseAsync(
                new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "summarize the lesson") });

            Assert.IsTrue(transport.NonStreamingWasCalled,
                "Non-streaming GetResponseAsync must use PostNonStreamingAsync, not the SSE path.");
            Assert.AreEqual("feedback_json", response.Text);
        }
    }
}
#endif