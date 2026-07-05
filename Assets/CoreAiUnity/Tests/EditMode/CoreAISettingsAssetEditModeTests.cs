using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAISettingsAsset"/> defaults and validation.
    /// </summary>
    public sealed class CoreAISettingsAssetEditModeTests
    {
        [Test]
        public void CreateAsset_WithDefaults_ShouldHaveCorrectValues()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.Auto, settings.ExecutionMode);
            Assert.AreEqual(LlmAutoPriority.LlmUnityFirst, settings.AutoPriority);
            Assert.AreEqual("http://localhost:1234/v1", settings.ApiBaseUrl);
            Assert.AreEqual("", settings.ApiKey);
            Assert.AreEqual("gpt-4o-mini", settings.ModelName);
            Assert.AreEqual(0.1f, settings.Temperature);
            Assert.IsFalse(settings.OverrideTemperature);
            Assert.AreEqual(2048, settings.MaxTokens);
            Assert.AreEqual(120, settings.RequestTimeoutSeconds);
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
            Assert.AreEqual(CoreAISettings.DefaultContextWindowTokens, settings.ContextWindowTokens);
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
        public void ModelName_ShouldFallbackToGgufForLocalModes_AndDefaultForNonLocalModes()
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

            // Auto + HTTP backend should not use local GGUF fallback
            settings.SetModelResolution(
                LlmExecutionMode.Auto, LlmBackendType.OpenAiHttp, "   ", "local-model.gguf");
            Assert.AreEqual("gpt-4o-mini", settings.ModelName);

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
            settings.ConfigureHttpApi("https://api.test.com/v1", "sk-123", "test-model", 0.5f, 60, 2048);

            Assert.AreEqual(LlmBackendType.OpenAiHttp, settings.BackendType);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, settings.ExecutionMode);
            Assert.AreEqual("https://api.test.com/v1", settings.ApiBaseUrl);
            Assert.AreEqual("sk-123", settings.ApiKey);
            Assert.AreEqual("test-model", settings.ModelName);
            Assert.AreEqual(0.5f, settings.Temperature);
            Assert.IsTrue(settings.OverrideTemperature);
            Assert.AreEqual(60, settings.RequestTimeoutSeconds);
            Assert.AreEqual(2048, settings.MaxTokens);

            Object.DestroyImmediate(settings);
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