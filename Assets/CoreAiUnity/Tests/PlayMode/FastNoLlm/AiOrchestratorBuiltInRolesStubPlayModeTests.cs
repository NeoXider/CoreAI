using System.Collections;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Fast path: all built-in roles with <see cref="StubLlmClient"/> (no model / HTTP).
    /// </summary>
    public sealed class AiOrchestratorBuiltInRolesStubPlayModeTests
    {
        [UnityTest]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithStub()
        {
            yield return AiOrchestratorBuiltInRolesPlayModeHarness.RunEachBuiltInRoleScenario(new StubLlmClient());
        }
    }
}