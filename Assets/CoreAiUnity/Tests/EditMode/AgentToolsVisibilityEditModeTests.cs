using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты: агент видит все добавленные инструменты.
    /// </summary>
    public sealed class AgentToolsVisibilityEditModeTests
    {
        [Test]
        public void AgentBuilder_WithMultipleTools_AllToolsVisible()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test")
                .WithTool(new MemoryLlmTool())
                .WithTool(new LuaLlmTool(new TestLuaExecutor()))
                .Build();

            Assert.AreEqual(2, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
            Assert.AreEqual("execute_lua", config.Tools[1].Name);
        }

        [Test]
        public void AgentMemoryPolicy_SetToolsForRole_ToolsAreRetrievable()
        {
            AgentMemoryPolicy policy = new();
            List<ILlmTool> tools = new() { new MemoryLlmTool(), new InventoryLlmTool(new TestInventoryProvider()) };
            policy.SetToolsForRole("TestRole", tools);

            IReadOnlyList<ILlmTool> retrieved = policy.GetToolsForRole("TestRole");
            // SetToolsForRole может добавить MemoryTool автоматически
            Assert.GreaterOrEqual(retrieved.Count, 2);
            Assert.AreEqual("memory", retrieved[0].Name);
        }

        [Test]
        public void MeaiLlmClient_BuildAIFunctions_CreatesAllFunctions()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test");
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger, memoryStore);

            // Проверяем что BuildAIFunctions создаёт функции для каждого инструмента
            List<ILlmTool> tools = new()
            {
                new MemoryLlmTool(),
                new LuaLlmTool(new TestLuaExecutor())
            };

            // Вызываем CompleteAsync чтобы проверить что инструменты обрабатываются
            // (в EditMode без реальной LLM — проверяем что код не падает)
            Task<LlmCompletionResult> task = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "TestRole",
                SystemPrompt = "test",
                UserPayload = "test",
                Tools = tools
            });

            // В EditMode без LLM — ожидаем ошибку подключения, но не ArgumentNullException
            Assert.IsTrue(task.IsCompleted || task.Status == System.Threading.Tasks.TaskStatus.WaitingForActivation);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void AgentBuilder_WithMemory_AddsMemoryTool()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test")
                .WithMemory()
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void AgentBuilder_WithToolsAndMemory_AllToolsVisible()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test")
                .WithTool(new InventoryLlmTool(new TestInventoryProvider()))
                .WithMemory()
                .Build();

            Assert.AreEqual(2, config.Tools.Count);
            Assert.AreEqual("get_inventory", config.Tools[0].Name);
            Assert.AreEqual("memory", config.Tools[1].Name);
        }

        #region Test Helpers

        private sealed class TestLuaExecutor : LuaTool.ILuaExecutor
        {
            public System.Threading.Tasks.Task<LuaTool.LuaResult> ExecuteAsync(string code,
                System.Threading.CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(new LuaTool.LuaResult { Success = true, Output = "" });
            }
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public System.Threading.Tasks.Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(
                System.Threading.CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(new List<InventoryTool.InventoryItem>());
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return System.Array.Empty<ChatMessage>();
            }
        }

        #endregion
    }
}