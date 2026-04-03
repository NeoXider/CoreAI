using System;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaCombat.Infrastructure
{
    public sealed class ArenaPlayerHealth : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 100;

        public int Current { get; private set; }
        public int Max => _runtimeMax > 0 ? _runtimeMax : maxHealth;
        public event Action<int, int> Changed;
        public event Action Died;

        private int _runtimeMax;
        private float _hpRegenPerSecond;

        private void Awake()
        {
            _runtimeMax = maxHealth;
            Current = _runtimeMax;
        }

        private void Update()
        {
            if (_hpRegenPerSecond <= 0f || Current <= 0 || Current >= Max)
                return;
            var add = _hpRegenPerSecond * Time.deltaTime;
            if (add <= 0f)
                return;
            int n = Mathf.Min(Max, Mathf.FloorToInt(Current + add));
            if (n > Current)
            {
                Current = n;
                Changed?.Invoke(Current, Max);
            }
        }

        public void ApplyFromCombatStats(IArenaCombatStats stats)
        {
            if (stats == null)
                return;
            _hpRegenPerSecond = stats.HpRegenPerSecond;
            int newMax = Mathf.Max(1, Mathf.RoundToInt(stats.MaxHealth));
            int oldMax = Mathf.Max(1, Max);
            float ratio = Current / (float)oldMax;
            _runtimeMax = newMax;
            Current = Mathf.Clamp(Mathf.RoundToInt(ratio * newMax), 1, newMax);
            Changed?.Invoke(Current, Max);
        }

        public void ApplyDamage(int amount)
        {
            if (amount <= 0 || Current <= 0)
                return;
            Current = Mathf.Max(0, Current - amount);
            Changed?.Invoke(Current, Max);
            if (Current == 0)
                Died?.Invoke();
        }
    }
}
