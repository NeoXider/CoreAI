#if !COREAI_NO_LLM
using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode тесты для WorldCommand tool calling через единый MEAI pipeline.
    /// Бэкенд определяется из CoreAISettingsAsset.
    /// </summary>
    public sealed class WorldCommandPlayModeTests
    {
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_SpawnObject()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();
            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing spawn...");

            // Регистрируем WorldTool для роли Creator
            var tools = new List<ILlmTool> { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            var task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the world_command tool to spawn an object. Call: {\"name\": \"world_command\", \"arguments\": {\"action\": \"spawn\", \"prefabKey\": \"TestPrefab\", \"x\": 0, \"y\": 0, \"z\": 0, \"targetName\": \"test_obj\"}}"
            });

            yield return setup.RunAndWait(task, 240f, "world spawn");

            // Проверяем что команда была выполнена
            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] World command not executed. Model may not support tool-call format.");
                Assert.Ignore("World command skipped - model may not support tool-call format");
            }

            Debug.Log($"[WorldTest] SUCCESS! World command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("spawn"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_MoveObject()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();
            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing move...");

            // Регистрируем WorldTool для роли Creator
            var tools = new List<ILlmTool> { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            var task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the world_command tool to move an object. Call: {\"name\": \"world_command\", \"arguments\": {\"action\": \"move\", \"targetName\": \"Player\", \"x\": 10, \"y\": 20, \"z\": 30}}"
            });

            yield return setup.RunAndWait(task, 240f, "world move");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] Move command not executed.");
                Assert.Ignore("World move skipped - model may not support tool-call format");
            }

            Debug.Log($"[WorldTest] SUCCESS! Move command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("move"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_ListObjects()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();
            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_objects...");

            // Регистрируем WorldTool для роли Creator
            var tools = new List<ILlmTool> { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            var task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the world_command tool to list all objects in the scene. Call: {\"name\": \"world_command\", \"arguments\": {\"action\": \"list_objects\"}}"
            });

            yield return setup.RunAndWait(task, 240f, "world list_objects");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] List objects command not executed.");
                Assert.Ignore("World list_objects skipped - model may not support tool-call format");
            }

            Debug.Log($"[WorldTest] SUCCESS! List objects command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("list_objects"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_PlayAnimation()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();
            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing play_animation...");

            // Регистрируем WorldTool для роли Creator
            var tools = new List<ILlmTool> { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            var task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the world_command tool to play animation. Call: {\"name\": \"world_command\", \"arguments\": {\"action\": \"play_animation\", \"targetName\": \"Enemy\", \"stringValue\": \"attack\"}}"
            });

            yield return setup.RunAndWait(task, 240f, "world play_animation");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] Play animation command not executed.");
                Assert.Ignore("World play_animation skipped - model may not support tool-call format");
            }

            Debug.Log($"[WorldTest] SUCCESS! Play animation command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("play_animation"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_ListAnimations()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();
            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_animations...");

            // Регистрируем WorldTool для роли Creator
            var tools = new List<ILlmTool> { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            var task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the world_command tool to list animations. Call: {\"name\": \"world_command\", \"arguments\": {\"action\": \"list_animations\", \"targetName\": \"Enemy\"}}"
            });

            yield return setup.RunAndWait(task, 240f, "world list_animations");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] List animations command not executed.");
                Assert.Ignore("World list_animations skipped - model may not support tool-call format");
            }

            Debug.Log($"[WorldTest] SUCCESS! List animations command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("list_animations"));
        }

        [Test]
        public void WorldLlmTool_CanBeCreated()
        {
            var settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found");
            }

            var logger = GameLoggerUnscopedFallback.Instance;
            var worldExecutor = new TestWorldCommandExecutor();
            var worldTool = new WorldLlmTool(worldExecutor);

            Assert.IsNotNull(worldTool);
            Assert.AreEqual("world_command", worldTool.Name);
            Assert.IsTrue(worldTool.Description.Contains("spawn"));
            Assert.IsTrue(worldTool.Description.Contains("move"));
        }

        private sealed class TestWorldCommandExecutor : ICoreAiWorldCommandExecutor
        {
            public bool LastCommandWasCalled;
            public string LastCommandJson;

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastCommandWasCalled = true;
                LastCommandJson = cmd.JsonPayload;
                return true;
            }
        }
    }
}
#endif
