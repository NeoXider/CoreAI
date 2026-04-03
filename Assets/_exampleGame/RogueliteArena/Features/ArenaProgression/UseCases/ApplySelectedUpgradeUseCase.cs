using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class ApplySelectedUpgradeUseCase : IApplySelectedUpgradeUseCase
    {
        private readonly ArenaTeamProgressionState _team;
        private readonly ArenaRunCombatModel _combat;
        private readonly ArenaRunBalanceConfig _balance;
        private readonly System.Action _onStatsChanged;

        public ApplySelectedUpgradeUseCase(
            ArenaTeamProgressionState team,
            ArenaRunCombatModel combat,
            ArenaRunBalanceConfig balance,
            System.Action onStatsChanged = null)
        {
            _team = team;
            _combat = combat;
            _balance = balance;
            _onStatsChanged = onStatsChanged;
        }

        public void Execute(ArenaUpgradeOffer offer, bool applyToCompanionToo)
        {
            if (offer?.Definition == null || _team == null || _combat == null)
                return;
            var def = offer.Definition;
            float m = offer.StatMultiplier;
            int cap = _balance != null ? _balance.MaxChoiceCount : 5;

            switch (def.Kind)
            {
                case ArenaUpgradeKind.PassiveSlotPlusOne:
                    _team.AddPassiveSlots(1);
                    break;
                case ArenaUpgradeKind.OfferExtraChoices:
                    _team.AddMaxChoices(1, cap);
                    break;
                case ArenaUpgradeKind.LegendaryDoublePickThisWave:
                    _team.DoublePickRemainingThisScreen = true;
                    break;
                default:
                    _combat.ApplyUpgradeToPlayer(def, m);
                    if (applyToCompanionToo)
                        _combat.ApplyUpgradeToCompanion(def, m);
                    break;
            }

            _onStatsChanged?.Invoke();
        }
    }
}
