using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
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
            public readonly List<ApplyAiGameCommand> Commands = new List<ApplyAiGameCommand>();

            public void Publish(ApplyAiGameCommand command) => Commands.Add(command);
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
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.15f, 300, out var handle, out var ignore))
                Assert.Ignore(ignore);

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                yield return RunEachBuiltInRoleScenario(handle.Client);
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static IEnumerator RunEachBuiltInRoleScenario(ILlmClient llm)
        {
            var sink = new ListSink();
            var host = new SoloAuthorityHost();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            var orch = new AiOrchestrator(
                host,
                llm,
                sink,
                telemetry,
                composer,
                new NullAgentMemoryStore(),
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());

            foreach (var role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                sink.Commands.Clear();
                var task = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = role,
                    Hint =
                        "playmode test: reply with a single short line of plain text (for example the word OK). No empty reply."
                });
                yield return new WaitUntil(() => task.IsCompleted);
                Assert.AreEqual(1, sink.Commands.Count, role);
                Assert.AreEqual(Envelope, sink.Commands[0].CommandTypeId);
                Assert.IsFalse(string.IsNullOrEmpty(sink.Commands[0].JsonPayload), role);
            }
        }
    }
}
