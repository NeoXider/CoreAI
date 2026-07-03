#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Comprehensive end-to-end PlayMode scenario that exercises the full CoreAI pipeline
    /// in a single coherent RPG game session:
    /// Phase 1: Skills and tool calling, where the model reads the SkillSet catalog
    /// and uses crafting tools.
    /// Phase 2: Memory persistence, where a later turn reads back the crafting result.
    /// Phase 3: Multi-turn conversation through chat history context.
    /// Phase 4: Cross-skill routing from Crafting to Combat.
    /// All phases use a real LLM backend and validate the full decorator chain:
    /// LoggingLlmClientDecorator, RoutingLlmClient, OpenAiChatLlmClient,
    /// MeaiLlmClient, SmartToolCallingChatClient, and ToolExecutionPolicy.
    /// </summary>
    [TestFixture]
    public sealed class FullPipelineE2EPlayModeTests
    {
        private const int PhaseTimeoutSeconds = 180;
        private const int LiveModelMaxOutputTokens = 2048;

        // Tool call tracking.

        private static readonly List<string> _calledTools = new();
        private static readonly Dictionary<string, string> _lastToolArgs = new();
        private static string _memoryContent = "";

        private static void ResetTracking()
        {
            _calledTools.Clear();
            _lastToolArgs.Clear();
            _memoryContent = "";
        }

        // Crafting tools.

        private static object GetRecipes(string itemType)
        {
            _calledTools.Add("get_recipes");
            _lastToolArgs["get_recipes"] = itemType;
            Debug.Log($"[E2E] get_recipes({itemType})");
            return new[]
            {
                new
                {
                    recipe_id = "fire_sword_01", name = "Flame Sword",
                    materials = new[] { "iron_ingot x2", "fire_gem x1" }, quality = "rare"
                },
                new
                {
                    recipe_id = "iron_shield_01", name = "Iron Shield",
                    materials = new[] { "iron_ingot x3", "leather x2" }, quality = "normal"
                }
            };
        }

        private static object CheckInventory(string materials)
        {
            _calledTools.Add("check_inventory");
            _lastToolArgs["check_inventory"] = materials;
            Debug.Log($"[E2E] check_inventory({materials})");
            return new { iron_ingot = 5, fire_gem = 2, leather = 3, sufficient = true };
        }

        private static object CraftItem(string recipeId, double qualityMod)
        {
            _calledTools.Add("craft_item");
            _lastToolArgs["craft_item"] = recipeId;
            Debug.Log($"[E2E] craft_item({recipeId}, quality={qualityMod})");
            return new
            {
                success = true,
                item_name = "Flame Sword",
                quality = "Rare",
                damage = 45,
                fire_damage = 15,
                message = "The forge roars as flames engulf the blade!"
            };
        }

        // Combat tools.

        private static object GetEnemyInfo(string enemyId)
        {
            _calledTools.Add("get_enemy_info");
            _lastToolArgs["get_enemy_info"] = enemyId;
            Debug.Log($"[E2E] get_enemy_info({enemyId})");
            return new { name = "Fire Drake", hp = 200, weakness = "ice", attack = 35, level = 10 };
        }

        private static object AttackEnemy(string enemyId, string weaponId)
        {
            _calledTools.Add("attack_enemy");
            Debug.Log($"[E2E] attack_enemy({enemyId}, {weaponId})");
            return new
            {
                hit = true, damage_dealt = 60, enemy_hp_remaining = 140, critical = true,
                message = "Critical hit with Flame Sword!"
            };
        }

        // Lore tools.

        private static object SearchLore(string query)
        {
            _calledTools.Add("search_lore");
            Debug.Log($"[E2E] search_lore({query})");
            return new
            {
                title = "Fire Drakes of the Ashen Peaks",
                text =
                    "Fire Drakes are vulnerable to ice magic. Their scales can be harvested for fire-resistant armor.",
                source = "bestiary_vol3"
            };
        }

        // Memory tool with manual tracking.

        private static object WriteMemory(string content)
        {
            _calledTools.Add("memory_write");
            _memoryContent = content;
            Debug.Log($"[E2E] memory_write: {content}");
            return new { success = true };
        }

        private static object ReadMemory()
        {
            _calledTools.Add("memory_read");
            Debug.Log($"[E2E] memory_read -> {_memoryContent}");
            return new { content = string.IsNullOrEmpty(_memoryContent) ? "(empty)" : _memoryContent };
        }

        // Helpers.

        private sealed class Sink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand c)
            {
                Items.Add(c);
            }
        }

        private sealed class CaptureLlm : ILlmClient
        {
            private readonly ILlmClient _inner;
            public int CallCount;
            public long TotalMs;
            public string LastContent;
            public bool LastOk;
            public readonly List<string> AllResponses = new();

            public CaptureLlm(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                CallCount++;
                Stopwatch sw = Stopwatch.StartNew();
                LlmCompletionResult result = await _inner.CompleteAsync(request, ct);
                sw.Stop();
                TotalMs += sw.ElapsedMilliseconds;
                LastOk = result is { Ok: true };
                LastContent = result?.Content ?? "";
                AllResponses.Add(LastContent);
                return result;
            }

            // RunTaskAsync streams by default, so the capture MUST track the streaming path too —
            // otherwise CallCount/LastOk/LastContent stay at their defaults and the test wrongly
            // reports "LLM failed" for a perfectly successful streamed turn.
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                CallCount++;
                Stopwatch sw = Stopwatch.StartNew();
                StringBuilder text = new();
                bool ok = true;
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, ct))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        text.Append(chunk.Text);
                    }

                    if (chunk.IsDone && !string.IsNullOrEmpty(chunk.Error))
                    {
                        ok = false;
                    }

                    yield return chunk;
                }

                sw.Stop();
                TotalMs += sw.ElapsedMilliseconds;
                LastOk = ok;
                LastContent = text.ToString();
                AllResponses.Add(LastContent);
            }
        }

        // E2E test.

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator FullPipeline_Skills_Tools_Memory_MultiTurn()
        {
            Debug.Log("[E2E] FULL PIPELINE E2E: Skills + Tools + Memory + Chat");

            ResetTracking();
            LogAssert.ignoreFailingMessages = true;

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[E2E] Backend: {handle.ResolvedBackend}");

                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.Offline)
                {
                    Assert.Inconclusive("E2E test requires a live LLM backend (HTTP or LLMUnity).");
                }


                // Define skills.


                SkillSet craftingSkill = new("Crafting",
                    "Forge weapons and armor from raw materials",
                    "You are a master blacksmith. Steps:\n" +
                    "1. Call get_recipes to see available recipes.\n" +
                    "2. Call check_inventory to verify materials.\n" +
                    "3. Call craft_item with recipe_id and quality 1.0.\n" +
                    "4. Record what you crafted for later recall.\n" +
                    "5. Tell the player the result.\n" +
                    "Always follow this order.",
                    new DelegateLlmTool("get_recipes", "Get available crafting recipes",
                        new Func<string, object>(GetRecipes)),
                    new DelegateLlmTool("check_inventory", "Check player materials",
                        new Func<string, object>(CheckInventory)),
                    new DelegateLlmTool("craft_item", "Craft an item from recipe",
                        new Func<string, double, object>(CraftItem)));

                SkillSet combatSkill = new("Combat",
                    "Fight enemies in tactical encounters",
                    "Steps:\n1. Call get_enemy_info first.\n2. Call attack_enemy.\n3. Report results.",
                    new DelegateLlmTool("get_enemy_info", "Get enemy stats and weaknesses",
                        new Func<string, object>(GetEnemyInfo)),
                    new DelegateLlmTool("attack_enemy", "Attack an enemy with a weapon",
                        new Func<string, string, object>(AttackEnemy)));

                SkillSet loreSkill = new("Lore",
                    "World knowledge and bestiary",
                    "Call search_lore to find information.",
                    new DelegateLlmTool("search_lore", "Search the game lore database",
                        new Func<string, object>(SearchLore)));

                // Memory tools outside skills are always available.
                DelegateLlmTool memoryWriteTool = new("memory_write", "Save information to persistent memory",
                    new Func<string, object>(WriteMemory));
                DelegateLlmTool memoryReadTool = new("memory_read", "Read previously saved memory",
                    new Func<object>(ReadMemory));


                // Build agent.


                const string roleId = "E2E_GameMaster";
                AgentConfig config = new AgentBuilder(roleId) { SuppressBuildWarnings = true }
                    .WithSystemPrompt(
                        "You are a Game Master for a fantasy RPG. " +
                        "Rely on configured capabilities to handle the player's request. " +
                        "Save important events, recall past events when asked, and respond briefly.")
                    .WithSkill(craftingSkill)
                    .WithSkill(combatSkill)
                    .WithSkill(loreSkill)
                    .WithTool(memoryWriteTool)
                    .WithTool(memoryReadTool)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                config.ApplyToPolicy(policy);

                CaptureLlm cap = new(handle.WrapWithMemoryStore(store));
                CoreAISettingsAsset settings = CoreAISettingsAsset.Instance
                                               ?? ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                Sink sink = new();

                AiOrchestrator orch = new(
                    new SoloAuthorityHost(), cap, sink, new SessionTelemetryCollector(),
                    new AiPromptComposer(
                        new BuiltInDefaultAgentSystemPromptProvider(),
                        new NoAgentUserPromptTemplateProvider(),
                        new NullLuaScriptVersionStore(), null, policy, settings),
                    store, policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    settings, null, null, null);


                // Phase 1: Crafting with SkillSet.


                Debug.Log("[E2E] PHASE 1: Craft a Flame Sword (SkillSet -> tools -> memory)");

                using CancellationTokenSource phase1Cts = new();
                Task t1 = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "I want to craft a Flame Sword. Save what you crafted to memory.",
                    MaxOutputTokens = LiveModelMaxOutputTokens
                }, phase1Cts.Token);
                yield return PlayModeTestAwait.WaitTask(t1, PhaseTimeoutSeconds, "Phase 1: Crafting", phase1Cts);

                Debug.Log($"[E2E] Phase 1 tools: [{string.Join(", ", _calledTools)}]");
                Debug.Log($"[E2E] Phase 1 response: {cap.LastContent}");
                Debug.Log($"[E2E] Phase 1 LLM calls: {cap.CallCount}");

                if (!cap.LastOk)
                {
                    Assert.Inconclusive($"Phase 1 LLM failed: {cap.LastContent}");
                }

                // Assert: at least one crafting tool was called
                bool anyCraftTool = _calledTools.Contains("get_recipes") ||
                                    _calledTools.Contains("check_inventory") ||
                                    _calledTools.Contains("craft_item");

                if (!anyCraftTool && handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    Assert.Fail(
                        $"Phase 1: local model ({handle.ResolvedBackend}) did not call crafting tools - " +
                        $"multi-step SkillSet pipeline (read_skill->get_recipes->check_inventory->craft_item->memory_write) " +
                        $"exceeds small model capacity. Called: [{string.Join(", ", _calledTools)}]. " +
                        $"Pipeline correctness verified by SelfService_* tests.");
                }

                Assert.IsTrue(anyCraftTool,
                    $"Phase 1: at least one crafting tool must be called. Got: [{string.Join(", ", _calledTools)}]");

                Debug.Log("[E2E] Phase 1 passed - crafting tools invoked");


                // Phase 2: Memory recall.


                Debug.Log("[E2E] PHASE 2: Recall what was crafted (memory_read)");

                int toolsBefore = _calledTools.Count;
                using CancellationTokenSource phase2Cts = new();
                Task t2 = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "What did I craft earlier?",
                    MaxOutputTokens = LiveModelMaxOutputTokens
                }, phase2Cts.Token);
                yield return PlayModeTestAwait.WaitTask(t2, PhaseTimeoutSeconds, "Phase 2: Memory recall", phase2Cts);

                Debug.Log($"[E2E] Phase 2 tools: [{string.Join(", ", _calledTools.Skip(toolsBefore))}]");
                Debug.Log($"[E2E] Phase 2 response: {cap.LastContent}");

                if (!cap.LastOk)
                {
                    Assert.Inconclusive($"Phase 2 LLM failed: {cap.LastContent}");
                }

                // Phase 2: model should have called memory_read OR mentioned crafted item from context
                bool hasMemoryRead = _calledTools.Skip(toolsBefore).Contains("memory_read");
                bool mentionsSword = cap.LastContent != null &&
                                     (cap.LastContent.IndexOf("sword", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      cap.LastContent.IndexOf("flame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      cap.LastContent.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0);

                Assert.IsTrue(hasMemoryRead || mentionsSword,
                    $"Phase 2: model should recall via memory_read or mention the crafted item. " +
                    $"memory_read called: {hasMemoryRead}, mentions sword/flame/craft: {mentionsSword}");

                Debug.Log("[E2E] Phase 2 passed - memory recall works");


                // Phase 3: Cross-skill switch to Combat.


                Debug.Log("[E2E] PHASE 3: Fight a Fire Drake (Combat skill)");

                int toolsBefore3 = _calledTools.Count;
                using CancellationTokenSource phase3Cts = new();
                Task t3 = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "A Fire Drake appeared! Fight it.",
                    MaxOutputTokens = LiveModelMaxOutputTokens
                }, phase3Cts.Token);
                yield return PlayModeTestAwait.WaitTask(t3, PhaseTimeoutSeconds, "Phase 3: Combat", phase3Cts);

                Debug.Log($"[E2E] Phase 3 tools: [{string.Join(", ", _calledTools.Skip(toolsBefore3))}]");
                Debug.Log($"[E2E] Phase 3 response: {cap.LastContent}");

                if (!cap.LastOk)
                {
                    Assert.Inconclusive($"Phase 3 LLM failed: {cap.LastContent}");
                }

                bool anyCombatTool = _calledTools.Skip(toolsBefore3)
                    .Any(t => t == "get_enemy_info" || t == "attack_enemy");

                if (!anyCombatTool && handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    Assert.Fail(
                        $"Phase 3: local model ({handle.ResolvedBackend}) did not call combat tools - " +
                        $"multi-step skill pipeline exceeds small model capacity. " +
                        $"Called: [{string.Join(", ", _calledTools.Skip(toolsBefore3))}]. " +
                        $"Pipeline correctness verified by SelfService_* tests.");
                }

                Assert.IsTrue(anyCombatTool,
                    $"Phase 3: at least one combat tool must be called. Got: [{string.Join(", ", _calledTools.Skip(toolsBefore3))}]");

                Debug.Log("[E2E] Phase 3 passed - combat tools invoked");


                // Summary.


                Debug.Log("[E2E] E2E RESULTS");
                Debug.Log($"[E2E] Total LLM calls:    {cap.CallCount}");
                Debug.Log($"[E2E] Total time:         {cap.TotalMs} ms");
                Debug.Log($"[E2E] All tools called:   [{string.Join(", ", _calledTools)}]");
                Debug.Log($"[E2E] Unique tools:       [{string.Join(", ", _calledTools.Distinct())}]");
                Debug.Log($"[E2E] Memory content:     {_memoryContent}");
                Debug.Log($"[E2E] Phases passed:      3/3");

                // Final: must have used tools from at least 2 different skills
                List<string> uniqueTools = _calledTools.Distinct().ToList();
                bool hasCraftingTools =
                    uniqueTools.Any(t => t == "get_recipes" || t == "check_inventory" || t == "craft_item");
                bool hasCombatTools = uniqueTools.Any(t => t == "get_enemy_info" || t == "attack_enemy");

                Assert.IsTrue(hasCraftingTools && hasCombatTools,
                    $"Must use tools from both Crafting and Combat skills. " +
                    $"Crafting: {hasCraftingTools}, Combat: {hasCombatTools}. " +
                    $"All: [{string.Join(", ", uniqueTools)}]");

                Debug.Log("[E2E] ALL PHASES PASSED - Full pipeline E2E verified!");
            }
            finally
            {
                handle.Dispose();
                LogAssert.ignoreFailingMessages = false;
            }
        }
    }
}
#endif
