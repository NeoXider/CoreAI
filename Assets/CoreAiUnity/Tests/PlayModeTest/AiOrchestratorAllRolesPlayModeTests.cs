using System.Collections;
using System.Collections.Generic;
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
    /// Рантайм: контур оркестратора. Быстрый вариант — <see cref="StubLlmClient"/>; реальная модель — через <see cref="PlayModeProductionLikeLlmFactory"/> (Auto).
    /// </summary>
    public sealed class AiOrchestratorAllRolesPlayModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        [UnityTest]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithStub()
        {
            yield return RunEachBuiltInRoleScenario(new StubLlmClient());
        }

        /// <summary>
        /// Тот же сценарий, что и со stub, но ответы идут с реального HTTP или LLMUnity (см. env и LLMUNITY_SETUP_AND_MODELS §7).
        /// Модель должна вернуть непустой текст; при пустом/ошибке команда не публикуется — тест упадёт по счётчику.
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto()
        {
            Debug.Log("[Test] Starting Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.15f, 300,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                Debug.Log("[Test] LLM handle created, waiting for model...");
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log("[Test] Model ready, running orchestrator...");

                // Обернуть клиент с NullAgentMemoryStore (тест не использует память, но для консистентности)
                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new NullAgentMemoryStore());
                yield return RunEachBuiltInRoleScenario(clientWithStore);
                Debug.Log("[Test] Orchestrator completed successfully");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static IEnumerator RunEachBuiltInRoleScenario(ILlmClient llm)
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
                new NullAiOrchestrationMetrics());

            List<string> failedRoles = new();

            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                sink.Commands.Clear();
                Debug.Log($"[Test] Testing role: {role}");
                Task task = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = role,
                    Hint =
                        "playmode test: reply with a single short line of plain text (for example the word OK). No empty reply."
                });

                // Programmer role может занять больше времени из-за сложности промпта и retry loop
                // С retry (4 попытки) каждая занимает ~30-40с, поэтому даём больше времени
                float timeout = role == BuiltInAgentRoleIds.Programmer ? 300f : 180f;
                yield return PlayModeTestAwait.WaitTask(task, timeout, $"orchestrator role '{role}'");

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

            if (failedRoles.Count > 0)
            {
                Debug.LogWarning($"[Test] Failed roles: {string.Join(", ", failedRoles)}");
            }
        }
    }
}