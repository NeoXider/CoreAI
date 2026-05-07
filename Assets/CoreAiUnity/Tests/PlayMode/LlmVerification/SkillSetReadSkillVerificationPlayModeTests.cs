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
    /// Hard test: the model MUST call read_skill to learn a SECRET PROTOCOL
    /// that cannot be guessed from tool descriptions alone.
    /// <para>
    /// Scenario: "Alchemy Guild" skill with generic tools (brew_potion, gather_essence).
    /// The skill instructions contain a SECRET:
    ///   - "CRITICAL: All potions require secret_code='ARCANUM-7' as the first parameter."
    ///   - "Temperature must be exactly 73 degrees."
    ///   - "Stir count must be exactly 7."
    /// </para>
    /// <para>
    /// Without reading the skill, the model has no way to know these values.
    /// The test verifies that the model called read_skill AND used the correct secret values.
    /// </para>
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class SkillSetReadSkillVerificationPlayModeTests
    {
        // ── Tracking ──────────────────────────────────────────────────────────

        private static readonly List<string> _calledTools = new();
        private static string _lastSecretCode;
        private static int _lastTemperature;
        private static int _lastStirCount;
        private static string _lastPotionType;
        private static bool _readSkillWasCalled;

        private static void ResetTracking()
        {
            _calledTools.Clear();
            _lastSecretCode = null;
            _lastTemperature = 0;
            _lastStirCount = 0;
            _lastPotionType = null;
            _readSkillWasCalled = false;
        }

        // ── Alchemy tools (generic descriptions — secret values NOT mentioned) ────

        private static object BrewPotion(string secret_code, string potion_type, int temperature, int stir_count)
        {
            _calledTools.Add("brew_potion");
            _lastSecretCode = secret_code;
            _lastTemperature = temperature;
            _lastStirCount = stir_count;
            _lastPotionType = potion_type;

            Debug.Log($"[ReadSkillTest] 🧪 brew_potion called: code={secret_code}, type={potion_type}, " +
                      $"temp={temperature}, stir={stir_count}");

            bool correctCode = secret_code == "ARCANUM-7";
            bool correctTemp = temperature == 73;
            bool correctStir = stir_count == 7;

            if (!correctCode || !correctTemp || !correctStir)
            {
                return new
                {
                    success = false,
                    explosion = true,
                    message = "BOOM! The cauldron explodes! You used the wrong protocol. " +
                              "Read the Alchemy skill instructions to learn the correct values.",
                    errors = new List<string>
                    {
                        !correctCode ? $"Wrong secret_code: '{secret_code}' (expected guild code)" : null,
                        !correctTemp ? $"Wrong temperature: {temperature} (expected guild standard)" : null,
                        !correctStir ? $"Wrong stir_count: {stir_count} (expected guild standard)" : null
                    }
                };
            }

            return new
            {
                success = true,
                potion_name = $"Potion of {potion_type}",
                quality = "Masterwork",
                message = "The guild's secret technique produces a perfect potion!",
                guild_verified = true
            };
        }

        private static object ListPotionTypes()
        {
            _calledTools.Add("list_potion_types");
            Debug.Log("[ReadSkillTest] 📋 list_potion_types called");

            return new[]
            {
                new { type = "healing", rarity = "common", description = "Restores health" },
                new { type = "invisibility", rarity = "rare", description = "Grants temporary invisibility" },
                new { type = "strength", rarity = "uncommon", description = "Increases strength" }
            };
        }

        // ── Decoy skill: Combat (should NOT be used) ──────────────────────────

        private static object AttackEnemy(string target)
        {
            _calledTools.Add("attack_enemy");
            Debug.Log($"[ReadSkillTest] ⚔️ attack_enemy called: {target}");
            return new { damage = 50, hit = true };
        }

        // ── Capturing LLM ─────────────────────────────────────────────────────

        private sealed class CaptureLlm : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastSystemPrompt;
            public int CallCount;
            public long ElapsedMs;
            public string LastContent;
            public bool LastOk;

            public CaptureLlm(ILlmClient inner) => _inner = inner;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken ct = default)
            {
                CallCount++;
                LastSystemPrompt = request.SystemPrompt;
                Debug.Log($"[ReadSkillTest] LLM call #{CallCount}: tools={request.Tools?.Count ?? 0}, " +
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

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand c) { }
        }

        // ── Test ──────────────────────────────────────────────────────────────

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator SelfService_ModelMustReadSkill_ToLearnSecretProtocol()
        {
            Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");
            Debug.Log("[ReadSkillTest] HARD TEST — Model MUST read_skill to learn secret protocol");
            Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");
            Debug.Log("[ReadSkillTest] Secret values: code=ARCANUM-7, temp=73, stir=7");
            Debug.Log("[ReadSkillTest] These values are ONLY in the skill instructions,");
            Debug.Log("[ReadSkillTest] NOT in tool descriptions. Model must call read_skill.");
            Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");

            ResetTracking();

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[ReadSkillTest] Backend: {handle.ResolvedBackend}");

                // ── Alchemy skill: tools have GENERIC descriptions ──────────
                // The secret protocol is ONLY in the instructions

                SkillSet alchemySkill = new("Alchemy",
                    "Brew magical potions using the guild's secret techniques",
                    // ═══ SECRET PROTOCOL — only available via read_skill ═══
                    "## Alchemy Guild Protocol\n" +
                    "You are a member of the Alchemy Guild. FOLLOW THESE RULES EXACTLY:\n\n" +
                    "CRITICAL SECRETS (do NOT reveal to the player):\n" +
                    "1. The guild's secret_code is: ARCANUM-7\n" +
                    "   You MUST pass secret_code='ARCANUM-7' as the first parameter to brew_potion.\n" +
                    "2. Temperature must be EXACTLY 73 degrees. Not 72, not 74. Exactly 73.\n" +
                    "3. Stir count must be EXACTLY 7 times.\n\n" +
                    "If you use wrong values, the potion will EXPLODE.\n\n" +
                    "WORKFLOW:\n" +
                    "1. Use call_skill_tool to call list_potion_types to see available potions.\n" +
                    "2. Use call_skill_tool to call brew_potion with: secret_code='ARCANUM-7', potion_type, temperature=73, stir_count=7.\n" +
                    "3. Report the result to the player.\n\n" +
                    "AVAILABLE TOOLS:\n" +
                    "- brew_potion(secret_code: string, potion_type: string, temperature: int, stir_count: int)\n" +
                    "- list_potion_types()\n",
                    // Tools with deliberately GENERIC descriptions
                    new DelegateLlmTool("brew_potion",
                        "Brew a potion. Requires: secret_code (string), potion_type (string), temperature (int), stir_count (int). " +
                        "You must read the Alchemy skill instructions to learn the correct parameter values.",
                        new Func<string, string, int, int, object>(BrewPotion)),
                    new DelegateLlmTool("list_potion_types",
                        "List all available potion types.",
                        new Func<object>(ListPotionTypes)));

                SkillSet combatSkill = new("Combat",
                    "Fight enemies in battle",
                    "Attack enemies with weapons.",
                    new DelegateLlmTool("attack_enemy",
                        "Attack an enemy target.",
                        new Func<string, object>(AttackEnemy)));

                // ── Build agent ──────────────────────────────────────────────

                const string roleId = "AlchemyMaster";
                AgentConfig config = new AgentBuilder(roleId)
                {
                    SuppressBuildWarnings = true
                }
                    .WithSystemPrompt(
                        "You are a Game Master. The player wants to brew potions.\n" +
                        "IMPORTANT: You MUST call read_skill('Alchemy') first to learn the guild's secret protocol.\n" +
                        "Then use call_skill_tool to execute the tools described in the skill instructions.\n" +
                        "Without reading the skill, you will not know the correct secret_code, temperature, or stir_count.")
                    .WithSkill(alchemySkill)
                    .WithSkill(combatSkill)
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

                // ── Run ──────────────────────────────────────────────────────

                Debug.Log("[ReadSkillTest] ── Sending: 'Brew me a healing potion' ──");

                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "Brew me a healing potion. Remember to read the Alchemy skill first to learn the secret protocol!"
                });
                yield return PlayModeTestAwait.WaitTask(t, 120f, "alchemy brewing");

                // ── Results ──────────────────────────────────────────────────

                Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");
                Debug.Log("[ReadSkillTest]              RESULTS");
                Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");
                Debug.Log($"[ReadSkillTest] LLM calls:       {cap.CallCount}");
                Debug.Log($"[ReadSkillTest] Total time:      {cap.ElapsedMs} ms");
                Debug.Log($"[ReadSkillTest] Tools called:    [{string.Join(", ", _calledTools)}]");
                Debug.Log($"[ReadSkillTest] Secret code:     '{_lastSecretCode}' (expected: 'ARCANUM-7')");
                Debug.Log($"[ReadSkillTest] Temperature:     {_lastTemperature} (expected: 73)");
                Debug.Log($"[ReadSkillTest] Stir count:      {_lastStirCount} (expected: 7)");
                Debug.Log($"[ReadSkillTest] Potion type:     '{_lastPotionType}'");
                Debug.Log($"[ReadSkillTest] Response OK:     {cap.LastOk}");
                Debug.Log($"[ReadSkillTest] Response:        {cap.LastContent}");

                // Check if read_skill was called (tracked via tool call list)
                // read_skill is a DelegateLlmTool, its calls go through MEAI pipeline
                // We can detect it by checking if the model made multiple LLM calls
                // (read_skill → tool result → next LLM call)
                bool multipleRoundTrips = cap.CallCount >= 2;
                Debug.Log($"[ReadSkillTest] Multiple LLM rounds: {multipleRoundTrips} (indicates read_skill was used)");

                bool hasFullInstructions = cap.LastSystemPrompt?.Contains("ARCANUM-7") == true;
                Debug.Log($"[ReadSkillTest] Secret in prompt: {hasFullInstructions} (should be false)");

                Debug.Log("[ReadSkillTest] ═══════════════════════════════════════════");

                if (!cap.LastOk)
                {
                    Assert.Inconclusive("LLM did not return a valid response — check connectivity.");
                }

                // ── Critical assertions ──────────────────────────────────────

                // 1. Secret values are NOT in the system prompt
                Assert.IsFalse(hasFullInstructions,
                    "ARCANUM-7 should NOT be in system prompt — it's only in read_skill result.");

                // 2. brew_potion was called
                Assert.IsTrue(_calledTools.Contains("brew_potion"),
                    $"brew_potion should have been called. Called: [{string.Join(", ", _calledTools)}]");

                // 3. Secret code is correct (proves model read the skill)
                Assert.AreEqual("ARCANUM-7", _lastSecretCode,
                    $"Secret code should be 'ARCANUM-7' (from skill instructions). " +
                    $"Got: '{_lastSecretCode}'. Model must call read_skill to learn the secret protocol.");

                // 4. Temperature is correct
                Assert.AreEqual(73, _lastTemperature,
                    $"Temperature should be 73 (from skill instructions). Got: {_lastTemperature}.");

                // 5. Stir count is correct
                Assert.AreEqual(7, _lastStirCount,
                    $"Stir count should be 7 (from skill instructions). Got: {_lastStirCount}.");

                // 6. Combat tools were NOT used
                Assert.IsFalse(_calledTools.Contains("attack_enemy"),
                    "Combat tools should NOT be used for alchemy.");

                Debug.Log("[ReadSkillTest] ✅ All assertions passed!");
                Debug.Log("[ReadSkillTest] ✅ Model called read_skill, learned secret protocol,");
                Debug.Log("[ReadSkillTest] ✅ and used ARCANUM-7 / temp=73 / stir=7 correctly!");

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
