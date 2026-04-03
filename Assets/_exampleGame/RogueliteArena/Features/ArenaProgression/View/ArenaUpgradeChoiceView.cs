using System;
using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.View
{
    public sealed class ArenaUpgradeChoiceView : MonoBehaviour
    {
        [SerializeField] private ArenaUpgradePresentationConfig presentation;
        [SerializeField] private ArenaUpgradeCardWidget[] cardPool = new ArenaUpgradeCardWidget[5];

        private Action<ArenaUpgradeOffer> _callback;

        public void ShowOffers(IReadOnlyList<ArenaUpgradeOffer> offers, Action<ArenaUpgradeOffer> onPicked)
        {
            _callback = onPicked;
            gameObject.SetActive(true);
            int n = offers != null ? offers.Count : 0;
            for (int i = 0; i < cardPool.Length; i++)
            {
                var w = cardPool[i];
                if (w == null)
                    continue;
                if (i < n)
                    w.Bind(offers[i], presentation, OnCardPicked);
                else
                    w.gameObject.SetActive(false);
            }
        }

        public void Hide()
        {
            _callback = null;
            gameObject.SetActive(false);
            if (cardPool == null)
                return;
            for (int i = 0; i < cardPool.Length; i++)
            {
                if (cardPool[i] != null)
                    cardPool[i].gameObject.SetActive(false);
            }
        }

        private void OnCardPicked(ArenaUpgradeOffer offer) => _callback?.Invoke(offer);
    }
}
