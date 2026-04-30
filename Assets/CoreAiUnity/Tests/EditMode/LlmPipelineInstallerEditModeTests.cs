#if !COREAI_NO_LLM
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Tests for <see cref="LlmPipelineInstaller"/> verifying that
    /// <see cref="IAgentMemoryStore"/> is correctly propagated through
    /// all LLM client construction paths (HTTP, ServerManaged, Auto).
    /// </summary>
    [TestFixture]
    public sealed class LlmPipelineInstallerEditModeTests
    {
        private sealed class StubMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state) { state = default; return false; }
            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => null;
        }

        /// <summary>
        /// BuildHttpClient must propagate memoryStore to OpenAiChatLlmClient
        /// so that the memory tool's AIFunction can be bound in MeaiLlmClient.
        /// Before the fix, BuildHttpClient was called without memoryStore,
        /// causing memory tool calls to be silently stripped as no-ops.
        /// </summary>
        [Test]
        public void BuildHttpClient_PassesMemoryStore_ToOpenAiChatLlmClient()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // Set minimum required fields for HTTP client creation
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            var memoryStore = new StubMemoryStore();

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientOwnedApi, memoryStore);

            Assert.IsNotNull(client, "BuildHttpClient should return a client");
            Assert.IsInstanceOf<OpenAiChatLlmClient>(client,
                "ClientOwnedApi mode should return OpenAiChatLlmClient");

            // Verify memory store was propagated by checking the inner MeaiLlmClient
            var meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(meaiField, "Should find _client field on OpenAiChatLlmClient");
            var meaiClient = meaiField.GetValue(client);
            Assert.IsNotNull(meaiClient, "MeaiLlmClient should exist");

            var storeField = meaiClient.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(storeField, "Should find _memoryStore field on MeaiLlmClient");
            var actualStore = storeField.GetValue(meaiClient);

            Assert.AreSame(memoryStore, actualStore,
                "BuildHttpClient must propagate memoryStore to MeaiLlmClient — " +
                "otherwise memory tool calls are silently stripped in HTTP modes");
        }

        [Test]
        public void BuildHttpClient_WithoutMemoryStore_StillCreatesClient()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientOwnedApi);

            Assert.IsNotNull(client, "Should work without memoryStore (backwards compatible)");

            // memoryStore should be null in the inner client
            var meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            var meaiClient = meaiField?.GetValue(client);
            var storeField = meaiClient?.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            var actualStore = storeField?.GetValue(meaiClient);

            Assert.IsNull(actualStore, "memoryStore should be null when not provided");
        }

        [Test]
        public void BuildHttpClient_ClientLimited_StillPassesMemoryStore()
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            SetField(settings, "apiBaseUrl", "http://localhost:1234/v1");
            SetField(settings, "modelName", "test-model");

            var memoryStore = new StubMemoryStore();

            ILlmClient client = LlmPipelineInstaller.BuildHttpClient(
                settings, LlmExecutionMode.ClientLimited, memoryStore);

            Assert.IsNotNull(client, "Should return a client for ClientLimited mode");
            // ClientLimited wraps in decorator — unwrap to verify
            Assert.IsInstanceOf<ClientLimitedLlmClientDecorator>(client,
                "ClientLimited should wrap in ClientLimitedLlmClientDecorator");

            var innerField = typeof(ClientLimitedLlmClientDecorator)
                .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(innerField, "Should find _inner field");
            var innerClient = innerField.GetValue(client) as OpenAiChatLlmClient;
            Assert.IsNotNull(innerClient, "Inner client should be OpenAiChatLlmClient");

            var meaiField = typeof(OpenAiChatLlmClient)
                .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            var meaiClient = meaiField?.GetValue(innerClient);
            var storeField = meaiClient?.GetType()
                .GetField("_memoryStore", BindingFlags.NonPublic | BindingFlags.Instance);
            var actualStore = storeField?.GetValue(meaiClient);

            Assert.AreSame(memoryStore, actualStore,
                "ClientLimited mode must also propagate memoryStore through the decorator chain");
        }

        private static void SetField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field == null)
            {
                // Unity SerializedField naming
                field = obj.GetType().GetField(fieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            field?.SetValue(obj, value);
        }
    }
}
#endif
