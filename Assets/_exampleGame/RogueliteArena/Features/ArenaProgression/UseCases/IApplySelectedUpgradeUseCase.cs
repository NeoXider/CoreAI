using CoreAI.ExampleGame.ArenaProgression.Infrastructure;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public interface IApplySelectedUpgradeUseCase
    {
        void Execute(ArenaUpgradeOffer offer, bool applyToCompanionToo);
    }
}
