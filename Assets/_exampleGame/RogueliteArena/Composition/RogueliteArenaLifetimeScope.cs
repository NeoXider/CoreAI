using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.Composition
{
    /// <summary>
    /// Scope фичи «Roguelite-арена». В инспекторе укажите <b>Parent</b> на объект с <see cref="CoreAI.Composition.CoreAILifetimeScope"/>.
    /// </summary>
    public sealed class RogueliteArenaLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // Регистрация презентеров, use case'ов и сервисов арены/хаба.
            // Опционально: IContainerBuilder.RegisterComponent на ArenaSurvivalSession в сцене
            // или фабрика IArenaSessionAuthority для тестов / NGO-хоста.
        }
    }
}
