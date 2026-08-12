namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Entry point for changing CoreAI log filtering while the game runs.
    /// <para>
    /// Every CoreAI logger — the one built by the dependency injection container and the unscoped
    /// fallback — filters against <see cref="Settings"/>, so changes here take effect immediately
    /// everywhere. The values are a runtime copy of the authored asset: editing them never writes
    /// to the ScriptableObject, so play-mode tweaks do not dirty project files.
    /// </para>
    /// <example>
    /// <code>
    /// GameLogFilter.MinimumLevel = GameLogLevel.Debug;
    /// GameLogFilter.SetFeatureEnabled(GameLogFeature.Llm, true);
    /// GameLogFilter.ResetToAuthored();
    /// </code>
    /// </example>
    /// </summary>
    public static class GameLogFilter
    {
        private static readonly RuntimeGameLogSettings RuntimeSettings = new();

        private static volatile GameLogSettingsOptions _authored = new()
        {
            EnabledFeatures = GameLogDefaults.EnabledFeatures,
            MinimumLevel = GameLogDefaults.MinimumLevel,
            IncludeCoreAiPrefix = true,
            IncludeFeaturePrefix = true
        };

        /// <summary>Live settings instance every CoreAI logger filters against.</summary>
        public static IGameLogSettings Settings => RuntimeSettings;

        /// <summary>Categories currently allowed through the filter.</summary>
        public static GameLogFeature EnabledFeatures
        {
            get => RuntimeSettings.EnabledFeatures;
            set => RuntimeSettings.EnabledFeatures = value;
        }

        /// <summary>Lowest level currently allowed through the filter.</summary>
        public static GameLogLevel MinimumLevel
        {
            get => RuntimeSettings.MinimumLevel;
            set => RuntimeSettings.MinimumLevel = value;
        }

        /// <summary>Whether Unity output starts with the library-level [CoreAI] prefix.</summary>
        public static bool IncludeCoreAiPrefix
        {
            get => RuntimeSettings.IncludeCoreAiPrefix;
            set => RuntimeSettings.IncludeCoreAiPrefix = value;
        }

        /// <summary>Whether messages passed to a sink start with their feature prefix.</summary>
        public static bool IncludeFeaturePrefix
        {
            get => RuntimeSettings.IncludeFeaturePrefix;
            set => RuntimeSettings.IncludeFeaturePrefix = value;
        }

        /// <summary>Turns a single category on or off without disturbing the others.</summary>
        public static void SetFeatureEnabled(GameLogFeature feature, bool enabled)
        {
            RuntimeSettings.SetFeatureEnabled(feature, enabled);
        }

        /// <summary>Reports whether the supplied category currently passes the filter.</summary>
        public static bool IsFeatureEnabled(GameLogFeature feature)
        {
            return RuntimeSettings.IsFeatureEnabled(feature);
        }

        /// <summary>Copies the current mask and level into a portable snapshot.</summary>
        public static GameLogSettingsOptions Snapshot()
        {
            return RuntimeSettings.ToOptions();
        }

        /// <summary>
        /// Adopts the authored settings as the new baseline and applies them. Called while the
        /// dependency injection container is built; pass null to fall back to
        /// <see cref="GameLogDefaults"/>.
        /// </summary>
        public static void UseAuthoredSettings(GameLogSettingsOptions authored)
        {
            _authored = new GameLogSettingsOptions
            {
                EnabledFeatures = authored?.EnabledFeatures ?? GameLogDefaults.EnabledFeatures,
                MinimumLevel = authored?.MinimumLevel ?? GameLogDefaults.MinimumLevel,
                IncludeCoreAiPrefix = authored?.IncludeCoreAiPrefix ?? true,
                IncludeFeaturePrefix = authored?.IncludeFeaturePrefix ?? true
            };

            RuntimeSettings.Apply(_authored);
        }

        /// <summary>Discards runtime edits and restores the authored values.</summary>
        public static void ResetToAuthored()
        {
            RuntimeSettings.Apply(_authored);
        }
    }
}
