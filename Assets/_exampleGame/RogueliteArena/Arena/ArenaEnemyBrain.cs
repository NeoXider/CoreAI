using UnityEngine;
using UnityEngine.AI;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaEnemyBrain : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 2.8f;
        [SerializeField] private int maxHp = 30;
        [SerializeField] private float contactTick = 0.35f;
        [SerializeField] private int contactDamage = 8;

        private int _hp;
        private float _moveSpeedRuntime;
        private int _contactDamageRuntime;
        private float _nextContact;
        private IArenaSessionAuthority _session;
        private NavMeshAgent _nav;

        private void Awake()
        {
            _hp = maxHp;
            _moveSpeedRuntime = moveSpeed;
            _contactDamageRuntime = contactDamage;
            _nav = GetComponent<NavMeshAgent>();
            if (_nav != null)
            {
                _nav.speed = _moveSpeedRuntime;
                _nav.stoppingDistance = 1f;
                _nav.updateRotation = true;
            }
        }

        /// <summary>Вызывать до <c>SetActive(true)</c> на экземпляре врага (спавн с шаблона).</summary>
        public void Configure(IArenaSessionAuthority session)
        {
            _session = session;
        }

        public void ApplyWaveStats(float hpMult, float damageMult, float moveSpeedMult)
        {
            _hp = Mathf.Max(1, Mathf.RoundToInt(maxHp * Mathf.Max(0.01f, hpMult)));
            _contactDamageRuntime = Mathf.Max(1, Mathf.RoundToInt(contactDamage * Mathf.Max(0.01f, damageMult)));
            _moveSpeedRuntime = Mathf.Max(0.1f, moveSpeed * Mathf.Max(0.01f, moveSpeedMult));
            if (_nav != null)
                _nav.speed = _moveSpeedRuntime;
        }

        private void OnEnable()
        {
            if (_session is { IsAuthoritativeSimulation: true })
                _session.NotifyEnemySpawned();
        }

        private void Update()
        {
            if (_session == null || !_session.IsAuthoritativeSimulation)
                return;
            if (_session.PrimaryPlayerTransform == null)
                return;
            var p = _session.PrimaryPlayerTransform.position;
            var flat = new Vector3(p.x, transform.position.y, p.z);
            if (_nav != null && _nav.isOnNavMesh)
                _nav.SetDestination(flat);
            else
            {
                var dir = (flat - transform.position).normalized;
                if (dir.sqrMagnitude > 0.01f)
                    transform.position += dir * (_moveSpeedRuntime * Time.deltaTime);
                transform.forward = dir;
            }

            if (Time.time < _nextContact)
                return;
            var dist = Vector3.Distance(transform.position, flat);
            if (dist > 1.1f)
                return;
            var ph = _session.PrimaryPlayerHealth;
            if (ph != null && ph.Current > 0)
            {
                ph.ApplyDamage(_contactDamageRuntime);
                _nextContact = Time.time + contactTick;
            }
        }

        public void TakeDamage(int amount)
        {
            if (_session == null || !_session.IsAuthoritativeSimulation)
                return;
            if (amount <= 0 || _hp <= 0)
                return;
            _hp -= amount;
            if (_hp <= 0)
                Die();
        }

        private void Die()
        {
            if (_session is { IsAuthoritativeSimulation: true })
                _session.NotifyEnemyDied();
            Destroy(gameObject);
        }
    }
}
