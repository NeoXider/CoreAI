#if COREAI_LLM && !UNITY_WEBGL
using System.Collections;
using System.Threading;
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
        // WHY: 240 s memory write + 2 x (1 s pause + 120 s recall) + the 20 s LiveTestRequestScope reserve = 502 s;
        // 600 s leaves ~100 s for setup. TestAgentSetup.Initialize is not capped (SharedLlmUnity allows up to 600 s
        // for a cold GGUF load and 300 s when another test is already loading); the Cap on every wait protects the
        // request, so a slower load shortens the turn or ends in the test's own cancelling wait, never a stranded
        // request.
        private const int TestTimeoutMs = 600000;

        private LiveTestRequestScope _requests;
        private TestAgentSetup _setup;

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

            _setup?.Dispose();
            _setup = null;
        }

        /// <summary>
        /// Writes a single fact into agent memory via the memory tool, then performs a recall turn
        /// and asserts that the published <c>ApplyAiGameCommand</c> sink contains the recalled fact.
        /// Backend choice follows <see cref="CoreAI.Infrastructure.Llm.CoreAISettingsAsset.BackendType"/>
        /// (Auto picks the first reachable backend).
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaAuto()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();

            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[Test] Backend: {setup.BackendName}");

            // Task 1: Write memory
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task t1 = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Please remember for later that the player likes apples."
            }, cts.Token));
            yield return setup.RunAndWait(t1, _requests.Cap(240f), "creator memory write", cts);

            if (!setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState st) ||
                string.IsNullOrWhiteSpace(st.Memory))
            {
                Debug.LogWarning("[Test] Model did not write memory");
                Assert.Fail("Model did not write memory via memory tool.");
            }

            StringAssert.Contains("apples", st.Memory.ToLowerInvariant(), "Memory should contain the remembered fact.");
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
                new NullAiOrchestrationMetrics(), ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                new Authority.LocalActorIdentityProvider("agent-memory-real-model-test"));

            const int recallMaxAttempts = 2;
            string recallResult = null;
            for (int attempt = 1; attempt <= recallMaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    yield return new WaitForSecondsRealtime(1f);
                }

                Task<string> tRecall = _requests.Track(orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "What is your available memory about apples?"
                }, cts.Token));
                yield return setup.RunAndWait(tRecall, _requests.Cap(120f),
                    $"creator memory recall ({attempt}/{recallMaxAttempts})", cts);

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
            StringAssert.Contains("apples", sink2.Items[0].JsonPayload.ToLowerInvariant());
            Debug.Log("[Test] Test completed successfully!");
        }
    }
}
#endif
