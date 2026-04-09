using Unity.Netcode;
using UnityEngine;
using CoreAI.ExampleGame.ArenaCombat.Infrastructure;

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
        [SerializeField] private Settings.SymbiosisGameSettings gameSettings;

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
            if (r == null) return;
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh);
            mat.SetColor(sh.name.Contains("Universal") ? "_BaseColor" : "_Color", c);
            r.sharedMaterial = mat;
        }

        void Update()
        {
            if (!IsServer || MyGhostOwner == null) return;

            // 1. Follow Owner logic
            float dist = Vector3.Distance(transform.position, MyGhostOwner.transform.position);
            if (dist > FollowRadius)
            {
                transform.position = Vector3.MoveTowards(transform.position, MyGhostOwner.transform.position, FollowSpeed * Time.deltaTime);
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
            var session = Object.FindAnyObjectByType<CoreAI.ExampleGame.ArenaSurvival.Infrastructure.ArenaSurvivalSession>();
            if (session == null) return;

            ArenaEnemyBrain nearestEnemy = null;
            float minDistance = AttackRange;

            foreach(var enemy in session.ActiveEnemiesList)
            {
                float d = Vector3.Distance(transform.position, enemy.transform.position);
                if (d < minDistance)
                {
                    minDistance = d;
                    nearestEnemy = enemy;
                }
            }

            if (nearestEnemy != null)
            {
                PerformAttack(nearestEnemy);
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
