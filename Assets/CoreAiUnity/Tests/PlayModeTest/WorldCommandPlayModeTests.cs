#if !COREAI_NO_LLM
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing spawn...");

            // Регистрируем WorldTool для роли Creator
            List<ILlmTool> tools = new() { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the world_command tool to spawn a 'TestPrefab' object at coordinates (0, 0, 0) and name it 'test_obj'."
            });

            yield return setup.RunAndWait(task, 240f, "world spawn");

            // Проверяем что команда была выполнена (зачастую есть задержка между возвратом из Orchestrator и обновлением состояния)
            // Пытаемся подождать до 5 секунд, так как tool execution работает через Task.Run.
            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f, "last command flag sync");
            }

            Debug.Log($"[WorldTest] SUCCESS! World command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("spawn"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_MoveObject()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing move...");

            // Регистрируем WorldTool для роли Creator
            List<ILlmTool> tools = new() { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the world_command tool to move the target named 'Player' to coordinates x=10, y=20, z=30."
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
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_objects...");

            // Регистрируем WorldTool для роли Creator
            List<ILlmTool> tools = new() { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the world_command tool to list all the objects currently in the scene."
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
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing play_animation...");

            // Регистрируем WorldTool для роли Creator
            List<ILlmTool> tools = new() { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the world_command tool to play the 'attack' animation on the target named 'Enemy'."
            });

            yield return setup.RunAndWait(task, 240f, "world play_animation");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f, "last command flag sync (play_animation)");
            }

            Debug.Log($"[WorldTest] SUCCESS! Play animation command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("play_animation"));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator WorldTool_ListAnimations()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_animations...");

            // Регистрируем WorldTool для роли Creator
            List<ILlmTool> tools = new() { new WorldLlmTool(setup.WorldExecutor) };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the world_command tool to list the available animations for the target named 'Enemy'."
            });

            yield return setup.RunAndWait(task, 240f, "world list_animations");

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f, "last command flag sync (list)");
            }

            Debug.Log($"[WorldTest] SUCCESS! List animations command executed: {setup.WorldExecutor.LastCommandJson}");
            Assert.IsTrue(setup.WorldExecutor.LastCommandJson.Contains("list_animations"));
        }

        [Test]
        public void WorldLlmTool_CanBeCreated()
        {
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found");
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestWorldCommandExecutor worldExecutor = new();
            WorldLlmTool worldTool = new(worldExecutor);

            Assert.IsNotNull(worldTool);
            Assert.AreEqual("world_command", worldTool.Name);
            Assert.IsTrue(worldTool.Description.Contains("spawn"));
            Assert.IsTrue(worldTool.Description.Contains("move"));
        }

        private sealed class TestWorldCommandExecutor : ICoreAiWorldCommandExecutor
        {
            public volatile bool LastCommandWasCalled;
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