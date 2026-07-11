using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="OfflineLlmClient"/>.
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
            // Creator has a role-specific offline stub.
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
            Assert.AreEqual(
                "{\"status\": \"offline\", \"role\":\"unknownrole\"}",
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
        public async Task CompleteAsync_SmartChat_DoesNotEchoUserPayload()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);

            string huge = "{\"telemetry\":{},\"hint\":\"hi\",\"blob\":\"" + new string('x', 5000) + "\"}";
            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                UserPayload = huge
            });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(settings.OfflineCustomResponse, result.Content);
            StringAssert.DoesNotContain("blob\":", result.Content);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_TeacherRole_UsesShortOfflineMessage()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            OfflineLlmClient client = new(settings);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                UserPayload = "{\"telemetry\":{},\"hint\":\"ку\"}"
            });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(settings.OfflineCustomResponse, result.Content);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_CustomResponse_ShouldReturnCustomText()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            settings.ConfigureOffline(true, "Custom offline message");

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

            settings.ConfigureOffline(true, "Custom", "Creator");

            OfflineLlmClient client = new(settings);

            // Creator gets the custom response.
            LlmCompletionResult result1 = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Creator",
                UserPayload = "test"
            });
            Assert.AreEqual("Custom", result1.Content);

            // Programmer keeps the role-specific stub.
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
