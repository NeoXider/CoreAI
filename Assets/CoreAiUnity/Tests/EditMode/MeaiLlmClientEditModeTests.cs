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
    /// <summary>
    /// EditMode тесты для MeaiLlmClient (фабричные методы).
    /// </summary>
    public sealed class MeaiLlmClientEditModeTests
    {
        [Test]
        public void CreateHttp_WithOpenAiSettings_ShouldNotThrow()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.GetType()
                .GetField("useOpenAiCompatibleHttp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, true);

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger);

            Assert.IsNotNull(client);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateHttp_WithCoreAiSettings_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger);

            Assert.IsNotNull(client);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateLlmUnity_RequiresAgent()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
            {
                MeaiLlmClient.CreateLlmUnity(null, GameLoggerUnscopedFallback.Instance);
            });
        }

        [Test]
        public void BuildAIFunctions_ShouldCreateMemoryTool()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger, memoryStore);

            // Проверяем что MemoryLlmTool создаёт AIFunction
            List<ILlmTool> tools = new() { new MemoryLlmTool() };
            client.SetTools(tools);

            Object.DestroyImmediate(settings);
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState { Memory = "" };
                return true;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return System.Array.Empty<ChatMessage>();
            }
        }
    }
}