#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.Scenarios.Complex
{
    /// <summary>
    /// Complex behavior scenario:
    /// Player greets merchant, asks goods, tries expensive purchase with not enough gold,
    /// negotiates discount, then successfully buys an item.
    /// </summary>
    public sealed class MerchantBehaviorChatWithToolsPlayModeTests
    {
        private const int LlmStepTimeoutSeconds = 240;
        private const int ScenarioStepMaxOutputTokens = 2048;

        [Test]
        public async Task MerchantEconomy_BuyWithoutDiscount_DoesNotMutateOnInsufficientGold()
        {
            MerchantInventoryProvider inventory = new();
            MerchantEconomyState economy = new(inventory);

            string result = await economy.BuyItemAsync("Iron Sword", 1);

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("insufficient_gold", result);
            Assert.AreEqual(40, economy.PlayerGold);
            Assert.AreEqual(2, inventory.Items.Single(i => i.Name == "Iron Sword").Quantity);
            Assert.IsEmpty(economy.PlayerInventory);
            CollectionAssert.Contains(economy.CallLog, "buy_item:Iron Sword:1:insufficient_gold");
        }

        [Test]
        public async Task MerchantEconomy_DiscountEnablesPurchaseWithinPlayerBudget()
        {
            MerchantInventoryProvider inventory = new();
            MerchantEconomyState economy = new(inventory);

            string discount = await economy.ApplyDiscountAsync("Iron Sword", 34);
            string purchase = await economy.BuyItemAsync("Iron Sword", 1);

            StringAssert.Contains("\"success\":true", discount);
            StringAssert.Contains("\"discountPercent\":34", discount);
            StringAssert.Contains("\"success\":true", purchase);
            Assert.AreEqual(0, economy.PlayerGold);
            Assert.AreEqual(1, inventory.Items.Single(i => i.Name == "Iron Sword").Quantity);
            CollectionAssert.AreEqual(new[] { "Iron Sword" }, economy.PlayerInventory);
            CollectionAssert.Contains(economy.CallLog, "apply_discount:Iron Sword:34");
            Assert.IsTrue(
                economy.CallLog.Any(c => c.StartsWith("buy_item:Iron Sword:1:success:40",
                    StringComparison.Ordinal)),
                "A 34% discount should round the 60g sword to exactly the player's 40g budget.");
        }

        [UnityTest]
        [Explicit("Long live-model negotiation scenario; run targeted when validating merchant behavior, not in mandatory full PlayMode.")]
        [Timeout(600000)]
        public IEnumerator MerchantChatWithTools_FullNegotiationFlow_CompletesPurchase()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.25f,
                    240,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[MerchantScenario] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                MerchantInventoryProvider inventory = new();
                MerchantEconomyState economy = new(inventory);
                ListSink sink = new();

                AgentMemoryPolicy policy = new();
                AgentBuilder builder = new AgentBuilder(BuiltInAgentRoleIds.Merchant)
                    .WithMode(AgentMode.ToolsAndChat)
                    .WithChatHistory()
                    .WithMemory()
                    .WithTool(new InventoryLlmTool(inventory))
                    .WithTool(new DelegateLlmTool("get_player_gold",
                        "Return current player gold amount.", new Func<Task<string>>(economy.GetPlayerGoldAsync)))
                    .WithTool(new DelegateLlmTool("apply_discount",
                        "Apply percent discount to an item. Args: itemName, percent.",
                        new Func<string, int, Task<string>>(economy.ApplyDiscountAsync)))
                    .WithTool(new DelegateLlmTool("buy_item",
                        "Try buying an item for player. Args: itemName, quantity.",
                        new Func<string, int, Task<string>>(economy.BuyItemAsync)));
                builder.Build().ApplyToPolicy(policy);

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
                AiOrchestrator orch = new(
                    new SoloAuthorityHost(),
                    clientWithMemory,
                    sink,
                    new SessionTelemetryCollector(),
                    new AiPromptComposer(
                        new BuiltInDefaultAgentSystemPromptProvider(),
                        new NoAgentUserPromptTemplateProvider(),
                        new NullLuaScriptVersionStore()),
                    store,
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    ScriptableObject.CreateInstance<CoreAISettingsAsset>());

                yield return RunStep(orch, sink, inventory, economy, store, "Step1_GreetAndList",
                    "You are a merchant NPC in game. Player says: 'Hi! What do you sell?'. " +
                    "Answer with available item names and prices.");

                const string step2Base =
                    "Player says: 'I want Leather Armor x1'. Player has low gold (40 gold). " +
                    "This step validates the purchase-failure tool path: call buy_item for Leather Armor x1. " +
                    "If purchase fails, explain briefly and suggest cheaper options.";
                yield return RunStep(orch, sink, inventory, economy, store, "Step2_TooExpensive", step2Base,
                    LlmToolChoiceMode.RequireSpecific, "buy_item");

                yield return RunStep(orch, sink, inventory, economy, store, "Step3_NegotiateAndBuy",
                    "Player says: 'I can spend at most 40 gold and I want one Iron Sword. " +
                    "Can we make a deal and complete the purchase if the price fits?'");

                Assert.IsTrue(inventory.CallLog.Any(c => c.StartsWith("get_inventory", StringComparison.Ordinal)),
                    "Merchant should inspect inventory.");
                Assert.IsTrue(
                    economy.CallLog.Any(c => c.StartsWith("buy_item:Leather Armor", StringComparison.Ordinal)),
                    "Scenario should attempt expensive purchase first.");
                Assert.IsTrue(
                    economy.CallLog.Any(c => c.StartsWith("apply_discount:Iron Sword:", StringComparison.Ordinal)),
                    "Scenario should negotiate discount.");
                Assert.IsTrue(
                    economy.CallLog.Any(c =>
                        c.StartsWith("buy_item:Iron Sword:1:success", StringComparison.Ordinal)),
                    "Scenario should finish with successful purchase.");
                Assert.AreEqual(1, economy.PlayerInventory.Count(i => i == "Iron Sword"));
                Assert.Less(economy.PlayerGold, 40, "Gold should decrease after successful purchase.");
                Assert.IsTrue(sink.Items.Count > 0, "Orchestrator should publish chat payloads.");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static IEnumerator RunStep(
            AiOrchestrator orch,
            ListSink sink,
            MerchantInventoryProvider inventory,
            MerchantEconomyState economy,
            InMemoryStore store,
            string label,
            string hint,
            LlmToolChoiceMode forcedToolMode = LlmToolChoiceMode.Auto,
            string requiredToolName = "")
        {
            Debug.Log($"[MerchantScenario] === {label} ===");
            Debug.Log($"[MerchantScenario] PLAYER: {hint}");
            int beforeCommands = sink.Items.Count;
            using CancellationTokenSource cts = new();
            Task task = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Merchant,
                Hint = hint,
                SourceTag = "BehaviorScenario",
                ForcedToolMode = forcedToolMode,
                RequiredToolName = requiredToolName ?? "",
                MaxOutputTokens = ScenarioStepMaxOutputTokens
            }, cts.Token);
            yield return PlayModeTestAwait.WaitTask(task, LlmStepTimeoutSeconds, label, cts);

            if (sink.Items.Count > beforeCommands)
            {
                for (int i = beforeCommands; i < sink.Items.Count; i++)
                {
                    ApplyAiGameCommand cmd = sink.Items[i];
                    Debug.Log($"[MerchantScenario] LLM CMD[{i}] type={cmd.CommandTypeId}");
                    Debug.Log($"[MerchantScenario] LLM RESPONSE[{i}]: {cmd.JsonPayload}");
                }
            }
            else
            {
                Debug.LogWarning($"[MerchantScenario] No command payload produced for step: {label}");
            }

            Debug.Log($"[MerchantScenario] Inventory tool calls: {string.Join(" | ", inventory.CallLog)}");
            Debug.Log($"[MerchantScenario] Economy tool calls: {string.Join(" | ", economy.CallLog)}");
            Debug.Log(
                $"[MerchantScenario] Player gold: {economy.PlayerGold}; Player items: {string.Join(",", economy.PlayerInventory)}");

            if (store.TryLoad(BuiltInAgentRoleIds.Merchant, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[MerchantScenario] Memory snapshot: {mem.Memory}");
            }
        }

        private sealed class MerchantInventoryProvider : InventoryTool.IInventoryProvider
        {
            public readonly List<InventoryTool.InventoryItem> Items = new()
            {
                new InventoryTool.InventoryItem { Name = "Iron Sword", Type = "weapon", Quantity = 2, Price = 60 },
                new InventoryTool.InventoryItem
                    { Name = "Health Potion", Type = "consumable", Quantity = 8, Price = 30 },
                new InventoryTool.InventoryItem { Name = "Leather Armor", Type = "armor", Quantity = 1, Price = 100 }
            };

            public readonly List<string> CallLog = new();

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken)
            {
                CallLog.Add($"get_inventory:{Items.Count}");
                return Task.FromResult(Items);
            }
        }

        private sealed class MerchantEconomyState
        {
            private readonly MerchantInventoryProvider _inventory;
            private readonly Dictionary<string, int> _discounts = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> PlayerInventory = new();
            public readonly List<string> CallLog = new();
            public int PlayerGold = 40;

            public MerchantEconomyState(MerchantInventoryProvider inventory)
            {
                _inventory = inventory;
            }

            public Task<string> GetPlayerGoldAsync()
            {
                CallLog.Add($"get_player_gold:{PlayerGold}");
                return Task.FromResult(JsonConvert.SerializeObject(new { success = true, gold = PlayerGold }));
            }

            public Task<string> ApplyDiscountAsync(string itemName, int percent)
            {
                int clamped = Math.Max(0, Math.Min(percent, 90));
                _discounts[itemName ?? string.Empty] = clamped;
                CallLog.Add($"apply_discount:{itemName}:{clamped}");
                return Task.FromResult(JsonConvert.SerializeObject(new
                {
                    success = true,
                    itemName,
                    discountPercent = clamped
                }));
            }

            public Task<string> BuyItemAsync(string itemName, int quantity)
            {
                InventoryTool.InventoryItem item = _inventory.Items
                    .FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    CallLog.Add($"buy_item:{itemName}:{quantity}:not_found");
                    return Task.FromResult(
                        JsonConvert.SerializeObject(new { success = false, error = "item_not_found" }));
                }

                if (quantity <= 0 || item.Quantity < quantity)
                {
                    CallLog.Add($"buy_item:{itemName}:{quantity}:invalid_qty");
                    return Task.FromResult(JsonConvert.SerializeObject(new
                        { success = false, error = "not_enough_stock" }));
                }

                int discount = _discounts.TryGetValue(item.Name, out int p) ? p : 0;
                int unitPrice = Math.Max(1, (int)Math.Round(item.Price * (100 - discount) / 100f));
                int total = unitPrice * quantity;
                if (PlayerGold < total)
                {
                    CallLog.Add($"buy_item:{itemName}:{quantity}:insufficient_gold");
                    return Task.FromResult(JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "insufficient_gold",
                        totalPrice = total,
                        playerGold = PlayerGold
                    }));
                }

                PlayerGold -= total;
                item.Quantity -= quantity;
                for (int i = 0; i < quantity; i++)
                {
                    PlayerInventory.Add(item.Name);
                }

                CallLog.Add($"buy_item:{itemName}:{quantity}:success:{total}");
                return Task.FromResult(JsonConvert.SerializeObject(new
                {
                    success = true,
                    itemName = item.Name,
                    quantity,
                    totalPrice = total,
                    remainingGold = PlayerGold
                }));
            }
        }

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
                Items.Add(command);
            }
        }
    }
}
#endif
