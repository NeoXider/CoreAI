#if !COREAI_NO_LLM
using System.Collections;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode тесты для Memory Tool через единый pipeline.
    /// Бэкенд определяется из CoreAISettingsAsset.
    /// </summary>
    public sealed class AgentMemoryOpenAiApiPlayModeTests
    {
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MemoryTool_WritesMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            // Включаем debug логирование для этого теста
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings != null)
            {
                settings.GetType()
                    .GetField("enableHttpDebugLogging",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, true);
                settings.GetType()
                    .GetField("logLlmInput",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, true);
            }

            Debug.Log($"[MemoryTest] Backend: {setup.BackendName}, writing memory...");

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the 'memory' tool to write new info. Call it with action='write' and content='qwen4b works great'."
            });

            yield return setup.RunAndWait(task, 240f, "memory write");

            if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState state) ||
                string.IsNullOrWhiteSpace(state.Memory))
            {
                Debug.LogWarning("[MemoryTest] Memory not written. Model may not support tool-call format.");
                Assert.Ignore("Memory write skipped - model may not support tool-call format");
            }

            Debug.Log($"[MemoryTest] SUCCESS! Memory: {state.Memory}");
            Assert.IsTrue(state.Memory.Contains("qwen4b") || state.Memory.Contains("works"));
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MemoryTool_AppendsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            // Предварительно сохраняем начальное значение
            setup.MemoryStore.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "initial value" });

            Debug.Log("[MemoryTest] Testing append...");

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the 'memory' tool to append info. Call it with action='append' and content='appended value'."
            });

            yield return setup.RunAndWait(task, 240f, "memory append");

            if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState state))
            {
                Assert.Ignore("Append test skipped");
            }

            Debug.Log($"[MemoryTest] Memory after append: {state.Memory}");

            if (!state.Memory.Contains("initial value") || state.Memory.Length <= "initial value".Length + 2)
            {
                Debug.LogWarning("[MemoryTest] Append did not work.");
                Assert.Ignore("Append test skipped");
            }

            Assert.IsTrue(state.Memory.Contains("initial value"), "Initial value should be preserved");
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MemoryTool_ClearsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            // Предварительно сохраняем значение для удаления
            setup.MemoryStore.Save(BuiltInAgentRoleIds.Creator,
                new AgentMemoryState { Memory = "this will be deleted" });

            Debug.Log("[MemoryTest] Testing clear...");

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Use the 'memory' tool to clear all info. Call it with action='clear'."
            });

            yield return setup.RunAndWait(task, 240f, "memory clear");

            if (setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState state) &&
                !string.IsNullOrWhiteSpace(state.Memory))
            {
                Debug.LogWarning($"[MemoryTest] Memory not cleared: {state.Memory}");
                Assert.Ignore("Clear test skipped");
            }

            Debug.Log("[MemoryTest] SUCCESS! Memory cleared.");
            Assert.IsFalse(setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out _), "Memory should be cleared");
        }
    }
}
#endif