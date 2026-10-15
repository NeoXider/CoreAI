using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAISettingsAsset"/> defaults and validation.
    /// </summary>
    public sealed class CoreAISettingsAssetEditModeTests
    {
        [Test]
        public void SavedTimeoutAndTokenValues_SurviveDeserialization()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                JsonUtility.FromJsonOverwrite(
                    "{\"requestTimeoutSeconds\":120,\"llmRequestTimeoutSeconds\":120," +
                    "\"maxTokens\":128000,\"enableMaxTokensOverriding\":true}", settings);
                Assert.AreEqual(120, settings.RequestTimeoutSeconds);
                Assert.AreEqual(120f, settings.LlmRequestTimeoutSeconds);
                Assert.AreEqual(128000, settings.MaxTokens);
                Assert.AreEqual(180f, settings.LlmResponseWaitTimeoutSeconds);
                settings.ApplyOptions(new CoreAISettingsOptions
                {
                    LlmRequestTimeoutSeconds = 0f,
                    LlmResponseWaitTimeoutSeconds = 0f,
                    MaxTokens = 0
                });
                Assert.AreEqual(0f, settings.LlmRequestTimeoutSeconds);
                Assert.AreEqual(0f, settings.LlmResponseWaitTimeoutSeconds);
                Assert.AreEqual(0, settings.MaxTokens);
                Assert.IsFalse(settings.OverrideMaxTokens);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [TestCase("maxToolCallRoundtrips")]
        [TestCase("llmUnityToolChannel")]
        public void SettingsInspector_ExposesSerializedToolControls(string propertyName)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                VisualTreeAsset template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                    "Assets/CoreAiUnity/Editor/CoreAISettingsAssetEditor.uxml");
                Assert.IsNotNull(template);
                TemplateContainer content = template.CloneTree();
                PropertyField field = content.Query<PropertyField>().ToList().Find(
                    candidate => candidate.bindingPath == propertyName);
                Assert.IsNotNull(field, "The custom inspector must expose " + propertyName + ".");
                SerializedObject serialized = new(settings);
                SerializedProperty property = serialized.FindProperty(propertyName);
                Assert.IsNotNull(property);
                field.BindProperty(property);
                property.intValue = 1;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (propertyName == "maxToolCallRoundtrips")
                {
                    Assert.AreEqual(1, settings.MaxToolCallRoundtrips);
                }
                else
                {
                    Assert.AreEqual(LlmToolChannel.Native, settings.LlmUnityToolChannel);
                }
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void CreateAsset_WithDefaults_ShouldHaveCorrectValues()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.Auto, settings.ExecutionMode);
            Assert.AreEqual(LlmAutoPriority.LlmUnityFirst, settings.AutoPriority);
            Assert.AreEqual("http://localhost:1234/v1", settings.ApiBaseUrl);
            Assert.AreEqual("", settings.ApiKey);
            Assert.AreEqual("Qwen3.5-2B-Q4_K_M.gguf", settings.ModelName,
                "No HTTP model ships by default; in Auto mode the getter falls back to the GGUF hint. " +
                "There is no built-in 'gpt-4o-mini' any more - an unset HTTP model must surface as an " +
                "explicit configuration error, not as silent traffic to a model nobody selected.");
            Assert.AreEqual(0.1f, settings.Temperature);
            Assert.IsFalse(settings.OverrideTemperature);
            Assert.IsFalse(settings.OverrideMaxTokens, "No output cap by default: the provider decides.");
            Assert.AreEqual(0, settings.MaxTokens, "0 = no max_tokens sent when overriding is disabled.");
            Assert.AreEqual(0, settings.RequestTimeoutSeconds);
            Assert.AreEqual(0f, settings.LlmRequestTimeoutSeconds);
            Assert.AreEqual(180f, settings.LlmResponseWaitTimeoutSeconds);
            SerializedObject defaults = new(settings);
            Assert.AreEqual(262144, defaults.FindProperty("maxTokens").intValue);
            Assert.AreEqual("", settings.LlmUnityAgentName);
            Assert.AreEqual("Qwen3.5-2B-Q4_K_M.gguf", settings.GgufModelPath);
            Assert.AreEqual(true, settings.LlmUnityDontDestroyOnLoad);
            Assert.AreEqual(120f, settings.LlmUnityStartupTimeoutSeconds);
            Assert.AreEqual(1f, settings.LlmUnityStartupDelaySeconds);
            Assert.AreEqual(false, settings.LlmUnityKeepAlive);
            Assert.AreEqual(1, settings.LlmUnityMaxConcurrentChats);
            Assert.AreEqual(3, settings.MaxLuaRepairRetries);
            Assert.AreEqual(3, settings.MaxToolCallRetries);
            Assert.AreEqual(1, settings.MaxLlmRequestRetries);
            Assert.IsFalse(settings.OverrideContextWindow, "No client-side window assumed by default.");
            Assert.AreEqual(CoreAISettings.UnlimitedContextWindowTokens, settings.ContextWindowTokens,
                "Unlimited sentinel when context window overriding is disabled.");
            Assert.AreEqual(false, settings.EnableMeaiDebugLogging);
            Assert.AreEqual(false, settings.EnableHttpDebugLogging);
            Assert.AreEqual(true, settings.EnableStreaming, "Streaming is enabled by default");
            Assert.AreEqual(true, settings.EnableTokenCalibration, "Token calibration is enabled by default");
            Assert.AreEqual(true, settings.WebGlNativeStreaming, "WebGL native SSE is enabled by default");
            Assert.AreEqual(false, settings.SameOriginCredentials);
            Assert.AreEqual(false, settings.OfflineUseCustomResponse);
            Assert.AreEqual("Offline mode: LLM unavailable", settings.OfflineCustomResponse);
            Assert.AreEqual("*", settings.OfflineCustomResponseRoles);
            Assert.AreEqual(0, settings.MaxClientLimitedRequestsPerSession);
            Assert.AreEqual(0, settings.MaxClientLimitedPromptChars);
            Assert.IsTrue(settings.EnableConversationHistorySummarization);
            Assert.AreEqual(0, settings.ConversationHistoryRecentTokenBudgetOverride);
            Assert.AreEqual(0, settings.ConversationRolledSummaryMaxTokens);
            Assert.AreEqual(CoreAISettings.DefaultConversationCompactionTriggerRatio,
                settings.ConversationCompactionTriggerRatio);
            Assert.IsTrue(settings.EnableContextPruning);
            Assert.AreEqual(3, settings.MaxRetainedToolResultMessages);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ReasoningFields_DefaultToLegacyCompatibleValues_AndBindInInspector()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                Assert.AreEqual(LlmReasoningEffort.Default, settings.ReasoningEffort);
                Assert.AreEqual(LlmReasoningDialect.Auto, settings.ReasoningDialect);
                Assert.AreEqual(LlmReasoningEffort.Default, settings.SecondaryReasoningEffort);
                Assert.AreEqual(LlmReasoningDialect.Auto, settings.SecondaryReasoningDialect);
                SerializedObject serialized = new(settings);
                foreach (string field in new[] { "reasoningEffort", "reasoningDialect",
                             "secondaryReasoningEffort", "secondaryReasoningDialect" })
                {
                    Assert.IsNotNull(serialized.FindProperty(field), field);
                }
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ReasoningFields_SurviveApiPresetsAndPortableRoundTrip()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset copy = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ConfigureReasoning(LlmReasoningEffort.High, LlmReasoningDialect.OpenAi);
                settings.ConfigureFallbackReasoning(LlmReasoningEffort.Low, LlmReasoningDialect.OpenRouter,
                    LlmReasoningMode.Enabled, 2048, "{\"top_k\":20}");
                settings.ConfigureHttpApi("https://primary.test/v1", "", "primary");
                settings.ConfigureClientOwnedApi("https://primary.test/v1", "", "primary");
                settings.ConfigureClientLimited("https://primary.test/v1", "", "primary", 3, 1024);
                settings.ConfigureServerManagedApi("https://proxy.test/v1", "primary");
                settings.ConfigureLlmUnity("", "model.gguf");
                settings.ConfigureOffline();
                settings.ConfigureAuto();
                settings.ConfigureFallbackBackend(true, "https://fallback.test/v1", "secondary");
                copy.ApplyOptions(settings.ToOptions());
                foreach (CoreAISettingsAsset actual in new[] { settings, copy })
                {
                    Assert.AreEqual(LlmReasoningEffort.High, actual.ReasoningEffort);
                    Assert.AreEqual(LlmReasoningDialect.OpenAi, actual.ReasoningDialect);
                    Assert.AreEqual(LlmReasoningEffort.Low, actual.SecondaryReasoningEffort);
                    Assert.AreEqual(LlmReasoningDialect.OpenRouter, actual.SecondaryReasoningDialect);
                }
                Assert.AreEqual(LlmReasoningMode.Enabled, settings.SecondaryReasoningMode);
                Assert.AreEqual(2048, settings.SecondaryThinkingBudgetTokens);
                Assert.AreEqual("{\"top_k\":20}", settings.SecondaryExtraBodyJson);
            }
            finally
            {
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void SerializedObject_HasEnableStreaming_ForInspectorBinding()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SerializedObject so = new(settings);
            SerializedProperty prop = so.FindProperty("enableStreaming");
            Assert.IsNotNull(prop, "CoreAISettingsAssetEditor Essentials binds enableStreaming.");
            Assert.AreEqual(SerializedPropertyType.Boolean, prop.propertyType);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void HasValidFallbackBackend_ShouldRequireEnabledAndConfiguredSecondaryBackend()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            settings.ConfigureFallbackBackend(true, "https://openrouter.ai/api/v1/", "google/gemini-2.5-flash");
            Assert.IsTrue(settings.HasValidFallbackBackend,
                "Fallback should be valid only when enabled and both URL + model are configured");
            Assert.AreEqual(
                "https://openrouter.ai/api/v1",
                settings.SecondaryApiBaseUrl,
                "Secondary URL should be normalized by stripping trailing slash");

            settings.ConfigureFallbackBackend(true, "https://openrouter.ai/api/v1/", "   ");
            Assert.IsFalse(settings.HasValidFallbackBackend,
                "Fallback should be invalid when secondary model is missing");

            settings.ConfigureFallbackBackend(true, "  ", "google/gemini-2.5-flash");
            Assert.IsFalse(settings.HasValidFallbackBackend,
                "Fallback should be invalid when secondary URL is missing");

            settings.ConfigureFallbackBackend(false, "https://openrouter.ai/api/v1", "google/gemini-2.5-flash");
            Assert.IsFalse(settings.HasValidFallbackBackend,
                "Fallback should be invalid when disabled even if URL+model are filled");

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void SecondaryApiBaseUrl_ShouldBeEmptyWhenUnset_AndReturnTrimmedValue()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreEqual(string.Empty, settings.SecondaryApiBaseUrl);

            settings.ConfigureFallbackBackend(false, "https://openrouter.ai/api/v1/", "m");
            Assert.AreEqual("https://openrouter.ai/api/v1", settings.SecondaryApiBaseUrl);

            settings.ConfigureFallbackBackend(false, "   ", "m");
            Assert.AreEqual(string.Empty, settings.SecondaryApiBaseUrl);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ApiBaseUrl_ShouldDefaultToLocalhostAndTrimTrailingSlash()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreEqual("http://localhost:1234/v1", settings.ApiBaseUrl);

            settings.SetApiBaseUrl("https://openrouter.ai/api/v1/");
            Assert.AreEqual("https://openrouter.ai/api/v1", settings.ApiBaseUrl);

            settings.SetApiBaseUrl("  ");
            Assert.AreEqual("http://localhost:1234/v1", settings.ApiBaseUrl);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ModelName_ShouldFallbackToGgufForLocalModes_AndStayEmptyForNonLocalModes()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Direct local-mode path
            settings.SetModelResolution(
                LlmExecutionMode.LocalModel, LlmBackendType.LlmUnity, "   ", "local-model.gguf");
            Assert.AreEqual("local-model.gguf", settings.ModelName);

            // Auto mode resolves to LocalModel when backend is LlmUnity
            settings.SetModelResolution(
                LlmExecutionMode.Auto, LlmBackendType.LlmUnity, "   ", "local-model.gguf");
            Assert.AreEqual("local-model.gguf", settings.ModelName);

            // Auto + HTTP backend should not use local GGUF fallback - and must not invent one either.
            settings.SetModelResolution(
                LlmExecutionMode.Auto, LlmBackendType.OpenAiHttp, "   ", "local-model.gguf");
            Assert.AreEqual("", settings.ModelName,
                "A hidden 'gpt-4o-mini' default sent traffic to a model nobody selected and then reported " +
                "that invented name in logs, usage history and the token estimator's encoding pick.");

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void EffectiveHttpRequestTimeoutSeconds_IsMinOfHttpAndOrchestratorCeiling()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("https://api.test.com/v1", "sk", "m", 0.1f, 500, 2048);
            settings.SetOrchestratorTimeoutSeconds(45f);
            Assert.AreEqual(45, settings.EffectiveHttpRequestTimeoutSeconds);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void BackendProperties_ShouldReturnCorrectBooleans()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Auto by default
            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            // Switch to HTTP
            settings.ConfigureHttpApi("https://api.openai.com/v1", "sk-test", "gpt-4");
            Assert.AreEqual(LlmBackendType.OpenAiHttp, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, settings.ExecutionMode);
            Assert.AreEqual(true, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseClientOwnedApi);
            Assert.AreEqual(false, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            // Switch to Offline
            settings.ConfigureOffline();
            Assert.AreEqual(LlmBackendType.Offline, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.Offline, settings.ExecutionMode);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(false, settings.UseLlmUnity);
            Assert.AreEqual(true, settings.UseOffline);

            // Switch to Auto
            settings.ConfigureAuto();
            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.Auto, settings.ExecutionMode);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureHttpApi_ShouldSetAllValues()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("https://api.test.com/v1", "sk-123", "test-model", 0.5f, 60, 2048, true);

            Assert.AreEqual(LlmBackendType.OpenAiHttp, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, settings.ExecutionMode);
            Assert.AreEqual("https://api.test.com/v1", settings.ApiBaseUrl);
            Assert.AreEqual("sk-123", settings.ApiKey);
            Assert.AreEqual("test-model", settings.ModelName);
            Assert.AreEqual(0.5f, settings.Temperature);
            Assert.IsTrue(settings.OverrideTemperature);
            Assert.AreEqual(60, settings.RequestTimeoutSeconds);
            Assert.IsTrue(settings.OverrideMaxTokens, "Explicit positive maxTokens enables the output cap.");
            Assert.AreEqual(2048, settings.MaxTokens);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureHttpApi_WithZeroMaxTokens_DisablesTheOutputCap()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("https://api.test.com/v1", "sk-123", "test-model", maxTokens: 0);

            Assert.IsFalse(settings.OverrideMaxTokens);
            Assert.AreEqual(0, settings.MaxTokens, "No max_tokens is sent; the provider decides.");

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ApplyOptions_ResolvedLimits_RoundtripTheOverrideToggles()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ApplyOptions(new CoreAISettingsOptions
                {
                    MaxTokens = 4096,
                    ContextWindowTokens = 32768
                });
                Assert.IsTrue(settings.OverrideMaxTokens);
                Assert.AreEqual(4096, settings.MaxTokens);
                Assert.IsTrue(settings.OverrideContextWindow);
                Assert.AreEqual(32768, settings.ContextWindowTokens);

                settings.ApplyOptions(new CoreAISettingsOptions
                {
                    MaxTokens = 0,
                    ContextWindowTokens = CoreAISettings.UnlimitedContextWindowTokens
                });
                Assert.IsFalse(settings.OverrideMaxTokens, "Resolved MaxTokens 0 maps back to override off.");
                Assert.AreEqual(0, settings.MaxTokens);
                Assert.IsFalse(settings.OverrideContextWindow,
                    "The unlimited sentinel maps back to override off.");
                Assert.AreEqual(CoreAISettings.UnlimitedContextWindowTokens, settings.ContextWindowTokens);

                // ToOptions -> ApplyOptions roundtrip is stable for the disabled state.
                CoreAISettingsOptions snapshot = settings.ToOptions();
                Assert.AreEqual(0, snapshot.MaxTokens);
                Assert.AreEqual(CoreAISettings.UnlimitedContextWindowTokens, snapshot.ContextWindowTokens);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ConfigureClientLimited_ShouldSetModeAndLimits()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureClientLimited("https://api.test.com/v1", "sk-123", "test-model", 3, 512);

            Assert.AreEqual(LlmExecutionMode.ClientLimited, settings.ExecutionMode);
            Assert.AreEqual(true, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseClientLimited);
            Assert.AreEqual(3, settings.MaxClientLimitedRequestsPerSession);
            Assert.AreEqual(512, settings.MaxClientLimitedPromptChars);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureServerManagedApi_ShouldAllowEmptyProviderKey()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureServerManagedApi("https://game.example.com/v1", "proxy-model");

            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, settings.ExecutionMode);
            Assert.AreEqual(true, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseServerManagedApi);
            Assert.AreEqual("", settings.ApiKey);
            Assert.AreEqual("https://game.example.com/v1", settings.ApiBaseUrl);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureLlmUnity_ShouldSetAllValues()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureLlmUnity("MyAgent", "model.gguf", true, 60f, 2f, false);

            Assert.AreEqual(LlmBackendType.LlmUnity, settings.BackendType);
            Assert.AreEqual("MyAgent", settings.LlmUnityAgentName);
            Assert.AreEqual("model.gguf", settings.GgufModelPath);
            Assert.AreEqual(true, settings.LlmUnityKeepAlive);
            Assert.AreEqual(60f, settings.LlmUnityStartupTimeoutSeconds);
            Assert.AreEqual(2f, settings.LlmUnityStartupDelaySeconds);
            Assert.AreEqual(false, settings.LlmUnityDontDestroyOnLoad);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void OfflineCustomResponse_ShouldMatchRoles()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Custom response is disabled by default
            Assert.AreEqual(false, settings.ShouldUseOfflineCustomResponse("Creator"));

            // Enable with wildcard roles
            settings.ConfigureOffline(true, roles: "*");
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Creator"));
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Programmer"));

            // Specific roles
            settings.ConfigureOffline(true, roles: "Creator,Programmer");
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Creator"));
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Programmer"));
            Assert.AreEqual(false, settings.ShouldUseOfflineCustomResponse("Merchant"));

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void SetInstance_ShouldOverrideSingleton()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset.SetInstance(settings);

            Assert.AreSame(settings, CoreAISettingsAsset.Instance);

            CoreAISettingsAsset.ResetInstance();
            Object.DestroyImmediate(settings);
        }
    }
}
