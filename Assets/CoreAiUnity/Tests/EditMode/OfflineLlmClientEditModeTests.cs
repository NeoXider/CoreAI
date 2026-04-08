using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для OfflineLlmClient.
    /// </summary>
    public sealed class OfflineLlmClientEditModeTests
    {
        [Test]
        public void Constructor_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);
            Assert.IsNotNull(client);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_ShouldReturnDefaultResponse()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Creator",
                SystemPrompt = "test",
                UserPayload = "test"
            });

            Assert.IsTrue(result.Ok);
            // Creator имеет специфичную заглушку
            Assert.AreEqual("{\"created\": false, \"note\": \"offline\"}", result.Content);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_UnknownRole_ShouldReturnGenericResponse()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "UnknownRole",
                SystemPrompt = "test",
                UserPayload = "hello"
            });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("{\"status\": \"offline\", \"role\": \"unknownrole\", \"echo\": \"hello\"}",
                result.Content);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_ProgrammerRole_ShouldReturnLua()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Programmer",
                SystemPrompt = "test",
                UserPayload = "create function"
            });

            Assert.IsTrue(result.Ok);
            Assert.IsTrue(result.Content.Contains("```lua"));
            Assert.IsTrue(result.Content.Contains("function noop"));

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_CustomResponse_ShouldReturnCustomText()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Включаем кастомный ответ
            settings.GetType()
                .GetField("offlineUseCustomResponse",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);
            settings.GetType()
                .GetField("offlineCustomResponse",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "Custom offline message");

            OfflineLlmClient client = new(settings);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Creator",
                SystemPrompt = "test",
                UserPayload = "test"
            });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("Custom offline message", result.Content);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_CustomResponseForSpecificRoles_ShouldApplyOnlyToThose()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            settings.GetType()
                .GetField("offlineUseCustomResponse",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);
            settings.GetType()
                .GetField("offlineCustomResponse",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "Custom");
            settings.GetType()
                .GetField("offlineCustomResponseRoles",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, "Creator");

            OfflineLlmClient client = new(settings);

            // Creator — кастомный
            LlmCompletionResult result1 = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Creator",
                UserPayload = "test"
            });
            Assert.AreEqual("Custom", result1.Content);

            // Programmer — заглушка по ролям
            LlmCompletionResult result2 = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Programmer",
                UserPayload = "test"
            });
            Assert.IsTrue(result2.Content.Contains("```lua"));

            Object.DestroyImmediate(settings);
        }
    }
}