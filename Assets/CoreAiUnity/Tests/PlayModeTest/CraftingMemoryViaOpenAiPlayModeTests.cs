using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// PlayMode тест крафта с памятью — OpenAI API (удалённая LM Studio).
    /// Полный воркфлоу: Крафт → Память → Проверка → Новый крафт без повторов.
    /// С подробным логированием запросов и ответов модели.
    /// </summary>
    public sealed class CraftingMemoryViaOpenAiPlayModeTests
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
        /// Полный воркфлоу крафта через OpenAI HTTP API: 3 итерации, AI запоминает каждый крафт
        /// и должен создавать уникальные предметы. Модель берётся из PlayModeOpenAiTestConfig.
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryOpenAi_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
            Debug.Log("[CraftingMemory.OpenAI] ═══ TEST START ═══");
            Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");

            // Создаём HTTP клиент через фабрику (как в игре)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                Debug.Log("[CraftingMemory.OpenAI] ✓ HTTP client created");
                Debug.Log($"[CraftingMemory.OpenAI] Base URL: {PlayModeOpenAiTestConfig.ResolveBaseUrl()}");
                Debug.Log($"[CraftingMemory.OpenAI] Model: {PlayModeOpenAiTestConfig.ResolveModelId()}");

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
                        Debug.Log($"[CraftingMemory.OpenAI] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                        bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                            craft4Name.ToLowerInvariant() == craft2Name.ToLowerInvariant();

                        if (!isDeterministic)
                        {
                            Debug.LogWarning($"[CraftingMemory.OpenAI] ⚠ DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                        }
                        else
                        {
                            Debug.Log($"[CraftingMemory.OpenAI] ✓ DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                        }
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 4")) yield break;
                }

                // ===== ФИНАЛЬНАЯ ПРОВЕРКА =====
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.OpenAI] ═══ FINAL VALIDATION ═══");
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                // Крафты 1, 2, 3 — уникальны
                var uniqueFirst3 = new HashSet<string>(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.OpenAI] ✓ First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.OpenAI] Crafted items: {string.Join(" | ", craftedNames)}");
                Debug.Log($"[CraftingMemory.OpenAI] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                    $"→ {(craft2Final.ToLowerInvariant() == craft4Final.ToLowerInvariant() ? "✓ SAME" : "⚠ DIFFERENT")}");
                Debug.Log($"[CraftingMemory.OpenAI] Full memory history:\n{memoryAccum}");
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.OpenAI] ═══ TEST PASSED ═══");
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// Упрощённый тест: 2 крафта с проверкой что второй НЕ повторяет первый.
        /// Быстрее для отладки.
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator CraftingMemoryOpenAi_TwoCrafts_SecondIsDifferent()
        {
            Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
            Debug.Log("[CraftingMemory.OpenAI] ═══ 2-CRAFT TEST START ═══");
            Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
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

                // ===== КРАФТ 1 =====
                string prompt1 = BuildCraftPrompt(1,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    "");

                LogBeforeModelCall("CRAFT 1: Steel + Fire Crystal", prompt1, store);

                ListSink sink1 = new();
                AiOrchestrator orch1 = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink1);

                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt1
                });

                yield return PlayModeTestAwait.WaitTask(t1, 300f, "craft 1");

                LogAfterModelCall("craft 1", sink1, store);

                if (sink1.Items.Count == 0)
                {
                    Assert.Fail("Craft 1 produced no output");
                    yield break;
                }

                string firstPayload = sink1.Items[0].JsonPayload;
                string firstName = CraftingMemoryItemNameExtractor.ExtractName(firstPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 1 name: '{firstName ?? "unknown"}'");

                // ===== КРАФТ 2 (те же ингредиенты + память) =====
                string memoryHint = "";
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState st1) &&
                    !string.IsNullOrWhiteSpace(st1.Memory))
                {
                    memoryHint = st1.Memory;
                }

                string prompt2 = BuildCraftPrompt(2,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    memoryHint);

                LogBeforeModelCall("CRAFT 2: Same ingredients, check memory", prompt2, store);

                ListSink sink2 = new();
                AiOrchestrator orch2 = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink2);

                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt2
                });

                yield return PlayModeTestAwait.WaitTask(t2, 300f, "craft 2");

                LogAfterModelCall("craft 2", sink2, store);

                if (sink2.Items.Count == 0)
                {
                    Assert.Fail("Craft 2 produced no output");
                    yield break;
                }

                string secondPayload = sink2.Items[0].JsonPayload;
                string secondName = CraftingMemoryItemNameExtractor.ExtractName(secondPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 2 name: '{secondName ?? "unknown"}'");

                // ===== ПРОВЕРКА: Имена разные =====
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.OpenAI] ═══ VALIDATION ═══");
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");

                if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(secondName))
                {
                    Assert.AreNotEqual(firstName.ToLowerInvariant(), secondName.ToLowerInvariant(),
                        $"Craft 2 repeated Craft 1 name! Both are '{firstName}' — model did NOT check memory!");

                    Debug.Log($"[CraftingMemory.OpenAI] ✓ Craft names are different:");
                    Debug.Log($"[CraftingMemory.OpenAI]   Craft 1: '{firstName}'");
                    Debug.Log($"[CraftingMemory.OpenAI]   Craft 2: '{secondName}'");
                }
                else
                {
                    Debug.LogWarning("[CraftingMemory.OpenAI] ⚠ Could not extract one or both craft names");
                }

                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.OpenAI] ═══ TEST PASSED ═══");
                Debug.Log("[CraftingMemory.OpenAI] ═══════════════════════════════════════");
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
                "1. First, use the memory tool to save this craft:\n" +
                "   {\"tool\": \"memory\", \"action\": \"write\", \"content\": \"Previous crafts: <list all crafts including this one>\"}\n\n" +
                "2. Then output Lua code in a fenced block:\n" +
                "   ```lua\n" +
                "   create_item('YourWeaponName', 'weapon', quality)\n" +
                "   report('crafted YourWeaponName')\n" +
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
                "1. Use memory tool:\n" +
                "   {\"tool\": \"memory\", \"action\": \"write\", \"content\": \"Previous crafts: <update list>\"}\n\n" +
                "2. Output Lua code:\n" +
                "   ```lua\n" +
                "   create_item('SameNameAsBefore', 'weapon', quality)\n" +
                "   report('crafted SameNameAsBefore')\n" +
                "   ```";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.OpenAI] │ 📤 SENDING TO MODEL: {label}");
            Debug.Log($"[CraftingMemory.OpenAI] ├─────────────────────────────────────────");

            // Логируем память, которую видит модель
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI] │ 📚 MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log($"[CraftingMemory.OpenAI] ├─────────────────────────────────────────");
            }
            else
            {
                Debug.Log($"[CraftingMemory.OpenAI] │ 📚 MEMORY: (empty — first craft)");
                Debug.Log($"[CraftingMemory.OpenAI] ├─────────────────────────────────────────");
            }

            Debug.Log($"[CraftingMemory.OpenAI] │ 📝 PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log($"[CraftingMemory.OpenAI] └─────────────────────────────────────────");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.OpenAI] │ 📥 MODEL RESPONSE: {label}");
            Debug.Log($"[CraftingMemory.OpenAI] ├─────────────────────────────────────────");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.OpenAI] │ ✅ Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.OpenAI] │ 📦 RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.OpenAI] │ ❌ NO COMMAND produced");
            }

            // Логируем память после ответа
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI] │ 💾 MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.OpenAI] │ 💾 MEMORY: (not written by model)");
            }

            Debug.Log($"[CraftingMemory.OpenAI] └─────────────────────────────────────────");
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

    /// <summary>
    /// Общий хелпер для извлечения имени предмета из payload.
    /// </summary>
    internal static class CraftingMemoryItemNameExtractor
    {
        private static readonly System.Text.RegularExpressions.Regex[] Patterns = {
            new System.Text.RegularExpressions.Regex("create_item\\s*\\(\\s*'([^']+)'"),
            new System.Text.RegularExpressions.Regex("create_item\\s*\\(\\s*\"([^\"]+)\""),
            new System.Text.RegularExpressions.Regex("\"name\"\\s*:\\s*\"([^\"]+)\""),
            new System.Text.RegularExpressions.Regex("Name\\s*=\\s*\"([^\"]+)\""),
        };

        public static string ExtractName(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;

            foreach (var regex in Patterns)
            {
                var match = regex.Match(payload);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return null;
        }
    }
}
