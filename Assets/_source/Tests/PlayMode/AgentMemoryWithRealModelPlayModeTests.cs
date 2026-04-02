using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using LLMUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Сквозная проверка памяти с реальной моделью через OpenAI-compatible HTTP (LM Studio и т.п.).
    /// Тест опциональный: без env vars пропускается.
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

        [UnityTest]
        public IEnumerator OpenAiHttp_Creator_WritesMemory_ThenNewDialog_RecallsMemory()
        {
            var baseUrl = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
                Assert.Ignore("Задайте COREAI_OPENAI_TEST_BASE (например http://IP:1234/v1) и COREAI_OPENAI_TEST_MODEL для проверки памяти через HTTP.");

            var model = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_MODEL");
            if (string.IsNullOrWhiteSpace(model))
                Assert.Ignore("Задайте COREAI_OPENAI_TEST_MODEL — id из GET .../v1/models");

            var apiKey = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_API_KEY") ?? "";

            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                true,
                baseUrl.Trim().TrimEnd('/'),
                apiKey,
                model.Trim(),
                0.0f,
                300);

            var llm = new OpenAiChatLlmClient(settings);
            var store = new InMemoryStore();
            var policy = new AgentMemoryPolicy();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());

            // Диалог 1: просим модель записать память через директиву.
            var sink1 = new ListSink();
            var orch1 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink1, telemetry, composer, store, policy,
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());
            var t1 = orch1.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "IMPORTANT: Reply with ONLY a fenced ```memory``` block containing exactly the single line: remember: apples"
            });
            yield return new WaitUntil(() => t1.IsCompleted);

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var st) && !string.IsNullOrWhiteSpace(st.Memory),
                "Модель не записала память (не найден/не распарсен блок ```memory``` в ответе).");
            Assert.AreEqual("remember: apples", st.Memory.Trim(), "Память должна быть строго 'remember: apples' для стабильности теста.");

            // Диалог 2: новый оркестратор (как отдельная сессия).
            var sink2 = new ListSink();
            var orch2 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink2, telemetry, composer, store, policy,
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());
            var t2 = orch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "What is your available memory? Reply with exactly: I remember: apples"
            });
            yield return new WaitUntil(() => t2.IsCompleted);

            Assert.AreEqual(1, sink2.Items.Count);
            StringAssert.Contains("I remember: apples", sink2.Items[0].JsonPayload);
        }

        [UnityTest]
        public IEnumerator LlmUnity_Creator_WritesMemory_ThenNewDialog_RecallsMemory_WhenModelConfigured()
        {
            var agent = UnityEngine.Object.FindFirstObjectByType<LLMAgent>();
            if (agent == null)
                Assert.Ignore("LLMAgent не найден на сцене — тест памяти через LLMUnity пропущен (откройте сцену с LLM, например RogueliteArena).");
            var llmComp = agent.GetComponent<LLM>();
            if (llmComp == null)
                Assert.Ignore("На объекте LLMAgent нет компонента LLM.");

            // Как в CoreAILifetimeScope / LlmUnityAutoDisableIfNoModel: подставить единственную доступную .gguf из Model Manager.
            LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llmComp);
            if (string.IsNullOrWhiteSpace(llmComp.model))
                Assert.Ignore(
                    "LLMUnity: LLM.model пусто и не удалось авто-назначить модель. В инспекторе выберите радиокнопку у модели или Load model, сохраните сцену; либо оставьте одну скачанную модель в Model Manager.");

            // Если LlmUnityAutoDisableIfNoModel уже отключил LLM в Awake, повторное enabled=true не запустит сервер.
            if (!llmComp.enabled || !agent.enabled)
                Assert.Ignore(
                    "LLMUnity был отключён при старте (пустой model до bootstrap). Сохраните сцену с выбранной моделью и перезапустите Play Mode Tests.");

            Task<bool> setupTask = LLM.WaitUntilModelSetup();
            yield return new WaitUntil(() => setupTask.IsCompleted);
            if (setupTask.IsFaulted)
                Assert.Fail(setupTask.Exception?.GetBaseException().Message ?? "LLM.WaitUntilModelSetup faulted");
            Assert.IsTrue(setupTask.Result, "LLMUnity: model setup failed (см. консоль LLMUnity).");

            var llm = new LlmUnityLlmClient(agent);
            var store = new InMemoryStore();
            var policy = new AgentMemoryPolicy();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());

            var sink1 = new ListSink();
            var orch1 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink1, telemetry, composer, store, policy,
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());
            var t1 = orch1.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "IMPORTANT: Reply with ONLY a fenced ```memory``` block containing exactly the single line: remember: apples"
            });
            yield return new WaitUntil(() => t1.IsCompleted);

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var st) && !string.IsNullOrWhiteSpace(st.Memory),
                "Модель не записала память (не найден/не распарсен блок ```memory``` в ответе).");
            Assert.AreEqual("remember: apples", st.Memory.Trim(), "Память должна быть строго 'remember: apples' для стабильности теста.");

            var sink2 = new ListSink();
            var orch2 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink2, telemetry, composer, store, policy,
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());
            var t2 = orch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "What is your available memory? Reply with exactly: I remember: apples"
            });
            yield return new WaitUntil(() => t2.IsCompleted);

            Assert.AreEqual(1, sink2.Items.Count);
            StringAssert.Contains("I remember: apples", sink2.Items[0].JsonPayload);
        }
    }
}

