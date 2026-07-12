using System;
using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using Neo.Save;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Serializes meta progression as JSON through <see cref="SaveProvider"/> using the configured persistence key.</summary>
    public sealed class ArenaMetaSaveGateway
    {
        /// <summary>JSON envelope for meta progression; upgrade ids round-trip verbatim regardless of their characters.</summary>
        [Serializable]
        private sealed class MetaSaveDto
        {
            public int ver = 1;
            public int xp;
            public int level = 1;
            public string[] unlocked = Array.Empty<string>();
        }

        private readonly ArenaPersistenceConfig _config;

        public ArenaMetaSaveGateway(ArenaPersistenceConfig config)
        {
            _config = config;
        }

        private string Key => _config != null ? _config.MetaSaveKey : "CoreAI.Arena.Meta.v1";

        public void LoadInto(ArenaMetaProgressionState meta)
        {
            if (meta == null)
            {
                return;
            }

            string raw = SaveProvider.GetString(Key, "");
            if (string.IsNullOrEmpty(raw))
            {
                meta.SetFromSnapshot(0, 1, Array.Empty<string>());
                return;
            }

            try
            {
                // WHY: legacy saves used a '|'-packed string; JSON always starts with '{'.
                if (raw[0] != '{')
                {
                    LoadLegacy(raw, meta);
                    return;
                }

                MetaSaveDto dto = JsonUtility.FromJson<MetaSaveDto>(raw);
                if (dto == null)
                {
                    meta.SetFromSnapshot(0, 1, Array.Empty<string>());
                    return;
                }

                meta.SetFromSnapshot(dto.xp, dto.level, dto.unlocked ?? Array.Empty<string>());
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
            {
                return;
            }

            List<string> ids = new();
            foreach (string id in meta.UnlockedUpgradeIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                }
            }

            MetaSaveDto dto = new()
            {
                ver = _config != null ? _config.SaveSchemaVersion : 1,
                xp = meta.MetaXp,
                level = meta.MetaLevel,
                unlocked = ids.ToArray()
            };

            SaveProvider.SetString(Key, JsonUtility.ToJson(dto));
            SaveProvider.Save();
        }

        private static void LoadLegacy(string raw, ArenaMetaProgressionState meta)
        {
            string[] parts = raw.Split('|');
            int xp = parts.Length > 1 && int.TryParse(parts[1], out int x) ? x : 0;
            int lvl = parts.Length > 2 && int.TryParse(parts[2], out int l) ? l : 1;
            string[] unlocked = Array.Empty<string>();
            if (parts.Length > 3 && !string.IsNullOrEmpty(parts[3]))
            {
                unlocked = parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }

            meta.SetFromSnapshot(xp, lvl, unlocked);
        }
    }
}
