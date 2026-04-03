using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.UseCases;
using CoreAI.ExampleGame.ArenaProgression.View;

namespace CoreAI.ExampleGame.ArenaProgression.Presenter
{
    public sealed class ArenaUpgradeDraftPresenter
    {
        private readonly ArenaTeamProgressionState _team;
        private readonly IRollUpgradeOffersUseCase _roll;
        private readonly IApplySelectedUpgradeUseCase _apply;
        private readonly ICompanionUpgradeBrain _companionBrain;
        private readonly ArenaUpgradeChoiceView _view;
        private readonly List<ArenaUpgradeOffer> _buffer = new();
        private readonly HashSet<string> _chosenThisScreen = new();
        private List<ArenaUpgradeOffer> _lastShown = new();

        public ArenaUpgradeDraftPresenter(
            ArenaTeamProgressionState team,
            IRollUpgradeOffersUseCase roll,
            IApplySelectedUpgradeUseCase apply,
            ICompanionUpgradeBrain companionBrain,
            ArenaUpgradeChoiceView view)
        {
            _team = team;
            _roll = roll;
            _apply = apply;
            _companionBrain = companionBrain;
            _view = view;
        }

        public void OpenDraft(HashSet<string> excludeIds = null)
        {
            if (_view == null)
                return;
            _chosenThisScreen.Clear();
            _team?.BeginDraftScreen();
            ShowNextRoll(excludeIds);
        }

        private void ShowNextRoll(HashSet<string> excludeIds)
        {
            _buffer.Clear();
            var exclude = new HashSet<string>(_chosenThisScreen);
            if (excludeIds != null)
            {
                foreach (var e in excludeIds)
                    exclude.Add(e);
            }

            _roll?.Execute(exclude, _buffer);
            _lastShown = new List<ArenaUpgradeOffer>(_buffer);
            _view?.ShowOffers(_buffer, OnPlayerPicked);
        }

        private void OnPlayerPicked(ArenaUpgradeOffer offer)
        {
            if (offer?.Definition == null)
                return;
            _apply?.Execute(offer, applyToCompanionToo: true);
            _chosenThisScreen.Add(offer.Definition.Id);
            _team?.ConsumePick();
            if (_team != null && _team.PicksRemainingThisScreen > 0)
            {
                ShowNextRoll(excludeIds: null);
                return;
            }

            CompanionPickAndHide();
        }

        private void CompanionPickAndHide()
        {
            if (_companionBrain != null && _lastShown != null && _lastShown.Count > 0)
            {
                var candidates = new List<ArenaUpgradeOffer>();
                for (int i = 0; i < _lastShown.Count; i++)
                {
                    var o = _lastShown[i];
                    if (o?.Definition != null && !_chosenThisScreen.Contains(o.Definition.Id))
                        candidates.Add(o);
                }

                var pick = _companionBrain.Pick(candidates);
                if (pick != null)
                    _apply?.Execute(pick, applyToCompanionToo: false);
            }

            _view?.Hide();
        }
    }
}
