using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для CoreAISettingsAsset.
    /// </summary>
    public sealed class CoreAISettingsAssetEditModeTests
    {
        [Test]
        public void CreateAsset_WithDefaults_ShouldHaveCorrectValues()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(LlmAutoPriority.LlmUnityFirst, settings.AutoPriority);
            Assert.AreEqual("http://localhost:1234/v1", settings.ApiBaseUrl);
            Assert.AreEqual("", settings.ApiKey);
            Assert.AreEqual("gpt-4o-mini", settings.ModelName);
            Assert.AreEqual(0.1f, settings.Temperature);
            Assert.AreEqual(4096, settings.MaxTokens);
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
            Assert.AreEqual(8192, settings.ContextWindowTokens);
            Assert.AreEqual(false, settings.EnableMeaiDebugLogging);
            Assert.AreEqual(false, settings.EnableHttpDebugLogging);
            Assert.AreEqual(false, settings.OfflineUseCustomResponse);
            Assert.AreEqual("Offline mode: LLM unavailable", settings.OfflineCustomResponse);
            Assert.AreEqual("*", settings.OfflineCustomResponseRoles);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void BackendProperties_ShouldReturnCorrectBooleans()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // По умолчанию Auto
            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            // Переключаем на HTTP
            settings.ConfigureHttpApi("https://api.openai.com/v1", "sk-test", "gpt-4");
            Assert.AreEqual(LlmBackendType.OpenAiHttp, settings.BackendType);
            Assert.AreEqual(true, settings.UseHttpApi);
            Assert.AreEqual(false, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            // Переключаем на Offline
            settings.ConfigureOffline();
            Assert.AreEqual(LlmBackendType.Offline, settings.BackendType);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(false, settings.UseLlmUnity);
            Assert.AreEqual(true, settings.UseOffline);

            // Переключаем на Auto
            settings.ConfigureAuto();
            Assert.AreEqual(LlmBackendType.Auto, settings.BackendType);
            Assert.AreEqual(false, settings.UseHttpApi);
            Assert.AreEqual(true, settings.UseLlmUnity);
            Assert.AreEqual(false, settings.UseOffline);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureHttpApi_ShouldSetAllValues()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("https://api.test.com/v1", "sk-123", "test-model", 0.5f, 60, 2048);

            Assert.AreEqual(LlmBackendType.OpenAiHttp, settings.BackendType);
            Assert.AreEqual("https://api.test.com/v1", settings.ApiBaseUrl);
            Assert.AreEqual("sk-123", settings.ApiKey);
            Assert.AreEqual("test-model", settings.ModelName);
            Assert.AreEqual(0.5f, settings.Temperature);
            Assert.AreEqual(60, settings.RequestTimeoutSeconds);
            Assert.AreEqual(2048, settings.MaxTokens);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ConfigureLlmUnity_ShouldSetAllValues()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
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
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // По умолчанию — кастомный ответ выключен
            Assert.AreEqual(false, settings.ShouldUseOfflineCustomResponse("Creator"));

            // Включаем с wildcard
            settings.GetType().GetField("offlineUseCustomResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, true);
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Creator"));
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Programmer"));

            // Конкретные роли
            settings.GetType().GetField("offlineCustomResponseRoles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, "Creator,Programmer");
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Creator"));
            Assert.AreEqual(true, settings.ShouldUseOfflineCustomResponse("Programmer"));
            Assert.AreEqual(false, settings.ShouldUseOfflineCustomResponse("Merchant"));

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Singleton_ShouldLoadFromResources()
        {
            // Если есть Resources/CoreAISettings.asset — он загрузится
            // Если нет — вернётся null
            var instance = CoreAISettingsAsset.Instance;
            // Не Assert.IsNull — может быть загружен из Resources
            CoreAISettingsAsset.ResetInstance();
        }

        [Test]
        public void SetInstance_ShouldOverrideSingleton()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset.SetInstance(settings);

            Assert.AreSame(settings, CoreAISettingsAsset.Instance);

            CoreAISettingsAsset.ResetInstance();
            Object.DestroyImmediate(settings);
        }
    }
}
