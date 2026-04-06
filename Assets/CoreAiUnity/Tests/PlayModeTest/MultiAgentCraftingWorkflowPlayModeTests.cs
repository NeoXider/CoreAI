using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Полный воркфлоу крафта с НЕСКОЛЬКИМИ АГЕНТАМИ:
    /// Creator (дизайн) → CoreMechanicAI (расчёт) → Programmer (Lua код).
    /// Каждый агент использует свою изолированную память, одну и ту же модель.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class MultiAgentCraftingWorkflowPlayModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return System.Array.Empty<CoreAI.Ai.ChatMessage>();
            }
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        /// <summary>
        /// Полный воркфлоу: Creator → CoreMechanicAI → Programmer
        /// </summary>
        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow()
        {
            Debug.Log("[MultiAgent] ═══════════════════════════════════════");
            Debug.Log("[MultiAgent] ═══ TEST START: Creator → CoreMechanic → Programmer ═══");
            Debug.Log("[MultiAgent] ═══════════════════════════════════════");

            // Backend из CoreAISettingsAsset (null = FromSettings)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // ===== ШАГ 1: Creator решает что крафтить =====
                {
                    Debug.Log("[MultiAgent] ┌─────────────────────────────────────────");
                    Debug.Log("[MultiAgent] │ ШАГ 1: Creator — дизайн крафта");
                    Debug.Log("[MultiAgent] ├─────────────────────────────────────────");

                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint = "Design a crafting recipe for a weapon made from these ingredients:\n" +
                               "- Iron (metal, hardness:60, magic:5, rarity:1)\n" +
                               "- Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)\n\n" +
                               "Output JSON with: item_type, estimated_damage, estimated_fire_damage, quality.\n" +
                               "Also save your design decision to memory using memory tool:\n" +
                               "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Design: Iron+Fire Crystal → weapon, damage ~45, fire ~15\"}}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "creator design");

                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    // Проверяем что Creator записал в память
                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorMem) &&
                        !string.IsNullOrWhiteSpace(creatorMem.Memory),
                        "Creator did not write to memory");

                    Debug.Log($"[MultiAgent] ✓ Creator memory: {creatorMem.Memory}");
                }

                // ===== ШАГ 2: CoreMechanicAI считает результат =====
                {
                    Debug.Log("[MultiAgent] ┌─────────────────────────────────────────");
                    Debug.Log("[MultiAgent] │ ШАГ 2: CoreMechanicAI — расчёт механики");
                    Debug.Log("[MultiAgent] ├─────────────────────────────────────────");

                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate the craft result for a weapon made from:\n" +
                               "- Iron (metal, hardness:60, magic:5, rarity:1)\n" +
                               "- Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)\n\n" +
                               "Output JSON: {\"item_name\": \"...\", \"damage\": N, \"fire_damage\": N, \"quality\": N}\n" +
                               "Save to memory using memory tool:\n" +
                               "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Craft#1: <item_name> damage:N fire:N quality:N\"}}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic calculation");

                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicMem) &&
                        !string.IsNullOrWhiteSpace(mechanicMem.Memory),
                        "CoreMechanicAI did not write to memory");

                    string mechanicMemory = mechanicMem.Memory;
                    Debug.Log($"[MultiAgent] ✓ CoreMechanicAI memory: {mechanicMemory}");

                    // Извлекаем имя предмета
                    string itemName =
                        CraftingMemoryItemNameExtractor.ExtractName(sink.Items.Count > 0
                            ? sink.Items[0].JsonPayload
                            : "");
                    if (string.IsNullOrEmpty(itemName))
                    {
                        // Пытаемся извлечь из памяти
                        Match match = System.Text.RegularExpressions.Regex.Match(mechanicMemory, @"Craft#1:\s*(\w+)");
                        if (match.Success)
                        {
                            itemName = match.Groups[1].Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(itemName))
                    {
                        Debug.Log($"[MultiAgent] ✓ Item name from CoreMechanicAI: '{itemName}'");
                    }
                }

                // ===== ШАГ 3: Programmer генерирует Lua код =====
                {
                    Debug.Log("[MultiAgent] ┌─────────────────────────────────────────");
                    Debug.Log("[MultiAgent] │ ШАГ 3: Programmer — генерация Lua");
                    Debug.Log("[MultiAgent] ├─────────────────────────────────────────");

                    LogAgentMemory(store, "Programmer");

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = "Generate Lua code for a crafted weapon:\n" +
                               "- Use create_item('ItemName', 'weapon', quality)\n" +
                               "- Add special effect for fire damage\n" +
                               "- Use report() to log the result\n\n" +
                               "Use the execute_lua tool:\n" +
                               "{\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('...', 'weapon', quality)\\nadd_special_effect('fire_damage: 15')\\nreport('crafted ...')\"}}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "programmer lua");

                    LogAgentResponse("programmer", sink);
                    LogAgentMemory(store, "Programmer");
                }

                // ===== ШАГ 4: CoreMechanicAI — повторный крафт (детерминизм) =====
                {
                    Debug.Log("[MultiAgent] ┌─────────────────────────────────────────");
                    Debug.Log("[MultiAgent] │ ШАГ 4: CoreMechanicAI — повторный крафт (детерминизм)");
                    Debug.Log("[MultiAgent] ├─────────────────────────────────────────");

                    // Сохраняем имя из первого крафта
                    string craft1Memory = "";
                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem1))
                    {
                        craft1Memory = mem1.Memory;
                        Debug.Log($"[MultiAgent] Previous craft memory: {craft1Memory}");
                    }

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate the craft result for the EXACT SAME ingredients:\n" +
                               "- Iron (metal, hardness:60, magic:5, rarity:1)\n" +
                               "- Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)\n\n" +
                               $"YOUR PREVIOUS CRAFT MEMORY: {craft1Memory}\n\n" +
                               "IMPORTANT: These are the SAME ingredients as before. " +
                               "Output the EXACT SAME result as your previous craft. " +
                               "Same item name, same stats — deterministic behavior.\n\n" +
                               "Save to memory and output JSON with the result."
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic repeat");

                    LogAgentResponse("mechanic repeat", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    // Проверяем что память обновлена
                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem2))
                    {
                        Debug.Log($"[MultiAgent] Final CoreMechanicAI memory:\n{mem2.Memory}");
                    }
                }

                // ===== ФИНАЛЬНАЯ ПРОВЕРКА =====
                Debug.Log("[MultiAgent] ═══════════════════════════════════════");
                Debug.Log("[MultiAgent] ═══ FINAL VALIDATION ═══");
                Debug.Log("[MultiAgent] ═══════════════════════════════════════");

                // Проверяем изоляцию памяти
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorState));
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicState));
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Programmer, out AgentMemoryState programmerState));

                Debug.Log($"[MultiAgent] Creator memory:      {creatorState.Memory}");
                Debug.Log($"[MultiAgent] CoreMechanic memory: {mechanicState.Memory}");
                Debug.Log($"[MultiAgent] Programmer memory:  {programmerState?.Memory ?? "(not written)"}");

                // Память разных агентов НЕ должна совпадать
                Assert.AreNotEqual(creatorState.Memory, mechanicState.Memory,
                    "Creator and CoreMechanicAI must have DIFFERENT memory");

                Debug.Log("[MultiAgent] ✓ Memory isolation verified");
                Debug.Log("[MultiAgent] ═══════════════════════════════════════");
                Debug.Log("[MultiAgent] ═══ TEST PASSED ═══");
                Debug.Log("[MultiAgent] ═══════════════════════════════════════");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// Упрощённый тест: только Creator → CoreMechanicAI (без Programmer)
        /// Быстрее для отладки.
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator MultiAgent_CreatorThenMechanic_QuickWorkflow()
        {
            Debug.Log("[MultiAgent.Quick] ═══════════════════════════════════════");
            Debug.Log("[MultiAgent.Quick] ═══ TEST START: Creator → CoreMechanic ═══");
            Debug.Log("[MultiAgent.Quick] ═══════════════════════════════════════");

            // Backend из CoreAISettingsAsset (null = FromSettings)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // ===== CREATOR =====
                {
                    Debug.Log("[MultiAgent.Quick] === CREATOR: Design craft ===");
                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint =
                            "Design a weapon from: Iron (hardness:60, rarity:1) + Fire Crystal (magic:85, rarity:4).\n" +
                            "Save to memory: {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Design: Iron+Fire Crystal → weapon\"}}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "creator");
                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState _creatorMemQuick) &&
                        !string.IsNullOrWhiteSpace(_creatorMemQuick.Memory),
                        "Creator did not write memory");
                }

                // ===== COREMECHANIC =====
                {
                    Debug.Log("[MultiAgent.Quick] === COREMECHANIC: Calculate result ===");
                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate weapon from: Iron (hardness:60) + Fire Crystal (magic:85).\n" +
                               "Output JSON with item_name, damage, fire_damage.\n" +
                               "Save to memory: {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Craft#1: <name> damage:N fire:N\"}}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic");
                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState _mechanicMemQuick) &&
                        !string.IsNullOrWhiteSpace(_mechanicMemQuick.Memory),
                        "CoreMechanicAI did not write memory");
                }

                // ===== ПРОВЕРКА ИЗОЛЯЦИИ =====
                Debug.Log("[MultiAgent.Quick] ═══ MEMORY ISOLATION CHECK ═══");

                AgentMemoryState creatorMem = store.States[BuiltInAgentRoleIds.Creator];
                AgentMemoryState mechanicMem = store.States[BuiltInAgentRoleIds.CoreMechanic];

                Debug.Log($"[MultiAgent.Quick] Creator memory:      {creatorMem.Memory}");
                Debug.Log($"[MultiAgent.Quick] CoreMechanic memory: {mechanicMem.Memory}");

                Assert.AreNotEqual(creatorMem.Memory, mechanicMem.Memory,
                    "Agents must have isolated memory");

                Debug.Log("[MultiAgent.Quick] ✓ Memory isolation verified");
                Debug.Log("[MultiAgent.Quick] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static AiOrchestrator CreateOrchestrator(
            ILlmClient client,
            IAgentMemoryStore store,
            AgentMemoryPolicy policy,
            SessionTelemetryCollector telemetry,
            AiPromptComposer composer,
            IAiGameCommandSink sink)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                telemetry,
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());
        }

        #region Logging

        private static void LogAgentMemory(InMemoryStore store, string roleId)
        {
            if (store.TryLoad(roleId, out AgentMemoryState mem) && !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[MultiAgent] 📚 {roleId} MEMORY: {mem.Memory}");
            }
            else
            {
                Debug.Log($"[MultiAgent] 📚 {roleId} MEMORY: (empty)");
            }
        }

        private static void LogAgentResponse(string label, ListSink sink)
        {
            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[MultiAgent] 📥 {label} RESPONSE ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[MultiAgent] ⚠ {label}: No response");
            }
        }

        #endregion
    }
#endif
}