using UnityEngine;

namespace CoreAI.ExampleGame.Arena
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
        public bool RunEnded { get; private set; }
        public bool PlayerWon { get; private set; }

        public void RegisterPrimaryPlayer(Transform playerTransform, ArenaPlayerHealth health)
        {
            PrimaryPlayerTransform = playerTransform;
            PrimaryPlayerHealth = health;
        }

        public void SetCurrentWave(int wave) => CurrentWave = wave;

        public void NotifyEnemySpawned() => AliveEnemies++;

        public void NotifyEnemyDied() => AliveEnemies = Mathf.Max(0, AliveEnemies - 1);

        public void EndRun(bool playerWon)
        {
            RunEnded = true;
            PlayerWon = playerWon;
        }

        /// <summary>Сборка арены из кода (<see cref="ArenaSurvivalProceduralSetup"/>); в префабах задавайте роль в инспекторе.</summary>
        public void SetRuntimeSimulationRole(ArenaSimulationRole role) => simulationRole = role;
    }
}
