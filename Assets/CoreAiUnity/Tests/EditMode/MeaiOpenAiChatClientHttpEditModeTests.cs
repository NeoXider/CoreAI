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
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>EditMode HTTP transport for <see cref="MeaiOpenAiChatClient"/> via <see cref="MeaiOpenAiChatClientEditorTestHooks"/> (no real network).</summary>
    public sealed class MeaiOpenAiChatClientHttpEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
        }

        [TearDown]
        public void TearDown()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
        }

        [Test]
        public void MeaiOpenAiChatClient_LivesInPortableCoreAssembly()
        {
            Assert.AreEqual("CoreAI.Core", typeof(MeaiOpenAiChatClient).Assembly.GetName().Name);
        }

        [TestCase("http://localhost:8080/v1/chat/completions", true)]
        [TestCase("https://LOCALHOST/v1/chat/completions", true)]
        [TestCase("http://127.0.0.1:8080/v1/chat/completions", true)]
        [TestCase("http://127.42.7.9/v1/chat/completions", true)]
        [TestCase("http://[::1]:8080/v1/chat/completions", true)]
        [TestCase("https://api.openai.com/v1/chat/completions", false)]
        [TestCase("https://llm.internal.example/v1/chat/completions", false)]
        [TestCase("not-a-url", false)]
        public void HttpTransport_BypassesProxyOnlyForLoopback(string url, bool expected)
        {
            Assert.AreEqual(expected, HttpClientOpenAiTransport.ShouldBypassProxy(url));
        }

        [Test]
        public async Task GetResponseAsync_Success_ReturnsAssistantText()
        {
            const string body =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello from mock\"}}]}";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(_ => OkJson(body))) { Timeout = System.TimeSpan.FromSeconds(30) };

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);
            MEAI.ChatResponse r =
                await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") });

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

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, null);

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

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);
            MEAI.ChatOptions options = new() { Temperature = 0.42f };
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, options);

            Assert.NotNull(capturedJson);
            StringAssert.Contains("\"temperature\"", capturedJson);
            StringAssert.Contains("0.42", capturedJson);
        }

        [Test]
        public async Task GetResponseAsync_ProviderDefaultReasoning_DoesNotAddThinkingFields()
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

            MeaiOpenAiChatClient client = new(new BodySettings());
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, null);

            Assert.NotNull(capturedJson);
            JObject request = JObject.Parse(capturedJson);
            Assert.IsNull(request["enable_thinking"]);
            Assert.IsNull(request["chat_template_kwargs"]);
            Assert.IsNull(request["thinking_budget"]);
        }

        [Test]
        public async Task GetResponseAsync_DisabledReasoning_AddsQwenThinkingControls()
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

            MeaiOpenAiChatClient client = new(new BodySettings
            {
                ReasoningModeValue = LlmReasoningMode.Disabled,
                ThinkingBudgetTokensValue = 128
            });
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, null);

            Assert.NotNull(capturedJson);
            JObject request = JObject.Parse(capturedJson);
            Assert.AreEqual(false, request["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(false, request["chat_template_kwargs"]?["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(128, request["thinking_budget"]?.Value<int>());
        }

        [Test]
        public async Task GetResponseAsync_ExtraBodyJson_MergesProviderFields()
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

            MeaiOpenAiChatClient client = new(new BodySettings
            {
                ExtraBodyJsonValue = "{\"top_k\":20,\"chat_template_kwargs\":{\"custom\":true}}",
                ReasoningModeValue = LlmReasoningMode.Enabled
            });
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, null);

            Assert.NotNull(capturedJson);
            JObject request = JObject.Parse(capturedJson);
            Assert.AreEqual(20, request["top_k"]?.Value<int>());
            Assert.AreEqual(true, request["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(true, request["chat_template_kwargs"]?["custom"]?.Value<bool>());
            Assert.AreEqual(true, request["chat_template_kwargs"]?["enable_thinking"]?.Value<bool>());
        }

        [Test]
        public async Task GetResponseAsync_429_MapsRetryAfterSeconds()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new DelegateHttpHandler(_ =>
                {
                    HttpResponseMessage r = new((HttpStatusCode)429)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                    r.Headers.TryAddWithoutValidation("Retry-After", "7");
                    return r;
                })) { Timeout = System.TimeSpan.FromSeconds(30) };

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);

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

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);
            List<string> parts = new();
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

            MeaiOpenAiChatClient client = new(TestHttpSettings.Instance);
            await foreach (MEAI.ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "x") }))
            {
                break;
            }

            Assert.NotNull(capturedJson);
            StringAssert.Contains("stream_options", capturedJson);
            StringAssert.Contains("include_usage", capturedJson);
        }

        [Test]
        public void MapHttpStatus_BareRateSubstring_IsNotRateLimited()
        {
            // "generate"/"moderate" contain "rate": these must classify by status, never as RateLimited.
            Assert.AreEqual(LlmErrorCode.BackendUnavailable,
                MeaiOpenAiChatClient.MapHttpStatusForTests(500,
                    "{\"error\":{\"message\":\"failed to generate a response\"}}", ""));
            Assert.AreEqual(LlmErrorCode.InvalidRequest,
                MeaiOpenAiChatClient.MapHttpStatusForTests(400,
                    "{\"error\":{\"message\":\"input was flagged as moderate risk\"}}", ""));
        }

        [Test]
        public void MapHttpStatus_ExplicitRateLimitSignals_AreRateLimited()
        {
            Assert.AreEqual(LlmErrorCode.RateLimited,
                MeaiOpenAiChatClient.MapHttpStatusForTests(429, "", ""));
            Assert.AreEqual(LlmErrorCode.RateLimited,
                MeaiOpenAiChatClient.MapHttpStatusForTests(503,
                    "{\"error\":{\"message\":\"Rate limit reached for requests\"}}", ""));
            Assert.AreEqual(LlmErrorCode.RateLimited,
                MeaiOpenAiChatClient.MapHttpStatusForTests(503,
                    "{\"error\":{\"code\":\"rate_limit_exceeded\"}}", ""));
            Assert.AreEqual(LlmErrorCode.RateLimited,
                MeaiOpenAiChatClient.MapHttpStatusForTests(503,
                    "{\"error\":{\"message\":\"Too many requests, slow down\"}}", ""));
        }

        [Test]
        public void MapHttpStatus_QuotaAndAuth_KeepTheirClassification()
        {
            Assert.AreEqual(LlmErrorCode.QuotaExceeded,
                MeaiOpenAiChatClient.MapHttpStatusForTests(402,
                    "{\"error\":{\"code\":\"quota_exceeded\"}}", ""));
            Assert.AreEqual(LlmErrorCode.AuthExpired,
                MeaiOpenAiChatClient.MapHttpStatusForTests(401, "", ""));
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

        private sealed class BodySettings : IOpenAiHttpSettings
        {
            public string ExtraBodyJsonValue { get; set; } = "";
            public LlmReasoningMode ReasoningModeValue { get; set; } = LlmReasoningMode.ProviderDefault;
            public int ThinkingBudgetTokensValue { get; set; }

            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 60;
            public int MaxTokens => 512;
            public string ExtraBodyJson => ExtraBodyJsonValue;
            public LlmReasoningMode ReasoningMode => ReasoningModeValue;
            public int ThinkingBudgetTokens => ThinkingBudgetTokensValue;
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
            StreamContent content = new(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>
    /// EditMode tests for <see cref="HttpClientOpenAiTransport.OpenSseResponseStreamAsync"/>:
    /// <see cref="OpenAiHttpPostRequest.TransportTimeoutSeconds"/> bounds ONLY the time-to-headers
    /// phase (a backend that accepts TCP but never sends headers used to hang until external
    /// cancellation), never the streaming body, and the error-body read honors the caller token.
    /// </summary>
    public sealed class HttpClientOpenAiTransportSseEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
        }

        [TearDown]
        public void TearDown()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
        }

        private static OpenAiHttpPostRequest SseRequest(int timeoutSeconds)
        {
            return new OpenAiHttpPostRequest
            {
                Url = "http://127.0.0.1:9/v1/chat/completions",
                JsonBody = "{}",
                AcceptEventStream = true,
                TransportTimeoutSeconds = timeoutSeconds
            };
        }

        [Test]
        public async Task OpenSse_BackendNeverSendsHeaders_CancelsAfterTransportTimeout()
        {
            // Backend accepts the request but never produces response headers.
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new AsyncDelegateHttpHandler(async (_, ct) =>
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    return null;
                })) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            HttpClientOpenAiTransport transport = new();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using OpenAiHttpSseOpenResult _ = await transport.OpenSseResponseStreamAsync(
                    SseRequest(1), System.Threading.CancellationToken.None);
            }
            catch (System.OperationCanceledException)
            {
                sw.Stop();
                Assert.Less(sw.ElapsedMilliseconds, 10000,
                    "Header timeout must fire near TransportTimeoutSeconds, not hang.");
                return;
            }

            Assert.Fail("Expected OperationCanceledException when no headers arrive within the transport timeout.");
        }

        [Test]
        public async Task OpenSse_HeaderTimeout_DoesNotBoundStreamingBody()
        {
            // Headers arrive immediately; the body's first bytes arrive AFTER the 1s header timeout.
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"late\"}}]}\n\n";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new AsyncDelegateHttpHandler((_, _) =>
                {
                    StreamContent content = new(new DelayedFirstReadStream(
                        Encoding.UTF8.GetBytes(sse), firstReadDelayMs: 1600));
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                })) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            HttpClientOpenAiTransport transport = new();
            using OpenAiHttpSseOpenResult open = await transport.OpenSseResponseStreamAsync(
                SseRequest(1), System.Threading.CancellationToken.None);

            Assert.AreEqual(200, open.StatusCode);
            Assert.NotNull(open.ResponseStream);
            using StreamReader reader = new(open.ResponseStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            Assert.AreEqual(sse, body,
                "The streaming body must stay readable after the header timeout has elapsed.");
        }

        [Test]
        public async Task OpenSse_ZeroTransportTimeout_DoesNotAddHeaderTimeout()
        {
            // 0/unset preserves the legacy behavior: only the caller token bounds the open phase.
            const string sse = "data: [DONE]\n\n";
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new AsyncDelegateHttpHandler(async (_, ct) =>
                {
                    await Task.Delay(300, ct);
                    StreamContent content = new(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                })) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            HttpClientOpenAiTransport transport = new();
            using OpenAiHttpSseOpenResult open = await transport.OpenSseResponseStreamAsync(
                SseRequest(0), System.Threading.CancellationToken.None);

            Assert.AreEqual(200, open.StatusCode);
        }

        [Test]
        public async Task OpenSse_ErrorBodyRead_HonorsCallerCancellation()
        {
            // Error status arrives, but the error body never does: the body read must observe
            // the caller token instead of hanging on an uncancellable ReadAsStringAsync.
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(
                new AsyncDelegateHttpHandler((_, _) =>
                {
                    HttpResponseMessage response = new(HttpStatusCode.InternalServerError)
                    {
                        Content = new StreamContent(new NeverEndingStream())
                    };
                    return Task.FromResult(response);
                })) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            HttpClientOpenAiTransport transport = new();
            using System.Threading.CancellationTokenSource cts = new(System.TimeSpan.FromMilliseconds(250));
            try
            {
                using OpenAiHttpSseOpenResult _ = await transport.OpenSseResponseStreamAsync(
                    SseRequest(0), cts.Token);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            Assert.Fail("Expected OperationCanceledException while reading a stalled error body.");
        }

        private sealed class AsyncDelegateHttpHandler : HttpMessageHandler
        {
            private readonly System.Func<HttpRequestMessage, System.Threading.CancellationToken,
                Task<HttpResponseMessage>> _respond;

            public AsyncDelegateHttpHandler(
                System.Func<HttpRequestMessage, System.Threading.CancellationToken,
                    Task<HttpResponseMessage>> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                System.Threading.CancellationToken cancellationToken)
            {
                return _respond(request, cancellationToken);
            }
        }

        /// <summary>Read-only stream whose FIRST read completes only after a delay (slow SSE body).</summary>
        private sealed class DelayedFirstReadStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _firstReadDelayMs;
            private int _pos;
            private bool _delayed;

            public DelayedFirstReadStream(byte[] data, int firstReadDelayMs)
            {
                _data = data;
                _firstReadDelayMs = firstReadDelayMs;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;

            public override long Position
            {
                get => _pos;
                set => throw new System.NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                System.Threading.CancellationToken cancellationToken)
            {
                if (!_delayed)
                {
                    _delayed = true;
                    await Task.Delay(_firstReadDelayMs, cancellationToken);
                }

                int n = System.Math.Min(count, _data.Length - _pos);
                System.Array.Copy(_data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadAsync(buffer, offset, count, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new System.NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new System.NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new System.NotSupportedException();
            }
        }

        /// <summary>Read-only stream that never delivers data; completes only via cancellation.</summary>
        private sealed class NeverEndingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => long.MaxValue;

            public override long Position
            {
                get => 0;
                set => throw new System.NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                System.Threading.CancellationToken cancellationToken)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadAsync(buffer, offset, count, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new System.NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new System.NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new System.NotSupportedException();
            }
        }
    }
}
#endif
