#if !COREAI_NO_LLM
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>EditMode HTTP transport for <see cref="MeaiOpenAiChatClient"/> via <see cref="MeaiOpenAiChatClientEditorTestHooks"/> (no real network).</summary>
    public sealed class MeaiOpenAiChatClientHttpEditModeTests
    {
        [SetUp]
        public void SetUp() => MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;

        [TearDown]
        public void TearDown() => MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;

        [Test]
        public void MeaiOpenAiChatClient_LivesInPortableCoreAssembly()
        {
            Assert.AreEqual("CoreAI.Core", typeof(MeaiOpenAiChatClient).Assembly.GetName().Name);
        }

        [Test]
        public async Task GetResponseAsync_Success_ReturnsAssistantText()
        {
            const string body =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello from mock\"}}]}";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(_ => OkJson(body))) { Timeout = System.TimeSpan.FromSeconds(30) };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);
            MEAI.ChatResponse r = await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") });

            Assert.AreEqual("hello from mock", r.Text);
        }

        [Test]
        public async Task GetResponseAsync_WithoutOptionsTemperature_OmitsTemperatureInJson()
        {
            const string body =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"x\"}}]}";
            string capturedJson = null;
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(req =>
                {
                    capturedJson = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return OkJson(body);
                }))
            {
                Timeout = System.TimeSpan.FromSeconds(30)
            };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, options: null);

            Assert.NotNull(capturedJson);
            Assert.That(capturedJson, Does.Not.Contain("\"temperature\""));
        }

        [Test]
        public async Task GetResponseAsync_WithOptionsTemperature_IncludesTemperatureInJson()
        {
            const string body =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"x\"}}]}";
            string capturedJson = null;
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(req =>
                {
                    capturedJson = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return OkJson(body);
                }))
            {
                Timeout = System.TimeSpan.FromSeconds(30)
            };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);
            var options = new MEAI.ChatOptions { Temperature = 0.42f };
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, options);

            Assert.NotNull(capturedJson);
            StringAssert.Contains("\"temperature\"", capturedJson);
            StringAssert.Contains("0.42", capturedJson);
        }

        [Test]
        public async Task GetResponseAsync_429_MapsRetryAfterSeconds()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(_ =>
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                    r.Headers.TryAddWithoutValidation("Retry-After", "7");
                    return r;
                })) { Timeout = System.TimeSpan.FromSeconds(30) };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);

            try
            {
                await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "x") });
            }
            catch (LlmClientException ex)
            {
                Assert.AreEqual(429, ex.HttpStatus);
                Assert.AreEqual(7, ex.RetryAfterSeconds);
                return;
            }

            Assert.Fail("Expected LlmClientException for HTTP 429.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_Sse_YieldsAggregatedText()
        {
            string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\n";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(_ => OkEventStream(new MemoryStream(Encoding.UTF8.GetBytes(sse)))))
            {
                Timeout = System.TimeSpan.FromHours(1)
            };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);
            var parts = new List<string>();
            await foreach (MEAI.ChatResponseUpdate u in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "x") }))
            {
                if (!string.IsNullOrEmpty(u.Text))
                {
                    parts.Add(u.Text);
                }
            }

            Assert.AreEqual("ab", string.Concat(parts));
        }

        [Test]
        public void ParseSseUpdates_FinalUsageChunk_YieldsUsageContent()
        {
            const string sse =
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":7,\"total_tokens\":10}}\n\n";
            List<MEAI.ChatResponseUpdate> updates = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, updates.Count);
            MEAI.UsageContent usage = updates[0].Contents?.OfType<MEAI.UsageContent>().FirstOrDefault();
            Assert.NotNull(usage);
            Assert.AreEqual(3, usage.Details.InputTokenCount);
            Assert.AreEqual(7, usage.Details.OutputTokenCount);
            Assert.AreEqual(10, usage.Details.TotalTokenCount);
        }

        [Test]
        public async Task GetStreamingResponseAsync_RequestJson_ContainsStreamOptionsIncludeUsage()
        {
            string capturedJson = null;
            string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(req =>
                {
                    capturedJson = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return OkEventStream(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
                }))
            {
                Timeout = System.TimeSpan.FromHours(1)
            };

            var client = new MeaiOpenAiChatClient(TestHttpSettings.Instance);
            await foreach (MEAI.ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "x") }))
            {
                break;
            }

            Assert.NotNull(capturedJson);
            StringAssert.Contains("stream_options", capturedJson);
            StringAssert.Contains("include_usage", capturedJson);
        }

        private sealed class TestHttpSettings : IOpenAiHttpSettings
        {
            public static readonly TestHttpSettings Instance = new();

            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 60;
            public int MaxTokens => 512;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public string ApiBaseUrl => "http://127.0.0.1:9";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "test-model";
            public IRequestHeaderProvider? HeaderProvider => null;
        }

        private sealed class DelegateHttpHandler : HttpMessageHandler
        {
            private readonly System.Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public DelegateHttpHandler(System.Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(_respond(request));
            }
        }

        private static HttpResponseMessage OkJson(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage OkEventStream(Stream stream)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
#endif
