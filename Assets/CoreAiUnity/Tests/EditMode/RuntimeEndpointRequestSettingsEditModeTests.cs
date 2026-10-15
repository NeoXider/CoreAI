#if COREAI_LLM
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class RuntimeEndpointRequestSettingsEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private JObject _body;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureHttpApi("https://global.test/v1", "", "global", 0.9f, maxTokens: 999,
                overrideTemperature: true);
            _settings.ConfigureReasoning(LlmReasoningEffort.Low, LlmReasoningDialect.AnthropicCompat);
            _settings.SetProviderBodyParameter("global_only", true);
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(new CaptureHandler(this));
        }

        [TearDown]
        public void TearDown()
        {
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
            Object.DestroyImmediate(_settings);
        }

        [TestCase(LlmEndpointKind.HttpOpenAi, false)]
        [TestCase(LlmEndpointKind.HttpOpenAi, true)]
        [TestCase(LlmEndpointKind.LlmUnity, false)]
        [TestCase(LlmEndpointKind.LlmUnity, true)]
        public async Task RuntimeEndpoint_DescriptorRequestSettingsReachWire(LlmEndpointKind kind, bool streaming)
        {
            LlmEndpointDescriptor descriptor = Descriptor(kind);
            ILlmClient client = await CreateClientAsync(descriptor);
            LlmCompletionRequest request = new() { UserPayload = "hello" };
            await CompleteAsync(client, request, streaming);

            Assert.AreEqual(0.35f, _body["temperature"]?.Value<float>());
            Assert.AreEqual("endpoint", _body["endpoint_only"]?.Value<string>());
            Assert.IsNull(_body["global_only"], "A runtime endpoint must own its provider body parameters.");
            Assert.AreEqual(0.35f, request.EffectiveTemperature);
            Assert.AreEqual(321, request.EffectiveMaxOutputTokens);
            Assert.AreEqual(LlmReasoningEffort.High, request.EffectiveReasoningEffort);
            if (kind == LlmEndpointKind.HttpOpenAi)
            {
                Assert.AreEqual("high", _body["reasoning_effort"]?.Value<string>());
                Assert.AreEqual(321, _body["max_completion_tokens"]?.Value<int>());
                Assert.IsNull(_body["max_tokens"]);
                Assert.AreEqual(LlmReasoningDialect.OpenAi, request.EffectiveReasoningDialect);
            }
            else
            {
                Assert.AreEqual(true, _body["enable_thinking"]?.Value<bool>());
                Assert.AreEqual(true, _body["chat_template_kwargs"]?["enable_thinking"]?.Value<bool>());
                Assert.AreEqual(2048, _body["thinking_budget"]?.Value<int>());
                Assert.AreEqual(321, _body["max_tokens"]?.Value<int>());
                Assert.IsNull(_body["thinking"], "Global Anthropic reasoning must not leak into a local endpoint.");
                Assert.AreEqual(LlmReasoningDialect.QwenTemplate, request.EffectiveReasoningDialect);
            }
        }

        [TestCase(false, 999)]
        [TestCase(true, 0)]
        public async Task RuntimeEndpoint_NullTemperatureAndZeroCapInheritUnlessExplicit(bool overrideCap, int expectedCap)
        {
            LlmEndpointDescriptor descriptor = Descriptor(LlmEndpointKind.HttpOpenAi);
            descriptor.Temperature = null;
            descriptor.MaxTokens = 0;
            descriptor.OverrideMaxTokens = overrideCap;
            descriptor.ExtraBodyJson = "";
            ILlmClient client = await CreateClientAsync(descriptor);
            await CompleteAsync(client, new LlmCompletionRequest { UserPayload = "hello" }, false);
            Assert.AreEqual(0.9f, _body["temperature"]?.Value<float>());
            if (overrideCap)
            {
                Assert.IsNull(_body["max_completion_tokens"]);
                Assert.IsNull(_body["max_tokens"]);
            }
            else
            {
                Assert.AreEqual(expectedCap, _body["max_completion_tokens"]?.Value<int>());
            }
        }

        [Test]
        public void LlmUnityEndpoint_ExplicitDialectSurvivesResolvedHostAddress()
        {
            LlmEndpointDescriptor descriptor = Descriptor(LlmEndpointKind.LlmUnity);
            descriptor.ReasoningDialect = LlmReasoningDialect.OpenRouter;
            OpenAiHttpOptions options = LlmEndpointClientFactory.BuildLlmUnityHttpOptions(
                descriptor, "key", _settings, 13333, "resolved-model");
            Assert.AreEqual(LlmReasoningDialect.OpenRouter, options.ReasoningDialect);
            Assert.AreEqual("http://localhost:13333/v1", options.ApiBaseUrl);
            Assert.AreEqual("resolved-model", options.Model);
            Assert.AreEqual("endpoint-model", descriptor.Model);
            Assert.AreEqual("https://endpoint.test/v1", descriptor.BaseUrl);
        }

        private async Task<ILlmClient> CreateClientAsync(LlmEndpointDescriptor descriptor)
        {
            if (descriptor.Kind == LlmEndpointKind.LlmUnity)
            {
                // WHY: exercise the production local HTTP path without starting a native model host.
                OpenAiHttpOptions options = LlmEndpointClientFactory.BuildLlmUnityHttpOptions(
                    descriptor, "", _settings, 13333, descriptor.Model);
                return new OpenAiChatLlmClient(options, _settings, GameLoggerUnscopedFallback.Instance, true);
            }

            LlmEndpointClientFactory factory = new(_settings, GameLoggerUnscopedFallback.Instance,
                readinessProbe: new ReadyProbe());
            LlmEndpointClientActivation activation = await factory.ActivateAsync(descriptor, "", CancellationToken.None);
            return activation.Client;
        }

        private static async Task CompleteAsync(ILlmClient client, LlmCompletionRequest request, bool streaming)
        {
            if (streaming)
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                {
                    Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
                }
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.IsTrue(result.Ok, result.Error);
            }
        }

        private static LlmEndpointDescriptor Descriptor(LlmEndpointKind kind)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = "endpoint",
                Kind = kind,
                BaseUrl = "https://endpoint.test/v1",
                Model = "endpoint-model",
                Temperature = 0.35f,
                MaxTokens = 321,
                ReasoningEffort = LlmReasoningEffort.High,
                ReasoningDialect = kind == LlmEndpointKind.LlmUnity ? LlmReasoningDialect.Auto : LlmReasoningDialect.OpenAi,
                ThinkingBudgetTokens = 2048,
                ExtraBodyJson = "{\"temperature\":0.8,\"reasoning_effort\":\"low\",\"endpoint_only\":\"endpoint\"}",
                ToolChannel = LlmToolChannel.Native
            };
        }

        private sealed class ReadyProbe : ILlmEndpointReadinessProbe
        {
            public Task<LlmEndpointReadinessResult> ProbeAsync(LlmEndpointReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmEndpointReadinessResult { IsReady = true, StatusCode = 200 });
            }
        }

        private sealed class CaptureHandler : HttpMessageHandler
        {
            private readonly RuntimeEndpointRequestSettingsEditModeTests _owner;

            public CaptureHandler(RuntimeEndpointRequestSettingsEditModeTests owner)
            {
                _owner = owner;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _owner._body = JObject.Parse(await request.Content.ReadAsStringAsync());
                bool streaming = _owner._body["stream"]?.Value<bool>() == true;
                string body = streaming
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
                };
            }
        }
    }
}
#endif
