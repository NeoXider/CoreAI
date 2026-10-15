#if COREAI_LLM
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace CoreAI.Tests.EditMode
{
    public sealed class RequestShapingEditModeTests
    {
        [Test]
        public async Task LegacyReasoning_RequestBodyMatchesGoldenBytes(
            [Values(false, true)] bool streaming,
            [Values(LlmReasoningMode.ProviderDefault, LlmReasoningMode.Enabled, LlmReasoningMode.Disabled)] LlmReasoningMode mode,
            [Values(0, 2048)] int budget)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ApiBaseUrl = "https://api.openai.com/v1";
            settings.ReasoningMode = mode;
            settings.ThinkingBudgetTokens = budget;
            string actual = await CaptureAsync(streaming, settings, new ChatOptions());
            string expected = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]";
            if (streaming)
            {
                expected += ",\"stream\":true,\"stream_options\":{\"include_usage\":true}";
            }
            if (mode != LlmReasoningMode.ProviderDefault)
            {
                string enabled = mode == LlmReasoningMode.Enabled ? "true" : "false";
                expected += ",\"enable_thinking\":" + enabled + ",\"chat_template_kwargs\":{\"enable_thinking\":" + enabled + "}";
                if (budget > 0)
                {
                    expected += ",\"thinking_budget\":" + budget;
                }
            }
            expected += "}";
            Assert.AreEqual(expected, actual, "Legacy controls must retain their complete pre-migration request bytes.");
        }

        [Test]
        public async Task Reasoning_DialectsShapeActualRequestBodies(
            [Values(false, true)] bool streaming,
            [Values(LlmReasoningDialect.OpenAi, LlmReasoningDialect.OpenRouter,
                LlmReasoningDialect.QwenTemplate, LlmReasoningDialect.AnthropicCompat,
                LlmReasoningDialect.GeminiCompat, LlmReasoningDialect.PassthroughEffort,
                LlmReasoningDialect.Off)] LlmReasoningDialect dialect)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = dialect;
            ChatOptions options = Reasoning(LlmReasoningEffort.High, 4096);
            options.MaxOutputTokens = 777;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            switch (dialect)
            {
                case LlmReasoningDialect.OpenAi:
                    Assert.AreEqual("high", body["reasoning_effort"]?.Value<string>());
                    Assert.AreEqual(777, body["max_completion_tokens"]?.Value<int>());
                    Assert.IsNull(body["max_tokens"]);
                    break;
                case LlmReasoningDialect.OpenRouter:
                    Assert.AreEqual("high", body["reasoning"]?["effort"]?.Value<string>());
                    Assert.AreEqual(4096, body["reasoning"]?["max_tokens"]?.Value<int>());
                    break;
                case LlmReasoningDialect.QwenTemplate:
                    Assert.AreEqual(true, body["enable_thinking"]?.Value<bool>());
                    Assert.AreEqual(true, body["chat_template_kwargs"]?["enable_thinking"]?.Value<bool>());
                    Assert.AreEqual(4096, body["thinking_budget"]?.Value<int>());
                    break;
                case LlmReasoningDialect.AnthropicCompat:
                    Assert.AreEqual("enabled", body["thinking"]?["type"]?.Value<string>());
                    Assert.AreEqual(4096, body["thinking"]?["budget_tokens"]?.Value<int>());
                    break;
                case LlmReasoningDialect.GeminiCompat:
                case LlmReasoningDialect.PassthroughEffort:
                    Assert.AreEqual("high", body["reasoning_effort"]?.Value<string>());
                    break;
                case LlmReasoningDialect.Off:
                    Assert.IsNull(body["reasoning_effort"]);
                    Assert.IsNull(body["reasoning"]);
                    Assert.IsNull(body["enable_thinking"]);
                    Assert.IsNull(body["thinking"]);
                    break;
            }
        }

        [Test]
        public async Task Reasoning_PerCallOverridesEndpointAndRawProviderFields(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.OpenAi;
            settings.ReasoningEffort = LlmReasoningEffort.Low;
            settings.ExtraBodyJson = "{\"reasoning_effort\":\"medium\",\"max_tokens\":99,\"max_completion_tokens\":100}";
            ChatOptions options = Reasoning(LlmReasoningEffort.XHigh);
            options.MaxOutputTokens = 256;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            Assert.AreEqual("xhigh", body["reasoning_effort"]?.Value<string>());
            Assert.AreEqual(256, body["max_completion_tokens"]?.Value<int>());
            Assert.IsNull(body["max_tokens"]);
        }

        [Test]
        public async Task Reasoning_DefaultInheritsEndpointEffortAndPerCallBudget(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.OpenRouter;
            settings.ReasoningEffort = LlmReasoningEffort.Low;
            settings.ThinkingBudgetTokens = 2048;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings,
                Reasoning(LlmReasoningEffort.Default, 3456)));
            Assert.AreEqual("low", body["reasoning"]?["effort"]?.Value<string>());
            Assert.AreEqual(3456, body["reasoning"]?["max_tokens"]?.Value<int>());
        }

        [Test]
        public async Task Reasoning_EndpointEffortAndBudgetReachActualBody(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.OpenRouter;
            settings.ReasoningEffort = LlmReasoningEffort.Medium;
            settings.ThinkingBudgetTokens = 2222;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, new ChatOptions()));
            Assert.AreEqual("medium", body["reasoning"]?["effort"]?.Value<string>());
            Assert.AreEqual(2222, body["reasoning"]?["max_tokens"]?.Value<int>());
        }

        [Test]
        public async Task Reasoning_OffSuppressesLegacyAndTypedControls(
            [Values(false, true)] bool streaming,
            [Values(false, true)] bool typed)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.Off;
            settings.ReasoningMode = LlmReasoningMode.Enabled;
            settings.ThinkingBudgetTokens = 2222;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings,
                typed ? Reasoning(LlmReasoningEffort.High) : new ChatOptions()));
            Assert.IsNull(body["reasoning_effort"]);
            Assert.IsNull(body["enable_thinking"]);
            Assert.IsNull(body["chat_template_kwargs"]);
            Assert.IsNull(body["thinking_budget"]);
            Assert.IsNull(body["thinking"]);
        }

        [Test]
        public async Task Reasoning_AnthropicNoneDisablesThinkingWithoutBudget(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.AnthropicCompat;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings,
                Reasoning(LlmReasoningEffort.None, 4096)));
            Assert.AreEqual("disabled", body["thinking"]?["type"]?.Value<string>());
            Assert.IsNull(body["thinking"]?["budget_tokens"]);
        }

        [Test]
        public async Task Reasoning_OpenAiTypedEffortPreservesUnrelatedTemplateParameters(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.OpenAi;
            settings.SetProviderBodyParameter("chat_template_kwargs", new JObject { ["custom_template"] = "keep" });
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, Reasoning(LlmReasoningEffort.Low)));
            Assert.AreEqual("low", body["reasoning_effort"]?.Value<string>());
            Assert.AreEqual("keep", body["chat_template_kwargs"]?["custom_template"]?.Value<string>());
            Assert.IsNull(body["chat_template_kwargs"]?["enable_thinking"]);
        }

        [Test]
        public async Task Reasoning_AutoChoosesOpenAiOnlyForTypedEffort(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ApiBaseUrl = "https://api.openai.com/v1";
            settings.ReasoningMode = LlmReasoningMode.Disabled;
            ChatOptions options = Reasoning(LlmReasoningEffort.High);
            options.MaxOutputTokens = 777;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            Assert.AreEqual("high", body["reasoning_effort"]?.Value<string>());
            Assert.AreEqual(777, body["max_completion_tokens"]?.Value<int>());
            Assert.IsNull(body["enable_thinking"]);
            Assert.IsNull(body["chat_template_kwargs"]);
        }

        [Test]
        public async Task Reasoning_NoneDisablesQwenInBothTemplateLocations(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.QwenTemplate;
            settings.ExtraBodyJson = "{\"enable_thinking\":true,\"chat_template_kwargs\":{\"enable_thinking\":true,\"other\":1}}";
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, Reasoning(LlmReasoningEffort.None)));
            Assert.AreEqual(false, body["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(false, body["chat_template_kwargs"]?["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(1, body["chat_template_kwargs"]?["other"]?.Value<int>());
        }

        [Test]
        public async Task Reasoning_AnthropicDefaultBudgetAndMinimumReachBody(
            [Values(false, true)] bool streaming,
            [Values(LlmReasoningEffort.Minimal, LlmReasoningEffort.Low, LlmReasoningEffort.Medium,
                LlmReasoningEffort.High, LlmReasoningEffort.XHigh)] LlmReasoningEffort effort,
            [Values(false, true)] bool explicitSmallBudget)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = LlmReasoningDialect.AnthropicCompat;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings,
                Reasoning(effort, explicitSmallBudget ? 10 : (int?)null)));
            int expected = explicitSmallBudget ? 1024 : effort switch
            {
                LlmReasoningEffort.Medium => 4096,
                LlmReasoningEffort.High => 8192,
                LlmReasoningEffort.XHigh => 16384,
                _ => 1024
            };
            Assert.AreEqual(expected, body["thinking"]?["budget_tokens"]?.Value<int>());
        }

        [Test]
        public async Task Reasoning_GeminiAliasesAndPassthroughRetainEffort(
            [Values(false, true)] bool streaming,
            [Values(LlmReasoningEffort.None, LlmReasoningEffort.Minimal, LlmReasoningEffort.Low,
                LlmReasoningEffort.Medium, LlmReasoningEffort.High, LlmReasoningEffort.XHigh)] LlmReasoningEffort effort,
            [Values(LlmReasoningDialect.GeminiCompat, LlmReasoningDialect.PassthroughEffort)] LlmReasoningDialect dialect)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = dialect;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, Reasoning(effort)));
            string expected = effort.ToString().ToLowerInvariant();
            if (dialect == LlmReasoningDialect.GeminiCompat)
            {
                expected = effort == LlmReasoningEffort.Minimal ? "low" : effort == LlmReasoningEffort.XHigh ? "high" : expected;
            }
            Assert.AreEqual(expected, body["reasoning_effort"]?.Value<string>());
        }

        [TestCase("https://api.openai.com/v1", LlmReasoningDialect.OpenAi)]
        [TestCase("https://team.openai.azure.com/openai", LlmReasoningDialect.OpenAi)]
        [TestCase("https://openrouter.ai/api/v1", LlmReasoningDialect.OpenRouter)]
        [TestCase("https://generativelanguage.googleapis.com/v1beta/openai", LlmReasoningDialect.GeminiCompat)]
        [TestCase("https://api.anthropic.com/v1", LlmReasoningDialect.AnthropicCompat)]
        [TestCase("https://dashscope.aliyuncs.com/compatible-mode/v1", LlmReasoningDialect.QwenTemplate)]
        [TestCase("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", LlmReasoningDialect.QwenTemplate)]
        [TestCase("http://llmunity:13333/v1", LlmReasoningDialect.QwenTemplate)]
        [TestCase("https://custom.example/v1", LlmReasoningDialect.PassthroughEffort)]
        public void Reasoning_AutoResolverRecognizesProviders(string url, LlmReasoningDialect expected)
        {
            Assert.AreEqual(expected, LlmReasoningDialectResolver.Resolve(url, LlmReasoningDialect.Auto, LlmReasoningEffort.High));
            Assert.AreEqual(LlmReasoningDialect.Off, LlmReasoningDialectResolver.Resolve(url, LlmReasoningDialect.Off, LlmReasoningEffort.High));
        }

        [Test]
        public void Reasoning_AutoUnknownLegacyUsesQwenTemplate()
        {
            Assert.AreEqual(LlmReasoningDialect.QwenTemplate,
                LlmReasoningDialectResolver.Resolve("https://custom.example/v1", LlmReasoningDialect.Auto,
                    LlmReasoningEffort.Default, LlmReasoningMode.Enabled));
        }

        [Test]
        public async Task Sampling_TypedValuesOverrideRawAndReachActualBody(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ExtraBodyJson = "{\"temperature\":1.0,\"max_tokens\":1,\"top_p\":1,\"seed\":1,\"stop\":[\"wrong\"],\"frequency_penalty\":0,\"presence_penalty\":0,\"response_format\":{\"type\":\"text\"}}";
            ChatOptions options = new()
            {
                Temperature = 0.3f,
                MaxOutputTokens = 654,
                TopP = 0.8f,
                Seed = 42,
                StopSequences = new[] { "END", "STOP" },
                FrequencyPenalty = 0.2f,
                PresencePenalty = -0.4f,
                ResponseFormat = ChatResponseFormat.Json
            };
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            Assert.That(body["temperature"]?.Value<double>(), Is.EqualTo(0.3).Within(0.00001));
            Assert.AreEqual(654, body["max_tokens"]?.Value<int>());
            Assert.That(body["top_p"]?.Value<double>(), Is.EqualTo(0.8).Within(0.00001));
            Assert.AreEqual(42, body["seed"]?.Value<int>());
            CollectionAssert.AreEqual(new[] { "END", "STOP" }, body["stop"]?.ToObject<string[]>());
            Assert.That(body["frequency_penalty"]?.Value<double>(), Is.EqualTo(0.2).Within(0.00001));
            Assert.That(body["presence_penalty"]?.Value<double>(), Is.EqualTo(-0.4).Within(0.00001));
            Assert.AreEqual("json_object", body["response_format"]?["type"]?.Value<string>());
        }

        [Test]
        public async Task Sampling_JsonSchemaResponseFormatReachesActualBody(
            [Values(false, true)] bool streaming)
        {
            using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}}}");
            ChatOptions options = new()
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "result", "Structured result")
            };
            JObject body = JObject.Parse(await CaptureAsync(streaming, Settings(), options));
            Assert.AreEqual("json_schema", body["response_format"]?["type"]?.Value<string>());
            Assert.AreEqual("result", body["response_format"]?["json_schema"]?["name"]?.Value<string>());
            Assert.AreEqual("boolean", body["response_format"]?["json_schema"]?["schema"]?["properties"]?["ok"]?["type"]?.Value<string>());
        }

        [Test]
        public async Task Sampling_RawTemplateParametersRemainWritableWithoutTypedReasoning(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.SetProviderBodyParameter("chat_template_kwargs", new JObject { ["custom_template"] = "keep" });
            settings.SetProviderBodyParameter("enable_thinking", true);
            settings.SetProviderBodyParameter("thinking_budget", 3456);
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, new ChatOptions()));
            Assert.AreEqual("keep", body["chat_template_kwargs"]?["custom_template"]?.Value<string>());
            Assert.AreEqual(true, body["enable_thinking"]?.Value<bool>());
            Assert.AreEqual(3456, body["thinking_budget"]?.Value<int>());
        }

        [Test]
        public async Task EndpointTemperature_PointOneAndTokenCapReachActualBody(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.Temperature = 0.1f;
            settings.OverrideTemperature = true;
            settings.MaxTokens = 987;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, new ChatOptions()));
            Assert.That(body["temperature"]?.Value<double>(), Is.EqualTo(0.1).Within(0.00001));
            Assert.AreEqual(987, body["max_tokens"]?.Value<int>());
        }

        [Test]
        public async Task TokenCap_ExplicitUnlimitedSuppressesRawProviderCaps(
            [Values(false, true)] bool streaming,
            [Values(false, true)] bool resolvedNull)
        {
            OpenAiHttpOptions settings = Settings();
            settings.MaxTokens = 999;
            settings.ExtraBodyJson = "{\"max_tokens\":100,\"max_completion_tokens\":200}";
            ChatOptions options = new()
            {
                MaxOutputTokens = resolvedNull ? null : (int?)0,
                AdditionalProperties = new AdditionalPropertiesDictionary()
            };
            if (resolvedNull)
            {
                options.AdditionalProperties["coreai.max_tokens_resolved"] = true;
            }

            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            Assert.IsNull(body["max_tokens"], "An explicit unlimited request must suppress lower-priority token caps.");
            Assert.IsNull(body["max_completion_tokens"], "The alternate completion cap must not survive explicit unlimited.");
        }

        [Test]
        public async Task TokenCap_TypedPositiveCapSuppressesAlternateRawCap(
            [Values(false, true)] bool streaming,
            [Values(LlmReasoningDialect.OpenAi, LlmReasoningDialect.PassthroughEffort)] LlmReasoningDialect dialect)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ReasoningDialect = dialect;
            settings.ExtraBodyJson = "{\"max_tokens\":100,\"max_completion_tokens\":200}";
            ChatOptions options = Reasoning(LlmReasoningEffort.Low);
            options.MaxOutputTokens = 555;
            JObject body = JObject.Parse(await CaptureAsync(streaming, settings, options));
            string expectedKey = dialect == LlmReasoningDialect.OpenAi ? "max_completion_tokens" : "max_tokens";
            string alternateKey = dialect == LlmReasoningDialect.OpenAi ? "max_tokens" : "max_completion_tokens";
            Assert.AreEqual(555, body[expectedKey]?.Value<int>());
            Assert.IsNull(body[alternateKey], "Only the effective dialect's typed cap may remain in the request.");
        }

        [Test]
        public async Task TypedRawConflict_WarnsOnceAcrossRepeatedRequests(
            [Values(false, true)] bool streaming)
        {
            OpenAiHttpOptions settings = Settings();
            settings.ExtraBodyJson = "{\"top_p\":1}";
            CaptureLog log = new();
            CaptureTransport transport = new();
            using MeaiOpenAiChatClient client = new(settings, transport, log);
            ChatOptions options = new() { TopP = 0.4f };
            await ExecuteAsync(client, streaming, options);
            int firstWarnings = log.Warnings.Count;
            await ExecuteAsync(client, streaming, options);
            Assert.AreEqual(1, firstWarnings, "A typed/raw collision must emit one warning.");
            Assert.AreEqual(firstWarnings, log.Warnings.Count, "The same collision must not warn on every request.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LegacyHttpSettings_UnsetOverridesOmitNumericDefaultsButExplicitOptionsReachWire(bool streaming)
        {
            CaptureTransport transport = new();
            using MeaiOpenAiChatClient client = new(new LegacyHttpSettings(), transport);
            await ExecuteAsync(client, streaming, null);
            JObject inherited = JObject.Parse(transport.Request.JsonBody);
            Assert.IsNull(inherited["temperature"], "A legacy numeric temperature does not opt into an override.");
            Assert.IsNull(inherited["max_tokens"], "A legacy numeric token cap does not opt into an override.");

            await ExecuteAsync(client, streaming, new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 42 });
            JObject explicitOptions = JObject.Parse(transport.Request.JsonBody);
            Assert.AreEqual(0.1f, (float)explicitOptions["temperature"]);
            Assert.AreEqual(42, (int)explicitOptions["max_tokens"]);
        }

        private sealed class LegacyHttpSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://legacy.test/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "m";
            public float Temperature => 0.7f;
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 512;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider HeaderProvider => null;
        }

        private static OpenAiHttpOptions Settings()
        {
            return new OpenAiHttpOptions
            {
                ApiBaseUrl = "https://custom.example/v1",
                Model = "m",
                OverrideTemperature = false,
                MaxTokens = 0,
                LogLlmInput = false,
                LogLlmOutput = false
            };
        }

        private static ChatOptions Reasoning(LlmReasoningEffort effort, int? budget = null)
        {
            return new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["coreai.reasoning"] = new LlmReasoningOptions(effort, budget)
                }
            };
        }

        private static async Task<string> CaptureAsync(bool streaming, OpenAiHttpOptions settings, ChatOptions options)
        {
            CaptureTransport transport = new();
            using MeaiOpenAiChatClient client = new(settings, transport);
            await ExecuteAsync(client, streaming, options);
            Assert.IsNotNull(transport.Request);
            Assert.AreEqual(streaming, transport.Request.AcceptEventStream);
            return transport.Request.JsonBody;
        }

        private static async Task ExecuteAsync(MeaiOpenAiChatClient client, bool streaming, ChatOptions options)
        {
            ChatMessage[] messages = { new(ChatRole.User, "hello") };
            if (streaming)
            {
                await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
                {
                    Assert.IsNotNull(update);
                }
            }
            else
            {
                await client.GetResponseAsync(messages, options);
            }
        }

        private sealed class CaptureLog : ILog
        {
            public List<string> Warnings { get; } = new();
            public void Debug(string message, string tag = null) { }
            public void Info(string message, string tag = null) { }
            public void Warn(string message, string tag = null) { Warnings.Add(message); }
            public void Error(string message, string tag = null) { }
        }

        private sealed class CaptureTransport : IOpenAiHttpTransport
        {
            public OpenAiHttpPostRequest Request { get; private set; }
            public string DebugLabel => "RequestShapingCapture";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                Request = request;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                Request = request;
                const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        ["Content-Type"] = new[] { "text/event-stream" }
                    }
                };
                return Task.FromResult(result.WithRawStream(new MemoryStream(Encoding.UTF8.GetBytes(sse))));
            }
        }
    }
}
#endif
