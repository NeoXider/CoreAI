using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для OpenAiChatLlmClient (фабрика для MeaiLlmClient).
    /// </summary>
    public sealed class OpenAiChatLlmClientEditModeTests
    {
        [Test]
        public void Constructor_WithOpenAiHttpSettings_ShouldCreateClient()
        {
            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType().GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, true);

            var client = new OpenAiChatLlmClient(settings);
            Assert.IsNotNull(client);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Constructor_WithCoreAiSettings_ShouldCreateClient()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            var client = new OpenAiChatLlmClient(settings);
            Assert.IsNotNull(client);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Constructor_WithFullParams_ShouldCreateClient()
        {
            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType().GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, true);
            settings.GetType().GetField("apiBaseUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, "http://localhost:1234/v1");
            settings.GetType().GetField("model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, "test-model");

            var client = new OpenAiChatLlmClient(settings, GameLoggerUnscopedFallback.Instance, null);
            Assert.IsNotNull(client);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Constructor_NullSettings_ShouldThrow()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
            {
                new OpenAiChatLlmClient((OpenAiHttpLlmSettings)null);
            });
        }

        [Test]
        public async Task CompleteAsync_WithoutRealBackend_ShouldReturnError()
        {
            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType().GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, true);
            settings.GetType().GetField("apiBaseUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, "http://invalid-host-test:9999/v1");
            settings.GetType().GetField("model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, "test");
            settings.GetType().GetField("requestTimeoutSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, 5);

            var client = new OpenAiChatLlmClient(settings);

            // Ожидаем ошибку подключения (Cannot resolve destination host или timeout)
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*\\[Llm\\] MeaiOpenAiChatClient: (Cannot resolve destination host|Request timeout|Network error).*"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*\\[Llm\\] MeaiLlmClient: HTTP error.*"));

            var result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Test",
                SystemPrompt = "test",
                UserPayload = "test"
            });

            // Ожидается ошибка подключения к несуществующему хосту
            Assert.IsFalse(result.Ok);

            Object.DestroyImmediate(settings);
        }
    }
}
