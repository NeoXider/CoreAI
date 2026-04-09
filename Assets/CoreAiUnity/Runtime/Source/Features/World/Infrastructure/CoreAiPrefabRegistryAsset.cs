using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Реестр префабов, доступных для безопасного спавна из Lua через CoreAI.
    /// Ключи: GUID-строка (желательно) и удобное имя (опционально).
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/World/Prefab Registry", fileName = "CoreAiPrefabRegistry")]
    public sealed class CoreAiPrefabRegistryAsset : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Стабильный ключ. Рекомендуется GUID (строка).")]
            public string Key = "";

            [Tooltip("Альтернативный ключ по имени (для удобства).")]
            public string Name = "";

            public GameObject Prefab;
        }

        [SerializeField] private List<Entry> entries = new();

        private readonly Dictionary<string, GameObject> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _byName = new(StringComparer.Ordinal);
        private bool _built;

        public bool TryResolve(string keyOrName, out GameObject prefab)
        {
            prefab = null;
            if (string.IsNullOrWhiteSpace(keyOrName))
            {
                return false;
            }

            EnsureBuilt();
            string k = keyOrName.Trim();
            if (_byKey.TryGetValue(k, out prefab))
            {
                return prefab != null;
            }

            if (_byName.TryGetValue(k, out prefab))
            {
                return prefab != null;
            }

            return false;
        }

        private void EnsureBuilt()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            _byKey.Clear();
            _byName.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e == null || e.Prefab == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(e.Key))
                {
                    _byKey[e.Key.Trim()] = e.Prefab;
                }

                if (!string.IsNullOrWhiteSpace(e.Name))
                {
                    _byName[e.Name.Trim()] = e.Prefab;
                }
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (entries == null) return;
            
            bool changed = false;
            foreach (var entry in entries)
            {
                if (entry.Prefab != null)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name))
                    {
                        entry.Name = entry.Prefab.name;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Key))
                    {
                        string path = UnityEditor.AssetDatabase.GetAssetPath(entry.Prefab);
                        if (!string.IsNullOrEmpty(path))
                        {
                            entry.Key = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                            changed = true;
                        }
                    }
                }
            }
            
            if (changed)
            {
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }
#endif
    }
}