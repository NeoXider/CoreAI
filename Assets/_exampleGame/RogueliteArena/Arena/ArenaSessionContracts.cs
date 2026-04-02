using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>Снимок состояния забега для UI и наблюдателей (без мутаций).</summary>
    public interface IArenaSessionView
    {
        bool IsAuthoritativeSimulation { get; }
        Transform PrimaryPlayerTransform { get; }
        ArenaPlayerHealth PrimaryPlayerHealth { get; }
        int CurrentWave { get; }
        int AliveEnemies { get; }
        bool RunEnded { get; }
        bool PlayerWon { get; }
    }

    /// <summary>Мутации состояния — только на узле с <see cref="IArenaSessionView.IsAuthoritativeSimulation"/>.</summary>
    public interface IArenaSessionAuthority : IArenaSessionView
    {
        void RegisterPrimaryPlayer(Transform playerTransform, ArenaPlayerHealth health);
        void SetCurrentWave(int wave);
        void NotifyEnemySpawned();
        void NotifyEnemyDied();
        void EndRun(bool playerWon);
    }
}
