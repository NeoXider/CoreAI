using System;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.ExampleGame.ArenaProgression.View
{
    public sealed class ArenaUpgradeCardWidget : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image frameImage;
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text descriptionText;

        private ArenaUpgradeOffer _offer;
        private Action<ArenaUpgradeOffer> _onPick;

        private void Awake()
        {
            if (button != null)
                button.onClick.AddListener(OnClick);
        }

        public void Bind(
            ArenaUpgradeOffer offer,
            ArenaUpgradePresentationConfig presentation,
            Action<ArenaUpgradeOffer> onPick)
        {
            _offer = offer;
            _onPick = onPick;
            gameObject.SetActive(offer != null);
            if (offer?.Definition == null)
                return;
            var def = offer.Definition;
            if (titleText != null)
                titleText.text = def.Title;
            if (descriptionText != null)
                descriptionText.text = def.Description;
            if (iconImage != null)
            {
                iconImage.sprite = def.Icon;
                iconImage.enabled = def.Icon != null;
            }

            if (presentation != null && frameImage != null)
            {
                var sp = presentation.GetFrame(offer.RolledRarity);
                if (sp != null)
                    frameImage.sprite = sp;
                var mat = presentation.GetMaterial(offer.RolledRarity);
                if (mat != null)
                    frameImage.material = mat;
            }
        }

        private void OnClick() => _onPick?.Invoke(_offer);
    }
}
