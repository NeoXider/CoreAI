using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode scenarios that verify crafting memory through an OpenAI-compatible backend.
    /// </summary>
    public sealed class CraftingMemoryViaOpenAiPlayModeTests
    {
        private const int LlmTurnTimeoutSeconds = 240;
        private const int LongScenarioTimeoutMs = 600000;

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

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
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
        ///     OpenAI HTTP API: 3 , AI
        ///     .    PlayModeOpenAiTestConfig.
        /// </summary>
        [UnityTest]
        [Timeout(LongScenarioTimeoutMs)]
        public IEnumerator CraftingMemoryOpenAi_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.OpenAI]  TEST START ");

            // Resolve the configured production-like LLM backend.
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    LlmTurnTimeoutSeconds,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                Debug.Log("[CraftingMemory.OpenAI]  HTTP client created");
                Debug.Log($"[CraftingMemory.OpenAI] Base URL: {PlayModeOpenAiTestConfig.ResolveBaseUrl()}");
                Debug.Log($"[CraftingMemory.OpenAI] Model: {PlayModeOpenAiTestConfig.ResolveModelId()}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                // Local models often re-emit identical tool payloads across iterations; duplicate guard
                // otherwise hits max consecutive errors. Harness sink is idempotent for these crafts.
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                //     MemoryStore (   )
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();
                List<string> craftedNames = new();
                // Canonical memory string used by later craft prompts.
                // Keep this separate from store memory because the model can write memory directly.
                string memoryAccum = "";

                // Craft 1.
                {
                    const string ing1 = "Iron";
                    const string ing2 = "Oak";
                    string prompt = BuildCraftPrompt(1,
                        "Iron (metal, hardness:60, magic:5, rarity:1)",
                        "Oak Wood (wood, hardness:40, magic:10, rarity:1)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 1: Iron + Oak", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        // Live models occasionally reply with the Lua as text instead of invoking the
                        // tool; the craft chain needs the real execute_lua record, so force the call.
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "execute_lua"
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 1");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 1", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.RequireExtractableExecuteLuaSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "craft 1");
                    string executedLua = ToolCallCapture.BuildExtractionPayload(executeLua);

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 1", 1, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // Craft 2 with the same ingredients plus memory.
                {
                    const string ing1 = "Steel";
                    const string ing2 = "Hardwood";
                    string prompt = BuildCraftPrompt(2,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 2: Steel + Hardwood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        // Live models occasionally reply with the Lua as text instead of invoking the
                        // tool; the craft chain needs the real execute_lua record, so force the call.
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "execute_lua"
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 2");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 2", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.RequireExtractableExecuteLuaSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "craft 2");
                    string executedLua = ToolCallCapture.BuildExtractionPayload(executeLua);

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 2", 2, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // Craft 3: Mithril + Enchanted Wood.
                {
                    const string ing1 = "Mithril";
                    const string ing2 = "Enchanted";
                    string prompt = BuildCraftPrompt(3,
                        "Mithril (metal, hardness:70, magic:60, rarity:4)",
                        "Enchanted Wood (wood, hardness:45, magic:70, rarity:3)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 3: Mithril + Enchanted Wood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        // Live models occasionally reply with the Lua as text instead of invoking the
                        // tool; the craft chain needs the real execute_lua record, so force the call.
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "execute_lua"
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 3");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 3", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.RequireExtractableExecuteLuaSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "craft 3");
                    string executedLua = ToolCallCapture.BuildExtractionPayload(executeLua);

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 3", 3, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // Final validation.
                Debug.Log("[CraftingMemory.OpenAI]  FINAL VALIDATION ");

                Assert.AreEqual(3, craftedNames.Count, "Must have 3 crafted items");

                HashSet<string> uniqueFirst3 = new(craftedNames.Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames)}");

                Debug.Log("[CraftingMemory.OpenAI]  First 3 crafts are unique");
                Debug.Log($"[CraftingMemory.OpenAI] Crafted items: {string.Join(" | ", craftedNames)}");
                Debug.Log($"[CraftingMemory.OpenAI] Canonical memory for prompts:\n{memoryAccum}");
                Debug.Log("[CraftingMemory.OpenAI]  TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [Test]
        public void CraftingMemoryNameExtractor_PrefersCraftItemOverIngredientNames()
        {
            const string payload =
                "local craft_id = 1\n" +
                "local ingredients = { { name = \"Iron\" }, { name = \"Oak Wood\" } }\n" +
                "memory = 'Craft #1: Created \"Ironwood Sword\" (Weapon). Ingredients: Iron, Oak Wood. Quality: 45.'";

            Assert.AreEqual("Ironwood Sword", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCanonicalMemoryLine()
        {
            const string payload =
                "{\"action\":\"append\",\"content\":\"Craft #2 - Steel-Hardwood Scimitar made from Steel + Hardwood\"}";

            Assert.AreEqual("Steel-Hardwood Scimitar", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsEscapedExecuteLuaArguments()
        {
            const string payload =
                "{\"code\":\"local item = create_item(\\\"Steel-Hardwood Halberd\\\", \\\"weapon\\\", 72)\\nreturn item.name\"}";

            Assert.AreEqual("Steel-Hardwood Halberd", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCreatedItemMemoryLine()
        {
            const string payload =
                "{\"action\":\"append\",\"content\":\"Craft #2 Created Item: Flame-Forged Warhammer (weapon). Quality: 102.\"}";

            Assert.AreEqual("Flame-Forged Warhammer", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCreateItemTypeNameOverload()
        {
            const string payload =
                "{\"code\":\"local item = CreateItem(\\\"weapon\\\", \\\"Flame-Forged Warhammer\\\")\\nreturn item\"}";

            Assert.AreEqual("Flame-Forged Warhammer", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCreateItemNameTypeOverload()
        {
            const string payload =
                "{\"code\":\"local item = CreateItem(\\\"Steelwood Blade\\\", \\\"weapon\\\")\\nreturn item\"}";

            Assert.AreEqual("Steelwood Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_DoesNotReadEscapedNewlineAsName()
        {
            const string payload =
                "{\"code\":\"local craft_id = 1\\nlocal ingredients = { \\\"Steel Ingot\\\", \\\"Fire Crystal\\\" }\\nlocal item_name = \\\"Infernal Steel Blade\\\"\"}";

            Assert.AreEqual("Infernal Steel Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsSingleQuotedLuaTableName()
        {
            const string payload =
                "{\"code\":\"local item = {name='Hardsteel Saber', type='weapon', quality=36}\\nreturn item\"}";

            Assert.AreEqual("Hardsteel Saber", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsEscapedJsonNameField()
        {
            const string payload =
                "{\"code\":\"return string.format('{\\\"name\\\":\\\"Ironwood Blade\\\",\\\"type\\\":\\\"weapon\\\"}')\"}";

            Assert.AreEqual("Ironwood Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsEscapedJsonItemNameField()
        {
            const string payload =
                "{\"code\":\"local result = \\\"{\\\\n\\\" ..\\n    \\\"  \\\\\\\"item_name\\\\\\\": \\\\\\\"Ironwood Blade\\\\\\\",\\\\n\\\" ..\\n    \\\"}\\\"\\nreturn result\"}";

            Assert.AreEqual("Ironwood Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsWeaponNameVariable()
        {
            const string payload =
                "{\"code\":\"local weapon_name = \\\"Steel-Hardwood Warblade\\\"\\nreturn { name = weapon_name, type = \\\"weapon\\\" }\"}";

            Assert.AreEqual("Steel-Hardwood Warblade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCraftLogWeaponLine()
        {
            const string payload =
                "Craft Log #1: Weapon \"Ironwood Blade\" crafted from Iron + Oak Wood. Quality: 46.";

            Assert.AreEqual("Ironwood Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCraftColonNameLine()
        {
            const string payload =
                "Craft #2: Steelwood Saber (weapon). Quality: 72. Ingredients: Steel, Hardwood.";

            Assert.AreEqual("Steelwood Saber", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsLuaCommentCraftColonNameLine()
        {
            const string payload =
                "-- Craft #2: Inferno Blade\n" +
                "-- Using Steel Ingot + Fire Crystal (same as Craft #1)\n" +
                "local item_name = \"Inferno Blade\"\n" +
                "return { name = item_name, type = \"weapon\" }";

            Assert.AreEqual("Inferno Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_ReadsCraftedMarkdownLine()
        {
            const string payload = "Crafted: **Ironwood Blade** | Type: weapon | Quality: 57";

            Assert.AreEqual("Ironwood Blade", CraftingMemoryItemNameExtractor.ExtractName(payload));
        }

        [Test]
        public void CraftingMemoryNameExtractor_DeterminismComparison_IgnoresWhitespaceAndCase()
        {
            Assert.IsTrue(CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(
                "Steel Hardwood Blade",
                "steelhardwoodblade"));
        }

        /// <summary>
        ///  : 2        .
        ///   .
        /// </summary>
        [UnityTest]
        [Explicit(
            "Targeted duplicate of ThreeCrafts_AllUnique for same-ingredient drift; too expensive for mandatory full live-model suite.")]
        [Timeout(LongScenarioTimeoutMs)]
        public IEnumerator CraftingMemoryOpenAi_TwoCrafts_SecondIsDifferent()
        {
            Debug.Log("[CraftingMemory.OpenAI]  2-CRAFT TEST START ");

            // Resolve the configured production-like LLM backend.
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    LlmTurnTimeoutSeconds,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();

                // Craft 1.
                string prompt1 = BuildCraftPrompt(1,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    "");

                LogBeforeModelCall("CRAFT 1: Steel + Fire Crystal", prompt1, store);

                ListSink sink1 = new();
                AiOrchestrator orch1 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink1);

                int toolMark1 = toolCalls.Count;
                using CancellationTokenSource cts1 = CreateTurnCancellation();
                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt1
                }, cts1.Token);

                yield return PlayModeTestAwait.WaitTask(t1, LlmTurnTimeoutSeconds, "craft 1");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("craft 1", sink1, store);

                LlmToolCallRecord firstExecuteLua = toolCalls.RequireExtractableExecuteLuaSince(
                    toolMark1, BuiltInAgentRoleIds.CoreMechanic, "craft 1");

                string firstPayload = ToolCallCapture.BuildExtractionPayload(firstExecuteLua);
                AssertExecuteLuaUsesNumericQualityIfPresent(firstPayload, "craft 1");
                string firstName = CraftingMemoryItemNameExtractor.ExtractName(firstPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 1 name: '{firstName ?? "unknown"}'");

                // Craft 2 with the same ingredients plus memory.
                // This harness only registers execute_lua (no memory ILlmTool), so the model's "memory" JSON
                // never hits IAgentMemoryStore. Feed craft #2 the canonical previous-crafts line from craft #1
                // so the prompt can require a different weapon name (same as ThreeCrafts memoryAccum pattern).
                string memoryHint = "";
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState st1) &&
                    !string.IsNullOrWhiteSpace(st1.Memory))
                {
                    memoryHint = st1.Memory.Trim();
                }

                if (string.IsNullOrWhiteSpace(memoryHint) && !string.IsNullOrWhiteSpace(firstName))
                {
                    memoryHint = $"Previous crafts: Craft #1 - {firstName} made from Steel + Fire";
                    store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryHint });
                    Debug.Log($"[CraftingMemory.OpenAI] Injected harness memory for craft 2 prompt:\n{memoryHint}");
                }

                string prompt2 = BuildCraftPrompt(2,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    memoryHint);

                LogBeforeModelCall("CRAFT 2: Same ingredients, check memory", prompt2, store);

                ListSink sink2 = new();
                AiOrchestrator orch2 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink2);

                int toolMark2 = toolCalls.Count;
                using CancellationTokenSource cts2 = CreateTurnCancellation();
                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt2
                }, cts2.Token);

                yield return PlayModeTestAwait.WaitTask(t2, LlmTurnTimeoutSeconds, "craft 2");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("craft 2", sink2, store);

                LlmToolCallRecord secondExecuteLua = toolCalls.RequireExtractableExecuteLuaSince(
                    toolMark2, BuiltInAgentRoleIds.CoreMechanic, "craft 2");

                string secondPayload = ToolCallCapture.BuildExtractionPayload(secondExecuteLua);
                AssertExecuteLuaUsesNumericQualityIfPresent(secondPayload, "craft 2");
                string secondName = CraftingMemoryItemNameExtractor.ExtractName(secondPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 2 name: '{secondName ?? "unknown"}'");

                // Final validation.
                Debug.Log("[CraftingMemory.OpenAI]  VALIDATION ");

                Assert.AreNotEqual(firstName.ToLowerInvariant(), secondName.ToLowerInvariant(),
                    $"Craft 2 repeated Craft 1 name. Both are '{firstName}'.");

                Debug.Log($"[CraftingMemory.OpenAI]  Craft names are different:");
                Debug.Log($"[CraftingMemory.OpenAI]   Craft 1: '{firstName}'");
                Debug.Log($"[CraftingMemory.OpenAI]   Craft 2: '{secondName}'");

                Debug.Log("[CraftingMemory.OpenAI]  TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Explicit(
            "Targeted live-model determinism check for repeated ingredients; run separately from mandatory full PlayMode.")]
        [Timeout(LongScenarioTimeoutMs)]
        public IEnumerator CraftingMemoryOpenAi_RepeatIngredients_SecondMatchesFirst()
        {
            Debug.Log("[CraftingMemory.OpenAI]  REPEAT-INGREDIENT DETERMINISM TEST START ");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    LlmTurnTimeoutSeconds,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();

                string prompt1 = BuildCraftPrompt(1,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    "");

                LogBeforeModelCall("DETERMINISM CRAFT 1: Steel + Fire Crystal", prompt1, store);

                ListSink sink1 = new();
                AiOrchestrator orch1 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink1);

                int toolMark1 = toolCalls.Count;
                using CancellationTokenSource cts1 = CreateTurnCancellation();
                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt1
                }, cts1.Token);

                yield return PlayModeTestAwait.WaitTask(t1, LlmTurnTimeoutSeconds, "determinism craft 1");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("determinism craft 1", sink1, store);

                LlmToolCallRecord firstExecuteLua = toolCalls.RequireExtractableExecuteLuaSince(
                    toolMark1, BuiltInAgentRoleIds.CoreMechanic, "determinism craft 1");
                string firstPayload = ToolCallCapture.BuildExtractionPayload(firstExecuteLua);
                AssertExecuteLuaUsesNumericQualityIfPresent(firstPayload, "determinism craft 1");
                string firstName = CraftingMemoryItemNameExtractor.ExtractName(firstPayload);

                string memoryHint = BuildCanonicalMemory("", 1, firstName, "Steel", "Fire");
                store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryHint });

                string prompt2 = BuildDeterministicCraftPrompt(2,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    memoryHint);

                LogBeforeModelCall("DETERMINISM CRAFT 2: Same ingredients", prompt2, store);

                ListSink sink2 = new();
                AiOrchestrator orch2 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink2);

                int toolMark2 = toolCalls.Count;
                using CancellationTokenSource cts2 = CreateTurnCancellation();
                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt2
                }, cts2.Token);

                yield return PlayModeTestAwait.WaitTask(t2, LlmTurnTimeoutSeconds, "determinism craft 2");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("determinism craft 2", sink2, store);

                LlmToolCallRecord secondExecuteLua = toolCalls.RequireExtractableExecuteLuaSince(
                    toolMark2, BuiltInAgentRoleIds.CoreMechanic, "determinism craft 2");
                string secondPayload = ToolCallCapture.BuildExtractionPayload(secondExecuteLua);
                AssertExecuteLuaUsesNumericQualityIfPresent(secondPayload, "determinism craft 2");
                string secondName = CraftingMemoryItemNameExtractor.ExtractName(secondPayload);

                Assert.IsTrue(CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(firstName, secondName),
                    $"Repeated ingredients should reproduce the recorded craft. First='{firstName}', second='{secondName}'.");
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
            policy.SetToolsForRole(BuiltInAgentRoleIds.CoreMechanic, new ILlmTool[]
            {
                new DelegateLlmTool("execute_lua", "Execute lua code to create item",
                    new Action<string>(code =>
                    {
                        sink.Publish(new ApplyAiGameCommand
                            { CommandTypeId = AiGameCommandTypeIds.Envelope, JsonPayload = code });
                    }))
            });

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                telemetry,
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(), ScriptableObject.CreateInstance<CoreAISettingsAsset>());
        }

        private static CancellationTokenSource CreateTurnCancellation()
        {
            CancellationTokenSource cts = new();
            cts.CancelAfter(TimeSpan.FromSeconds(LlmTurnTimeoutSeconds));
            return cts;
        }

        private static string BuildCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft. No previous crafts to check.\n\n"
                : $"YOUR MEMORY (previous crafts):\n{previousCrafts}\n\n" +
                  "Create a weapon that is distinct from the previous crafts recorded above.\n\n";

            string instructions =
                "Apply the craft in the game by CALLING the execute_lua tool (a text reply with Lua code does " +
                "not execute anything). " +
                "Do not call memory(...) inside Lua; this test harness persists canonical memory after execute_lua. " +
                "The created item should have a concrete name, type 'weapon', and a numeric quality value. " +
                "Pass Lua code that contains the concrete item name, for example local weapon_name = \"Name\".";

            return header + ingredients + memorySection + instructions;
        }

        private static string BuildDeterministicCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft.\n\n"
                : $"YOUR MEMORY (ALL previous crafts):\n{previousCrafts}\n\n";

            string instructions =
                "These ingredients were used before. CALL the execute_lua tool (a text reply with Lua code does " +
                "not execute anything) using the recorded craft memory to recreate the consistent result " +
                "that the game should produce for the same ingredients. Do not call memory(...) inside Lua; " +
                "pass Lua code that contains the concrete item name, " +
                "for example local weapon_name = \"Name\".";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void AssertExecuteLuaUsesNumericQualityIfPresent(string payload, string label)
        {
            if (string.IsNullOrEmpty(payload) ||
                payload.IndexOf("create_item", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            // The quality argument must be numeric. Accept either an integer literal directly in the
            // call, or a Lua variable that the same code defines from numbers (reasoning models often
            // compute quality from ingredient stats, e.g. local quality = (75 + 50) // 2). The original
            // bug being guarded against is passing an UNDEFINED identifier such as create_item(...,
            // 'weapon', quality) with no quality definition anywhere in the code.
            bool hasNumericLiteralArg = Regex.IsMatch(
                payload,
                @"create_item\s*\([^)]*,\s*['""]weapon['""]\s*,\s*\d+",
                RegexOptions.IgnoreCase);
            bool definesNumericQuality = Regex.IsMatch(
                payload,
                @"\bqualit\w*\s*=\s*[^\r\n=]*\d",
                RegexOptions.IgnoreCase);

            if (!hasNumericLiteralArg && !definesNumericQuality)
            {
                Assert.Fail(
                    $"[{label}] create_item must receive a numeric quality (integer literal or a variable " +
                    $"defined from numbers in the same code). Payload:\n{payload}");
            }
        }

        private IEnumerator FlushMemoryStorePersistenceFrames()
        {
            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }
        }

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI]   SENDING TO MODEL: {label}");

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
            }
            else
            {
                Debug.Log("[CraftingMemory.OpenAI]   MEMORY: (empty - first craft)");
            }

            Debug.Log($"[CraftingMemory.OpenAI]   PROMPT ({prompt.Length} chars):\n{prompt}");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI]   MODEL RESPONSE: {label}");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.OpenAI]   Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.OpenAI]   RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.OpenAI]   NO COMMAND produced");
            }

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log(
                    "[CraftingMemory.OpenAI]   MEMORY: (empty in store after turn - harness may sync after ExtractCraftInfo)");
            }
        }

        private static bool ExtractCraftInfo(
            string executedLuaPayload,
            InMemoryStore store,
            List<string> craftedNames,
            ref string memoryAccum,
            string label,
            int craftNumber,
            string ingredient1Short,
            string ingredient2Short)
        {
            string payload = executedLuaPayload ?? "";
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                Assert.Inconclusive(
                    $"[{label}] execute_lua completed but item name could not be extracted. Payload: {payload}");
                return false;
            }

            AssertExecuteLuaUsesNumericQualityIfPresent(payload, label);

            craftedNames.Add(itemName);
            memoryAccum = BuildCanonicalMemory(memoryAccum, craftNumber, itemName, ingredient1Short, ingredient2Short);

            // Keep store aligned with memoryAccum (what the next BuildCraftPrompt uses). Do not append
            // "| New: ..." to model-written memory; that made "MEMORY VISIBLE TO MODEL" logs misleading.
            store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryAccum });

            Debug.Log($"[{label}]  Crafted: '{itemName}'");
            return true;
        }

        private static bool HasExtractableCraftName(string executedLuaPayload)
        {
            return !string.IsNullOrEmpty(CraftingMemoryItemNameExtractor.ExtractName(executedLuaPayload));
        }

        private static string BuildCanonicalMemory(
            string existing,
            int craftNumber,
            string itemName,
            string ingredient1Short,
            string ingredient2Short)
        {
            string entry = $"Craft #{craftNumber} - {itemName} made from {ingredient1Short} + {ingredient2Short}";
            if (string.IsNullOrWhiteSpace(existing))
            {
                return $"Previous crafts: {entry}";
            }

            // Avoid double-appending the same craft number if model spammed multiple tool calls.
            if (existing.Contains($"Craft #{craftNumber} -", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            return $"{existing}, {entry}";
        }

        #endregion

        private sealed class ToolCallCapture : IDisposable
        {
            private readonly IDisposable _subscription;
            private readonly List<LlmToolCallRecord> _records = new();

            public ToolCallCapture()
            {
                _subscription = CoreAi.SubscribeToolCalls(_records.Add);
            }

            public int Count => _records.Count;

            public LlmToolCallRecord TryGetCompletedToolSince(int startIndex, string roleId, string toolName)
            {
                return _records
                    .Skip(startIndex)
                    .LastOrDefault(r =>
                        r.Status == "completed" &&
                        string.Equals(r.Info.RoleId, roleId, StringComparison.Ordinal) &&
                        string.Equals(r.Info.ToolName, toolName, StringComparison.Ordinal));
            }

            public void Dispose()
            {
                _subscription.Dispose();
            }

            public LlmToolCallRecord RequireCompletedToolSince(int startIndex, string roleId, string toolName,
                string label)
            {
                LlmToolCallRecord record = _records
                    .Skip(startIndex)
                    .LastOrDefault(r =>
                        r.Status == "completed" &&
                        string.Equals(r.Info.RoleId, roleId, StringComparison.Ordinal) &&
                        string.Equals(r.Info.ToolName, toolName, StringComparison.Ordinal));

                if (record == null)
                {
                    string seen = string.Join(", ", _records.Skip(startIndex)
                        .Select(r => $"{r.Info.RoleId}:{r.Info.ToolName}:{r.Status}"));
                    Assert.Fail(
                        $"[{label}] Expected completed tool '{toolName}' for role '{roleId}'. Seen: [{seen}]");
                }

                return record;
            }

            public LlmToolCallRecord RequireExtractableExecuteLuaSince(int startIndex, string roleId, string label)
            {
                LlmToolCallRecord record = RequireCompletedToolSince(startIndex, roleId, "execute_lua", label);
                string payload = BuildExtractionPayload(record);
                if (!HasExtractableCraftName(payload))
                {
                    string seen = string.Join("\n---\n", _records.Skip(startIndex)
                        .Select(r => $"{r.Info.RoleId}:{r.Info.ToolName}:{r.Status}\n{BuildExtractionPayload(r)}"));
                    Assert.Fail(
                        $"[{label}] execute_lua completed, but no craft item name could be extracted. " +
                        $"This is a model/tool-output quality failure, not a retryable harness prompt. Seen:\n{seen}");
                }

                return record;
            }

            public static string BuildExtractionPayload(LlmToolCallRecord record)
            {
                if (record == null)
                {
                    return "";
                }

                string args = record.Info.ArgumentsJson ?? "";
                string result = record.ResultJson ?? "";
                if (string.IsNullOrWhiteSpace(result))
                {
                    return args;
                }

                return string.IsNullOrWhiteSpace(args) ? result : args + "\n" + result;
            }
        }
    }

    /// <summary>
    /// Extracts crafted item names from model payloads while ignoring common filler words.
    /// </summary>
    internal static class CraftingMemoryItemNameExtractor
    {
        private static readonly HashSet<string> JunkSingleWordNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "with", "the", "a", "an", "and", "or", "for", "from", "to", "of", "in", "on", "at", "is", "it", "as", "be",
            "quality", "weapon", "memory", "item", // line "- Weapon created" () vs
            "iron", "steel", "hardwood", "oak", "mithril", "enchanted", "wood", "crystal", "fire",
            "execute_lua", // tool JSON envelope: "name": "execute_lua" must not become the item name
            "nlocal"
        };

        private static readonly Regex[] Patterns =
        {
            // Lua: create_item('Name', ...)
            new("create_item\\s*\\(\\s*'([^']+)'"),
            new("create_item\\s*\\(\\s*\"([^\"]+)\""),
            new(@"create_item\s*\(\s*\\""\s*([^""\\]+)\s*\\""", RegexOptions.IgnoreCase),
            // Lua: CreateItem("weapon", "Name")
            new("CreateItem\\s*\\(\\s*\"weapon\"\\s*,\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase),
            new(@"CreateItem\s*\(\s*\\""\s*weapon\s*\\""\s*,\s*\\""\s*([^""\\]+)\s*\\""", RegexOptions.IgnoreCase),
            new("CreateItem\\s*\\(\\s*\"([^\"]+)\"\\s*,\\s*\"weapon\"", RegexOptions.IgnoreCase),
            new(@"CreateItem\s*\(\s*\\""\s*([^""\\]+)\s*\\""\s*,\s*\\""\s*weapon\s*\\""", RegexOptions.IgnoreCase),
            // Markdown craft line used by some local models (before generic **Bold** single-token matches).
            new(@"\*\*Weapon\s+crafted\*\*\s*:\s*([^\r\n*]+?)(?=\s+created\b)", RegexOptions.IgnoreCase),
            // Response line: Crafted: **Ironwood Blade** | Type: weapon
            new(@"Crafted\s*:\s*\*{0,2}([^\r\n\|\*]+?)\*{0,2}\s*\|", RegexOptions.IgnoreCase),
            // Prose: Created "IronOak Blade" weapon ...
            new("Created\\s+\"([^\"]+)\"\\s+weapon", RegexOptions.IgnoreCase),
            // Memory: Craft #1: Created "Ironwood Sword" (Weapon).
            new("Craft #\\d+\\s*:\\s*Created\\s+\"([^\"]+)\"\\s*\\(", RegexOptions.IgnoreCase),
            // Memory: Craft #1: Ironwood Blade (weapon).
            new("Craft #\\d+\\s*:\\s*([^\\r\\n\\(\\.]+?)\\s*\\(", RegexOptions.IgnoreCase),
            // Memory: Craft #2 Created Item: Flame-Forged Warhammer (weapon).
            new("Craft #\\d+\\s+Created\\s+Item\\s*:\\s*([^\\r\\n\\(\\.]+)", RegexOptions.IgnoreCase),
            // Prose: memory line in these tests
            new("details for \"([^\"]+)\""),
            // e.g. **Memory updated** with Craft #3 entry for "MithrilEnchant Blade "
            new("entry for \"([^\"]+)\""),
            // "The weapon "SteelHardwood Blade" has been crafted"
            new("(?:[Tt]he )?weapon\\s+\"([^\"]+)\""),
            // Memory: Craft Log #1: Weapon "Ironwood Blade" crafted from ...
            new("Weapon\\s+\"([^\"]+)\"\\s+crafted\\b", RegexOptions.IgnoreCase),
            // Canonical memory line: Craft #2 - Steel-Hardwood Scimitar made from Steel + Hardwood
            new("Craft #\\d+\\s*-\\s*([A-Za-z0-9][A-Za-z0-9_ -]*?)\\s+made\\s+from\\b",
                RegexOptions.IgnoreCase),
            // " with Craft #4 - SteelHardwood Blade (identical to "
            new("Craft #\\d+\\s*-\\s*([A-Za-z0-9][A-Za-z0-9_ ]*?)\\s*\\("),
            // Lua comments may summarize the concrete item before the variable assignment:
            // -- Craft #2: Inferno Blade
            new(@"^\s*--\s*Craft #\d+\s*:\s*([A-Za-z0-9][A-Za-z0-9_ -]*[A-Za-z0-9])\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            // Thinking traces may mention a PascalCase craft name next to the craft number.
            new("Craft #\\d+\\s*[-:]+\\s*\"?([A-Z][A-Za-z0-9]+(?:[A-Z][a-z]+)+)\"?", RegexOptions.IgnoreCase),
            // Thinking traces may quote the selected craft name in natural language.
            new("(?:should be|exact name|use|same as)\\s+\"([A-Z][A-Za-z0-9_ ]+)\"", RegexOptions.IgnoreCase),
            // Lua table field inside generated code (before generic JSON "name": tool keys)
            new(@"\bitem_name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase),
            new(@"\bitem_name\s*=\s*\\""([^""]+)\\""", RegexOptions.IgnoreCase),
            new(@"\b(?:weapon|craft)_?name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase),
            new(@"\b(?:weapon|craft)_?name\s*=\s*\\""([^""]+)\\""", RegexOptions.IgnoreCase),
            // Lua: return "Flameforged Steel Sword" (bare quoted name as the chunk result).
            // Case-sensitive first letter: only capitalized item names, not lowercase status strings.
            new(@"\breturn\s+\\?""([A-Z][A-Za-z0-9_' -]{2,}?)\\?"""),
            // Degenerate tool call where the WHOLE code argument is just the quoted item name:
            // {"code":"\"Flamesteel Blade\""}
            new(@"""code""\s*:\s*""\\""([A-Z][A-Za-z0-9_' -]{2,}?)\\"""""),
            new("\"item_name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase),
            new(@"\\\""item_name\\\""\s*:\s*\\\""([^""\\]+)\\\""", RegexOptions.IgnoreCase),
            new(@"\bname\s*=\s*'([^']+)'", RegexOptions.IgnoreCase),
            new(@"\bname\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase),
            new(@"\bname\s*=\s*\\""([^""]+)\\""", RegexOptions.IgnoreCase),
            // JSON: "name": "..." (may match tool envelope; junk-filter execute_lua / memory above)
            new("\"name\"\\s*:\\s*\"([^\"]+)\""),
            new(@"\\\""name\\\""\s*:\s*\\\""([^""\\]+)\\\""", RegexOptions.IgnoreCase),
            new("Name\\s*=\\s*\"([^\"]+)\""),
            // Quoted PascalCase compound craft names.
            new("\"([A-Z][a-z]+(?:[A-Z][a-z]+){1,})\""),
            // " crafted with quality" must NOT match "with" as the name  (?!with\b)
            new("\\bcrafted\\s+(?!with\\b)\\s*\\*{0,2}([A-Za-z][A-Za-z0-9_']*(?:\\s+[A-Za-z][A-Za-z0-9_']*)*)\\*{0,2}",
                RegexOptions.IgnoreCase),
            // Freeform: "X created" (PascalCase multi-part)
            new("\\*{0,2}([A-Z][A-Za-z]{2,}(?:[A-Z][a-z]+)+)\\*{0,2}\\s+(?:created|crafted|forged)",
                RegexOptions.IgnoreCase),
            // Markdown bold: **WeaponName** (one word, after higher-priority patterns)
            new("\\*\\*([A-Z][A-Za-z0-9_]{3,})\\*\\*")
        };

        /// <summary>
        /// Compares craft names for the determinism step: models often drift on whitespace
        /// (e.g. <c>SteelHardwood Spear</c> vs <c>SteelHardwoodSpear</c>).
        /// </summary>
        public static string NormalizeForDeterminismComparison(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            return Regex.Replace(name, @"\s+", "");
        }

        public static bool NamesMatchForDeterminism(string a, string b)
        {
            return string.Equals(
                NormalizeForDeterminismComparison(a),
                NormalizeForDeterminismComparison(b),
                StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractName(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return null;
            }

            string name = ExtractNameFromPatterns(payload);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            string normalizedEscapedJson = payload.Replace(@"\\\""", @"\""");
            if (!string.Equals(normalizedEscapedJson, payload, StringComparison.Ordinal))
            {
                return ExtractNameFromPatterns(normalizedEscapedJson);
            }

            return null;
        }

        private static string ExtractNameFromPatterns(string payload)
        {
            foreach (Regex regex in Patterns)
            {
                foreach (Match match in regex.Matches(payload))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    string name = match.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(name) || IsJunkName(name))
                    {
                        continue;
                    }

                    return name;
                }
            }

            return null;
        }

        private static bool IsJunkName(string name)
        {
            if (JunkSingleWordNames.Contains(name))
            {
                return true;
            }

            if (name.Contains("\\n", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("\\r", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("n", StringComparison.OrdinalIgnoreCase) && name.Length > 1 &&
                 JunkSingleWordNames.Contains(name.Substring(1))))
            {
                return true;
            }

            // Reject empty or one-character names.
            if (name.Length <= 1)
            {
                return true;
            }

            return false;
        }
    }
}