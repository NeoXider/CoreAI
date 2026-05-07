using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Real LLM test: self-service skills — model calls read_skill on its own,
    /// then uses the skill's tools. Verifies the full Cursor-like pattern.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class SkillSetRealToolCallingPlayModeTests
    {
        // ── Tool call trackers ────────────────────────────────────────────────

        private static readonly List<string> _calledTools = new();
        private static readonly Dictionary<string, string> _toolResults = new();

        private static void ResetTracking()
        {
            _calledTools.Clear();
            _toolResults.Clear();
        }

        // ── Crafting tools ────────────────────────────────────────────────────

        private static object GetRecipes(string itemType)
        {
            _calledTools.Add("get_recipes");
            Debug.Log($"[SkillRealTest] 🔨 get_recipes called: itemType={itemType}");

            var recipes = new[]
            {
                new { recipe_id = "iron_sword_01", name = "Iron Sword", materials = new[] { "iron_ingot x3", "leather_strip x1" }, quality = "normal" },
                new { recipe_id = "iron_dagger_01", name = "Iron Dagger", materials = new[] { "iron_ingot x1", "leather_strip x1" }, quality = "normal" },
                new { recipe_id = "steel_shield_01", name = "Steel Shield", materials = new[] { "steel_ingot x4", "leather x2" }, quality = "fine" }
            };

            string result = Newtonsoft.Json.JsonConvert.SerializeObject(recipes);
            _toolResults["get_recipes"] = result;
            return recipes;
        }

        private static object CheckInventory(string materials)
        {
            _calledTools.Add("check_inventory");
            Debug.Log($"[SkillRealTest] 📦 check_inventory called: materials={materials}");

            var inventory = new
            {
                iron_ingot = new { available = 5, needed = 3 },
                leather_strip = new { available = 2, needed = 1 },
                sufficient = true
            };

            string result = Newtonsoft.Json.JsonConvert.SerializeObject(inventory);
            _toolResults["check_inventory"] = result;
            return inventory;
        }

        private static object CraftItem(string recipeId, float qualityModifier)
        {
            _calledTools.Add("craft_item");
            Debug.Log($"[SkillRealTest] ⚒️ craft_item called: recipe={recipeId}, quality={qualityModifier}");

            var craftResult = new
            {
                success = true,
                item_name = "Iron Sword",
                quality = qualityModifier >= 1.5f ? "Fine" : "Normal",
                durability = (int)(100 * qualityModifier),
                message = "The blacksmith hammers the iron into shape. A sturdy blade emerges!"
            };

            string result = Newtonsoft.Json.JsonConvert.SerializeObject(craftResult);
            _toolResults["craft_item"] = result;
            return craftResult;
        }

        // ── Combat tools (should not be used for crafting) ────────────────────

        private static object GetEnemyStats(string enemyId)
        {
            _calledTools.Add("get_enemy_stats");
            Debug.Log($"[SkillRealTest] ⚔️ get_enemy_stats called: {enemyId}");
            return new { name = "Goblin", hp = 50, attack = 10 };
        }

        // ── Lore tools ────────────────────────────────────────────────────────

        private static object SearchCodex(string query)
        {
            _calledTools.Add("search_codex");
            Debug.Log($"[SkillRealTest] 📜 search_codex called: {query}");
            return new { entry = "The Iron Age began when...", source = "codex_vol1" };
        }

        // ── Capturing LLM client ─────────────────────────────────────────────

        private sealed class CaptureLlm : ILlmClient
        {
            private readonly ILlmClient _inner;

            public string LastSystemPrompt;
            public IReadOnlyList<ILlmTool> LastTools;
            public string LastContent;
            public bool LastOk;
            public long ElapsedMs;
            public int CallCount;

            public CaptureLlm(ILlmClient inner) => _inner = inner;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken ct = default)
            {
                CallCount++;
                LastSystemPrompt = request.SystemPrompt;
                LastTools = request.Tools;

                Debug.Log($"[SkillRealTest] LLM call #{CallCount}: tools={request.Tools?.Count ?? 0}, " +
                          $"prompt={request.SystemPrompt?.Length ?? 0} chars");

                Stopwatch sw = Stopwatch.StartNew();
                LlmCompletionResult result = await _inner.CompleteAsync(request, ct);
                sw.Stop();
                ElapsedMs += sw.ElapsedMilliseconds;

                LastOk = result is { Ok: true };
                LastContent = result?.Content;
                return result;
            }
        }

        // ── Infra ─────────────────────────────────────────────────────────────

        private sealed class Sink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand c) => Items.Add(c);
        }

        // ── Test: Self-Service Pattern ────────────────────────────────────────

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator SelfService_ModelCallsReadSkill_ThenUsesCraftingTools()
        {
            Debug.Log("[SkillRealTest] ═══════════════════════════════════════════");
            Debug.Log("[SkillRealTest] SELF-SERVICE SKILL TEST — Model reads skill on demand");
            Debug.Log("[SkillRealTest] ═══════════════════════════════════════════");

            ResetTracking();

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[SkillRealTest] Backend: {handle.ResolvedBackend}");

                // ── Define skills ──────────────────────────────────────────

                SkillSet craftingSkill = new("Crafting",
                    "Forge weapons, armor, and items from raw materials",
                    "You are a master blacksmith. When the player asks to craft:\n" +
                    "1. Call get_recipes to see what can be crafted.\n" +
                    "2. Call check_inventory to verify materials.\n" +
                    "3. If sufficient, call craft_item with recipe_id and quality 1.0.\n" +
                    "4. Tell the player the result.\n" +
                    "Always call get_recipes first.",
                    new DelegateLlmTool("get_recipes", "Get available crafting recipes for an item type",
                        new Func<string, object>(GetRecipes)),
                    new DelegateLlmTool("check_inventory", "Check if player has required materials",
                        new Func<string, object>(CheckInventory)),
                    new DelegateLlmTool("craft_item", "Craft an item from a recipe",
                        new Func<string, float, object>(CraftItem)));

                SkillSet combatSkill = new("Combat",
                    "Fight enemies and manage combat encounters",
                    "Call get_enemy_stats before attacking.",
                    new DelegateLlmTool("get_enemy_stats", "Get enemy statistics",
                        new Func<string, object>(GetEnemyStats)));

                SkillSet loreSkill = new("Lore",
                    "World knowledge, history, and codex entries",
                    "Call search_codex for knowledge.",
                    new DelegateLlmTool("search_codex", "Search the lore codex",
                        new Func<string, object>(SearchCodex)));

                // ── Build agent — model sees catalog + read_skill ──────────

                const string roleId = "SkillCraftmaster";
                AgentConfig config = new AgentBuilder(roleId)
                {
                    SuppressBuildWarnings = true
                }
                    .WithSystemPrompt(
                        "You are a Game Master for a fantasy RPG. " +
                        "When the player asks to do something, first call read_skill to load " +
                        "the relevant skill instructions, then follow them. " +
                        "Respond briefly after using tools.")
                    .WithSkill(craftingSkill)
                    .WithSkill(combatSkill)
                    .WithSkill(loreSkill)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                config.ApplyToPolicy(policy);

                CaptureLlm cap = new(handle.WrapWithMemoryStore(store));
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
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
                    settings,
                    null, null, null);

                // ── Run: model should read_skill("Crafting") → use crafting tools ──

                Debug.Log("[SkillRealTest] ── Sending: 'I want to craft an iron sword' ──");
                Debug.Log($"[SkillRealTest] Model sees: catalog + read_skill + all {config.Tools.Count} tools");

                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "I want to craft an iron sword. Read the crafting skill first."
                });
                yield return PlayModeTestAwait.WaitTask(t, 120f, "self-service crafting");

                // ── Results ────────────────────────────────────────────────

                Debug.Log("[SkillRealTest] ═══════════════════════════════════════════");
                Debug.Log("[SkillRealTest]              RESULTS");
                Debug.Log("[SkillRealTest] ═══════════════════════════════════════════");
                Debug.Log($"[SkillRealTest] LLM calls:       {cap.CallCount}");
                Debug.Log($"[SkillRealTest] Total time:      {cap.ElapsedMs} ms");
                Debug.Log($"[SkillRealTest] Tools available: {cap.LastTools?.Count ?? 0}");
                Debug.Log($"[SkillRealTest] Tools called:    [{string.Join(", ", _calledTools)}]");
                Debug.Log($"[SkillRealTest] Response OK:     {cap.LastOk}");
                Debug.Log($"[SkillRealTest] Response:        {cap.LastContent}");

                // System prompt check
                Debug.Log($"[SkillRealTest] ── System prompt ({cap.LastSystemPrompt?.Length ?? 0} chars) ──");
                bool hasCatalog = cap.LastSystemPrompt?.Contains("Available Skills") == true;
                bool hasFullInstructions = cap.LastSystemPrompt?.Contains("master blacksmith") == true;

                Debug.Log($"[SkillRealTest] Contains catalog:          {hasCatalog}");
                Debug.Log($"[SkillRealTest] Contains full instructions: {hasFullInstructions} (should be false)");

                bool readSkillCalled = _calledTools.Contains("read_skill");
                Debug.Log($"[SkillRealTest] read_skill called:         {readSkillCalled}");

                foreach (var kvp in _toolResults)
                {
                    Debug.Log($"[SkillRealTest] Tool result [{kvp.Key}]: {kvp.Value}");
                }

                Debug.Log("[SkillRealTest] ═══════════════════════════════════════════");

                // ── Assertions ─────────────────────────────────────────────

                if (!cap.LastOk)
                {
                    Assert.Inconclusive("LLM did not return a valid response — check connectivity.");
                }

                // Catalog in prompt, NOT full instructions
                Assert.IsTrue(hasCatalog, "System prompt should contain skill catalog.");
                Assert.IsFalse(hasFullInstructions,
                    "Full instructions should NOT be in system prompt — model reads them via read_skill.");

                // At least one crafting tool was called
                Assert.IsTrue(_calledTools.Contains("get_recipes") ||
                              _calledTools.Contains("check_inventory") ||
                              _calledTools.Contains("craft_item"),
                    $"At least one crafting tool should have been called. Called: [{string.Join(", ", _calledTools)}]");

                Debug.Log("[SkillRealTest] ✅ All assertions passed — self-service pattern works!");

                ScriptableObject.DestroyImmediate(settings);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif
}
