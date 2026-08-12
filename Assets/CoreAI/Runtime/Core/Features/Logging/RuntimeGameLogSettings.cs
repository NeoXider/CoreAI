using System.Threading;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Mutable log filter the running game evaluates against. Reads are lock-free so background
    /// threads (LLM streaming, tool execution) can log while another thread changes the filter.
    /// </summary>
    public sealed class RuntimeGameLogSettings : IGameLogSettings, IGameLogFormattingSettings
    {
        private int _enabledFeatures;
        private int _minimumLevel;
        private int _includeCoreAiPrefix;
        private int _includeFeaturePrefix;

        /// <summary>Initializes runtime settings from the unconfigured defaults.</summary>
        public RuntimeGameLogSettings()
            : this(GameLogDefaults.EnabledFeatures, GameLogDefaults.MinimumLevel)
        {
        }

        /// <summary>Initializes runtime settings from an explicit mask and level.</summary>
        public RuntimeGameLogSettings(GameLogFeature enabledFeatures, GameLogLevel minimumLevel)
            : this(enabledFeatures, minimumLevel, true, true)
        {
        }

        /// <summary>Initializes runtime settings from explicit filtering and formatting values.</summary>
        public RuntimeGameLogSettings(
            GameLogFeature enabledFeatures,
            GameLogLevel minimumLevel,
            bool includeCoreAiPrefix,
            bool includeFeaturePrefix)
        {
            _enabledFeatures = (int)enabledFeatures;
            _minimumLevel = (int)minimumLevel;
            _includeCoreAiPrefix = includeCoreAiPrefix ? 1 : 0;
            _includeFeaturePrefix = includeFeaturePrefix ? 1 : 0;
        }

        /// <summary>Categories currently allowed through the filter.</summary>
        public GameLogFeature EnabledFeatures
        {
            get => (GameLogFeature)Volatile.Read(ref _enabledFeatures);
            set => Volatile.Write(ref _enabledFeatures, (int)value);
        }

        /// <summary>Lowest level currently allowed through the filter.</summary>
        public GameLogLevel MinimumLevel
        {
            get => (GameLogLevel)Volatile.Read(ref _minimumLevel);
            set => Volatile.Write(ref _minimumLevel, (int)value);
        }

        /// <inheritdoc />
        public bool IncludeCoreAiPrefix
        {
            get => Volatile.Read(ref _includeCoreAiPrefix) != 0;
            set => Volatile.Write(ref _includeCoreAiPrefix, value ? 1 : 0);
        }

        /// <inheritdoc />
        public bool IncludeFeaturePrefix
        {
            get => Volatile.Read(ref _includeFeaturePrefix) != 0;
            set => Volatile.Write(ref _includeFeaturePrefix, value ? 1 : 0);
        }

        /// <summary>Turns a single category on or off without disturbing the others.</summary>
        public void SetFeatureEnabled(GameLogFeature feature, bool enabled)
        {
            int mask = (int)feature;
            if (mask == 0)
            {
                return;
            }

            int current;
            int updated;
            do
            {
                current = Volatile.Read(ref _enabledFeatures);
                updated = enabled ? current | mask : current & ~mask;
                if (updated == current)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _enabledFeatures, updated, current) != current);
        }

        /// <summary>Reports whether every bit of the supplied category is currently enabled.</summary>
        public bool IsFeatureEnabled(GameLogFeature feature)
        {
            int mask = (int)feature;
            return mask != 0 && (Volatile.Read(ref _enabledFeatures) & mask) == mask;
        }

        /// <inheritdoc />
        public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
        {
            if (feature == GameLogFeature.None)
            {
                return false;
            }

            // WHY: Both fields are read once so a concurrent filter change cannot mix old mask with new level.
            int features = Volatile.Read(ref _enabledFeatures);
            int minimum = Volatile.Read(ref _minimumLevel);
            return (features & (int)feature) != 0 && (int)level >= minimum;
        }

        /// <summary>Copies the current values into a portable snapshot.</summary>
        public GameLogSettingsOptions ToOptions()
        {
            return new GameLogSettingsOptions
            {
                EnabledFeatures = EnabledFeatures,
                MinimumLevel = MinimumLevel,
                IncludeCoreAiPrefix = IncludeCoreAiPrefix,
                IncludeFeaturePrefix = IncludeFeaturePrefix
            };
        }

        /// <summary>Overwrites both values from a portable snapshot; null restores the defaults.</summary>
        public void Apply(GameLogSettingsOptions options)
        {
            EnabledFeatures = options?.EnabledFeatures ?? GameLogDefaults.EnabledFeatures;
            MinimumLevel = options?.MinimumLevel ?? GameLogDefaults.MinimumLevel;
            IncludeCoreAiPrefix = options?.IncludeCoreAiPrefix ?? true;
            IncludeFeaturePrefix = options?.IncludeFeaturePrefix ?? true;
        }
    }
}
