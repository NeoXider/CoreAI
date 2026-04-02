using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Настройки логирования по фичам (аналог идеи Feature Log Settings из GameDev-Last-War).
    /// </summary>
    [CreateAssetMenu(fileName = "GameLogSettings", menuName = "CoreAI/Logging/Game Log Settings")]
    public sealed class GameLogSettingsAsset : ScriptableObject, IGameLogSettings
    {
        [Tooltip("Для каких категорий разрешён вывод")]
        [SerializeField]
        private GameLogFeature enabledFeatures = GameLogFeature.AllBuiltIn;

        [Tooltip("Минимальный уровень: например Warning отсечёт Debug и Info")]
        [SerializeField]
        private GameLogLevel minimumLevel = GameLogLevel.Debug;

        private void OnValidate()
        {
            // До добавления Llm в enum «все встроенные» давали маску без бита Llm — дополняем при открытии asset.
            const GameLogFeature legacyAllBuiltIn =
                GameLogFeature.Core | GameLogFeature.Composition | GameLogFeature.MessagePipe |
                GameLogFeature.ExampleRoguelite;
            if (enabledFeatures == legacyAllBuiltIn)
                enabledFeatures = GameLogFeature.AllBuiltIn;
        }

        /// <inheritdoc />
        public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
        {
            if (feature == GameLogFeature.None)
                return false;

            if ((enabledFeatures & feature) == 0)
                return false;

            return level >= minimumLevel;
        }
    }
}
