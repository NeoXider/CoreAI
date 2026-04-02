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
    /// Рантайм (Play Mode): оркестратор + StubLlmClient публикуют команду для каждой встроенной роли.
    /// </summary>
    public sealed class AiOrchestratorAllRolesPlayModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new List<ApplyAiGameCommand>();

            public void Publish(ApplyAiGameCommand command) => Commands.Add(command);
        }

        [UnityTest]
        public IEnumerator Orchestrator_EachBuiltInRole_PublishesEnvelope()
        {
            var sink = new ListSink();
            var host = new SoloAuthorityHost();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider());
            var orch = new AiOrchestrator(host, new StubLlmClient(), sink, telemetry, composer);

            foreach (var role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                sink.Commands.Clear();
                var task = orch.RunTaskAsync(new AiTaskRequest { RoleId = role, Hint = "playmode" });
                yield return new WaitUntil(() => task.IsCompleted);
                Assert.AreEqual(1, sink.Commands.Count, role);
                Assert.AreEqual(Envelope, sink.Commands[0].CommandTypeId);
                Assert.IsFalse(string.IsNullOrEmpty(sink.Commands[0].JsonPayload), role);
            }
        }
    }
}
