#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards OpenAI-compatible transport headers: WebGL player omits CORS-sensitive correlation headers
    /// while keeping Authorization and OpenRouter-specific headers (<see cref="MeaiOpenAiChatClient"/>).
    /// </summary>
    public sealed class MeaiOpenAiChatClientTransportHeadersEditModeTests
    {
        private const string OpenRouterChatUrl = "https://openrouter.ai/api/v1/chat/completions";

        [SetUp]
        public void SetUp()
        {
            LlmAuthContextRegistry.ClearProvider();
        }

        [TearDown]
        public void TearDown()
        {
            LlmAuthContextRegistry.ClearProvider();
        }

        private sealed class StubHttpSettings : IOpenAiHttpSettings
        {
            public IRequestHeaderProvider? HeaderProviderImpl { get; set; }

            public string ApiBaseUrl => "https://openrouter.ai/api/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "m";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => HeaderProviderImpl;
        }

        private sealed class StubAuth : ILlmAuthContextProvider
        {
            public string GetAuthorizationHeader()
            {
                return "";
            }

            public string TenantId => "tenant-a";
            public string UserId => "user-b";
            public string SessionId => "sess-c";
        }

        private sealed class StubHeaderProvider : IRequestHeaderProvider
        {
            private readonly IReadOnlyList<KeyValuePair<string, string>> _extra;
            private readonly string _idem;
            private readonly string _reqId;

            public StubHeaderProvider(
                IReadOnlyList<KeyValuePair<string, string>> extra,
                string idempotencyKey = "hp-idem",
                string requestId = "hp-req-id")
            {
                _extra = extra;
                _idem = idempotencyKey;
                _reqId = requestId;
            }

            public IReadOnlyList<KeyValuePair<string, string>> GetHeaders()
            {
                return _extra;
            }

            public string IdempotencyKey => _idem;
            public string RequestId => _reqId;
        }

        private static string? FirstHeaderValue(IReadOnlyList<KeyValuePair<string, string>> list, string name)
        {
            foreach (KeyValuePair<string, string> kv in list)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            return null;
        }

        private static bool HasHeader(IReadOnlyList<KeyValuePair<string, string>> list, string name)
        {
            return FirstHeaderValue(list, name) != null;
        }

        [Test]
        public void BuildTransportHeaders_OmitCorrelationFalse_IncludesContextAuthAndCorrelationHeaders()
        {
            LlmAuthContextRegistry.SetProvider(new StubAuth());
            StubHttpSettings settings = new()
            {
                HeaderProviderImpl = new StubHeaderProvider(
                    new List<KeyValuePair<string, string>> { new("X-Custom-Ok", "1") })
            };

            using (LlmRequestContext.Begin("Teacher", "trace-xyz", "idem-abc"))
            {
                List<KeyValuePair<string, string>> list = MeaiOpenAiChatClient.BuildTransportHeadersForTests(
                    OpenRouterChatUrl,
                    false,
                    false,
                    "Bearer sk-test",
                    settings,
                    NullLog.Instance);

                Assert.IsTrue(HasHeader(list, OpenAiHttpConstants.HttpRefererHeaderName));
                Assert.AreEqual("Bearer sk-test", FirstHeaderValue(list, "Authorization"));
                Assert.AreEqual("idem-abc", FirstHeaderValue(list, "Idempotency-Key"));
                Assert.AreEqual("trace-xyz", FirstHeaderValue(list, "X-Request-Id"));
                Assert.AreEqual("Teacher", FirstHeaderValue(list, "X-Coreai-Role"));
                Assert.AreEqual("tenant-a", FirstHeaderValue(list, "X-Tenant-Id"));
                Assert.AreEqual("user-b", FirstHeaderValue(list, "X-User-Id"));
                Assert.AreEqual("sess-c", FirstHeaderValue(list, "X-Session-Id"));
                Assert.AreEqual("1", FirstHeaderValue(list, "X-Custom-Ok"));
                Assert.That(
                    list.Count(kv => string.Equals(kv.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)),
                    Is.EqualTo(1),
                    "Only LlmRequestContext supplies Idempotency-Key; HeaderProvider must not duplicate when already present.");
            }
        }

        [Test]
        public void BuildTransportHeaders_OmitCorrelationTrue_StripsCorrelation_KeepsAuthAndOpenRouterHeaders()
        {
            LlmAuthContextRegistry.SetProvider(new StubAuth());
            StubHttpSettings settings = new() { HeaderProviderImpl = null };

            using (LlmRequestContext.Begin("Teacher", "trace-xyz", "idem-abc"))
            {
                List<KeyValuePair<string, string>> list = MeaiOpenAiChatClient.BuildTransportHeadersForTests(
                    OpenRouterChatUrl,
                    false,
                    true,
                    "Bearer sk-test",
                    settings,
                    NullLog.Instance);

                Assert.IsTrue(HasHeader(list, OpenAiHttpConstants.HttpRefererHeaderName));
                Assert.IsTrue(HasHeader(list, "X-Title"));
                Assert.AreEqual("Bearer sk-test", FirstHeaderValue(list, "Authorization"));
                Assert.IsFalse(HasHeader(list, "Idempotency-Key"));
                Assert.IsFalse(HasHeader(list, "X-Request-Id"));
                Assert.IsFalse(HasHeader(list, "X-Coreai-Role"));
                Assert.IsFalse(HasHeader(list, "X-Tenant-Id"));
                Assert.IsFalse(HasHeader(list, "X-User-Id"));
                Assert.IsFalse(HasHeader(list, "X-Session-Id"));
            }
        }

        [Test]
        public void BuildTransportHeaders_OmitCorrelationTrue_FiltersSensitiveNamesFromHeaderProviderGetHeaders()
        {
            StubHttpSettings settings = new()
            {
                HeaderProviderImpl = new StubHeaderProvider(
                    new List<KeyValuePair<string, string>>
                    {
                        new("X-Request-Id", "should-drop"),
                        new("Idempotency-Key", "should-drop-2"),
                        new("X-Tenant-Id", "should-drop-3"),
                        new("X-Client-Keep", "ok-value")
                    },
                    "hp-idem",
                    "hp-req")
            };

            List<KeyValuePair<string, string>> list = MeaiOpenAiChatClient.BuildTransportHeadersForTests(
                "https://api.openai.com/v1/chat/completions",
                false,
                true,
                null,
                settings,
                NullLog.Instance);

            Assert.IsFalse(HasHeader(list, "X-Request-Id"));
            Assert.IsFalse(HasHeader(list, "Idempotency-Key"));
            Assert.IsFalse(HasHeader(list, "X-Tenant-Id"));
            Assert.AreEqual("ok-value", FirstHeaderValue(list, "X-Client-Keep"));
            Assert.IsFalse(HasHeader(list, "HTTP-Referer"));
        }

        [Test]
        public void BuildTransportHeaders_OmitCorrelationTrue_DoesNotAppendHeaderProviderIdempotencyOrRequestId()
        {
            StubHttpSettings settings = new()
            {
                HeaderProviderImpl = new StubHeaderProvider(
                    new List<KeyValuePair<string, string>>(),
                    "from-hp-only",
                    "from-hp-req-only")
            };

            List<KeyValuePair<string, string>> list = MeaiOpenAiChatClient.BuildTransportHeadersForTests(
                "https://api.example/v1/chat/completions",
                false,
                true,
                "",
                settings,
                NullLog.Instance);

            Assert.IsFalse(HasHeader(list, "Idempotency-Key"));
            Assert.IsFalse(HasHeader(list, "X-Request-Id"));
        }

        [Test]
        public void BuildTransportHeaders_OmitCorrelationFalse_AppendsHeaderProviderIdempotencyWhenMissing()
        {
            StubHttpSettings settings = new()
            {
                HeaderProviderImpl = new StubHeaderProvider(
                    new List<KeyValuePair<string, string>>(),
                    "from-hp-only",
                    "from-hp-req-only")
            };

            List<KeyValuePair<string, string>> list = MeaiOpenAiChatClient.BuildTransportHeadersForTests(
                "https://api.example/v1/chat/completions",
                false,
                false,
                "",
                settings,
                NullLog.Instance);

            Assert.IsTrue(HasHeader(list, "Idempotency-Key"));
            Assert.That(
                list.Count(kv => string.Equals(kv.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)),
                Is.EqualTo(1));
            Assert.AreEqual("from-hp-only", FirstHeaderValue(list, "Idempotency-Key"));
            Assert.AreEqual("from-hp-req-only", FirstHeaderValue(list, "X-Request-Id"));
        }
    }
}
#endif