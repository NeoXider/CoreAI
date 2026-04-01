using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.Composition
{
    /// <summary>
    /// Scope фичи «Roguelite-арена». В инспекторе укажите <b>Parent</b> на объект с <see cref="CoreAI.Composition.GameLifetimeScope"/>.
    /// </summary>
    public sealed class RogueliteArenaLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // Регистрация презентеров, use case'ов и сервисов арены/хаба
        }
    }
}
