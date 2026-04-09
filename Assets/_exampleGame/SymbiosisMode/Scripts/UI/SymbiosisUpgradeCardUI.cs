using CoreAI.ExampleGame.SymbiosisMode.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    public class SymbiosisUpgradeCardUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI descriptionText;
        [SerializeField] private Image iconImage;
        [SerializeField] private Button cardButton;

        private SymbiosisUpgradeData _data;
        private Action<SymbiosisUpgradeData> _onSelected;

        public void Setup(SymbiosisUpgradeData data, Action<SymbiosisUpgradeData> onSelected)
        {
            _data = data;
            _onSelected = onSelected;

            if (titleText != null) titleText.text = data.UpgradeName;
            if (descriptionText != null) descriptionText.text = data.Description;

            cardButton.onClick.RemoveAllListeners();
            cardButton.onClick.AddListener(OnCardClicked);
        }

        private void OnCardClicked()
        {
            _onSelected?.Invoke(_data);
        }
    }
}
