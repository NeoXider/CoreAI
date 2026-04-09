using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    /// <summary>
    /// Биндит данные игрока-призрака и состояние волны к ручному Canvas.
    /// </summary>
    public class SymbiosisUiMediator : MonoBehaviour
    {
        [Header("UI References")]
        public RectTransform HealthFill;
        public TextMeshProUGUI WaveText;
        public Image GhostColorIndicator;

        [Header("Game References")]
        private SymbiosisGhostPlayer _localGhost;
        private ArenaSurvival.Infrastructure.ArenaSurvivalSession _session;

        private float _maxFillWidth = 390f;

        private void Start()
        {
            _session = Object.FindAnyObjectByType<ArenaSurvival.Infrastructure.ArenaSurvivalSession>();
            if (HealthFill != null) _maxFillWidth = HealthFill.sizeDelta.x;
            
            UpdateWaveText(1);
        }

        private void Update()
        {
            // Ленивый поиск локального игрока, если еще не найден
            if (_localGhost == null)
            {
                foreach (var ghost in Object.FindObjectsByType<SymbiosisGhostPlayer>(FindObjectsSortMode.None))
                {
                    if (ghost.IsOwner)
                    {
                        _localGhost = ghost;
                        _localGhost.OnChangePercentHealth += UpdateHealthBar;
                        UpdateHealthBar(_localGhost.Health / _localGhost.MaxHealth);
                        break;
                    }
                }
            }

            if (_session != null && WaveText != null)
            {
                UpdateWaveText(_session.CurrentWave);
            }
        }

        private void UpdateHealthBar(float percent)
        {
            if (HealthFill == null) return;
            
            Vector2 size = HealthFill.sizeDelta;
            size.x = _maxFillWidth * percent;
            HealthFill.sizeDelta = size;

            // Simple color logic
            if (GhostColorIndicator != null)
            {
                GhostColorIndicator.color = Color.Lerp(Color.red, Color.green, percent);
            }
        }

        private void UpdateWaveText(int wave)
        {
            if (WaveText != null)
            {
                WaveText.text = $"WAVE: {wave}";
            }
        }

        private void OnDestroy()
        {
            if (_localGhost != null)
            {
                _localGhost.OnChangePercentHealth -= UpdateHealthBar;
            }
        }
    }
}
