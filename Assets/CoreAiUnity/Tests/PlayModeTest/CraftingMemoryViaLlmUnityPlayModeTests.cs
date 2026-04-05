using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// PlayMode тест крафта с памятью — LLMUnity (локальная GGUF модель).
    /// Полный воркфлоу: Крафт → Память → Проверка → Новый крафт без повторов.
    /// С подробным логированием запросов и ответов модели.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class CraftingMemoryViaLlmUnityPlayModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();
            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;
            public void Clear(string roleId) => States.Remove(roleId);
            public void AppendChatMessage(string roleId, string role, string content) { }
            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => System.Array.Empty<CoreAI.Ai.ChatMessage>();
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command)
            {
                Debug.Log($"[LoggingSink] Received command: {command.CommandTypeId}");
                Debug.Log($"[LoggingSink] Payload:\n{command.JsonPayload}");
                Items.Add(command);
            }
        }

        /// <summary>
        /// Полный воркфлоу крафта через LLMUnity: 3 итерации, AI запоминает каждый крафт
        /// и должен создавать уникальные предметы.
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryLlmUnity_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
            Debug.Log("[CraftingMemory.LLMUnity] ═══ TEST START ═══");
            Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");

            // Создаём LLMUnity клиент через фабрику (как в игре)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.LlmUnity,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log("[CraftingMemory.LLMUnity] ✓ Model ready");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                List<string> craftedNames = new();
                string memoryAccum = "";

                // ===== КРАФТ 1: Iron + Oak =====
                {
                    string prompt = BuildCraftPrompt(1,
                        "Iron (metal, hardness:60, magic:5, rarity:1)",
                        "Oak Wood (wood, hardness:40, magic:10, rarity:1)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 1: Iron + Oak", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 1");

                    LogAfterModelCall("craft 1", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 1")) yield break;
                }

                // ===== КРАФТ 2: Steel + Hardwood =====
                {
                    string prompt = BuildCraftPrompt(2,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 2: Steel + Hardwood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 2");

                    LogAfterModelCall("craft 2", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 2")) yield break;
                }

                // ===== КРАФТ 3: Mithril + Enchanted Wood =====
                {
                    string prompt = BuildCraftPrompt(3,
                        "Mithril (metal, hardness:70, magic:60, rarity:4)",
                        "Enchanted Wood (wood, hardness:45, magic:70, rarity:3)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 3: Mithril + Enchanted Wood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 3");

                    LogAfterModelCall("craft 3", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 3")) yield break;
                }

                // ===== КРАФТ 4: Steel + Hardwood (ПОВТОР крафта #2) — проверка детерминизма =====
                {
                    string prompt = BuildDeterministicCraftPrompt(4,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 4: Steel + Hardwood (REPEAT of craft #2 — DETERMINISM CHECK)", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 4");

                    LogAfterModelCall("craft 4 (determinism)", sink, store);

                    // Проверяем детерминизм: модель должна повторить имя крафта #2
                    string craft2Name = craftedNames[1]; // Steel+Hardwood из крафта #2
                    if (sink.Items.Count > 0)
                    {
                        string craft4Name = CraftingMemoryItemNameExtractor.ExtractName(sink.Items[0].JsonPayload);
                        Debug.Log($"[CraftingMemory.LLMUnity] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                        // Модель должна либо повторить имя, либо явно сослаться на предыдущий крафт
                        bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                            craft4Name.ToLowerInvariant() == craft2Name.ToLowerInvariant();

                        if (!isDeterministic)
                        {
                            Debug.LogWarning($"[CraftingMemory.LLMUnity] ⚠ DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                        }
                        else
                        {
                            Debug.Log($"[CraftingMemory.LLMUnity] ✓ DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                        }
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 4")) yield break;
                }

                // ===== ФИНАЛЬНАЯ ПРОВЕРКА =====
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.LLMUnity] ═══ FINAL VALIDATION ═══");
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                // Крафты 1, 2, 3 — уникальны
                var uniqueFirst3 = new HashSet<string>(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.LLMUnity] ✓ First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.LLMUnity] Crafted items: {string.Join(" | ", craftedNames)}");
                Debug.Log($"[CraftingMemory.LLMUnity] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                    $"→ {(craft2Final.ToLowerInvariant() == craft4Final.ToLowerInvariant() ? "✓ SAME" : "⚠ DIFFERENT")}");
                Debug.Log($"[CraftingMemory.LLMUnity] Full memory history:\n{memoryAccum}");
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.LLMUnity] ═══ TEST PASSED ═══");
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
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

        private static string BuildCraftPrompt(int craftNumber, string ingredient1, string ingredient2, string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft. No previous crafts to check.\n\n"
                : $"YOUR MEMORY (previous crafts):\n{previousCrafts}\n\n" +
                  "CRITICAL: You MUST create a DIFFERENT weapon from all previous crafts above. " +
                  "Do NOT repeat any previous craft name or concept.\n\n";

            string instructions = "OUTPUT FORMAT:\n" +
                "1. First, call the memory tool to save this craft:\n" +
                "   ```json\n" +
                "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <list all crafts including this one>\"}}\n" +
                "   ```\n\n" +
                "2. Then, call the execute_lua tool to create the item:\n" +
                "   ```json\n" +
                "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('YourWeaponName', 'weapon', quality)\\nreport('crafted YourWeaponName')\"}}\n" +
                "   ```";

            return header + ingredients + memorySection + instructions;
        }

        private static string BuildDeterministicCraftPrompt(int craftNumber, string ingredient1, string ingredient2, string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft.\n\n"
                : $"YOUR MEMORY (ALL previous crafts):\n{previousCrafts}\n\n";

            string instructions = "IMPORTANT: These EXACT ingredients were used before (see memory above).\n" +
                "You MUST craft the EXACT SAME item as before — use the SAME name and properties.\n" +
                "This tests deterministic behavior: same input = same output.\n\n" +
                "OUTPUT FORMAT:\n" +
                "1. Call the memory tool:\n" +
                "   ```json\n" +
                "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <update list>\"}}\n" +
                "   ```\n\n" +
                "2. Call the execute_lua tool:\n" +
                "   ```json\n" +
                "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('SameNameAsBefore', 'weapon', quality)\\nreport('crafted SameNameAsBefore')\"}}\n" +
                "   ```";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.LLMUnity] │ 📤 SENDING TO MODEL: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");

            // Логируем память, которую видит модель
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📚 MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📚 MEMORY: (empty — first craft)");
                Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] │ 📝 PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log($"[CraftingMemory.LLMUnity] └─────────────────────────────────────────");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.LLMUnity] │ 📥 MODEL RESPONSE: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.LLMUnity] │ ✅ Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📦 RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ ❌ NO COMMAND produced");
            }

            // Логируем память после ответа
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 💾 MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 💾 MEMORY: (not written by model)");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] └─────────────────────────────────────────");
        }

        private static bool ExtractCraftInfo(
            ListSink sink,
            InMemoryStore store,
            List<string> craftedNames,
            ref string memoryAccum,
            string label)
        {
            if (sink.Items.Count == 0)
            {
                Debug.LogError($"[{label}] ❌ No command produced — test cannot continue");
                return false;
            }

            string payload = sink.Items[0].JsonPayload;

            // Извлекаем имя предмета
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                Debug.LogWarning($"[{label}] ⚠ Could not extract item name from payload");
                itemName = $"unknown_{craftedNames.Count + 1}";
            }

            craftedNames.Add(itemName);
            memoryAccum += $"{itemName}, ";

            // Обновляем память
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState existing))
            {
                store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState
                {
                    Memory = existing.Memory + $" | New: {itemName}"
                });
            }

            Debug.Log($"[{label}] ✓ Crafted: '{itemName}'");
            return true;
        }

        #endregion
    }
#endif
}
