using CoreAI.ExampleGame.ArenaProgression.UseCases;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using UnityEngine;
using UnityEngine.AI;

namespace CoreAI.ExampleGame.ArenaCombat.Infrastructure
{
    public sealed class ArenaEnemyBrain : MonoBehaviour
    {
        [SerializeField]
        private float moveSpeed = 2.8f;

        [SerializeField]
        private int maxHp = 30;

        [SerializeField]
        private float contactTick = 0.35f;

        [SerializeField]
        private int contactDamage = 8;

        private int _hp;
        private float _moveSpeedRuntime;
        private int _contactDamageRuntime;
        private float _nextContact;
        private bool _waveStatsApplied;
        private IArenaSessionAuthority _session;
        private IArenaKillXpService _killXp;
        private NavMeshAgent _nav;

        private void Awake()
        {
            // Enemies are instantiated from an INACTIVE template, so the director calls
            // ApplyWaveStats BEFORE SetActive(true) — and Awake runs after it. Defaults must not
            // clobber already-applied wave scaling (this silently disabled wave difficulty).
            if (!_waveStatsApplied)
            {
                _hp = maxHp;
                _moveSpeedRuntime = moveSpeed;
                _contactDamageRuntime = contactDamage;
            }

            _nav = GetComponent<NavMeshAgent>();
            if (_nav != null)
            {
                _nav.speed = _moveSpeedRuntime;
                _nav.stoppingDistance = 1f;
                _nav.updateRotation = true;
            }
        }

        /// <summary>Call before <c>SetActive(true)</c> on enemies spawned from a template.</summary>
        public void Configure(IArenaSessionAuthority session, IArenaKillXpService killXp)
        {
            _session = session;
            _killXp = killXp;
        }

        public void ApplyWaveStats(float hpMult, float damageMult, float moveSpeedMult)
        {
            _waveStatsApplied = true;
            _hp = Mathf.Max(1, Mathf.RoundToInt(maxHp * Mathf.Max(0.01f, hpMult)));
            _contactDamageRuntime = Mathf.Max(1, Mathf.RoundToInt(contactDamage * Mathf.Max(0.01f, damageMult)));
            _moveSpeedRuntime = Mathf.Max(0.1f, moveSpeed * Mathf.Max(0.01f, moveSpeedMult));
            if (_nav == null)
            {
                _nav = GetComponent<NavMeshAgent>(); // called pre-activation, before Awake fetched it
            }

            if (_nav != null)
            {
                _nav.speed = _moveSpeedRuntime;
            }
        }

        private void OnEnable()
        {
            if (_session is { IsAuthoritativeSimulation: true })
            {
                _session.NotifyEnemySpawned();
                _session.RegisterEnemy(this);
            }
        }

        private void OnDisable()
        {
            if (_session is { IsAuthoritativeSimulation: true })
            {
                _session.UnregisterEnemy(this);
            }
        }

        private void Update()
        {
            if (_session == null || !_session.IsAuthoritativeSimulation)
            {
                return;
            }

            if (_session.PrimaryPlayerTransform == null)
            {
                return;
            }

            Vector3 p = _session.PrimaryPlayerTransform.position;
            Vector3 flat = new(p.x, transform.position.y, p.z);
            if (_nav != null && _nav.isOnNavMesh)
            {
                _nav.SetDestination(flat);
            }
            else
            {
                Vector3 dir = (flat - transform.position).normalized;
                if (dir.sqrMagnitude > 0.01f)
                {
                    transform.position += dir * (_moveSpeedRuntime * Time.deltaTime);
                }

                transform.forward = dir;
            }

            if (Time.time < _nextContact)
            {
                return;
            }

            float dist = Vector3.Distance(transform.position, flat);
            if (dist > 1.1f)
            {
                return;
            }

            ArenaPlayerHealth ph = _session.PrimaryPlayerHealth;
            if (ph != null && ph.Current > 0)
            {
                ph.ApplyDamage(_contactDamageRuntime);
                _nextContact = Time.time + contactTick;
            }
        }

        public void TakeDamage(int amount)
        {
            if (_session == null || !_session.IsAuthoritativeSimulation)
            {
                return;
            }

            if (amount <= 0 || _hp <= 0)
            {
                return;
            }

            _hp -= amount;
            if (_hp <= 0)
            {
                Die();
            }
        }

        private void Die()
        {
            if (_session is { IsAuthoritativeSimulation: true })
            {
                _session.NotifyEnemyDied();
                _killXp?.AwardKill();
            }

            Destroy(gameObject);
        }
    }
}
