namespace CoreAI.ExampleGame.Arena
{
    /// <summary>
    /// Локальный "Creator" для демо без реального LLM: генерирует план волны из текущего состояния сессии.
    /// Это пример-логика, не часть Core.
    /// </summary>
    public static class ArenaLocalWavePlanner
    {
        public static ArenaWavePlan CreatePlan(int waveIndex1Based, IArenaSessionView session, IArenaWaveSchedule fallback)
        {
            var hp = session?.PrimaryPlayerHealth;
            var hp01 = hp != null && hp.Max > 0 ? (float)hp.Current / hp.Max : 1f;

            // Чем ниже HP — тем мягче волна.
            var pressure = hp01 < 0.35f ? 0.7f : hp01 < 0.6f ? 0.9f : 1.1f;

            var baseCount = fallback != null ? fallback.GetEnemyCountForWave(waveIndex1Based) : 2 + (waveIndex1Based - 1) * 2;
            var count = (int)(baseCount * pressure);
            if (count < 1) count = 1;

            return new ArenaWavePlan
            {
                waveIndex1Based = waveIndex1Based,
                enemyCount = count,
                enemyHpMult = pressure,
                enemyDamageMult = pressure,
                enemyMoveSpeedMult = 1f,
                spawnIntervalSeconds = 0.45f,
                spawnRadius = 17.5f
            };
        }
    }
}

