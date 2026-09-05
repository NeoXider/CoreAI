using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Demos.Shared;
using TMPro;
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo driver for SkillSet + AgentBuilder: a "Game Master" agent with two on-demand skills
    /// (Crafting and Combat). The model only ever sees the two meta-tools (<c>read_skill</c>,
    /// <c>call_skill_tool</c>) plus the skill catalog, and loads tool schemas on demand.
    /// Requires a configured LLM backend (CoreAISettings: LLMUnity model or HTTP API).
    /// </summary>
    public sealed class SkillsDemoController : MonoBehaviour
    {
        private const string RoleId = "DemoGameMaster";

        private readonly Dictionary<string, int> _inventory = new()
        {
            { "iron_ingot", 3 },
            { "wood", 5 },
            { "leather", 2 }
        };

        private readonly List<string> _crafted = new();
        private int _enemyHp = 100;

        private const string AskCaption = "Ask the Game Master";

        private AgentConfig _agent;
        private CoreAiDemoPanel _panel;
        private TMP_InputField _inputField;
        private string _input = "Craft me an iron sword, then attack the training dummy";
        private string _response = "";
        private bool _busy;

        private void Start()
        {
            _panel = CoreAiDemoPanel.Create(
                "CoreAI — Skills Demo",
                "read_skill / call_skill_tool: a Game Master agent with two on-demand skills.");

            SkillSet crafting = new(
                "Crafting",
                "Forge weapons and armor from raw materials",
                "1. Call check_inventory to see available materials.\n" +
                "2. Call craft_item with the item name; it consumes 1 iron_ingot and 1 wood.",
                new DelegateLlmTool("check_inventory", "List raw materials in stock",
                    new Func<string>(() => string.Join(", ", _inventory.Select(p => $"{p.Key} x{p.Value}")))),
                new DelegateLlmTool("craft_item", "Craft an item by name",
                    new Func<string, string>(CraftItem)));

            SkillSet combat = new(
                "Combat",
                "Fight the training dummy",
                new DelegateLlmTool("attack", "Attack the training dummy for the given damage",
                    new Func<int, string>(Attack)),
                new DelegateLlmTool("get_enemy_status", "Current HP of the training dummy",
                    new Func<string>(() => $"Training dummy HP: {_enemyHp}")));

            _agent = new AgentBuilder(RoleId)
                .WithSystemPrompt(
                    "You are a Game Master. Read the relevant skill before using its tools. " +
                    "Reply briefly with what you did.")
                .WithSkill(crafting)
                .WithSkill(combat)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            // WHY: ApplyToPolicy dereferences CoreAIAgent.Policy; if the LLM module is uninitialized (no scene
            // scope / backend) it is null, so guard and disable gracefully instead of throwing an NRE in Start.
            if (CoreAIAgent.Policy == null)
            {
                _response = "LLM module is not initialized (CoreAIAgent facade is empty); demo is inactive.";
                _panel.Log(_response);
                Debug.LogWarning("[SkillsDemo] " + _response);
                enabled = false;
                return;
            }

            _agent.ApplyToPolicy(CoreAIAgent.Policy);

            _inputField = _panel.AddInput("Ask the Game Master...");
            _inputField.text = _input;
            _panel.AddButton(AskCaption, Ask);
            RefreshStatus();
        }

        /// <summary>Recomputes the status block shown in the panel log.</summary>
        private void RefreshStatus()
        {
            string inventory = string.Join(", ", _inventory.Select(p => $"{p.Key} x{p.Value}"));
            string crafted = _crafted.Count == 0 ? "-" : string.Join(", ", _crafted);
            string busy = _busy ? "\n(waiting for the model...)" : "";
            _panel.SetLog(
                $"Inventory: {inventory}\nCrafted: {crafted}\nTraining dummy HP: {_enemyHp}{busy}\n\n{_response}");
        }

        private string CraftItem(string itemName)
        {
            string item = string.IsNullOrWhiteSpace(itemName) ? "item" : itemName.Trim();
            if (_inventory["iron_ingot"] < 1 || _inventory["wood"] < 1)
            {
                return "Crafting failed: need at least 1 iron_ingot and 1 wood.";
            }

            _inventory["iron_ingot"]--;
            _inventory["wood"]--;
            _crafted.Add(item);
            RefreshStatus();
            return
                $"Crafted '{item}'. Materials left: iron_ingot x{_inventory["iron_ingot"]}, wood x{_inventory["wood"]}.";
        }

        private string Attack(int damage)
        {
            int dmg = Mathf.Clamp(damage, 1, 50);
            _enemyHp = Mathf.Max(0, _enemyHp - dmg);
            RefreshStatus();
            return $"Hit for {dmg}. Training dummy HP: {_enemyHp}.";
        }

        private void Ask()
        {
            if (_busy || _inputField == null || string.IsNullOrWhiteSpace(_inputField.text))
            {
                return;
            }

            _input = _inputField.text;
            _busy = true;
            _response = "...";
            _panel.SetButtonInteractable(AskCaption, false);
            RefreshStatus();
            try
            {
                // Callback arrives on the Unity main thread (captured SynchronizationContext).
                _agent.AskWithCallback(_input, text =>
                {
                    _response = string.IsNullOrEmpty(text) ? "(empty response - check LLM backend)" : text;
                    _busy = false;
                    _panel.SetButtonInteractable(AskCaption, true);
                    RefreshStatus();
                });
            }
            catch (Exception ex)
            {
                _response = $"Error: {ex.Message}";
                _busy = false;
                _panel.SetButtonInteractable(AskCaption, true);
                RefreshStatus();
                Debug.LogError($"[SkillsDemo] {ex}");
            }
        }
    }
}
