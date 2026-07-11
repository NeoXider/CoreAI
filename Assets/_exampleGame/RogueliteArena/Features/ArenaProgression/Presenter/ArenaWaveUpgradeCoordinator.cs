using System.Collections;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Presenter
{
    /// <summary>Phase-two integration point for inserting upgrade drafts into the arena director flow.</summary>
    public interface IRunWaveUpgradeFlow
    {
        IEnumerator RunWaveEndUpgradeFlowCoroutine();
    }

    public sealed class ArenaWaveUpgradeCoordinator : MonoBehaviour, IRunWaveUpgradeFlow
    {
        private ArenaUpgradeDraftPresenter _presenter;

        public void Init(ArenaUpgradeDraftPresenter presenter)
        {
            _presenter = presenter;
        }

        public IEnumerator RunWaveEndUpgradeFlowCoroutine()
        {
            if (_presenter == null)
            {
                yield break;
            }

            _presenter.OpenDraft();
            yield break;
        }
    }
}
