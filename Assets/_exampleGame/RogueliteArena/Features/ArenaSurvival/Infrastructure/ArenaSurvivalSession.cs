using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaSurvival.Infrastructure
{
    /// <summary>
    /// Состояние одного забега на арене. Не используйте синглтон: в мультиплеере экземпляр на матч / на комнату у хоста;
    /// клиент читает реплицированные поля или отдельный view-model.
    /// </summary>
    public sealed class ArenaSurvivalSession : MonoBehaviour, IArenaSessionAuthority
    {
        [SerializeField] private ArenaSimulationRole simulationRole = ArenaSimulationRole.AuthoritativeHost;

        public bool IsAuthoritativeSimulation => simulationRole == ArenaSimulationRole.AuthoritativeHost;

        public Transform PrimaryPlayerTransform { get; private set; }
        public ArenaPlayerHealth PrimaryPlayerHealth { get; private set; }
        public int CurrentWave { get; private set; }
        public int AliveEnemies { get; private set; }
        public int KillsThisWave { get; private set; }
        public int TotalKillsRun { get; private set; }
        public bool RunEnded { get; private set; }
        public bool PlayerWon { get; private set; }

        public event System.Action<int> AliveEnemiesChanged;

        /// <summary>Вызывается при смене номера волны (для вспомогательных LLM-задач в примере).</summary>
        public event System.Action<int> CurrentWaveChanged;

        /// <summary>Событие для <see cref="ArenaAiTaskBus"/> и квестов; вызовите из логики босса.</summary>
        public event System.Action BossDefeated;

        public void RegisterPrimaryPlayer(Transform playerTransform, ArenaPlayerHealth health)
        {
            PrimaryPlayerTransform = playerTransform;
            PrimaryPlayerHealth = health;
        }

        public void SetCurrentWave(int wave)
        {
            CurrentWave = wave;
            CurrentWaveChanged?.Invoke(wave);
        }

        public void NotifyEnemySpawned()
        {
            AliveEnemies++;
            AliveEnemiesChanged?.Invoke(AliveEnemies);
        }

        public void NotifyEnemyDied()
        {
            AliveEnemies = Mathf.Max(0, AliveEnemies - 1);
            AliveEnemiesChanged?.Invoke(AliveEnemies);
            KillsThisWave++;
            TotalKillsRun++;
        }

        public void ResetKillsThisWave() => KillsThisWave = 0;

        public void NotifyBossDefeated() => BossDefeated?.Invoke();

        public void EndRun(bool playerWon)
        {
            RunEnded = true;
            PlayerWon = playerWon;
        }

        /// <summary>Сборка арены из кода (<see cref="ArenaSurvivalProceduralSetup"/>); в префабах задавайте роль в инспекторе.</summary>
        public void SetRuntimeSimulationRole(ArenaSimulationRole role) => simulationRole = role;
    }
}
