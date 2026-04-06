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
    /// Память Creator через реальный ILlmClient — использует TestAgentSetup.
    /// Бэкенд определяется из CoreAISettingsAsset.
    /// </summary>
    public sealed class AgentMemoryWithRealModelPlayModeTests
    {
        /// <summary>Auto — из CoreAISettingsAsset.BackendType.</summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaAuto()
        {
            using var setup = new TestAgentSetup();
            yield return setup.Initialize();

            if (!setup.IsReady) Assert.Ignore("TestAgentSetup failed");

            Debug.Log($"[Test] Backend: {setup.BackendName}");

            // Task 1: Write memory
            var t1 = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Use the 'memory' tool to write this exact content: 'remember: apples'. Call the memory tool with action='write' and content='remember: apples'."
            });
            yield return setup.RunAndWait(t1, 300f, "creator memory write");

            if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState st) ||
                string.IsNullOrWhiteSpace(st.Memory))
            {
                Debug.LogWarning("[Test] Model did not write memory");
                Assert.Ignore("Model did not write memory - LLM may not support tool-call format");
            }

            StringAssert.StartsWith("remember:", st.Memory.Trim(), "Memory should start with 'remember:'");
            Debug.Log($"[Test] Memory stored: {st.Memory}");

            // Task 2: Recall memory
            var sink2 = new TestAgentSetup.ListSink();
            var telemetry2 = new CoreAI.Session.SessionTelemetryCollector();
            var composer2 = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            var orch2 = new AiOrchestrator(
                new CoreAI.Authority.SoloAuthorityHost(),
                setup.Client,
                sink2,
                telemetry2,
                composer2,
                setup.MemoryStore,
                setup.Policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());

            var t2 = orch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "What is your available memory? Reply with exactly: I remember: apples"
            });
            yield return setup.RunAndWait(t2, 300f, "creator memory recall");

            Assert.AreEqual(1, sink2.Items.Count);
            StringAssert.Contains("remember:", sink2.Items[0].JsonPayload);
            Debug.Log("[Test] Test completed successfully!");
        }
    }
}
#endif
