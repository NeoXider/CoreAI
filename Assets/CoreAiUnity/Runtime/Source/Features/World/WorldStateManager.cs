using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    public sealed class WorldStateManager : IWorldStateManager, IDisposable
    {
        private const string SaveFileName = "world_state.json";
        private const string FormatVersion = "1.1";

        private static readonly Color NoColor = new(-1f, -1f, -1f, -1f);

        [Serializable]
        private sealed class ObjectData
        {
            public string id;
            public string prefabKey;
            public string name;
            public float px, py, pz;
            public float rx, ry, rz;
            public float sx, sy, sz;
            public string parent;
            public bool active;
            public float cr, cg, cb, ca;
        }

        [Serializable]
        private sealed class SaveData
        {
            public string version;
            public string timestamp;
            public string scene;
            public ObjectData[] objects;
        }

        private readonly IGameLogger _logger;
        private readonly ICoreAiPrefabRegistry _prefabRegistry;
        private readonly bool _allowPrimitives;

        private string _saveFilePath;
        private bool _disposed;

        public bool HasSavedState => File.Exists(_saveFilePath);

        public event Action StateReset;

        public WorldStateManager(
            IGameLogger logger,
            ICoreAiPrefabRegistry prefabRegistry = null,
            bool allowPrimitives = true)
        {
            _logger = logger;
            _prefabRegistry = prefabRegistry;
            _allowPrimitives = allowPrimitives;
            _saveFilePath = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.WorldState,
                SaveFileName);
        }

        public void Initialize()
        {
            Application.quitting += OnApplicationQuitting;

            if (HasSavedState)
            {
                _logger.LogInfo(GameLogFeature.Core,
                    $"[WorldState] Save file found, auto-loading...");
                TryLoad();
            }
        }

        public void Save()
        {
            if (_disposed)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            WorldObjectComponent[] allTags = UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(
                FindObjectsSortMode.None);

            if (allTags.Length == 0)
            {
                _logger.LogInfo(GameLogFeature.Core,
                    "[WorldState] No world objects to save.");
                return;
            }

            List<ObjectData> objects = new(allTags.Length);
            for (int i = 0; i < allTags.Length; i++)
            {
                WorldObjectComponent tag = allTags[i];
                if (tag == null || tag.gameObject == null)
                {
                    continue;
                }

                Transform t = tag.transform;
                Color color = ReadColor(tag.gameObject);
                bool hasColor = color.r >= 0f;
                objects.Add(new ObjectData
                {
                    id = tag.persistentId,
                    prefabKey = tag.prefabKey,
                    name = tag.gameObject.name,
                    px = t.position.x,
                    py = t.position.y,
                    pz = t.position.z,
                    rx = t.eulerAngles.x,
                    ry = t.eulerAngles.y,
                    rz = t.eulerAngles.z,
                    sx = t.localScale.x,
                    sy = t.localScale.y,
                    sz = t.localScale.z,
                    parent = t.parent != null ? t.parent.gameObject.name : "",
                    active = tag.gameObject.activeSelf,
                    cr = hasColor ? color.r : -1f,
                    cg = hasColor ? color.g : -1f,
                    cb = hasColor ? color.b : -1f,
                    ca = hasColor ? color.a : -1f
                });
            }

            SaveData data = new()
            {
                version = FormatVersion,
                timestamp = DateTime.UtcNow.ToString("O"),
                scene = sceneName,
                objects = objects.ToArray()
            };

            try
            {
                string dir = Path.GetDirectoryName(_saveFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonUtility.ToJson(data, true);
                string tmpPath = _saveFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                if (File.Exists(_saveFilePath))
                {
                    File.Replace(tmpPath, _saveFilePath, null);
                }
                else
                {
                    File.Move(tmpPath, _saveFilePath);
                }

                _logger.LogInfo(GameLogFeature.Core,
                    $"[WorldState] Saved {objects.Count} objects.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Core,
                    $"[WorldState] Save failed: {ex.Message}");
            }
        }

        public bool TryLoad(string sceneName = null)
        {
            if (_disposed)
            {
                return false;
            }

            if (!File.Exists(_saveFilePath))
            {
                return false;
            }

            SaveData data;
            try
            {
                string json = File.ReadAllText(_saveFilePath);
                data = JsonUtility.FromJson<SaveData>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Core,
                    $"[WorldState] Load parse failed: {ex.Message}");
                return false;
            }

            if (data?.objects == null || data.objects.Length == 0)
            {
                return false;
            }

            string currentScene = SceneManager.GetActiveScene().name;
            string targetScene = sceneName ?? data.scene;
            if (!string.IsNullOrEmpty(targetScene) &&
                !string.IsNullOrEmpty(currentScene) &&
                !string.Equals(currentScene, targetScene, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInfo(GameLogFeature.Core,
                    $"[WorldState] Scene mismatch (current='{currentScene}', saved='{targetScene}'), skipping load.");
                return false;
            }

            // Clean slate: destroy any existing world objects (e.g. from a previous load or
            // editor-placed objects) so we never accumulate duplicate persistentIds.
            DestroyAllWorldObjects();

            int spawned = 0;
            int failed = 0;
            bool hasColor = string.Compare(data.version, "1.1", StringComparison.Ordinal) >= 0;
            Dictionary<string, GameObject> idToGo = new(data.objects.Length);

            for (int i = 0; i < data.objects.Length; i++)
            {
                ObjectData obj = data.objects[i];
                if (string.IsNullOrEmpty(obj.prefabKey))
                {
                    failed++;
                    continue;
                }

                try
                {
                    GameObject go = SpawnFromSnapshot(obj, hasColor);
                    if (go != null)
                    {
                        idToGo[obj.id] = go;
                        spawned++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(GameLogFeature.Core,
                        $"[WorldState] Failed to spawn '{obj.name}' ({obj.prefabKey}): {ex.Message}");
                    failed++;
                }
            }

            for (int i = 0; i < data.objects.Length; i++)
            {
                ObjectData obj = data.objects[i];
                if (string.IsNullOrEmpty(obj.parent))
                {
                    continue;
                }

                if (idToGo.TryGetValue(obj.id, out GameObject child) &&
                    idToGo.TryGetValue(obj.parent, out GameObject parent))
                {
                    child.transform.SetParent(parent.transform, true);
                }
                else
                {
                    GameObject found = GameObject.Find(obj.parent);
                    if (found != null && idToGo.TryGetValue(obj.id, out child))
                    {
                        child.transform.SetParent(found.transform, true);
                    }
                }
            }

            _logger.LogInfo(GameLogFeature.Core,
                $"[WorldState] Loaded {spawned} objects ({failed} failed).");
            return spawned > 0;
        }

        public void Reset()
        {
            if (_disposed)
            {
                return;
            }

            DestroyAllWorldObjects();

            try
            {
                if (File.Exists(_saveFilePath))
                {
                    File.Delete(_saveFilePath);
                }

                string tmpPath = _saveFilePath + ".tmp";
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Core,
                    $"[WorldState] Reset: failed to delete save file: {ex.Message}");
            }

            StateReset?.Invoke();
            _logger.LogInfo(GameLogFeature.Core,
                "[WorldState] Reset: all world objects destroyed, save file deleted.");
        }

        private static void DestroyAllWorldObjects()
        {
            WorldObjectComponent[] allTags = UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(
                FindObjectsSortMode.None);
            for (int i = 0; i < allTags.Length; i++)
            {
                if (allTags[i] != null && allTags[i].gameObject != null)
                {
                    UnityEngine.Object.Destroy(allTags[i].gameObject);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Application.quitting -= OnApplicationQuitting;
        }

        private static Color ReadColor(GameObject go)
        {
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return NoColor;
            }

            MaterialPropertyBlock mpb = new();
            renderer.GetPropertyBlock(mpb);
            if (mpb.HasColor("_Color"))
            {
                return mpb.GetColor("_Color");
            }

            if (mpb.HasColor("_BaseColor"))
            {
                return mpb.GetColor("_BaseColor");
            }

            return NoColor;
        }

        private static void ApplyColor(GameObject go, Color color)
        {
            if (go == null || color.r < 0f)
            {
                return;
            }

            Renderer[] renderers = go.GetComponents<Renderer>();
            if (renderers.Length == 0)
            {
                return;
            }

            MaterialPropertyBlock mpb = new();
            foreach (Renderer renderer in renderers)
            {
                renderer.GetPropertyBlock(mpb);
                mpb.SetColor("_Color", color);
                mpb.SetColor("_BaseColor", color);
                renderer.SetPropertyBlock(mpb);
            }
        }

        private void OnApplicationQuitting()
        {
            Save();
        }

        private GameObject SpawnFromSnapshot(ObjectData obj, bool hasColor = false)
        {
            Vector3 pos = new(obj.px, obj.py, obj.pz);
            Quaternion rot = Quaternion.Euler(obj.rx, obj.ry, obj.rz);
            Vector3 scale = new(obj.sx, obj.sy, obj.sz);

            string key = obj.prefabKey;
            GameObject go = null;

            if (_prefabRegistry != null && _prefabRegistry.TryResolve(key, out GameObject prefab) && prefab != null)
            {
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            }
            else if (_allowPrimitives && CoreAiPrimitiveFactory.IsPrimitiveKey(key))
            {
                go = CoreAiPrimitiveFactory.Create(key);
                if (go != null)
                {
                    go.transform.position = pos;
                    go.transform.rotation = rot;
                }
            }

            if (go == null)
            {
                return null;
            }

            go.name = obj.name;
            go.transform.localScale = scale;
            go.SetActive(obj.active);

            WorldObjectComponent tag = go.AddComponent<WorldObjectComponent>();
            tag.persistentId = obj.id;
            tag.prefabKey = obj.prefabKey;

            if (hasColor && obj.cr >= 0f)
            {
                ApplyColor(go, new Color(obj.cr, obj.cg, obj.cb, obj.ca));
            }

            return go;
        }
    }
}
