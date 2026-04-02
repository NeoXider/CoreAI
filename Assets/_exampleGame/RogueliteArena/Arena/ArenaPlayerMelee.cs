using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaPlayerMelee : MonoBehaviour
    {
        [SerializeField] private float range = 2.2f;
        [SerializeField] private float cooldown = 0.45f;
        [SerializeField] private int damage = 28;
        [SerializeField] private LayerMask enemyLayers = ~0;

        private float _nextHit;

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.Space) && !Input.GetMouseButtonDown(0))
                return;
            if (Time.time < _nextHit)
                return;
            _nextHit = Time.time + cooldown;

            var origin = transform.position + Vector3.up * 0.9f;
            var forward = Camera.main != null ? Camera.main.transform.forward : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = transform.forward;
            forward.Normalize();

            var hits = Physics.SphereCastAll(origin, 0.35f, forward, range, enemyLayers, QueryTriggerInteraction.Collide);
            foreach (var hit in hits)
            {
                var brain = hit.collider.GetComponentInParent<ArenaEnemyBrain>();
                if (brain != null)
                    brain.TakeDamage(damage);
            }
        }
    }
}
