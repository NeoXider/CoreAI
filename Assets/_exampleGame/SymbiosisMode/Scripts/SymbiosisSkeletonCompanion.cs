using Unity.Netcode;
using UnityEngine;
using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;

namespace CoreAI.ExampleGame.SymbiosisMode
{
    public enum CompanionAiMode
    {
        LlmLocal_2B,
        LlmApi,
        Off
    }

    [RequireComponent(typeof(NetworkObject))]
    public class SymbiosisSkeletonCompanion : NetworkBehaviour
    {
        [Header("AI Settings")]
        public CompanionAiMode SelectedAiMode = CompanionAiMode.Off;

        [Header("Game Settings (SO)")]
        [SerializeField]
        private Settings.SymbiosisGameSettings gameSettings;

        [Header("References")]
        public SymbiosisGhostPlayer MyGhostOwner;

        [Header("Stats")]
        public float FollowRadius = 2f;

        public float FollowSpeed = 4f;
        public float VampirismRatio = 0.5f; // 50% of damage becomes heal
        public int Damage = 10;
        public float AttackCooldown = 2f;
        public float AttackRange = 3f;

        private float _lastAttackTime;

        private void Start()
        {
            if (gameSettings != null)
            {
                FollowRadius = gameSettings.SkeletonFollowRadius;
                FollowSpeed = gameSettings.SkeletonFollowSpeed;
                VampirismRatio = gameSettings.SkeletonVampirismRatio;
                Damage = gameSettings.SkeletonDamage;
                AttackCooldown = gameSettings.SkeletonAttackCooldown;
                AttackRange = gameSettings.SkeletonAttackRange;
            }

            Renderer ren = GetComponentInChildren<Renderer>();
            if (ren != null)
            {
                ren.material.color = Color.green; // Prototype Skeleton Color
            }
        }

        public override void OnNetworkSpawn()
        {
            // Skeletons are server-simulated
            if (!IsServer)
            {
                enabled = false;
            }
        }

        private static void ApplyLitColor(Renderer r, Color c)
        {
            if (r == null)
            {
                return;
            }

            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material mat = new(sh);
            mat.SetColor(sh.name.Contains("Universal") ? "_BaseColor" : "_Color", c);
            r.sharedMaterial = mat;
        }

        private void Update()
        {
            if (!IsServer || MyGhostOwner == null)
            {
                return;
            }

            // 1. Follow Owner logic (stance widens/tightens the leash)
            float dist = Vector3.Distance(transform.position, MyGhostOwner.transform.position);
            if (dist > FollowRadius * _stanceFollowMultiplier)
            {
                transform.position = Vector3.MoveTowards(transform.position, MyGhostOwner.transform.position,
                    FollowSpeed * Time.deltaTime);
            }

            // 2. Combat Logic
            if (Time.time - _lastAttackTime >= AttackCooldown)
            {
                if (SelectedAiMode == CompanionAiMode.Off)
                {
                    AttackNearestEnemyFallback();
                }
                else
                {
                    // To be triggered by CoreAI tools (LLM) later
                }
            }
        }

        private void AttackNearestEnemyFallback()
        {
            TryAttackNearestEnemy();
        }

        /// <summary>
        /// Attacks the nearest enemy inside the (stance-modified) attack range. Public entry point
        /// for the LLM tool (`skeleton_attack_nearest`); respects the attack cooldown. Returns
        /// false when on cooldown, no session, or no enemy in range — so a tool call is honest
        /// about whether anything happened.
        /// </summary>
        public bool TryAttackNearestEnemy()
        {
            if (Time.time - _lastAttackTime < AttackCooldown)
            {
                return false;
            }

            ArenaSurvivalSession session =
                FindAnyObjectByType<ArenaSurvivalSession>();
            if (session == null)
            {
                return false;
            }

            ArenaEnemyBrain nearestEnemy = null;
            float minDistance = AttackRange * _stanceRangeMultiplier;

            foreach (ArenaEnemyBrain enemy in session.ActiveEnemiesList)
            {
                float d = Vector3.Distance(transform.position, enemy.transform.position);
                if (d < minDistance)
                {
                    minDistance = d;
                    nearestEnemy = enemy;
                }
            }

            if (nearestEnemy == null)
            {
                return false;
            }

            PerformAttack(nearestEnemy);
            return true;
        }

        /// <summary>
        /// Combat stance set by the LLM tool (`skeleton_set_stance`): aggressive extends attack
        /// reach and lets the skeleton roam further from its ghost; defensive does the opposite;
        /// balanced (default) is neutral. Unknown values fall back to balanced.
        /// </summary>
        public string CurrentStance { get; private set; } = "balanced";

        private float _stanceRangeMultiplier = 1f;
        private float _stanceFollowMultiplier = 1f;

        public void SetStance(string stance)
        {
            switch ((stance ?? "").Trim().ToLowerInvariant())
            {
                case "aggressive":
                    CurrentStance = "aggressive";
                    _stanceRangeMultiplier = 1.5f;
                    _stanceFollowMultiplier = 1.75f;
                    break;
                case "defensive":
                    CurrentStance = "defensive";
                    _stanceRangeMultiplier = 0.7f;
                    _stanceFollowMultiplier = 0.6f;
                    break;
                default:
                    CurrentStance = "balanced";
                    _stanceRangeMultiplier = 1f;
                    _stanceFollowMultiplier = 1f;
                    break;
            }
        }

        public void PerformAttack(ArenaEnemyBrain target)
        {
            _lastAttackTime = Time.time;
            Debug.Log($"[Server] Skeleton -> attacked enemy. Dealt {Damage} damage.");

            target.TakeDamage(Damage);

            // Calculate heal 
            float healAmount = Damage * VampirismRatio;

            // Ask player to heal
            MyGhostOwner.HealFromSkeleton(healAmount);
        }
    }
}
