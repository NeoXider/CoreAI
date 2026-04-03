using System;
using System.Collections.Generic;

namespace CoreAI.ExampleGame.ArenaProgression.Domain
{
    public interface IArenaCombatStats
    {
        float MaxHealth { get; }
        float HpRegenPerSecond { get; }
        float MeleeDamage { get; }
        float AttackCooldownSeconds { get; }
    }

    /// <summary>Персистентная мета-прогрессия (между забегами).</summary>
    public sealed class ArenaMetaProgressionState
    {
        public int MetaXp { get; private set; }
        public int MetaLevel { get; private set; }
        public readonly HashSet<string> UnlockedUpgradeIds = new();

        public void SetFromSnapshot(int metaXp, int metaLevel, IEnumerable<string> unlocked)
        {
            MetaXp = metaXp;
            MetaLevel = metaLevel;
            UnlockedUpgradeIds.Clear();
            if (unlocked != null)
            {
                foreach (var id in unlocked)
                {
                    if (!string.IsNullOrEmpty(id))
                        UnlockedUpgradeIds.Add(id);
                }
            }
        }

        public void AddMetaXp(int amount)
        {
            if (amount <= 0)
                return;
            MetaXp += amount;
        }

        public void RecomputeMetaLevel(Func<int, int> levelFromTotalXp)
        {
            MetaLevel = levelFromTotalXp != null ? levelFromTotalXp(MetaXp) : 1;
        }

        public void Unlock(string upgradeId)
        {
            if (!string.IsNullOrEmpty(upgradeId))
                UnlockedUpgradeIds.Add(upgradeId);
        }
    }

    /// <summary>Сессионное состояние команды: XP, уровень, модификаторы драфта.</summary>
    public sealed class ArenaTeamProgressionState
    {
        public int SessionTotalXp { get; private set; }
        public int SessionLevel { get; private set; }
        public int MaxChoicesOffered { get; private set; }
        public int PassiveSlotCount { get; private set; }
        public bool DoublePickRemainingThisScreen { get; set; }
        public int PicksRemainingThisScreen { get; set; } = 1;

        public event Action<int, int> SessionXpChanged;
        public event Action<int> SessionLevelChanged;

        public void ConfigureStart(int startChoices, int passiveSlotsStart = 0)
        {
            MaxChoicesOffered = Math.Max(1, startChoices);
            PassiveSlotCount = Math.Max(0, passiveSlotsStart);
            PicksRemainingThisScreen = 1;
            DoublePickRemainingThisScreen = false;
        }

        public void SetLevelFromCurve(int level)
        {
            if (SessionLevel == level)
                return;
            SessionLevel = level;
            SessionLevelChanged?.Invoke(level);
        }

        public void AddSessionXp(int amount)
        {
            if (amount <= 0)
                return;
            SessionTotalXp += amount;
            SessionXpChanged?.Invoke(SessionTotalXp, amount);
        }

        public void AddMaxChoices(int delta, int cap)
        {
            MaxChoicesOffered = Math.Min(cap, Math.Max(1, MaxChoicesOffered + delta));
        }

        public void AddPassiveSlots(int delta) => PassiveSlotCount = Math.Max(0, PassiveSlotCount + delta);

        public void BeginDraftScreen()
        {
            PicksRemainingThisScreen = DoublePickRemainingThisScreen ? 2 : 1;
            DoublePickRemainingThisScreen = false;
        }

        public void ConsumePick()
        {
            PicksRemainingThisScreen = Math.Max(0, PicksRemainingThisScreen - 1);
        }

        public void ResetSession()
        {
            SessionTotalXp = 0;
            SessionLevel = 1;
            DoublePickRemainingThisScreen = false;
            PicksRemainingThisScreen = 1;
        }
    }
}
