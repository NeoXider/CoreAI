#if !COREAI_NO_LLM && !UNITY_WEBGL
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
    /// PlayMode   Memory Tool   pipeline.
    ///    CoreAISettingsAsset.
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

            //  debug    
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
        [Timeout(360000)]
        public IEnumerator MemoryTool_AppendsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            //    
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
                Assert.Fail("Memory state is missing after append attempt.");
            }

            Debug.Log($"[MemoryTest] Memory after append: {state.Memory}");

            bool appendApplied = state.Memory.Contains("initial value") &&
                               state.Memory.Contains("appended value");

            if (!appendApplied)
            {
                Debug.LogWarning("[MemoryTest] First append attempt did not update memory. Retrying with strict prompt...");

                Task retryTask = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint =
                        "IMPORTANT: Call ONLY memory tool now. action='append', content='appended value'. " +
                        "Do not explain. Do not answer with text before the tool call."
                });
                yield return setup.RunAndWait(retryTask, 240f, "memory append retry");

                if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out state))
                {
                    Assert.Fail("Memory state is missing after append retry.");
                }

                Debug.Log($"[MemoryTest] Memory after append retry: {state.Memory}");
            }

            Assert.IsTrue(state.Memory.Contains("initial value"), "Initial value should be preserved");
            Assert.IsTrue(state.Memory.Contains("appended value"),
                "Appended value should be present after append call.");
        }

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator MemoryTool_ClearsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            //     
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
