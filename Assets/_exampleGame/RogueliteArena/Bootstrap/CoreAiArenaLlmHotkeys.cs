using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.ExampleGame.Arena;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer;

namespace CoreAI.ExampleGame.Bootstrap
{
    /// <summary>
    /// Ручные демо-вызовы LLM на арене: F1 — Creator (план текущей волны или свободная задача),
    /// F2 — AINpc выбирает стойку компаньона (JSON → <see cref="ArenaCompanionBot"/>).
    /// F9 по-прежнему у <see cref="CoreAiLuaHotkey"/> (Programmer).
    /// </summary>
    public sealed class CoreAiArenaLlmHotkeys : MonoBehaviour
    {
        /// <summary>Префикс в <see cref="AiTaskRequest.Hint"/> — слушатель компаньона реагирует только на такие ответы AINpc.</summary>
        public const string CompanionHotkeyHintPrefix = "companion_hotkey_F2";

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;

            var scope = GetComponentInParent<CoreAILifetimeScope>();
            if (scope == null)
                return;

            if (kb.f1Key.wasPressedThisFrame)
                TryFireCreator(scope);
            if (kb.f2Key.wasPressedThisFrame)
                TryFireCompanionNpc(scope);
        }

        private static void TryFireCreator(CoreAILifetimeScope scope)
        {
            var orch = scope.Container.Resolve<IAiOrchestrationService>();
            var session = Object.FindFirstObjectByType<ArenaSurvivalSession>();
            var wave = session != null ? Mathf.Max(1, session.CurrentWave) : 1;
            var planner = Object.FindFirstObjectByType<ArenaCreatorWavePlanner>();

            if (planner != null && !planner.ForceLinearWavePlans)
            {
                planner.RequestWavePlan(wave);
                Debug.Log($"[CoreAI.ExampleGame] F1 → Creator: запрос плана волны {wave} (ArenaCreatorWavePlanner).");
            }
            else
            {
                _ = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint =
                        $"manual_hotkey_F1 arena_creator_adhoc wave={wave}. " +
                        "Output compact JSON only, no markdown. " +
                        "Use exactly: {\"commandType\":\"ArenaWavePlan\",\"payload\":{" +
                        $"\"waveIndex1Based\":{wave},\"enemyCount\":4,\"enemyHpMult\":1,\"enemyDamageMult\":1," +
                        "\"enemyMoveSpeedMult\":1,\"spawnIntervalSeconds\":0.45,\"spawnRadius\":17.5}}. " +
                        "You may change enemyCount and multipliers; waveIndex1Based must match the wave above.",
                    CancellationScope = "arena_hotkey_f1",
                    Priority = 100
                });
                Debug.Log($"[CoreAI.ExampleGame] F1 → Creator: ad-hoc задача (планировщик недоступен или только линейные волны), wave={wave}.");
            }
        }

        private static void TryFireCompanionNpc(CoreAILifetimeScope scope)
        {
            var orch = scope.Container.Resolve<IAiOrchestrationService>();
            var session = Object.FindFirstObjectByType<ArenaSurvivalSession>();
            var alive = session != null ? session.AliveEnemies : -1;
            var wave = session != null ? session.CurrentWave : 0;

            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = CompanionHotkeyHintPrefix +
                       " Pick companion combat demeanor. Output JSON only, no markdown fences. " +
                       "Schema: {\"stance\":\"aggressive\"|\"defensive\"|\"balanced\",\"battle_cry\":\"one short phrase\"}. " +
                       "aggressive: chase enemies from farther away, fight more eagerly. " +
                       "defensive: stay closer to the player, engage only nearby threats. " +
                       "balanced: middle ground. " +
                       $"Context: wave={wave}, alive_enemies={alive}.",
                CancellationScope = "arena_hotkey_f2_companion",
                Priority = 50
            });
            Debug.Log("[CoreAI.ExampleGame] F2 → AINpc: ждём JSON со stance; компаньон сменит скорость/радиусы (см. лог после ответа модели).");
        }
    }
}
