using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public static class ArenaWavePlanValidator
    {
        public static bool TryValidate(ArenaWavePlan plan, int waveIndex1Based, out string failReason)
        {
            failReason = null;
            if (plan == null)
            {
                failReason = "plan is null";
                return false;
            }

            if (plan.waveIndex1Based != 0 && plan.waveIndex1Based != waveIndex1Based)
            {
                failReason = "wave index mismatch";
                return false;
            }

            if (plan.enemyCount < 0 || plan.enemyCount > 500)
            {
                failReason = "enemyCount out of range";
                return false;
            }

            if (plan.enemyHpMult < 0.25f || plan.enemyHpMult > 5f)
            {
                failReason = "enemyHpMult out of range";
                return false;
            }

            if (plan.enemyDamageMult < 0.25f || plan.enemyDamageMult > 5f)
            {
                failReason = "enemyDamageMult out of range";
                return false;
            }

            if (plan.enemyMoveSpeedMult < 0.25f || plan.enemyMoveSpeedMult > 3f)
            {
                failReason = "enemyMoveSpeedMult out of range";
                return false;
            }

            if (plan.spawnIntervalSeconds < 0.05f || plan.spawnIntervalSeconds > 3f)
            {
                failReason = "spawnIntervalSeconds out of range";
                return false;
            }

            if (plan.spawnRadius < 5f || plan.spawnRadius > 60f)
            {
                failReason = "spawnRadius out of range";
                return false;
            }

            return true;
        }
    }
}

