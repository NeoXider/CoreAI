using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Unity-side prefab lookup contract. Implementations may wrap ScriptableObject assets or runtime maps.
    /// </summary>
    public interface ICoreAiPrefabRegistry
    {
        bool TryResolve(string keyOrName, out GameObject prefab);
    }

    /// <summary>
    /// Unity-side prefab catalog contract for read-only prefab key enumeration.
    /// </summary>
    public interface ICoreAiPrefabCatalog
    {
        /// <summary>Lists distinct prefab keys available for read-only discovery.</summary>
        System.Collections.Generic.IReadOnlyList<string> ListPrefabKeys();
    }

    /// <summary>
    /// Stores prefab lookup entries used by CoreAI world command execution.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/World/Prefab Registry", fileName = "CoreAiPrefabRegistry")]
    public sealed class CoreAiPrefabRegistryAsset : ScriptableObject, ICoreAiPrefabRegistry, ICoreAiPrefabCatalog
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Stable key. A GUID string is recommended.")]
            public string Key = "";

            [Tooltip("Optional human-readable key.")]
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

        /// <inheritdoc />
        public System.Collections.Generic.IReadOnlyList<string> ListPrefabKeys()
        {
            EnsureBuilt();
            HashSet<string> keys = new(StringComparer.Ordinal);
            List<string> result = new();

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || entry.Prefab == null)
                {
                    continue;
                }

                string key = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name.Trim() : entry.Key?.Trim();
                if (string.IsNullOrEmpty(key) || !keys.Add(key))
                {
                    continue;
                }

                result.Add(key);
            }

            return result;
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
            if (entries == null)
            {
                return;
            }

            bool changed = false;
            foreach (Entry entry in entries)
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
