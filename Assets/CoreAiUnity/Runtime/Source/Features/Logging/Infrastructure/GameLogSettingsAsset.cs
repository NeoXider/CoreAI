#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// ScriptableObject settings for CoreAI game logging filters. This is the authoring source:
    /// the running game filters against a runtime copy (<see cref="GameLogFilter"/>), so play-mode
    /// changes never write back into this asset.
    /// </summary>
    [CreateAssetMenu(fileName = "GameLogSettings", menuName = "CoreAI/Logging/Game Log Settings")]
    public sealed class GameLogSettingsAsset : ScriptableObject, IGameLogSettings
    {
        private const int CurrentSettingsVersion = 1;

        private const GameLogFeature LegacyAllBeforeLlm =
            GameLogFeature.Core | GameLogFeature.Composition | GameLogFeature.MessagePipe |
            GameLogFeature.ExampleRoguelite;

        private const GameLogFeature LegacyAllBeforeMetrics = LegacyAllBeforeLlm | GameLogFeature.Llm;

        [Tooltip("Log categories that pass the filter; unchecked categories are dropped entirely.")]
        [SerializeField]
        private GameLogFeature enabledFeatures = GameLogFeature.All;

        [Tooltip("Minimum log level; Warning hides Debug and Info.")]
        [SerializeField]
        private GameLogLevel minimumLevel = GameLogLevel.Debug;

        [HideInInspector]
        [SerializeField]
        private int settingsVersion = CurrentSettingsVersion;

        private void OnValidate()
        {
            if (!TryMigrateFeatures(settingsVersion, enabledFeatures, out GameLogFeature migrated))
            {
                settingsVersion = CurrentSettingsVersion;
                return;
            }

            enabledFeatures = migrated;
            settingsVersion = CurrentSettingsVersion;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// Widens the "everything" preset of assets serialized before <see cref="CurrentSettingsVersion"/>,
        /// whose preset predates the Llm and Metrics categories. Keyed on the version field, never on the
        /// mask itself, so a deliberate selection that happens to equal a legacy preset survives.
        /// Internal for EditMode tests.
        /// </summary>
        internal static bool TryMigrateFeatures(int version, GameLogFeature features, out GameLogFeature migrated)
        {
            migrated = features;
            if (version >= CurrentSettingsVersion)
            {
                return false;
            }

            if (features != LegacyAllBeforeLlm && features != LegacyAllBeforeMetrics)
            {
                return false;
            }

            migrated = GameLogFeature.AllBuiltIn;
            return true;
        }

        /// <inheritdoc />
        public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
        {
            if (feature == GameLogFeature.None)
            {
                return false;
            }

            if ((enabledFeatures & feature) == 0)
            {
                return false;
            }

            return level >= minimumLevel;
        }

        /// <summary>
        /// Builds a Unity-free logging settings snapshot.
        /// </summary>
        public GameLogSettingsOptions ToOptions()
        {
            return new GameLogSettingsOptions
            {
                EnabledFeatures = enabledFeatures,
                MinimumLevel = minimumLevel
            };
        }

        /// <summary>
        /// Copies portable logging settings into this Unity authoring asset. Authoring-time only —
        /// runtime code changes logging through <see cref="GameLogFilter"/>, which never touches the asset.
        /// </summary>
        public void ApplyOptions(GameLogSettingsOptions options)
        {
            if (options == null)
            {
                return;
            }

            enabledFeatures = options.EnabledFeatures;
            minimumLevel = options.MinimumLevel;
        }
    }
}
