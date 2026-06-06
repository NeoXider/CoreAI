using CoreAI.ExampleGame.ArenaProgression.Domain;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.ArenaCombat.Infrastructure
{
    public sealed class ArenaPlayerMelee : MonoBehaviour
    {
        [SerializeField] private float range = 2.2f;
        [SerializeField] private float cooldown = 0.45f;
        [SerializeField] private int damage = 28;
        [SerializeField] private LayerMask enemyLayers = ~0;

        private float _nextHit;
        private int _damageRuntime;
        private float _cooldownRuntime;

        private void Awake()
        {
            _damageRuntime = damage;
            _cooldownRuntime = cooldown;
        }

        public void ApplyFromCombatStats(IArenaCombatStats stats)
        {
            if (stats == null)
            {
                return;
            }

            _damageRuntime = Mathf.Max(1, Mathf.RoundToInt(stats.MeleeDamage));
            _cooldownRuntime = Mathf.Max(0.05f, stats.AttackCooldownSeconds);
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool wantHit =
                (kb != null && kb.spaceKey.wasPressedThisFrame) ||
                (mouse != null && mouse.leftButton.wasPressedThisFrame);

            if (!wantHit)
            {
                return;
            }

            if (Time.time < _nextHit)
            {
                return;
            }

            _nextHit = Time.time + _cooldownRuntime;

            Vector3 origin = transform.position + Vector3.up * 0.9f;
            Vector3 forward = Camera.main != null ? Camera.main.transform.forward : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = transform.forward;
            }

            forward.Normalize();

            RaycastHit[] hits = Physics.SphereCastAll(origin, 0.35f, forward, range, enemyLayers,
                QueryTriggerInteraction.Collide);
            foreach (RaycastHit hit in hits)
            {
                ArenaEnemyBrain brain = hit.collider.GetComponentInParent<ArenaEnemyBrain>();
                if (brain != null)
                {
                    brain.TakeDamage(_damageRuntime);
                }
            }
        }
    }
}