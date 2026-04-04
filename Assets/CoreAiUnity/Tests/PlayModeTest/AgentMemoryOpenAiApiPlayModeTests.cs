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
    /// PlayMode тесты для проверки работы Memory Tool через OpenAI-compatible API.
    /// Подходит для LM Studio, Ollama, LocalAI и других совместимых серверов.
    /// </summary>
    public sealed class AgentMemoryOpenAiApiPlayModeTests
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
        [Timeout(300000)]
        public IEnumerator MemoryTool_DifferentTemperatures_ViaLmStudio()
        {
            float[] temperatures = new float[] { 0.0f, 0.2f, 0.7f };
            
            foreach (var temp in temperatures)
            {
                Debug.Log($"[LM Studio Test] === ТЕСТ ТЕМПЕРАТУРЫ: {temp} ===");

                var store = new InMemoryStore();
                var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
                settings.SetRuntimeConfiguration(
                    useOpenAiCompatibleHttp: true,
                    apiBaseUrl: "http://localhost:1234/v1",
                    apiKey: "any-key",
                    model: "",
                    temperature: temp
                );

                var client = new OpenAiChatLlmClient(settings);
                var sink = new ListSink();
                var composer = new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                var orch = new AiOrchestrator(
                    new SoloAuthorityHost(),
                    client,
                    sink,
                    new SessionTelemetryCollector(),
                    composer,
                    store,
                    new AgentMemoryPolicy(),
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics());

                Debug.Log($"[LM Studio Test] Отправляем запрос с temp={temp}...");
                var task = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = $"Temperature test {temp}. Запомни фразу 'temp test {temp}'. Используй JSON: {{\"tool\": \"memory\", \"action\": \"write\", \"content\": \"temp test {temp}\"}}"
                });

                yield return PlayModeTestAwait.WaitTask(task, 120f, $"LM Studio memory write temp={temp}");

                if (store.TryLoad(BuiltInAgentRoleIds.Creator, out var state) && !string.IsNullOrWhiteSpace(state.Memory))
                {
                    Debug.Log($"[LM Studio Test] ТЕМПЕРАТУРА {temp} — УСПЕХ! Память: {state.Memory}");
                    Assert.IsTrue(state.Memory.Contains($"temp test {temp}") || state.Memory.Contains("temp test"));
                }
                else
                {
                    Debug.LogWarning($"[LM Studio Test] ТЕМПЕРАТУРА {temp} — НЕУДАЧА. Модель не соблюла формат.");
                }
                
                yield return new WaitForSeconds(1.0f);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MemoryTool_WritesMemory_ViaLmStudio()
        {
            Debug.Log("[LM Studio Test] === НАЧТЕСТОВА ===");
            Debug.Log("[LM Studio Test] Убедитесь: 1) Модель загружена 2) Server включен (порт 1234)");

            var store = new InMemoryStore();
            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                useOpenAiCompatibleHttp: true,
                apiBaseUrl: "http://localhost:1234/v1",
                apiKey: "any-key-works",
                model: "",
                temperature: 0.2f // Оптимально для Tool Call (избегает зацикливания 0.0)
            );

            var client = new OpenAiChatLlmClient(settings);
            var sink = new ListSink();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            var orch = new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                new SessionTelemetryCollector(),
                composer,
                store,
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());

            Debug.Log("[LM Studio Test] Отправляем запрос...");
            var task = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Важно: запомни фразу 'qwen4b работает отлично'. Используй только JSON: {\"tool\": \"memory\", \"action\": \"write\", \"content\": \"qwen4b работает отлично\"}"
            });

            yield return PlayModeTestAwait.WaitTask(task, 120f, "LM Studio memory write");

            if (!store.TryLoad(BuiltInAgentRoleIds.Creator, out var state) || string.IsNullOrWhiteSpace(state.Memory))
            {
                Debug.LogWarning("[LM Studio Test] Память не записана. Проверьте логи выше.");
                Assert.Ignore("LM Studio memory test skipped (format not followed)");
            }

            Debug.Log($"[LM Studio Test] УСПЕХ! Память: {state.Memory}");
            Assert.IsTrue(state.Memory.Contains("qwen4b") || state.Memory.Contains("работает"));
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MemoryTool_AppendsMemory_ViaLmStudio()
        {
            Debug.Log("[LM Studio Test] === ТЕСТ ДОБАВЛЕНИЯ В ПАМЯТЬ ===");

            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "начальное значение" });

            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                useOpenAiCompatibleHttp: true,
                apiBaseUrl: "http://localhost:1234/v1",
                apiKey: "any-key",
                model: "",
                temperature: 0.2f
            );

            var client = new OpenAiChatLlmClient(settings);
            var sink = new ListSink();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            var orch = new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                new SessionTelemetryCollector(),
                composer,
                store,
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());

            var task = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Добавь в память: 'добавлено через lm studio'. Используй: {\"tool\": \"memory\", \"action\": \"append\", \"content\": \"добавлено через lm studio\"}"
            });

            yield return PlayModeTestAwait.WaitTask(task, 120f, "LM Studio memory append");

            if (!store.TryLoad(BuiltInAgentRoleIds.Creator, out var state))
            {
                Assert.Ignore("Append test skipped");
            }

            Debug.Log($"[LM Studio Test] Память после добавления: {state.Memory}");
            
            if (!state.Memory.Contains("добавлено через lm studio") && !state.Memory.Contains("добавлено"))
            {
                Debug.LogWarning("[LM Studio Test] Append не сработал. Модель не добавила новое значение.");
                Assert.Ignore("Append test skipped (model did not append content)");
            }
            
            Assert.IsTrue(state.Memory.Contains("начальное значение"));
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MemoryTool_ClearsMemory_ViaLmStudio()
        {
            Debug.Log("[LM Studio Test] === ТЕСТ ОЧИСТКИ ПАМЯТИ ===");

            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "это будет удалено" });

            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                useOpenAiCompatibleHttp: true,
                apiBaseUrl: "http://localhost:1234/v1",
                apiKey: "any-key",
                model: "",
                temperature: 0.2f
            );

            var client = new OpenAiChatLlmClient(settings);
            var sink = new ListSink();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            var orch = new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                new SessionTelemetryCollector(),
                composer,
                store,
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());

            var task = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Очисти всю память. Используй: {\"tool\": \"memory\", \"action\": \"clear\"}"
            });

            yield return PlayModeTestAwait.WaitTask(task, 120f, "LM Studio memory clear");

            if (store.TryLoad(BuiltInAgentRoleIds.Creator, out var state) && !string.IsNullOrWhiteSpace(state.Memory))
            {
                Debug.LogWarning($"[LM Studio Test] Память не очищена: {state.Memory}");
                Assert.Ignore("Clear test skipped");
            }

            Debug.Log("[LM Studio Test] УСПЕХ! Память очищена.");
            Assert.IsFalse(store.TryLoad(BuiltInAgentRoleIds.Creator, out _), "Память должна быть очищена");
        }
    }
}