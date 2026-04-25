using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
#if !COREAI_NO_LLM
    public sealed class MeaiLlmClientEditModeTests
    {
        [Test]
        public void CreateHttp_WithOpenAiSettings_ShouldNotThrow()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateHttp_WithCoreAiSettings_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            OpenAiChatLlmClient client = new(settings);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateLlmUnity_RequiresAgent()
        {
            Exception ex = Assert.Catch<Exception>(() =>
            {
                MeaiLlmClient.CreateLlmUnity(null, GameLoggerUnscopedFallback.Instance, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
            });

#if UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            Assert.That(ex, Is.TypeOf<NotSupportedException>());
#else
            Assert.That(ex, Is.TypeOf<System.ArgumentNullException>());
#endif
        }

        [Test]
        public void BuildAIFunctions_ShouldCreateMemoryTool()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger, memoryStore);

            List<ILlmTool> tools = new() { new MemoryLlmTool() };
            client.SetTools(tools);

            UnityEngine.Object.DestroyImmediate(settings);
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState { Memory = "" };
                return true;
            }

            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => System.Array.Empty<ChatMessage>();
        }
    }
#endif
}
