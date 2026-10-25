using System;
using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmConfigurationDefectsEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private readonly List<HubSettingsPage> _pages = new List<HubSettingsPage>();

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (HubSettingsPage page in _pages)
            {
                page.OnDestroyed();
            }

            _pages.Clear();
            Object.DestroyImmediate(_settings);
        }

        private HubSettingsPage Own(HubSettingsPage page)
        {
            _pages.Add(page);
            return page;
        }

#if COREAI_LLM
        [TestCase(false)]
        [TestCase(true)]
        public void AssetAdapters_ForwardNullableOverridesAndTypedReasoning(bool enabled)
        {
            _settings.ConfigureHttpApi("https://primary.test/v1", "", "primary", 0.3f,
                maxTokens: enabled ? 333 : 0, overrideTemperature: enabled);
            _settings.ConfigureReasoning(LlmReasoningEffort.High, LlmReasoningDialect.OpenAi);
            IOpenAiHttpSettings secondary = new LlmPipelineInstaller.SecondarySettingsAdapter(_settings);
            IOpenAiHttpSettings managed = new LlmPipelineInstaller.ServerManagedCoreSettingsAdapter(_settings);
            IOpenAiHttpSettings local = new LlmUnityServerHttpSettings(_settings, 13333, "local");
            foreach (IOpenAiHttpSettings adapter in new[] { secondary, managed, local })
            {
                Assert.AreEqual(enabled ? (float?)0.3f : null, adapter.TemperatureOverride);
                Assert.AreEqual(enabled ? (int?)333 : null, adapter.MaxTokensOverride);
            }
            Assert.AreEqual(LlmReasoningEffort.High, managed.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, managed.ReasoningDialect);
            Assert.AreEqual(LlmReasoningEffort.High, local.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, local.ReasoningDialect);
            _settings.ConfigureReasoning(LlmReasoningEffort.High);
            Assert.AreEqual(LlmReasoningDialect.QwenTemplate, local.ReasoningDialect);
            ILlmClient primary = new OpenAiChatLlmClient(_settings, supportsNativeToolCalling: true);
            LlmCompletionRequest request = new();
            primary.ResolveRequestSettings(request);
            Assert.AreEqual(LlmReasoningEffort.High, request.EffectiveReasoningEffort);
            Assert.AreEqual(enabled ? (float?)0.3f : null, request.EffectiveTemperature);
            Assert.AreEqual(enabled ? (int?)333 : null, request.EffectiveMaxOutputTokens);
        }

        [Test]
        public async Task SecondaryEndpoint_RequestBodyUsesOwnDialectAndProviderFields()
        {
            _settings.ConfigureHttpApi("https://primary.test/v1", "", "primary", 0.3f,
                maxTokens: 333, overrideTemperature: true);
            _settings.ConfigureReasoning(LlmReasoningEffort.High, LlmReasoningDialect.QwenTemplate);
            _settings.SetProviderBodyParameter("primary_only", new JValue(true));
            _settings.ConfigureFallbackBackend(true, "https://fallback.test/v1", "fallback");
            _settings.ConfigureFallbackReasoning(LlmReasoningEffort.Low, LlmReasoningDialect.OpenAi,
                extraBody: "{\"fallback_only\":true}");
            BodyCaptureHandler handler = new();
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () => new HttpClient(handler, false);
            try
            {
                ILlmClient secondary = LlmPipelineInstaller.BuildSecondaryHttpClient(_settings, null);
                LlmCompletionResult result = await secondary.CompleteAsync(new LlmCompletionRequest
                {
                    UserPayload = "hello"
                });
                Assert.IsTrue(result.Ok, result.Error);
                JObject body = JObject.Parse(handler.Body);
                Assert.AreEqual("fallback", body["model"]?.Value<string>());
                Assert.AreEqual("low", body["reasoning_effort"]?.Value<string>());
                Assert.AreEqual(0.3f, body["temperature"]?.Value<float>());
                Assert.AreEqual(333, body["max_completion_tokens"]?.Value<int>());
                Assert.IsTrue(body["fallback_only"]?.Value<bool>());
                Assert.IsNull(body["primary_only"]);
                Assert.IsNull(body["enable_thinking"]);
                Assert.IsNull(body["max_tokens"]);
            }
            finally
            {
                MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
                handler.Dispose();
            }
        }

        private sealed class BodyCaptureHandler : HttpMessageHandler
        {
            public string Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Body = await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")
                };
            }
        }

        [TestCase(0f, 0)]
        [TestCase(0.5f, 1)]
        [TestCase(600f, 600)]
        [TestCase(1.5f, 2)]
        public void BuildHttpOptions_UsesExplicitTotalTimeoutWithoutHiddenCap(float ceiling, int expected)
        {
            _settings.SetOrchestratorTimeoutSeconds(ceiling);
            OpenAiHttpOptions options = LlmEndpointClientFactory.BuildHttpOptions(Descriptor(), "", _settings);
            Assert.AreEqual(expected, options.RequestTimeoutSeconds);
            Assert.AreEqual(_settings.EffectiveHttpRequestTimeoutSeconds, options.RequestTimeoutSeconds);
        }

        [Test]
        public void BuildHttpOptions_PreservesEndpointTimeoutOverrides()
        {
            LlmEndpointDescriptor first = Descriptor();
            first.RequestTimeoutSeconds = 240;
            first.ResponseWaitTimeoutSeconds = 0;
            OpenAiHttpOptions options = LlmEndpointClientFactory.BuildHttpOptions(first, "", _settings);
            Assert.AreEqual(240, options.RequestTimeoutSeconds);
            Assert.AreEqual(0, options.ResponseWaitTimeoutSeconds);

            LlmEndpointDescriptor second = Descriptor();
            OpenAiHttpOptions other = LlmEndpointClientFactory.BuildHttpOptions(second, "", _settings);
            Assert.AreEqual(0, other.RequestTimeoutSeconds);
            Assert.AreEqual(180, other.ResponseWaitTimeoutSeconds);
        }

        [TestCase(0f)]
        [TestCase(73f)]
        public void BuildHttpOptions_InheritsGlobalResponseWaitWithoutEnablingTotalCeiling(float wait)
        {
            OpenAiHttpOptions options = LlmEndpointClientFactory.BuildHttpOptions(
                Descriptor(), "", new CoreAISettingsOptions { LlmResponseWaitTimeoutSeconds = wait });
            Assert.AreEqual((int)wait, options.ResponseWaitTimeoutSeconds);
            Assert.AreEqual(0, options.RequestTimeoutSeconds);
        }

        [TestCase(0f, 0)]
        [TestCase(0.5f, 1)]
        [TestCase(600f, 600)]
        public void BuildHttpOptions_PortableSettingsPreserveUnlimitedAndExplicitCeiling(float ceiling, int expected)
        {
            OpenAiHttpOptions options = LlmEndpointClientFactory.BuildHttpOptions(
                Descriptor(), "", new CoreAISettingsOptions { LlmRequestTimeoutSeconds = ceiling });
            Assert.AreEqual(expected, options.RequestTimeoutSeconds);
        }

        [Test]
        public void BuildHttpOptions_HonoursCustomHttpCapWithNoOrLargerCeiling()
        {
            _settings.ConfigureHttpApi("https://example.test/v1", "", "test", timeoutSeconds: 73);
            foreach (float ceiling in new[] { 0f, 600f })
            {
                _settings.SetOrchestratorTimeoutSeconds(ceiling);
                Assert.AreEqual(73,
                    LlmEndpointClientFactory.BuildHttpOptions(Descriptor(), "", _settings).RequestTimeoutSeconds);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void HttpEndpoint_RejectsMissingModelBeforeTransport(string model)
        {
            LlmEndpointDescriptor endpoint = Descriptor();
            endpoint.Model = model;
            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                LlmEndpointClientFactory.BuildHttpOptions(endpoint, "", _settings));
            StringAssert.Contains("model", error.Message);
            LlmEndpointClientFactory factory = new(_settings, GameLoggerUnscopedFallback.Instance);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await factory.ActivateAsync(endpoint, "", CancellationToken.None));
            using LlmClientRegistry registry = new(GameLoggerUnscopedFallback.Instance, _settings);
            Assert.Throws<ArgumentException>(() => registry.AddOrUpdateEndpointAsync(endpoint));
            StringAssert.Contains("model", HubSettingsPage.ValidateEndpoint(endpoint));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void LlmUnityHttpSettings_RejectsMissingModelInsteadOfInventingOne(string model)
        {
            Assert.Throws<ArgumentException>(() => new LlmUnityServerHttpSettings(_settings, 13333, model));
        }
#endif

        [TestCase("ConfigureHttpApi", LlmExecutionMode.ClientOwnedApi)]
        [TestCase("ConfigureClientOwnedApi", LlmExecutionMode.ClientOwnedApi)]
        [TestCase("ConfigureClientLimited", LlmExecutionMode.ClientLimited)]
        [TestCase("ConfigureServerManagedApi", LlmExecutionMode.ServerManagedApi)]
        public void ConfigureApi_OriginalClrSignaturesForwardExplicitOptions(string methodName, LlmExecutionMode expectedMode)
        {
            bool limited = methodName == "ConfigureClientLimited";
            Type[] parameterTypes = limited
                ? new[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(int),
                    typeof(float), typeof(int), typeof(int), typeof(bool) }
                : new[] { typeof(string), typeof(string), typeof(string), typeof(float), typeof(int),
                    typeof(int), typeof(bool) };
            MethodInfo method = typeof(CoreAISettingsAsset).GetMethod(methodName, parameterTypes);
            Assert.IsNotNull(method, "Existing compiled callers require the original CLR signature.");
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.IsFalse(parameter.IsOptional, parameter.Name);
            }
            object[] arguments = limited
                ? new object[] { "https://legacy.test/v1", "key", "model", 5, 2000, 0.8f, 41, 512, true }
                : methodName == "ConfigureServerManagedApi"
                    ? new object[] { "https://legacy.test/v1", "model", "key", 0.8f, 41, 512, true }
                    : new object[] { "https://legacy.test/v1", "key", "model", 0.8f, 41, 512, true };
            _settings.ConfigureReasoning(LlmReasoningEffort.High, LlmReasoningDialect.OpenAi);
            method.Invoke(_settings, arguments);
            Assert.AreEqual(expectedMode, _settings.ExecutionMode);
            Assert.AreEqual("model", _settings.ModelName);
            Assert.AreEqual("key", _settings.ApiKey);
            Assert.AreEqual(0.8f, _settings.Temperature);
            Assert.AreEqual(41, _settings.RequestTimeoutSeconds);
            Assert.AreEqual(512, _settings.MaxTokens);
            Assert.IsTrue(_settings.OverrideTemperature);
            Assert.IsTrue(_settings.OverrideMaxTokens);
            Assert.AreEqual(LlmReasoningEffort.High, _settings.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, _settings.ReasoningDialect);
            if (limited)
            {
                Assert.AreEqual(5, _settings.MaxClientLimitedRequestsPerSession);
                Assert.AreEqual(2000, _settings.MaxClientLimitedPromptChars);
            }
        }

        [TestCase("http", false)]
        [TestCase("owned", false)]
        [TestCase("limited", false)]
        [TestCase("managed", false)]
        [TestCase("http", true)]
        [TestCase("owned", true)]
        [TestCase("limited", true)]
        [TestCase("managed", true)]
        public void ConfigureApi_OmittedOptionsKeepCurrentValues(string method, bool overrides)
        {
            _settings.ConfigureHttpApi("https://old.test/v1", "old", "old", 0.7f, 73,
                overrides ? 321 : 0, overrides);
            Configure(method);
            Assert.AreEqual(0.7f, _settings.Temperature);
            Assert.AreEqual(73, _settings.RequestTimeoutSeconds);
            Assert.AreEqual(overrides, _settings.OverrideTemperature);
            Assert.AreEqual(overrides, _settings.OverrideMaxTokens);
            Assert.AreEqual(overrides ? 321 : 0, _settings.MaxTokens);
            Assert.AreEqual("new", _settings.ModelName);
        }

        [TestCase("http")]
        [TestCase("owned")]
        [TestCase("limited")]
        [TestCase("managed")]
        public void ConfigureApi_ExplicitOptionsStillApply(string method)
        {
            Configure(method, 0.8f, 41, 512, true);
            Assert.AreEqual(0.8f, _settings.Temperature);
            Assert.AreEqual(41, _settings.RequestTimeoutSeconds);
            Assert.AreEqual(512, _settings.MaxTokens);
            Assert.IsTrue(_settings.OverrideTemperature);
            Configure(method, maxTokens: 0, overrideTemperature: false);
            Assert.IsFalse(_settings.OverrideTemperature);
            Assert.AreEqual(0, _settings.MaxTokens);
            Assert.AreEqual(0.8f, _settings.Temperature);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ApplyServerManagedApi_PreservesTemperatureOverride(bool enabled)
        {
            CoreAISettingsAsset previous = CoreAISettingsAsset.Instance;
            CoreAISettingsAsset.SetInstance(_settings);
            try
            {
                _settings.ConfigureHttpApi("https://old.test/v1", "", "old", 0.6f, 73, 512, enabled);
                CoreAiBackend.ApplyServerManagedApi("https://proxy.test/v1", "new");
                Assert.AreEqual(enabled, _settings.OverrideTemperature);
                Assert.AreEqual(0.6f, _settings.Temperature);
                Assert.AreEqual(512, _settings.MaxTokens);
            }
            finally
            {
                CoreAISettingsAsset.SetInstance(previous);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(7)]
        public void Pipeline_UsesConfiguredOrchestrationConcurrency(int concurrency)
        {
            SetField(_settings, "maxConcurrentOrchestrations", concurrency);
            ContainerBuilder builder = new();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(_settings);
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            builder.RegisterInstance<IAgentMemoryStore>(new NullAgentMemoryStore());
            LlmPipelineInstaller.RegisterLlmPipelineForPlatform(builder, _settings, null, RuntimePlatform.WebGLPlayer);
            using IObjectResolver container = builder.Build();
            Assert.AreEqual(concurrency, container.Resolve<AiOrchestrationQueueOptions>().MaxConcurrent);
        }

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
        [TestCase(1)]
        [TestCase(3)]
        public void LlmUnityHost_UsesConfiguredChatConcurrency(int concurrency)
        {
            SetField(_settings, "llmUnityMaxConcurrentChats", concurrency);
            GameObject host = new("Inactive concurrency test host");
            host.SetActive(false);
            try
            {
                LLM llm = host.AddComponent<LLM>();
                LLMAgent agent = host.AddComponent<LLMAgent>();
                SetField(llm, "_model", "test.gguf");
                LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, _settings, GameLoggerUnscopedFallback.Instance);
                Assert.AreEqual(concurrency, llm.parallelPrompts);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
#endif

        [Test]
        public void HubEdit_AllPublicSettableDescriptorPropertiesSurvive()
        {
            LlmEndpointDescriptor original = PopulatedDescriptor();
            RoutingController controller = new(original);
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            page.CreatePageContent();
            Invoke(page, "LoadEndpointById", original.EndpointId);
            LlmEndpointDescriptor edited = (LlmEndpointDescriptor)Invoke(page, "ReadEndpointEditor");
            Assert.AreNotSame(original, edited);
            AssertPropertiesEqual(original, edited);
        }

        [Test]
        public void RegistryCloneAndFingerprint_CoverEveryPublicSettableDescriptorProperty()
        {
            LlmEndpointDescriptor populated = PopulatedDescriptor();
            AssertPropertiesEqual(populated, FileLlmEndpointRegistryStore.CloneDescriptor(populated));
            foreach (PropertyInfo property in DescriptorProperties())
            {
                LlmEndpointDescriptor original = new();
                LlmEndpointDescriptor changed = new();
                property.SetValue(changed, property.GetValue(populated));
                if (Equals(property.GetValue(original), property.GetValue(changed)))
                {
                    Assert.Fail("The reflection fixture must change property " + property.Name);
                }

                Assert.AreNotEqual(Fingerprint(original), Fingerprint(changed), property.Name);
            }
        }

        [Test]
        public void HubEdit_PreservesExplicitNullHiddenProperties()
        {
            LlmEndpointDescriptor original = PopulatedDescriptor();
            original.ExtraBodyJson = null;
            typeof(LlmEndpointDescriptor).GetProperty("Temperature")?.SetValue(original, null);
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: new RoutingController(original)));
            page.CreatePageContent();
            Invoke(page, "LoadEndpointById", original.EndpointId);
            LlmEndpointDescriptor edited = (LlmEndpointDescriptor)Invoke(page, "ReadEndpointEditor");
            AssertPropertiesEqual(original, edited);
        }

        [Test]
        public async Task HubRename_RoundTripsHiddenBehaviourThroughRegistry()
        {
            LlmEndpointDescriptor original = PopulatedDescriptor();
            original.Kind = LlmEndpointKind.Offline;
            original.Active = false;
            original.KeepWarm = false;
            using LlmClientRegistry registry = new(GameLoggerUnscopedFallback.Instance, _settings);
            await registry.AddOrUpdateEndpointAsync(original);
            RoutingController controller = new(registry.GetEndpoints()[0].Descriptor);
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            page.CreatePageContent();
            Invoke(page, "LoadEndpointById", original.EndpointId);
            ((TextField)GetField(page, "_endpointName")).SetValueWithoutNotify("Renamed");
            LlmEndpointDescriptor edited = (LlmEndpointDescriptor)Invoke(page, "ReadEndpointEditor");
            LlmEndpointSnapshot saved = await registry.AddOrUpdateEndpointAsync(edited);
            Assert.AreEqual("Renamed", saved.Descriptor.DisplayName);
            Assert.AreEqual("DisplayName", original.DisplayName, "Editing must not mutate the original descriptor.");
            original.DisplayName = "Renamed";
            AssertPropertiesEqual(original, saved.Descriptor);
        }

        private void Configure(string method, float? temperature = null, int? timeout = null,
            int? maxTokens = null, bool? overrideTemperature = null)
        {
            switch (method)
            {
                case "http":
                    _settings.ConfigureHttpApi("https://new.test/v1", "key", "new", temperature, timeout,
                        maxTokens, overrideTemperature);
                    break;
                case "owned":
                    _settings.ConfigureClientOwnedApi("https://new.test/v1", "key", "new", temperature, timeout,
                        maxTokens, overrideTemperature);
                    break;
                case "limited":
                    _settings.ConfigureClientLimited("https://new.test/v1", "key", "new", 3, 1000, temperature,
                        timeout, maxTokens, overrideTemperature);
                    break;
                case "managed":
                    _settings.ConfigureServerManagedApi("https://new.test/v1", "new", "key", temperature, timeout,
                        maxTokens, overrideTemperature);
                    break;
                default:
                    Assert.Fail(method);
                    break;
            }
        }

        private static LlmEndpointDescriptor Descriptor()
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = "test", DisplayName = "Test", BaseUrl = "https://example.test/v1", Model = "test"
            };
        }

        private static IEnumerable<PropertyInfo> DescriptorProperties()
        {
            foreach (PropertyInfo property in typeof(LlmEndpointDescriptor).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod?.IsPublic == true)
                {
                    yield return property;
                }
            }
        }

        private static LlmEndpointDescriptor PopulatedDescriptor()
        {
            LlmEndpointDescriptor descriptor = new();
            foreach (PropertyInfo property in DescriptorProperties())
            {
                Type type = property.PropertyType;
                object value = type == typeof(string) ? property.Name
                    : type == typeof(int) ? (object)4096
                    : type == typeof(int?) ? (object)75
                    : type == typeof(bool) ? !((bool)property.GetValue(descriptor))
                    : type == typeof(float?) ? 0.7f
                    : type.IsEnum ? Enum.GetValues(type).GetValue(1)
                    : throw new InvalidOperationException("Add a reflection fixture value for " + property.Name);
                property.SetValue(descriptor, value);
            }

            descriptor.Model = "http-model-alias";
            descriptor.LocalModelPath = "local-model.gguf";
            return descriptor;
        }

        private static void AssertPropertiesEqual(LlmEndpointDescriptor expected, LlmEndpointDescriptor actual)
        {
            foreach (PropertyInfo property in DescriptorProperties())
            {
                Assert.AreEqual(property.GetValue(expected), property.GetValue(actual), property.Name);
            }
        }

        private static string Fingerprint(LlmEndpointDescriptor descriptor)
        {
            MethodInfo method = typeof(LlmClientRegistry).GetMethod("DescriptorFingerprint", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { descriptor });
        }

        private static object Invoke(object target, string name, params object[] arguments)
        {
            return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, arguments);
        }

        private static object GetField(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private sealed class RoutingController : ICoreAiRoutingUiController
        {
            private readonly LlmEndpointDescriptor _descriptor;
            public event Action Changed { add { } remove { } }
            public RoutingController(LlmEndpointDescriptor descriptor) { _descriptor = descriptor; }
            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints() => new[] { new LlmEndpointSnapshot { Descriptor = _descriptor } };
            public IReadOnlyList<LlmRuntimeProfile> GetProfiles() => Array.Empty<LlmRuntimeProfile>();
            public string GetProfileForRole(string roleId) => "";
            public CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId) => new(true);
            public Task<CoreAiRoutingUiResult> SaveEndpointAsync(LlmEndpointDescriptor endpoint, string sessionApiKey,
                CancellationToken cancellationToken = default) => Task.FromResult(new CoreAiRoutingUiResult(true));
            public Task<CoreAiRoutingUiResult> RemoveEndpointAsync(string endpointId,
                CancellationToken cancellationToken = default) => Task.FromResult(new CoreAiRoutingUiResult(true));
        }
    }
}
