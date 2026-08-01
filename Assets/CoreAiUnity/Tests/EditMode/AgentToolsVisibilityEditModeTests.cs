using System;
using System.Collections.Generic;
using System.Linq;
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
    /// EditMode coverage that verifies an agent exposes every registered tool.
    /// </summary>
    public sealed class AgentToolsVisibilityEditModeTests
    {
        [Test]
        public void AgentBuilder_WithMultipleTools_AllToolsVisible()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test")
                .WithTool(new MemoryLlmTool())
                .WithTool(new LuaLlmTool(new TestLuaExecutor(), ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    Logging.NullLog.Instance))
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
            // Кастомный список уже содержит memory — синглтон из политики не дублируется.
            Assert.AreEqual(2, retrieved.Count);
            Assert.AreEqual("memory", retrieved[0].Name);
        }

        [Test]
        public void AgentMemoryPolicy_SetToolsForRole_PreservesSkillMetaTools()
        {
            AgentMemoryPolicy policy = new();
            policy.AddSkillForRole("TestRole",
                new SkillSet("MemorySkill", "Uses memory", "Remember facts.", new MemoryLlmTool()));

            policy.SetToolsForRole("TestRole", new ILlmTool[]
            {
                new InventoryLlmTool(new TestInventoryProvider())
            });

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("TestRole");
            Assert.IsTrue(tools.Any(tool => tool.Name == "read_skill"));
            Assert.IsTrue(tools.Any(tool => tool.Name == "call_skill_tool"));
            Assert.IsTrue(tools.Any(tool => tool.Name == "get_inventory"));
        }

        [Test]
        public void AgentMemoryPolicy_GetToolsForRole_DedupesMemory_AfterAgentBuilderApplyToPolicy()
        {
            AgentMemoryPolicy policy = new();
            new AgentBuilder(BuiltInAgentRoleIds.Creator)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .Build()
                .ApplyToPolicy(policy);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(BuiltInAgentRoleIds.Creator);
            int memoryCount = 0;
            foreach (ILlmTool t in tools)
            {
                if (string.Equals(t.Name, "memory", StringComparison.OrdinalIgnoreCase))
                {
                    memoryCount++;
                }
            }

            Assert.AreEqual(1, memoryCount);
        }

        [Test]
        public void MeaiLlmClient_BuildAIFunctions_MapsEveryRequestedToolOffline()
        {
#if !COREAI_LLM
            Assert.Ignore("COREAI_LLM is not set: MeaiLlmClient (HTTP/MEAI pipeline) is excluded from the build.");
#else
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset luaSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ConfigureHttpApi("http://offline.invalid/v1", "", "test");
                MeaiLlmClient client = MeaiLlmClient.CreateHttp(
                    settings,
                    GameLoggerUnscopedFallback.Instance,
                    new TestMemoryStore());
                List<ILlmTool> tools = new()
                {
                    new MemoryLlmTool(),
                    new LuaLlmTool(new TestLuaExecutor(), luaSettings, Logging.NullLog.Instance)
                };

                IReadOnlyList<Microsoft.Extensions.AI.AIFunction> functions =
                    client.BuildAIFunctions(tools, "TestRole");

                Assert.AreEqual(2, functions.Count);
                CollectionAssert.AreEqual(
                    new[] { "execute_lua", "memory" },
                    functions.Select(function => function.Name).ToArray());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(luaSettings);
                UnityEngine.Object.DestroyImmediate(settings);
            }
#endif
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
            public Task<LuaTool.LuaResult> ExecuteAsync(string code,
                System.Threading.CancellationToken ct)
            {
                return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = "" });
            }
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(
                System.Threading.CancellationToken ct)
            {
                return Task.FromResult(new List<InventoryTool.InventoryItem>());
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

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        #endregion
    }
}
