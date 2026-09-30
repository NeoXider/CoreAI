#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode: one natural-language <see cref="AiTaskRequest.Hint"/> should drive several tool types in sequence
    /// (e.g. <see cref="WorldLlmTool"/> then <see cref="MemoryLlmTool"/>) and end with a normal assistant reply.
    /// Requires a live tool-capable HTTP / LLMUnity backend (same as <see cref="WorldCommandPlayModeTests"/>).
    /// </summary>
    public sealed class MultiToolChainPlayModeTests
    {
        // WHY: one 240 s multi-tool turn + the 20 s LiveTestRequestScope reserve = 260 s; 360 s leaves ~100 s for
        // setup. TestAgentSetup.Initialize is not capped (SharedLlmUnity allows up to 600 s for a cold GGUF load
        // and 300 s when another test is already loading); the Cap on every wait protects the request, so a slower
        // load shortens the turn or ends in the test's own cancelling wait, never a stranded request.
        private const int TestTimeoutMs = 360000;

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

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Creator_OneHint_WorldSpawnThenMemoryWriteThenPlainReply()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            }

            WorldLlmTool worldTool = new(setup.WorldExecutor, settings, GameLoggerUnscopedFallback.Instance);

            new AgentBuilder(BuiltInAgentRoleIds.Creator)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .WithTool(worldTool)
                .Build()
                .ApplyToPolicy(setup.Policy);

            setup.MemoryStore.Clear(BuiltInAgentRoleIds.Creator);

            const string marker = "MultiToolChainTest";

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Spawn TestPrefab as chain_obj at position (1,2,3), remember the note '" + marker +
                    ": spawned chain_obj at (1,2,3)', then answer with one short confirmation."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "multi-tool chain", cts);

            bool spawned = setup.WorldExecutor.AllCommandsJson.Any(static j =>
                j != null &&
                j.Contains("spawn", StringComparison.OrdinalIgnoreCase) &&
                j.Contains("chain_obj", StringComparison.OrdinalIgnoreCase));

            Assert.IsTrue(spawned,
                "Expected at least one world_command spawn for chain_obj. Got: " +
                string.Join(" | ", setup.WorldExecutor.AllCommandsJson));

            bool loaded = setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState mem);
            bool memOk = loaded && mem != null && !string.IsNullOrWhiteSpace(mem.Memory) &&
                         mem.Memory.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

            Assert.IsTrue(memOk,
                $"Memory should contain marker '{marker}'. Loaded={loaded}, body='{mem?.Memory ?? "(null)"}'");

            Debug.Log("[MultiToolChain] ✓ world spawn + memory + reply chain");
        }
    }
}
#endif
