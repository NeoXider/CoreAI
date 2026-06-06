using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;
using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.Composition
{
    /// <summary>
    /// Lifetime scope for the Roguelite Arena feature. Assign <b>Parent</b> in the Inspector
    /// to the object that owns <see cref="CoreAI.Composition.CoreAILifetimeScope"/>.
    /// VS-style progression is bootstrapped by <see cref="ArenaSurvivalProceduralSetup"/>.
    /// </summary>
    public sealed class RogueliteArenaLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // Опционально: сюда — регистрация SO/UseCases, если уводим проводку с SessionHost на VContainer.
        }
    }
}