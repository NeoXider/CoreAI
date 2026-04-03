using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Память Creator через реальный <see cref="ILlmClient"/> — тот же выбор бэкенда, что в игре (<see cref="PlayModeProductionLikeLlmFactory"/>).
    /// </summary>
    public sealed class AgentMemoryWithRealModelPlayModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;
            public void Clear(string roleId) => States.Remove(roleId);
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        /// <summary>Рекомендуемый сценарий: Auto = HTTP при наличии env, иначе LLMUnity (как <see cref="CoreAI.Composition.CoreAILifetimeScope"/>).</summary>
        [UnityTest]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaProductionLikeBackend_Auto()
        {
            return RunCreatorMemoryWithBackendPreference(null);
        }

        [UnityTest]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaOpenAiHttpOnly()
        {
            return RunCreatorMemoryWithBackendPreference(PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp);
        }

#if !COREAI_NO_LLM
        [UnityTest]
        public IEnumerator Creator_WritesMemory_ThenRecalls_ViaLlmUnityOnly()
        {
            return RunCreatorMemoryWithBackendPreference(PlayModeProductionLikeLlmBackend.LlmUnity);
        }
#endif

        private static IEnumerator RunCreatorMemoryWithBackendPreference(PlayModeProductionLikeLlmBackend? preference)
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(preference, 0f, 300, out var handle, out var ignore))
                Assert.Ignore(ignore);

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                var store = new InMemoryStore();
                var policy = new AgentMemoryPolicy();
                var telemetry = new SessionTelemetryCollector();
                var composer = new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                var sink1 = new ListSink();
                var orch1 = new AiOrchestrator(
                    new SoloAuthorityHost(),
                    handle.Client,
                    sink1,
                    telemetry,
                    composer,
                    store,
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics());
                var t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint =
                        "IMPORTANT: Reply with ONLY a fenced ```memory``` block containing exactly the single line: remember: apples"
                });
                yield return new WaitUntil(() => t1.IsCompleted);

                Assert.IsTrue(
                    store.TryLoad(BuiltInAgentRoleIds.Creator, out var st) && !string.IsNullOrWhiteSpace(st.Memory),
                    "Модель не записала память (не найден/не распарсен блок ```memory``` в ответе).");
                Assert.AreEqual(
                    "remember: apples",
                    st.Memory.Trim(),
                    "Память должна быть строго 'remember: apples' для стабильности теста.");

                var sink2 = new ListSink();
                var orch2 = new AiOrchestrator(
                    new SoloAuthorityHost(),
                    handle.Client,
                    sink2,
                    telemetry,
                    composer,
                    store,
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics());
                var t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "What is your available memory? Reply with exactly: I remember: apples"
                });
                yield return new WaitUntil(() => t2.IsCompleted);

                Assert.AreEqual(1, sink2.Items.Count);
                StringAssert.Contains("I remember: apples", sink2.Items[0].JsonPayload);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
}
