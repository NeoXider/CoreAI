using CoreAI.Infrastructure.Logging;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Корневой scope сессии. Повесьте на объект в сцене (корень загрузки игры).
    /// Дочерние feature-scope'ы можно добавлять как <see cref="LifetimeScope"/> с parent.
    /// </summary>
    public sealed class GameLifetimeScope : LifetimeScope
    {
        [Tooltip("Если null — логируются все фичи (DefaultGameLogSettings). Иначе — фильтр по флагам и минимальному уровню.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        protected override void Configure(IContainerBuilder builder)
        {
            if (gameLogSettings != null)
                builder.RegisterInstance<IGameLogSettings>(gameLogSettings);
            else
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();

            builder.RegisterCore();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
        }
    }
}
