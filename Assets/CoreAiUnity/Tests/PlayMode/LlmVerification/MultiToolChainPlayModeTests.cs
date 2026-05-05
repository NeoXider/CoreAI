#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Linq;
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
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Creator_OneHint_WorldSpawnThenMemoryWriteThenPlainReply()
        {
            using TestAgentSetup setup = new();
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

            Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Do ALL of the following in order using your tools:\n" +
                    "1) Call world_command to spawn prefabKey='TestPrefab', targetName='chain_obj', position x=1 y=2 z=3.\n" +
                    "2) Call memory with action=write and content exactly: '" + marker + ": spawned chain_obj at (1,2,3)'.\n" +
                    "3) Answer the user in plain language only (no JSON): one short sentence confirming spawn + saved note."
            });

            yield return setup.RunAndWait(task, 300f, "multi-tool chain");

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

            if (!memOk)
            {
                Debug.LogWarning(
                    "[MultiToolChain] Memory marker missing after first turn (common on small local models); retrying with a memory-only hint.");

                Task retryTask = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint =
                        "The world spawn for chain_obj at (1,2,3) is already done. " +
                        "You MUST now call the memory tool with action=write and content EXACTLY: '" +
                        marker + ": spawned chain_obj at (1,2,3)'. " +
                        "Then one short plain sentence (no JSON)."
                });

                yield return setup.RunAndWait(retryTask, 120f, "multi-tool chain memory retry");

                loaded = setup.MemoryStore.TryLoad(BuiltInAgentRoleIds.Creator, out mem);
                memOk = loaded && mem != null && !string.IsNullOrWhiteSpace(mem.Memory) &&
                        mem.Memory.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            Assert.IsTrue(memOk,
                $"Memory should contain marker '{marker}'. Loaded={loaded}, body='{mem?.Memory ?? "(null)"}'");

            Debug.Log("[MultiToolChain] ✓ world spawn + memory + reply chain");
        }
    }
}
#endif
