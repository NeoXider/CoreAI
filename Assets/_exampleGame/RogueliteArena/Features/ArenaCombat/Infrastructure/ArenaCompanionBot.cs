using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaCombat.Infrastructure
{
    /// <summary>Companion combat stance selected by AINpc responses; affects speed and radii.</summary>
    public enum CompanionCombatStance
    {
        /// <summary>Baseline values configured in the Inspector.</summary>
        Balanced = 0,

        /// <summary>More aggressive stance with higher speed, wider aggro, and shorter follow distance.</summary>
        Aggressive = 1,

        /// <summary>Defensive stance that stays closer to the player with lower aggro and speed.</summary>
        Defensive = 2
    }

    /// <summary>Simple companion bot that follows the player, attacks nearby enemies, and reacts to AINpc stance changes.</summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class ArenaCompanionBot : MonoBehaviour
    {
        [SerializeField] private float followDistance = 2.5f;
        [SerializeField] private float moveSpeed = 6f;
        [SerializeField] private float gravity = -25f;
        [SerializeField] private float enemyAcquireRadius = 12f;
        [SerializeField] private float attackRange = 2.2f;
        [SerializeField] private float attackCooldown = 0.55f;
        [SerializeField] private int attackDamage = 18;

        private int _attackDamageRuntime;
        private float _attackCooldownRuntime;

        private CharacterController _cc;
        private float _vy;
        private float _nextAttack;
        private IArenaSessionView _session;

        private CompanionCombatStance _stance = CompanionCombatStance.Balanced;
        private float _baseMoveSpeed;
        private float _baseFollowDistance;
        private float _baseEnemyAcquireRadius;
        private Renderer _visRenderer;

        /// <summary>Current stance after the most recent direct or AINpc-driven change.</summary>
        public CompanionCombatStance CurrentStance => _stance;

        public void Init(IArenaSessionView session) => _session = session;

        /// <summary>
        /// Applies stance multipliers to speed, follow distance, and enemy search radius.
        /// </summary>
        /// <param name="logChange">False during Awake initialization to avoid noisy startup logs.</param>
        public void ApplyCombatStance(CompanionCombatStance stance, bool logChange = true)
        {
            _stance = stance;
            switch (stance)
            {
                case CompanionCombatStance.Aggressive:
                    moveSpeed = _baseMoveSpeed * 1.38f;
                    followDistance = _baseFollowDistance * 0.78f;
                    enemyAcquireRadius = _baseEnemyAcquireRadius * 1.5f;
                    break;
                case CompanionCombatStance.Defensive:
                    moveSpeed = _baseMoveSpeed * 0.8f;
                    followDistance = _baseFollowDistance * 1.42f;
                    enemyAcquireRadius = _baseEnemyAcquireRadius * 0.58f;
                    break;
                default:
                    moveSpeed = _baseMoveSpeed;
                    followDistance = _baseFollowDistance;
                    enemyAcquireRadius = _baseEnemyAcquireRadius;
                    break;
            }

            if (logChange)
            {
                Debug.Log(
                    "[CoreAI.ExampleGame] Компаньон: стойка " + stance +
                    $" → speed={moveSpeed:F1}, follow={followDistance:F1}, acquireRadius={enemyAcquireRadius:F1}");
            }

            ApplyStanceVisual(stance);
        }

        public void ApplyFromCombatStats(IArenaCombatStats stats)
        {
            if (stats == null)
                return;
            _attackDamageRuntime = Mathf.Max(1, Mathf.RoundToInt(stats.MeleeDamage));
            attackCooldown = Mathf.Max(0.05f, stats.AttackCooldownSeconds);
            _attackCooldownRuntime = attackCooldown;
        }

        private void Awake()
        {
            _attackDamageRuntime = attackDamage;
            _attackCooldownRuntime = attackCooldown;
            _cc = GetComponent<CharacterController>();
            var vis = transform.Find("Vis");
            if (vis != null)
                _visRenderer = vis.GetComponent<Renderer>();
            _baseMoveSpeed = moveSpeed;
            _baseFollowDistance = followDistance;
            _baseEnemyAcquireRadius = enemyAcquireRadius;
            ApplyCombatStance(CompanionCombatStance.Balanced, logChange: false);
        }

        private void ApplyStanceVisual(CompanionCombatStance stance)
        {
            if (_visRenderer == null)
                return;
            var c = stance switch
            {
                CompanionCombatStance.Aggressive => new Color(0.95f, 0.35f, 0.25f),
                CompanionCombatStance.Defensive => new Color(0.35f, 0.55f, 0.95f),
                _ => new Color(0.2f, 0.95f, 0.6f)
            };
            ApplyLitBaseColor(_visRenderer, c);
        }

        private static void ApplyLitBaseColor(Renderer r, Color c)
        {
            if (r == null || r.sharedMaterial == null)
                return;
            var m = r.material;
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", c);
            else
                m.color = c;
        }

        private void Update()
        {
            if (_session == null || _session.PrimaryPlayerTransform == null)
                return;

            var targetPos = ChooseMoveTarget();
            MoveTowards(targetPos);
            TryAttack();
        }

        private Vector3 ChooseMoveTarget()
        {
            var player = _session.PrimaryPlayerTransform;
            var playerPos = player.position;

            // Если рядом есть враг — стремимся к нему, иначе держимся рядом с игроком.
            var enemy = FindNearestEnemy(playerPos);
            if (enemy != null)
                return enemy.transform.position;

            var back = -player.forward;
            back.y = 0f;
            if (back.sqrMagnitude < 0.01f)
                back = Vector3.back;
            back.Normalize();
            return playerPos + back * followDistance;
        }

        private ArenaEnemyBrain FindNearestEnemy(Vector3 from)
        {
            var best = (ArenaEnemyBrain)null;
            var bestD2 = enemyAcquireRadius * enemyAcquireRadius;
            var all = Object.FindObjectsByType<ArenaEnemyBrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var e in all)
            {
                var d2 = (e.transform.position - from).sqrMagnitude;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = e;
                }
            }

            return best;
        }

        private void MoveTowards(Vector3 worldTarget)
        {
            var pos = transform.position;
            var delta = worldTarget - pos;
            delta.y = 0f;
            var dir = delta.sqrMagnitude > 0.01f ? delta.normalized : Vector3.zero;
            var move = dir * (moveSpeed * Time.deltaTime);

            if (_cc.isGrounded && _vy < 0f)
                _vy = -2f;
            _vy += gravity * Time.deltaTime;
            move.y = _vy * Time.deltaTime;
            _cc.Move(move);

            if (dir.sqrMagnitude > 0.01f)
                transform.forward = dir;
        }

        private void TryAttack()
        {
            if (Time.time < _nextAttack)
                return;

            var enemy = FindNearestEnemy(transform.position);
            if (enemy == null)
                return;

            var d = Vector3.Distance(transform.position, enemy.transform.position);
            if (d > attackRange)
                return;

            _nextAttack = Time.time + _attackCooldownRuntime;
            enemy.TakeDamage(_attackDamageRuntime);
        }
    }
}

