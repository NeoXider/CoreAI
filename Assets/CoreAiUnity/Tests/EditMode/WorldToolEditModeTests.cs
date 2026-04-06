using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для WorldTool (MEAI function calling для world commands).
    /// </summary>
    [TestFixture]
    public sealed class WorldToolEditModeTests
    {
        #region WorldTool Tests

        [Test]
        public void WorldTool_CreateAIFunction_ReturnsNonNull()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("world_command", function.Name);
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_Spawn_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("spawn", prefabKey: "Enemy", x: 1f, y: 2f, z: 3f);
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("spawn", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("spawn"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Enemy"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_Move_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("move", instanceId: "obj1", x: 10f, y: 20f, z: 30f);
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("move", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("move"));
            Assert.IsTrue(executor.LastCommandJson.Contains("obj1"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_Destroy_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("destroy", instanceId: "obj1");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("destroy", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("destroy"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_SpawnWithoutPrefab_ReturnsError()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("spawn", instanceId: "obj1", x: 0f, y: 0f, z: 0f);
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.Contains("required") || result.Message.Contains("Missing"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_UnknownAction_ReturnsError()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("invalid_action");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.Contains("Unknown action"));
        }

        // TODO: play_sound удалён из WorldTool, будет реализован отдельно через IAudioController (на потом)

        [Test]
        public async Task WorldTool_ExecuteAsync_PlayAnimation_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("play_animation", instanceId: "enemy1", stringValue: "attack");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("play_animation", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("play_animation"));
            Assert.IsTrue(executor.LastCommandJson.Contains("attack"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_LoadScene_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("load_scene", stringValue: "Level2");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("load_scene", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("load_scene"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Level2"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_ListObjects_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("list_objects");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("list_objects", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("list_objects"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_ListAnimations_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("list_animations", instanceId: "enemy1");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("list_animations", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("list_animations"));
            Assert.IsTrue(executor.LastCommandJson.Contains("enemy1"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_MoveWithTargetName_IncludesTargetName()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("move", targetName: "Player", x: 10f, y: 20f, z: 30f);
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("move", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("move"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Player"));
        }

        [Test]
        public async Task WorldTool_ExecuteAsync_DestroyWithTargetName_IncludesTargetName()
        {
            TestWorldExecutor executor = new();
            WorldTool tool = new(executor);

            string resultJson = await tool.ExecuteAsync("destroy", targetName: "Enemy");
            WorldTool.WorldResult result = JsonSerializer.Deserialize<WorldTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("destroy", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("destroy"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Enemy"));
        }

        #endregion

        #region WorldLlmTool Tests

        [Test]
        public void WorldLlmTool_CreateAIFunction_ReturnsNonNull()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("world_command", function.Name);
            StringAssert.Contains("world", function.Description.ToLowerInvariant());
        }

        [Test]
        public void WorldLlmTool_Properties_AreValid()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor);

            Assert.AreEqual("world_command", tool.Name);
            StringAssert.Contains("spawn", tool.Description);
            StringAssert.Contains("move", tool.Description);
            StringAssert.Contains("destroy", tool.Description);
            StringAssert.Contains("action", tool.ParametersSchema);
        }

        #endregion

        #region Test Helpers

        private sealed class TestWorldExecutor : ICoreAiWorldCommandExecutor
        {
            public string LastCommandJson;

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastCommandJson = cmd.JsonPayload;
                return true; // Всегда возвращаем успех для тестов
            }
        }

        #endregion
    }
}
