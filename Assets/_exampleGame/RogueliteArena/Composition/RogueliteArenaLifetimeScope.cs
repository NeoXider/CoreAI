using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;
using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.Composition
{
    /// <summary>
    /// Scope фичи «Roguelite-арена». В инспекторе укажите <b>Parent</b> на объект с <see cref="CoreAI.Composition.CoreAILifetimeScope"/>.
    /// Прогрессия VS-style поднимается из <see cref="ArenaSurvivalProceduralSetup"/> через <c>ArenaProgressionSessionHost</c> (см. <c>Docs/ARENA_PROGRESSION.md</c>).
    /// </summary>
    public sealed class RogueliteArenaLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // Опционально: сюда — регистрация SO/UseCases, если уводим проводку с SessionHost на VContainer.
        }
    }
}
