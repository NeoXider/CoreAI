using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CoreAI.Infrastructure.Logging;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    public sealed class WorldStateManager : IWorldStateManager, IDisposable
    {
        private const string SaveFileName = "world_state.json";
        private const string FormatVersion = "1.1";

        /// <summary>
        /// Default periodic auto-save interval, shared by the manager's always-on loop and by
        /// <c>WorldStateAutoSaveHook</c>'s optional override.
        /// </summary>
        public const float DefaultAutoSaveIntervalSeconds = 60f;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        /// <summary>On WebGL pushes the in-memory IDBFS tree into IndexedDB after a save so it survives a reload/tab close.</summary>
        private static void PersistFsForWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }

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
        private CancellationTokenSource _autoSaveCts;

        // Objects whose prefabKey could not be resolved on the last load (e.g. a prefab not yet
        // registered). Retained in memory and re-written on every Save() so a temporarily missing
        // prefab never permanently deletes the object from the snapshot.
        private List<ObjectData> _unresolvedObjects = new();

        public bool HasSavedState => File.Exists(_saveFilePath);

        public event Action StateReset;

        public bool WorldRestoreCompleted { get; private set; }

        public event Action RestoreCompleted;

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

            // The restore attempt above is fully synchronous (TryLoad spawns everything before
            // returning), so by this point startup restore is done either way. Anything that spawns
            // its own objects on startup (e.g. Lua mod rehydrate, see CoreAiModsInstaller) must wait
            // for this before running — see WORLD_COMMANDS.md §7.
            WorldRestoreCompleted = true;
            RestoreCompleted?.Invoke();

            // Always-on crash protection: previously this only ran in the Hub demo scene via the
            // optional WorldStateAutoSaveHook MonoBehaviour, so every other scene only persisted on a
            // clean Application.quitting. Starting it here covers every scene that wires WorldStateManager.
            if (Application.isPlaying)
            {
                StartAutoSave(DefaultAutoSaveIntervalSeconds);
            }
        }

        /// <summary>
        /// (Re)starts the periodic auto-save loop at the given interval. Safe to call again to change
        /// the interval (e.g. from <c>WorldStateAutoSaveHook</c>'s optional override) — the previous
        /// loop is cancelled first. Passing an interval &lt;= 0 stops periodic saving.
        /// </summary>
        public void StartAutoSave(float intervalSeconds)
        {
            _autoSaveCts?.Cancel();
            _autoSaveCts = null;

            if (intervalSeconds <= 0f || _disposed)
            {
                return;
            }

            _autoSaveCts = new CancellationTokenSource();
            AutoSaveLoop(intervalSeconds, _autoSaveCts.Token).Forget();
        }

        private async UniTaskVoid AutoSaveLoop(float intervalSeconds, CancellationToken ct)
        {
            int delayMs = Mathf.Max(1, Mathf.RoundToInt(intervalSeconds * 1000f));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(delayMs, cancellationToken: ct);
                    Save();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
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
                FindObjectsInactive.Include, FindObjectsSortMode.None);

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

                // Prefer the parent's persistentId (stable across renames and duplicate names);
                // fall back to the parent's name only when the parent isn't itself a tracked
                // world object (e.g. a static scene root).
                string parentValue = "";
                if (t.parent != null)
                {
                    WorldObjectComponent parentTag = t.parent.GetComponent<WorldObjectComponent>();
                    parentValue = parentTag != null ? parentTag.persistentId : t.parent.gameObject.name;
                }

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
                    parent = parentValue,
                    active = tag.gameObject.activeSelf,
                    cr = hasColor ? color.r : -1f,
                    cg = hasColor ? color.g : -1f,
                    cb = hasColor ? color.b : -1f,
                    ca = hasColor ? color.a : -1f
                });
            }

            // Re-append objects whose prefab could not be resolved on the last load, so the
            // pending fix doesn't disappear from the snapshot just because it isn't in the scene.
            if (_unresolvedObjects.Count > 0)
            {
                HashSet<string> savedIds = new();
                foreach (ObjectData saved in objects)
                {
                    savedIds.Add(saved.id);
                }

                foreach (ObjectData retained in _unresolvedObjects)
                {
                    if (!savedIds.Contains(retained.id))
                    {
                        objects.Add(retained);
                    }
                }
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

                PersistFsForWebGl();

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

            if (data == null)
            {
                return false;
            }

            // A snapshot with an empty objects array is a valid state (the world was emptied and
            // saved as such) — it must still clean-slate the scene, not be treated as "no data".
            ObjectData[] snapshotObjects = data.objects ?? Array.Empty<ObjectData>();

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
            Dictionary<string, GameObject> idToGo = new(snapshotObjects.Length);
            List<ObjectData> unresolved = new();

            for (int i = 0; i < snapshotObjects.Length; i++)
            {
                ObjectData obj = snapshotObjects[i];
                if (string.IsNullOrEmpty(obj.prefabKey))
                {
                    failed++;
                    unresolved.Add(obj);
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
                        unresolved.Add(obj);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(GameLogFeature.Core,
                        $"[WorldState] Failed to spawn '{obj.name}' ({obj.prefabKey}): {ex.Message}");
                    failed++;
                    unresolved.Add(obj);
                }
            }

            _unresolvedObjects = unresolved;
            if (unresolved.Count > 0)
            {
                _logger.LogWarning(GameLogFeature.Core,
                    "[WorldState] Retained unresolved object(s), will retry on next load: " +
                    string.Join(", ", unresolved.ConvertAll(o => o.id)));
            }

            for (int i = 0; i < snapshotObjects.Length; i++)
            {
                ObjectData obj = snapshotObjects[i];
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
            return true;
        }

        public void Reset()
        {
            if (_disposed)
            {
                return;
            }

            DestroyAllWorldObjects();

            // Otherwise a later Save() would re-append entries from a prefab that went missing
            // before this Reset, resurrecting them into the fresh snapshot.
            _unresolvedObjects.Clear();

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
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < allTags.Length; i++)
            {
                if (allTags[i] != null && allTags[i].gameObject != null)
                {
                    GameObject go = allTags[i].gameObject;

                    // Object.Destroy() only takes effect at the end of the frame, but the load path
                    // spawns replacement instances (same names, possibly same parents) within the
                    // same frame. Detach, deactivate, and rename immediately so GameObject.Find()
                    // and name-based parent resolution can never bind to an instance that is merely
                    // pending destruction. DestroyImmediate() is avoided here since this method also
                    // runs from Reset(), which may be called mid-callback where immediate destruction
                    // of components could be unsafe.
                    go.transform.SetParent(null, true);
                    go.SetActive(false);
                    go.name += "__pending_destroy";
                    UnityEngine.Object.Destroy(go);
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
            _autoSaveCts?.Cancel();
            _autoSaveCts = null;
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