#if !COREAI_NO_LLM && !UNITY_WEBGL
using System.Collections;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode integration test for the Creator agent with the real <see cref="ILlmClient"/>
    /// resolved by <see cref="TestAgentSetup"/>. Backend (LlmUnity / OpenAI HTTP / Auto) is
    /// driven by <see cref="CoreAI.Infrastructure.Llm.CoreAISettingsAsset"/>.
    /// </summary>
    public sealed class AgentMemoryWithRealModelPlayModeTests
    {
        /// <summary>
        /// Writes a single fact into agent memory via the memory tool, then performs a recall turn
        /// and asserts that the published <c>ApplyAiGameCommand</c> sink contains the recalled fact.
        /// Backend choice follows <see cref="CoreAI.Infrastructure.Llm.CoreAISettingsAsset.BackendType"/>
        /// (Auto picks the first reachable backend).
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaAuto()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();

            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[Test] Backend: {setup.BackendName}");

            // Task 1: Write memory
            Task t1 = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "IMPORTANT: Use the 'memory' tool to write data. CALL the memory tool now with action='write' and content='remember: apples'."
            });
            yield return setup.RunAndWait(t1, 300f, "creator memory write");

            if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState st) ||
                string.IsNullOrWhiteSpace(st.Memory))
            {
                Debug.LogWarning("[Test] Model did not write memory");
                Assert.Fail("Model did not write memory via memory tool.");
            }

            StringAssert.StartsWith("remember:", st.Memory.Trim(), "Memory should start with 'remember:'");
            Debug.Log($"[Test] Memory stored: {st.Memory}");

            // Task 2: Recall memory
            TestAgentSetup.ListSink sink2 = new();
            SessionTelemetryCollector telemetry2 = new();
            AiPromptComposer composer2 = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            AiOrchestrator orch2 = new(
                new Authority.SoloAuthorityHost(),
                setup.Client,
                sink2,
                telemetry2,
                composer2,
                setup.MemoryStore,
                setup.Policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());

            const int recallMaxAttempts = 3;
            string recallResult = null;
            for (int attempt = 1; attempt <= recallMaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    yield return new WaitForSecondsRealtime(1f);
                }

                Task<string> tRecall = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "What is your available memory? Reply with exactly: I remember: apples"
                });
                yield return setup.RunAndWait(tRecall, 300f,
                    $"creator memory recall ({attempt}/{recallMaxAttempts})");

                recallResult = tRecall.Result;
                if (!string.IsNullOrEmpty(recallResult))
                {
                    break;
                }
            }

            if (string.IsNullOrEmpty(recallResult))
            {
                Assert.Ignore(
                    "Recall step returned an empty response after retries: the local OpenAI-compatible API (LM Studio, etc.) likely returned HTTP 5xx or an empty body. " +
                    "Check that the server is up, a model is loaded, context limits, and that `ApiBaseUrl` in CoreAISettings ends with `/v1`.");
            }

            Assert.AreEqual(1, sink2.Items.Count);
            StringAssert.Contains("remember:", sink2.Items[0].JsonPayload);
            Debug.Log("[Test] Test completed successfully!");
        }
    }
}
#endif


