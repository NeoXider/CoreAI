using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaSurvival.Domain
{
    /// <summary>Снимок состояния забега для UI и наблюдателей (без мутаций).</summary>
    public interface IArenaSessionView
    {
        bool IsAuthoritativeSimulation { get; }
        Transform PrimaryPlayerTransform { get; }
        ArenaPlayerHealth PrimaryPlayerHealth { get; }
        int CurrentWave { get; }
        int AliveEnemies { get; }
        System.Collections.Generic.IReadOnlyCollection<ArenaEnemyBrain> ActiveEnemiesList { get; }
        /// <summary>Убийства на текущей волне (сбрасывается при старте новой волны).</summary>
        int KillsThisWave { get; }
        /// <summary>Всего убийств за забег.</summary>
        int TotalKillsRun { get; }
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
        void RegisterEnemy(ArenaEnemyBrain enemy);
        void UnregisterEnemy(ArenaEnemyBrain enemy);
        void ResetKillsThisWave();
        /// <summary>Вызывайте при поражении босса (хук для шины ИИ).</summary>
        void NotifyBossDefeated();
        void EndRun(bool playerWon);
    }
}
