namespace CoreAI.ExampleGame.ArenaAi.Domain
{
    /// <summary>Константы <see cref="CoreAI.Ai.AiTaskRequest.SourceTag"/> для арены-примера.</summary>
    public static class ArenaAiSourceTags
    {
        public const string DirectorWaveStart = "arena_director:wave_start";
        public const string DirectorPreNextWave = "arena_director:pre_next_wave";
        public const string HotkeyF1 = "hotkey:F1";
        public const string HotkeyF2 = "hotkey:F2";
        public const string WaveChangedEvent = "arena_event:wave_changed";
        public const string PlayerHpCritical = "arena_event:player_hp_critical";
        public const string BossDefeated = "arena_event:boss_defeated";
        public const string RoomEnteredPrefix = "arena_event:room_enter:";
        public const string AuxEveryNWaves = "arena_aux:every_n_waves";
    }
}
