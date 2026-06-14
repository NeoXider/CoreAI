using System.Collections;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live backend: same built-in role sweep as the stub test, using <see cref="PlayModeProductionLikeLlmFactory"/> (Auto).
    /// </summary>
    public sealed class AiOrchestratorBuiltInRolesProductionLlmPlayModeTests
    {
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto()
        {
            Debug.Log("[Test] Starting Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.15f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            LogAssert.ignoreFailingMessages = true;
            try
            {
                Debug.Log("[Test] LLM handle created, waiting for model...");
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log("[Test] Model ready, running orchestrator...");

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new NullAgentMemoryStore());
                yield return AiOrchestratorBuiltInRolesPlayModeHarness.RunEachBuiltInRoleScenario(clientWithStore);
                Debug.Log("[Test] Orchestrator completed successfully");
            }
            finally
            {
                handle.Dispose();
                LogAssert.ignoreFailingMessages = false;
            }
        }
    }
}
