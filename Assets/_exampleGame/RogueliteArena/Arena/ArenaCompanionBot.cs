using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>Боевой темп компаньона после ответа AINpc (F2): заметно меняет скорость и радиусы.</summary>
    public enum CompanionCombatStance
    {
        /// <summary>Базовые параметры из инспектора.</summary>
        Balanced = 0,

        /// <summary>Дальше лезет к врагам, выше скорость, уже «хвост» у игрока.</summary>
        Aggressive = 1,

        /// <summary>Ближе к игроку, меньше агро-радиус, медленнее.</summary>
        Defensive = 2
    }

    /// <summary>Простой бот-компаньон: следует за игроком, бьёт ближайшего врага. F2 + AINpc меняет <see cref="CompanionCombatStance"/>.</summary>
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

        private CharacterController _cc;
        private float _vy;
        private float _nextAttack;
        private IArenaSessionView _session;

        private CompanionCombatStance _stance = CompanionCombatStance.Balanced;
        private float _baseMoveSpeed;
        private float _baseFollowDistance;
        private float _baseEnemyAcquireRadius;

        /// <summary>Текущая стойка после последнего применения (в т.ч. с F2).</summary>
        public CompanionCombatStance CurrentStance => _stance;

        public void Init(IArenaSessionView session) => _session = session;

        /// <summary>
        /// Применяет множители к скорости, дистанции следования и радиусу поиска врагов (видно в игре и в логе).
        /// </summary>
        /// <param name="logChange">Ложь при инициализации из Awake.</param>
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
        }

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _baseMoveSpeed = moveSpeed;
            _baseFollowDistance = followDistance;
            _baseEnemyAcquireRadius = enemyAcquireRadius;
            ApplyCombatStance(CompanionCombatStance.Balanced, logChange: false);
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
            var all = Object.FindObjectsByType<ArenaEnemyBrain>(FindObjectsSortMode.None);
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

            _nextAttack = Time.time + attackCooldown;
            enemy.TakeDamage(attackDamage);
        }
    }
}

