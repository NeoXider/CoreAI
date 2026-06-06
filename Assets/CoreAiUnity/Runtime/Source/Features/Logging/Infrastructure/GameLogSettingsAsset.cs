using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// ScriptableObject settings for CoreAI game logging filters.
    /// </summary>
    [CreateAssetMenu(fileName = "GameLogSettings", menuName = "CoreAI/Logging/Game Log Settings")]
    public sealed class GameLogSettingsAsset : ScriptableObject, IGameLogSettings
    {
        [Tooltip("Minimum log level; Warning hides Debug and Info.")] [SerializeField]
        private GameLogFeature enabledFeatures = GameLogFeature.AllBuiltIn;

        [Tooltip("Minimum log level; Warning hides Debug and Info.")] [SerializeField]
        private GameLogLevel minimumLevel = GameLogLevel.Debug;

        private void OnValidate()
        {
            const GameLogFeature legacyAllBuiltIn =
                GameLogFeature.Core | GameLogFeature.Composition | GameLogFeature.MessagePipe |
                GameLogFeature.ExampleRoguelite;
            if (enabledFeatures == legacyAllBuiltIn)
            {
                enabledFeatures = GameLogFeature.AllBuiltIn;
            }
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
        /// Copies portable logging settings into this Unity authoring asset.
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