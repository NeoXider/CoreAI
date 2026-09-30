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
        // WHY: 120 s optional GGUF load + nine sequential role waits (Programmer 300 s, eight others 180 s each
        // = 1740 s) = 1860 s; 1920 s leaves the 20 s LiveTestRequestScope reserve plus margin. The former 600 s
        // aborted a slow sweep mid-role and left that role's request running.
        private const int TestTimeoutMs = 1_920_000;

        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup; cancel and let the abandoned turn unwind
            // before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _handle?.Dispose();
            _handle = null;
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto()
        {
            Debug.Log("[Test] Starting Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.15f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running role turn.
            _handle = handle;
            LogAssert.ignoreFailingMessages = true;

            Debug.Log("[Test] LLM handle created, waiting for model...");
            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            Debug.Log("[Test] Model ready, running orchestrator...");

            ILlmClient clientWithStore = handle.WrapWithMemoryStore(new NullAgentMemoryStore());
            yield return AiOrchestratorBuiltInRolesPlayModeHarness.RunEachBuiltInRoleScenario(clientWithStore, _requests);
            Debug.Log("[Test] Orchestrator completed successfully");
        }
    }
}
