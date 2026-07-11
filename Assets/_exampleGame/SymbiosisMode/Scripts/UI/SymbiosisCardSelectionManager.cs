using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;
using CoreAI.ExampleGame.SymbiosisMode.Settings;
using UnityEngine;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    public class SymbiosisCardSelectionManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private SymbiosisGameSettings gameSettings;

        [SerializeField]
        private GameObject cardsPanel;

        [SerializeField]
        private Transform cardsContainer;

        [SerializeField]
        private SymbiosisUpgradeCardUI cardPrefab;

        private ArenaSurvivalSession _session;

        private void Start()
        {
            if (cardsPanel == null || cardsContainer == null || cardPrefab == null)
            {
                Debug.LogWarning(
                    "[CardManager] cardsPanel/cardsContainer/cardPrefab not wired in the inspector; card selection disabled.");
                enabled = false;
                return;
            }

            cardsPanel.SetActive(false);
            _session = FindAnyObjectByType<ArenaSurvivalSession>();
            if (_session != null)
            {
                // We show cards when wave is completed
                _session.CurrentWaveChanged += OnWaveChanged;
            }
        }

        private void OnDestroy()
        {
            if (_session != null)
            {
                _session.CurrentWaveChanged -= OnWaveChanged;
            }
        }

        private void OnWaveChanged(int newWave)
        {
            // Do not show cards on initial wave start (wave 1) usually.
            // Wave starts at 1, so when it changes to 2+, we offer cards.
            if (newWave > 1)
            {
                ShowCards();
            }
        }

        public void ShowCards()
        {
            if (cardsPanel == null || cardsContainer == null || cardPrefab == null)
            {
                return; // not wired — Start already warned and disabled the component
            }

            // A single empty inspector slot in AvailableUpgrades would otherwise crash the card
            // screen at random (Setup dereferences the data), so nulls are filtered up front.
            List<SymbiosisUpgradeData> available = new();
            if (gameSettings != null && gameSettings.AvailableUpgrades != null)
            {
                foreach (SymbiosisUpgradeData u in gameSettings.AvailableUpgrades)
                {
                    if (u != null)
                    {
                        available.Add(u);
                    }
                }
            }

            if (available.Count == 0)
            {
                Debug.LogWarning("[CardManager] No upgrades configured!");
                return;
            }

            Time.timeScale = 0f; // Pause game
            cardsPanel.SetActive(true);

            // Clean up old cards
            foreach (Transform child in cardsContainer)
            {
                Destroy(child.gameObject);
            }

            // Pick 3 random cards for now (or all if < 3)
            int count = Mathf.Min(3, available.Count);

            for (int i = 0; i < count; i++)
            {
                int r = Random.Range(0, available.Count);
                SymbiosisUpgradeData picked = available[r];
                available.RemoveAt(r); // avoid duplicates if possible

                SymbiosisUpgradeCardUI cardObj = Instantiate(cardPrefab, cardsContainer);
                cardObj.Setup(picked, OnCardSelected);
            }
        }

        private void OnCardSelected(SymbiosisUpgradeData data)
        {
            Debug.Log($"[CardManager] Upgrade Selected: {data.UpgradeName}");

            // Apply logic
            ApplyUpgrade(data);

            // Resume game
            cardsPanel.SetActive(false);
            Time.timeScale = 1f;
        }

        private void ApplyUpgrade(SymbiosisUpgradeData data)
        {
            SymbiosisGhostPlayer p = FindAnyObjectByType<SymbiosisGhostPlayer>();
            SymbiosisSkeletonCompanion s = FindAnyObjectByType<SymbiosisSkeletonCompanion>();

            switch (data.Type)
            {
                case UpgradeType.MaxHealth:
                    if (p != null)
                    {
                        p.MaxHealth += data.Amount;
                    }

                    break;
                case UpgradeType.Damage:
                    if (s != null)
                    {
                        s.Damage += (int)data.Amount;
                    }

                    break;
                case UpgradeType.Speed:
                    if (p != null)
                    {
                        p.MoveSpeed += data.Amount;
                    }

                    break;
                case UpgradeType.Vampirism:
                    // Currently unapplied directly, reserved for skeleton
                    break;
            }
        }
    }
}
