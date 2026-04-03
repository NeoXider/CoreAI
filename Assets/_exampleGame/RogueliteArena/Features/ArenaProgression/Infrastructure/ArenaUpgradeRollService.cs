using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using Neo.Tools;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Ролл офферов драфта по контенту и Neoxider ChanceData (упрощённо для примера).</summary>
    public sealed class ArenaUpgradeRollService
    {
        private readonly ArenaProgressionContent _content;
        private readonly ArenaRarityLuckModifier _luck = new();

        public ArenaUpgradeRollService(ArenaProgressionContent content)
        {
            _content = content;
        }

        public bool TryRollOffers(ArenaTeamProgressionState team, HashSet<string> excludeIds, List<ArenaUpgradeOffer> into)
        {
            if (into == null || _content == null || _content.Upgrades == null || _content.Upgrades.Count == 0)
                return false;

            into.Clear();
            var balance = _content.RunBalance;
            int count = team != null ? Mathf.Clamp(team.MaxChoicesOffered, 1, balance != null ? balance.MaxChoiceCount : 5) : 3;

            var pool = new List<ArenaUpgradeDefinition>();
            for (int i = 0; i < _content.Upgrades.Count; i++)
            {
                var u = _content.Upgrades[i];
                if (u == null)
                    continue;
                if (excludeIds != null && excludeIds.Contains(u.Id))
                    continue;
                pool.Add(u);
            }

            if (pool.Count == 0)
                return false;

            ChanceManager rarityCopy = null;
            if (_content.RarityRoll != null)
            {
                rarityCopy = CloneManager(_content.RarityRoll.Manager);
                float luck = team != null ? Mathf.Clamp01(team.SessionLevel * 0.02f) : 0f;
                _luck.Apply(rarityCopy, luck);
            }

            var used = new HashSet<string>();
            for (int n = 0; n < count && pool.Count > 0; n++)
            {
                int idx = Random.Range(0, pool.Count);
                var def = pool[idx];
                pool.RemoveAt(idx);

                if (!used.Add(def.Id))
                {
                    n--;
                    continue;
                }

                var rarity = RollRarity(def, rarityCopy);
                float mult = balance != null ? balance.GetStatMultiplier(rarity) : 1f;
                into.Add(new ArenaUpgradeOffer(def, rarity, mult));
            }

            return into.Count > 0;
        }

        private static ArenaRarity RollRarity(ArenaUpgradeDefinition def, ChanceManager rarityCopy)
        {
            if (rarityCopy == null || rarityCopy.Count <= 0)
                return def.Rarity;
            int id = rarityCopy.GetChanceId();
            return id >= 0 && id <= (int)ArenaRarity.Legendary ? (ArenaRarity)id : def.Rarity;
        }

        private static ChanceManager CloneManager(ChanceManager src)
        {
            var dst = new ChanceManager();
            if (src == null)
                return dst;
            for (int i = 0; i < src.Count; i++)
                dst.AddChance(src.GetChanceValue(i));
            dst.Sanitize();
            return dst;
        }
    }
}
