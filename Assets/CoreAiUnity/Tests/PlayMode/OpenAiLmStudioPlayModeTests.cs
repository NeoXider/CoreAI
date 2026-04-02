using System;
using System.Collections;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Реальный HTTP к OpenAI-совместимому серверу (LM Studio и т.д.). Без переменных окружения тест пропускается.
    /// Пример (PowerShell):<br/>
    /// $env:COREAI_OPENAI_TEST_BASE = "http://10.0.0.1:1234/v1"<br/>
    /// $env:COREAI_OPENAI_TEST_MODEL = "имя-модели-из-/v1/models"<br/>
    /// Затем Play Mode Tests в Test Runner.
    /// </summary>
    public sealed class OpenAiLmStudioPlayModeTests
    {
        [UnityTest]
        public IEnumerator OpenAiChatLlmClient_Completes_WhenEnvConfigured()
        {
            var baseUrl = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Assert.Ignore(
                    "Задайте COREAI_OPENAI_TEST_BASE (например http://IP:1234/v1) и COREAI_OPENAI_TEST_MODEL для проверки LM Studio.");
            }

            var model = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_MODEL");
            if (string.IsNullOrWhiteSpace(model))
                Assert.Ignore("Задайте COREAI_OPENAI_TEST_MODEL — id из GET .../v1/models");

            var apiKey = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_API_KEY") ?? "";

            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                true,
                baseUrl.Trim().TrimEnd('/'),
                apiKey,
                model.Trim(),
                0.2f,
                300);

            var client = new OpenAiChatLlmClient(settings);
            var task = client.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = BuiltInAgentRoleIds.PlayerChat,
                    SystemPrompt = "Reply with exactly the single word: PONG",
                    UserPayload = "ping"
                });

            yield return new WaitUntil(() => task.IsCompleted);

            if (task.IsFaulted)
                Assert.Fail(task.Exception?.GetBaseException().Message ?? "Task faulted");

            var result = task.Result;
            Assert.IsTrue(result.Ok, result.Error ?? "(no error text)");
            StringAssert.Contains("PONG", result.Content.ToUpperInvariant());
        }
    }
}
