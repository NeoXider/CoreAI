using System;
using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using Neo.Save;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Сериализация меты в <see cref="SaveProvider"/> (строка под ключом из <see cref="ArenaPersistenceConfig"/>).</summary>
    public sealed class ArenaMetaSaveGateway
    {
        private readonly ArenaPersistenceConfig _config;

        public ArenaMetaSaveGateway(ArenaPersistenceConfig config)
        {
            _config = config;
        }

        private string Key => _config != null ? _config.MetaSaveKey : "CoreAI.Arena.Meta.v1";

        public void LoadInto(ArenaMetaProgressionState meta)
        {
            if (meta == null)
                return;
            var raw = SaveProvider.GetString(Key, "");
            if (string.IsNullOrEmpty(raw))
            {
                meta.SetFromSnapshot(0, 1, Array.Empty<string>());
                return;
            }

            try
            {
                var parts = raw.Split('|');
                int xp = parts.Length > 1 && int.TryParse(parts[1], out var x) ? x : 0;
                int lvl = parts.Length > 2 && int.TryParse(parts[2], out var l) ? l : 1;
                var unlocked = Array.Empty<string>();
                if (parts.Length > 3 && !string.IsNullOrEmpty(parts[3]))
                    unlocked = parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                meta.SetFromSnapshot(xp, lvl, unlocked);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ArenaMetaSaveGateway] Load failed, reset meta. {e.Message}");
                meta.SetFromSnapshot(0, 1, Array.Empty<string>());
            }
        }

        public void Save(ArenaMetaProgressionState meta)
        {
            if (meta == null)
                return;
            int ver = _config != null ? _config.SaveSchemaVersion : 1;
            var ids = new List<string>();
            foreach (var id in meta.UnlockedUpgradeIds)
            {
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }

            var raw = $"{ver}|{meta.MetaXp}|{meta.MetaLevel}|{string.Join(",", ids)}";
            SaveProvider.SetString(Key, raw);
            SaveProvider.Save();
        }
    }
}
