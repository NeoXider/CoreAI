using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Shared PlayMode runner: every built-in role through <see cref="AiOrchestrator"/> with a supplied <see cref="ILlmClient"/>.
    /// </summary>
    public static class AiOrchestratorBuiltInRolesPlayModeHarness
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        public static IEnumerator RunEachBuiltInRoleScenario(ILlmClient llm)
        {
            ListSink sink = new();
            SoloAuthorityHost host = new();
            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            AiOrchestrator orch = new(
                host,
                llm,
                sink,
                telemetry,
                composer,
                new NullAgentMemoryStore(),
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>());

            List<string> failedRoles = new();

            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                sink.Commands.Clear();
                Debug.Log($"[Test] Testing role: {role}");

                // WHY: a slow model can blow the per-role budget, and without a token the abandoned
                // request keeps holding the single live backend — the next roles then fail for lack of a
                // free model rather than for anything of their own. Cancelling on timeout keeps one slow
                // role from cascading into the rest of the sweep.
                using CancellationTokenSource roleCts = new();
                Task task = orch.RunTaskAsync(
                    new AiTaskRequest
                    {
                        RoleId = role,
                        Hint =
                            "playmode test: reply with a single short line of plain text (for example the word OK). No empty reply."
                    },
                    roleCts.Token);

                float timeout = role == BuiltInAgentRoleIds.Programmer ? 300f : 180f;
                yield return PlayModeTestAwait.WaitTask(task, timeout, $"orchestrator role '{role}'", roleCts);

                if (sink.Commands.Count == 0)
                {
                    Debug.LogWarning($"[Test] Role {role} produced no commands, continuing...");
                    failedRoles.Add(role);
                    continue;
                }

                Assert.AreEqual(1, sink.Commands.Count, role);
                Assert.AreEqual(Envelope, sink.Commands[0].CommandTypeId);
                Assert.IsFalse(string.IsNullOrEmpty(sink.Commands[0].JsonPayload), role);
                Debug.Log($"[Test] Role {role} passed, response: {sink.Commands[0].JsonPayload}");
            }

            // WHY: a role that publishes no envelope is a real regression (LLM produced nothing usable),
            // not a warning. Fail the sweep so zero-output roles cannot pass green.
            Assert.AreEqual(
                0,
                failedRoles.Count,
                $"Built-in roles produced no command envelope: {string.Join(", ", failedRoles)}");
        }
    }
}
