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
    /// EditMode coverage for <see cref="OpenAiChatLlmClient"/> and MEAI client factory wiring.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class OpenAiChatLlmClientEditModeTests
    {
        [Test]
        public void Constructor_WithOpenAiHttpSettings_ShouldCreateClient()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            OpenAiChatLlmClient client = new(settings);
            Assert.IsNotNull(client);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Constructor_WithCoreAiSettings_ShouldCreateClient()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            OpenAiChatLlmClient client = new(settings);
            Assert.IsNotNull(client);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Constructor_WithFullParams_ShouldCreateClient()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);
            settings.GetType()
                .GetField("apiBaseUrl",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "http://localhost:1234/v1");
            settings.GetType()
                .GetField("model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "test-model");

            OpenAiChatLlmClient client = new(settings, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                GameLoggerUnscopedFallback.Instance, null);
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
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);
            settings.GetType()
                .GetField("apiBaseUrl",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "http://invalid-host-test:9999/v1");
            settings.GetType()
                .GetField("model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "test");
            settings.GetType().GetField("requestTimeoutSeconds",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, 5);

            OpenAiChatLlmClient client = new(settings);

            // Сбой сети / таймаут: UWR-тексты, HttpClient SendAsync, или TaskCanceledException при таймауте HttpClient.
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    @".*\[Llm\] MeaiOpenAiChatClient: (Cannot resolve destination host|Request timeout|Network error|SendAsync failed:|Send failed:|Request timeout or transport canceled|stream open: Request timeout or transport canceled).*"));
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(@".*\[Llm\] MeaiLlmClient:.*"));

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
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
#endif
}
