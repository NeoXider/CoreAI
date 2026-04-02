using System;
using CoreAI.Ai;
using CoreAI.ExampleGame.Bootstrap;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>
    /// Реакция на ответ AINpc после F2: разбор JSON/эвристик и вызов <see cref="ArenaCompanionBot.ApplyCombatStance"/>.
    /// </summary>
    public sealed class ArenaCompanionAiListener : MonoBehaviour
    {
        [Serializable]
        private sealed class CompanionStanceDto
        {
            public string stance;
            public string battle_cry;
            public string battleCry;
        }

        private void OnEnable()
        {
            AiGameCommandRouter.CommandReceived += OnCommand;
        }

        private void OnDisable()
        {
            AiGameCommandRouter.CommandReceived -= OnCommand;
        }

        private void OnCommand(ApplyAiGameCommand cmd)
        {
            if (cmd == null || cmd.CommandTypeId != AiGameCommandTypeIds.Envelope)
                return;
            if (!string.Equals(cmd.SourceRoleId, BuiltInAgentRoleIds.AiNpc, StringComparison.Ordinal))
                return;

            var hint = cmd.SourceTaskHint ?? "";
            if (!hint.Contains(CoreAiArenaLlmHotkeys.CompanionHotkeyHintPrefix, StringComparison.Ordinal))
                return;

            var raw = cmd.JsonPayload ?? "";
            if (!TryResolveStance(raw, out var stance, out var flavor))
            {
                Debug.LogWarning(
                    "[CoreAI.ExampleGame] AINpc (F2): не удалось разобрать stance из ответа (ожидается JSON с полем stance или слова aggressive/defensive/balanced). Payload: " +
                    (raw.Length > 200 ? raw.Substring(0, 200) + "…" : raw));
                return;
            }

            var bot = FindCompanion();
            if (bot == null)
            {
                Debug.LogWarning("[CoreAI.ExampleGame] AINpc (F2): компаньон не найден в сцене.");
                return;
            }

            bot.ApplyCombatStance(stance);
            if (!string.IsNullOrWhiteSpace(flavor))
                Debug.Log($"[CoreAI.ExampleGame] Компаньон (AINpc): «{flavor.Trim()}»");
        }

        private static ArenaCompanionBot FindCompanion()
        {
            var bots = FindObjectsByType<ArenaCompanionBot>(FindObjectsInactive.Exclude);
            return bots != null && bots.Length > 0 ? bots[0] : null;
        }

        private static bool TryResolveStance(string raw, out CompanionCombatStance stance, out string flavor)
        {
            stance = CompanionCombatStance.Balanced;
            flavor = null;

            if (!LlmResponseSanitizer.TryPrepareJsonObject(raw ?? "", out var json))
                json = null;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var dto = JsonUtility.FromJson<CompanionStanceDto>(json);
                    if (dto != null)
                    {
                        flavor = !string.IsNullOrWhiteSpace(dto.battle_cry)
                            ? dto.battle_cry
                            : dto.battleCry;
                        if (TryMapStance(dto.stance, out stance))
                            return true;
                    }
                }
                catch
                {
                    // fallback heuristics below
                }
            }

            if (TryMapStance(raw, out stance))
                return true;

            return false;
        }

        private static bool TryMapStance(string token, out CompanionCombatStance stance)
        {
            stance = CompanionCombatStance.Balanced;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var t = token.Trim().ToLowerInvariant();
            if (t.Contains("aggress") || t.Contains("агресс"))
            {
                stance = CompanionCombatStance.Aggressive;
                return true;
            }

            if (t.Contains("defens") || t.Contains("осторож") || t.Contains("защит"))
            {
                stance = CompanionCombatStance.Defensive;
                return true;
            }

            if (t.Contains("balanced") || t.Contains("balance") || t.Contains("нейтрал") || t.Contains("сбаланс"))
            {
                stance = CompanionCombatStance.Balanced;
                return true;
            }

            return false;
        }
    }
}
