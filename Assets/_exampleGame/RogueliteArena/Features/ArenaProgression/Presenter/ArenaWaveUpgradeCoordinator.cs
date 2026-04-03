using System.Collections;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Presenter
{
    /// <summary>Фаза 2: вставка в <see cref="CoreAI.ExampleGame.ArenaSurvival.UseCases.ArenaSurvivalDirector"/>. Сейчас — заглушка API.</summary>
    public interface IRunWaveUpgradeFlow
    {
        IEnumerator RunWaveEndUpgradeFlowCoroutine();
    }

    public sealed class ArenaWaveUpgradeCoordinator : MonoBehaviour, IRunWaveUpgradeFlow
    {
        private ArenaUpgradeDraftPresenter _presenter;

        public void Init(ArenaUpgradeDraftPresenter presenter) => _presenter = presenter;

        public IEnumerator RunWaveEndUpgradeFlowCoroutine()
        {
            if (_presenter == null)
                yield break;
            _presenter.OpenDraft();
            yield break;
        }
    }
}
