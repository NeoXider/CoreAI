using CoreAI.ExampleGame.ArenaSurvival.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.SymbiosisMode.Settings
{
    [CreateAssetMenu(fileName = "SymbiosisGameSettings", menuName = "Symbiosis/Game Settings")]
    public class SymbiosisGameSettings : ArenaDirectorSettings
    {
        [Header("Ghost Player Init Stats")]
        public float GhostMaxHealth = 100f;
        public float GhostMoveSpeed = 5f;

        [Header("Skeleton Companion Init Stats")]
        public float SkeletonFollowRadius = 2f;
        public float SkeletonFollowSpeed = 4f;
        public float SkeletonVampirismRatio = 0.5f;
        public int SkeletonDamage = 10;
        public float SkeletonAttackCooldown = 2f;
        public float SkeletonAttackRange = 3f;

        [Header("Upgrades Pool")]
        public SymbiosisUpgradeData[] AvailableUpgrades;
    }
}
