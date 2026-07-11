using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaSurvival.Domain
{
    /// <summary>Read-only run-state snapshot for UI and observers.</summary>
    public interface IArenaSessionView
    {
        bool IsAuthoritativeSimulation { get; }
        Transform PrimaryPlayerTransform { get; }
        ArenaPlayerHealth PrimaryPlayerHealth { get; }
        int CurrentWave { get; }
        int AliveEnemies { get; }
        System.Collections.Generic.IReadOnlyCollection<ArenaEnemyBrain> ActiveEnemiesList { get; }

        /// <summary>Kills during the current wave; resets when a new wave starts.</summary>
        int KillsThisWave { get; }

        /// <summary>Total kills during the run.</summary>
        int TotalKillsRun { get; }

        bool RunEnded { get; }
        bool PlayerWon { get; }
    }

    /// <summary>Run-state mutations allowed only on the authoritative simulation peer.</summary>
    public interface IArenaSessionAuthority : IArenaSessionView
    {
        void RegisterPrimaryPlayer(Transform playerTransform, ArenaPlayerHealth health);
        void SetCurrentWave(int wave);
        void NotifyEnemySpawned();
        void NotifyEnemyDied();
        void RegisterEnemy(ArenaEnemyBrain enemy);
        void UnregisterEnemy(ArenaEnemyBrain enemy);
        void ResetKillsThisWave();

        /// <summary>Call when a boss is defeated so the AI bus can react.</summary>
        void NotifyBossDefeated();

        void EndRun(bool playerWon);
    }
}
