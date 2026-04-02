using System;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaPlayerHealth : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 100;

        public int Current { get; private set; }
        public int Max => maxHealth;
        public event Action<int, int> Changed;
        public event Action Died;

        private void Awake()
        {
            Current = maxHealth;
        }

        public void ApplyDamage(int amount)
        {
            if (amount <= 0 || Current <= 0)
                return;
            Current = Mathf.Max(0, Current - amount);
            Changed?.Invoke(Current, maxHealth);
            if (Current == 0)
                Died?.Invoke();
        }
    }
}
