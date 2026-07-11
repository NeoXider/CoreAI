using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Applies validated CoreAI world commands to Unity scene objects.</summary>
    public sealed class CoreAiWorldCommandExecutor : ICoreAiWorldCommandExecutor
    {
        /// <summary>Maximum entries accepted by a single <c>spawn_batch</c> command.</summary>
        public const int MaxSpawnBatchSize = 100;

        /// <summary>Maximum prefab/primitive keys listed in an unknown-prefabKey error message.</summary>
        private const int MaxKeysInErrorMessage = 20;

        private readonly IGameLogger _logger;
        private readonly ICoreAiPrefabRegistry _prefabRegistry;
        private readonly HashSet<string> _allowedScenes;
        private readonly bool _allowPrimitives;
        private readonly List<ICoreAiCustomWorldCommandHandler> _customHandlers = new();
        private MaterialPropertyBlock _sharedColorMpb;

        /// <summary>
        /// Opt-in collision-avoidance for <c>spawn</c>. OFF by default: the model's explicit coordinates are
        /// authoritative, so adjacent/stacked/on-ground placements are honored. Set to true only when a caller
        /// wants <see cref="ValidateSpawnPosition"/> to reject positions overlapping non-trigger colliders.
        /// </summary>
        public bool RejectOverlappingSpawns { get; set; }

        /// <param name="allowedScenes">
        /// Optional scene whitelist for <c>load_scene</c>. When null/empty any Build-Settings scene stays
        /// loadable (legacy). Enforced here (not only on the Lua binding) so every path — the native
        /// <c>world_command</c> tool and the Lua <c>coreai_world_load_scene</c> binding alike — honours the
        /// same restriction, closing the gap where the native tool bypassed the Lua-only whitelist.
        /// </param>
        /// <param name="allowPrimitives">
        /// When true (default), <c>spawn</c> falls back to creating a built-in Unity primitive
        /// (cube/sphere/cylinder/capsule/plane/empty via <see cref="CoreAiPrimitiveFactory"/>) when the
        /// requested <c>prefabKey</c> is not a registered prefab, so the tool works without a prefab registry.
        /// </param>
        public CoreAiWorldCommandExecutor(
            IGameLogger logger,
            ICoreAiPrefabRegistry prefabRegistry = null,
            IEnumerable<string> allowedScenes = null,
            bool allowPrimitives = true)
        {
            _logger = logger;
            _prefabRegistry = prefabRegistry;
            _allowPrimitives = allowPrimitives;
            if (allowedScenes != null)
            {
                HashSet<string> set = new(StringComparer.Ordinal);
                foreach (string scene in allowedScenes)
                {
                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        set.Add(scene.Trim());
                    }
                }

                _allowedScenes = set.Count > 0 ? set : null;
            }
        }

        /// <summary>Registers a game-specific world-command handler (see <see cref="ICoreAiCustomWorldCommandHandler"/>).</summary>
        public void RegisterCustomHandler(ICoreAiCustomWorldCommandHandler handler)
        {
            if (handler == null || _customHandlers.Contains(handler))
            {
                return;
            }

            _customHandlers.Add(handler);
        }

        public bool TryExecute(ApplyAiGameCommand cmd)
        {
            if (cmd == null || !string.Equals(cmd.CommandTypeId, AiGameCommandTypeIds.WorldCommand,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string json = cmd.JsonPayload ?? "";
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            CoreAiWorldCommandEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] bad json: {ex.Message}");
                return false;
            }

            if (env == null || string.IsNullOrWhiteSpace(env.action))
            {
                return false;
            }

            // Clear per-call diagnostics up front so a stale value from a previous command never leaks
            // into this command's result (LastErrorMessage/LastSpawnBatchResult are consumed immediately
            // by the caller after a failed/batch call, mirroring LastListedObjects/LastListedAnimations).
            LastErrorMessage = "";

            switch (env.action.Trim())
            {
                case "spawn":
                    return TrySpawn(env);
                case "spawn_batch":
                    return TrySpawnBatch(env);
                case "list_prefabs":
                    return TryListPrefabs();
                case "move":
                    return TryMove(env);
                case "rotate":
                    return TryRotate(env);
                case "change":
                case "set_transform":
                    return TryChange(env);
                case "destroy":
                    return TryDestroy(env);
                case "set_active":
                    return TrySetActive(env);
                case "parent":
                    return TryParent(env);
                case "set_scale":
                    return TrySetScale(env);
                case "set_color":
                    return TrySetColor(env);
                case "load_scene":
                    return TryLoadScene(env);
                case "reload_scene":
                    return TryReloadScene();
                case "list_objects":
                    return TryListObjects(env);
                case "play_animation":
                    return TryPlayAnimation(env);
                case "list_animations":
                    return TryListAnimations(env);
                case "play_sound":
                    return TryPlaySound(env);
                case "stop_animation":
                    return TryStopAnimation(env);
                case "set_volume":
                    return TrySetVolume(env);
                case "show_text":
                    return TryShowText(env);
                case "hide_panel":
                    return TryHidePanel(env);
                case "apply_force":
                    return TryApplyForce(env);
                case "set_velocity":
                    return TrySetVelocity(env);
                default:
                    for (int i = 0; i < _customHandlers.Count; i++)
                    {
                        ICoreAiCustomWorldCommandHandler handler = _customHandlers[i];
                        if (handler != null && handler.CanHandle(env.action) && handler.TryExecute(env))
                        {
                            return true;
                        }
                    }

                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] unknown action '{env.action}'");
                    return false;
            }
        }

        private bool TrySpawn(CoreAiWorldCommandEnvelope env)
        {
            GameObject spawned = TrySpawnCore(env, out string error);
            if (spawned != null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(error))
            {
                LastErrorMessage = error;
            }

            return false;
        }

        /// <summary>
        /// Shared spawn implementation used by both <c>spawn</c> and each entry of <c>spawn_batch</c>.
        /// Returns the spawned instance, or null with <paramref name="error"/> describing why (e.g. an
        /// unknown prefabKey lists the available keys so the model can self-correct in one round).
        /// </summary>
        private GameObject TrySpawnCore(CoreAiWorldCommandEnvelope env, out string error)
        {
            error = "";
            string key = (env.prefabKeyOrName ?? "").Trim();

            string targetName = (env.targetName ?? "").Trim();
            if (string.IsNullOrEmpty(targetName))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] spawn missing targetName");
                return null;
            }

            Vector3 pos = new(env.x, env.y, env.z);

            // The model's explicit coordinates are authoritative by default: adjacent/stacked/on-the-ground
            // placements (castle blocks, coins on a floor) are legitimate, and the PhysX broadphase is stale in
            // edit-mode / mid-batch. So the overlap rejection is opt-in (off by default) — only honored when the
            // request explicitly asks to avoid collisions. The check remains available for callers that want it.
            if (RejectOverlappingSpawns && !ValidateSpawnPosition(pos, 0.5f))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] spawn blocked: position ({pos.x},{pos.y},{pos.z}) overlaps existing colliders");
                error = $"spawn blocked: position ({pos.x},{pos.y},{pos.z}) overlaps existing colliders.";
                return null;
            }

            // Rotation (fx/fy/fz) and scale can be set during spawn for convenience.
            Quaternion rotation = Quaternion.identity;
            if (env.fx != 0f || env.fy != 0f || env.fz != 0f)
            {
                rotation = Quaternion.Euler(env.fx, env.fy, env.fz);
            }

            Vector3 scale = ResolveScale(env);

            // Registered prefab takes precedence; otherwise fall back to a built-in primitive so the world
            // tool is usable without any prefab registry assigned.
            GameObject spawned = null;

            if (_prefabRegistry != null && _prefabRegistry.TryResolve(key, out GameObject prefab) && prefab != null)
            {
                spawned = UnityEngine.Object.Instantiate(prefab, pos, rotation);
                spawned.name = targetName;
                if (scale != Vector3.one)
                {
                    spawned.transform.localScale = scale;
                }
            }
            else if (_allowPrimitives && CoreAiPrimitiveFactory.IsPrimitiveKey(key))
            {
                spawned = CoreAiPrimitiveFactory.Create(key);
                spawned.name = targetName;
                spawned.transform.position = pos;
                spawned.transform.rotation = rotation;
                if (scale != Vector3.one)
                {
                    spawned.transform.localScale = scale;
                }
            }

            if (spawned != null)
            {
                TryParentSpawned(spawned, env.stringValue, env.worldPositionStays);

                if (spawned.GetComponent<WorldObjectComponent>() == null)
                {
                    WorldObjectComponent tag = spawned.AddComponent<WorldObjectComponent>();
                    tag.persistentId = Guid.NewGuid().ToString("N");
                    tag.prefabKey = key;
                }

                return spawned;
            }

            if (_prefabRegistry == null && !_allowPrimitives)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] prefab registry not assigned");
                error = "spawn failed: no prefab registry is assigned and primitives are disabled.";
                return null;
            }

            error = BuildUnknownPrefabKeyMessage(key);
            _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] {error}");
            return null;
        }

        /// <summary>
        /// Builds a self-correcting error listing available primitive and registered-prefab keys
        /// (truncated to <see cref="MaxKeysInErrorMessage"/>) for an unresolvable spawn prefabKey.
        /// </summary>
        private string BuildUnknownPrefabKeyMessage(string key)
        {
            if (!_allowPrimitives)
            {
                return $"prefab not found: '{key}'.";
            }

            string message = $"Unknown prefabKey '{key}'. Available primitives: {CoreAiPrimitiveFactory.SupportedKeys}.";

            if (_prefabRegistry is ICoreAiPrefabCatalog catalog)
            {
                IReadOnlyList<string> keys = catalog.ListPrefabKeys();
                if (keys.Count > 0)
                {
                    int shown = Math.Min(keys.Count, MaxKeysInErrorMessage);
                    string keyList = string.Join(", ", keys.Take(shown));
                    string more = keys.Count > shown ? $" (+{keys.Count - shown} more)" : "";
                    message += $" Registered prefabs: {keyList}{more}.";
                }
            }

            return message;
        }

        /// <summary>
        /// Spawns every entry in <c>env.items</c> (up to <see cref="MaxSpawnBatchSize"/>), reusing
        /// <see cref="TrySpawnCore"/> per item. Atomicity is intentionally simple: a per-item failure
        /// (unknown prefabKey, missing name) is counted and skipped, not rolled back, so one bad entry
        /// never discards the objects already spawned earlier in the same batch.
        /// </summary>
        private bool TrySpawnBatch(CoreAiWorldCommandEnvelope env)
        {
            CoreAiSpawnBatchItem[] items = env.items ?? System.Array.Empty<CoreAiSpawnBatchItem>();
            int count = Math.Min(items.Length, MaxSpawnBatchSize);
            string namePrefix = (env.targetName ?? "").Trim();
            string defaultPrefab = (env.prefabKeyOrName ?? "").Trim();
            Vector3 defaultEuler = new(env.fx, env.fy, env.fz);
            Vector3 defaultNonUniformScale = new(env.scaleX, env.scaleY, env.scaleZ);

            CoreAiSpawnBatchResult result = new();

            for (int i = 0; i < count; i++)
            {
                CoreAiSpawnBatchItem item = items[i] ?? new CoreAiSpawnBatchItem();

                string prefab = !string.IsNullOrWhiteSpace(item.prefabKey) ? item.prefabKey.Trim() : defaultPrefab;
                string name = !string.IsNullOrWhiteSpace(item.name)
                    ? item.name.Trim()
                    : !string.IsNullOrEmpty(namePrefix)
                        ? $"{namePrefix}_{i + 1}"
                        : $"{prefab}_{i + 1}";
                string parent = !string.IsNullOrWhiteSpace(item.parent) ? item.parent : env.stringValue;
                bool worldPositionStays = item.hasWorldPositionStays
                    ? item.worldPositionStays
                    : env.worldPositionStays;

                bool itemHasRotation = item.rx != 0f || item.ry != 0f || item.rz != 0f;
                Vector3 euler = itemHasRotation ? new Vector3(item.rx, item.ry, item.rz) : defaultEuler;

                bool itemHasNonUniformScale = item.scaleX > 0f || item.scaleY > 0f || item.scaleZ > 0f;
                Vector3 nonUniformScale = itemHasNonUniformScale
                    ? new Vector3(item.scaleX, item.scaleY, item.scaleZ)
                    : defaultNonUniformScale;
                float uniformScale = item.scale > 0f ? item.scale : env.floatValue;

                CoreAiWorldCommandEnvelope itemEnv = CoreAiWorldCommandEnvelope.Spawn(
                    prefab, name, new Vector3(item.x, item.y, item.z), euler, uniformScale, nonUniformScale);
                itemEnv.stringValue = parent ?? "";
                itemEnv.worldPositionStays = worldPositionStays;

                GameObject spawned = TrySpawnCore(itemEnv, out _);
                if (spawned == null)
                {
                    result.Failed++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.color))
                {
                    TrySetColorOnGameObject(spawned, item.color);
                }

                result.Spawned++;
                if (result.Names.Count < 10)
                {
                    result.Names.Add(spawned.name);
                }
            }

            LastSpawnBatchResult = result;
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] spawn_batch: {result.Spawned} spawned, {result.Failed} failed (of {count} requested)");
            return true;
        }

        private bool TryListPrefabs()
        {
            List<string> keys = new();
            if (_prefabRegistry is ICoreAiPrefabCatalog catalog)
            {
                keys.AddRange(catalog.ListPrefabKeys());
            }

            LastListedPrefabKeys = keys;
            _logger.LogInfo(GameLogFeature.MessagePipe, $"[World] list_prefabs: {keys.Count} registered prefab keys");
            return true;
        }

        /// <summary>
        /// Checks whether a spawn position is free of blocking colliders.
        /// </summary>
        private bool ValidateSpawnPosition(Vector3 position, float checkRadius)
        {
            Collider[] overlaps = Physics.OverlapSphere(position, checkRadius);
            // Iterate through the data sequence.
            foreach (Collider col in overlaps)
            {
                if (!col.isTrigger)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryApplyForce(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] apply_force: object not found (name='{env.targetName}')");
                return false;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] apply_force: no Rigidbody on '{go.name}'");
                return false;
            }

            Vector3 force = new(env.fx, env.fy, env.fz);
            rb.AddForce(force, ForceMode.Impulse);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] apply_force: ({force.x},{force.y},{force.z}) on '{go.name}'");
            return true;
        }

        private bool TrySetVelocity(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_velocity: object not found (name='{env.targetName}')");
                return false;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_velocity: no Rigidbody on '{go.name}'");
                return false;
            }

            Vector3 velocity = new(env.fx, env.fy, env.fz);
            rb.linearVelocity = velocity;
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] set_velocity: ({velocity.x},{velocity.y},{velocity.z}) on '{go.name}'");
            return true;
        }

        private bool TryStopAnimation(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: object not found (name='{env.targetName}')");
                return false;
            }

            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.enabled)
            {
                animator.StopPlayback();
                animator.speed = 0f;
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: stopped on '{go.name}' (Animator)");
                return true;
            }

            Animation animation = go.GetComponent<Animation>();
            if (animation != null && animation.enabled)
            {
                animation.Stop();
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: stopped on '{go.name}' (Animation)");
                return true;
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] stop_animation: no Animator/Animation on '{go.name}'");
            return false;
        }

        private bool TrySetVolume(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_volume: object not found (name='{env.targetName}')");
                return false;
            }

            AudioSource[] sources = go.GetComponents<AudioSource>();
            if (sources == null || sources.Length == 0)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_volume: no AudioSource on '{go.name}'");
                return false;
            }

            float volume = Mathf.Clamp01(env.floatValue);
            foreach (AudioSource src in sources)
            {
                src.volume = volume;
            }

            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] set_volume: {volume} on '{go.name}' ({sources.Length} sources)");
            return true;
        }

        private bool TryShowText(CoreAiWorldCommandEnvelope env)
        {
            if (string.IsNullOrEmpty(env.stringValue))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] show_text: text is empty");
                return false;
            }

            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] show_text: object not found (name='{env.targetName}')");
                return false;
            }

            UnityEngine.UI.Text uiText = go.GetComponent<UnityEngine.UI.Text>();
            if (uiText != null)
            {
                uiText.text = env.stringValue;
                go.SetActive(true);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] show_text: UI.Text set on '{go.name}'");
                return true;
            }

            TextMesh textMesh = go.GetComponent<TextMesh>();
            if (textMesh != null)
            {
                textMesh.text = env.stringValue;
                go.SetActive(true);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] show_text: TextMesh set on '{go.name}'");
                return true;
            }

            TextMesh newMesh = go.AddComponent<TextMesh>();
            newMesh.text = env.stringValue;
            newMesh.fontSize = 24;
            newMesh.characterSize = 0.1f;
            go.SetActive(true);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] show_text: TextMesh created on '{go.name}'");
            return true;
        }

        private bool TryHidePanel(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] hide_panel: object not found (name='{env.targetName}')");
                return false;
            }

            go.SetActive(false);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] hide_panel: '{go.name}' deactivated");
            return true;
        }

        private bool TryMove(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            go.transform.position = new Vector3(env.x, env.y, env.z);
            return true;
        }

        private bool TryRotate(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            go.transform.rotation = Quaternion.Euler(env.fx, env.fy, env.fz);
            return true;
        }

        private bool TryChange(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            if (!IsFiniteScale(env))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_transform: non-finite scale rejected (name='{env.targetName}')");
                return false;
            }

            bool legacySetTransform = string.Equals(env.action, "set_transform", StringComparison.Ordinal);
            bool hasParentCommand = !string.IsNullOrWhiteSpace(env.stringValue);
            bool useLocalSpace = false;
            if (hasParentCommand && !env.worldPositionStays)
            {
                useLocalSpace = TryParentSpawned(go, env.stringValue, false) && go.transform.parent != null;
            }

            if (legacySetTransform || (env.hasPosition && !HasAxisPositionFlags(env)))
            {
                if (useLocalSpace)
                {
                    go.transform.localPosition = new Vector3(env.x, env.y, env.z);
                }
                else
                {
                    go.transform.position = new Vector3(env.x, env.y, env.z);
                }
            }
            else if (env.hasPosition || HasAxisPositionFlags(env))
            {
                Vector3 pos = useLocalSpace ? go.transform.localPosition : go.transform.position;
                if (env.hasX)
                {
                    pos.x = env.x;
                }

                if (env.hasY)
                {
                    pos.y = env.y;
                }

                if (env.hasZ)
                {
                    pos.z = env.z;
                }

                if (useLocalSpace)
                {
                    go.transform.localPosition = pos;
                }
                else
                {
                    go.transform.position = pos;
                }
            }

            if (legacySetTransform || (env.hasRotation && !HasAxisRotationFlags(env)))
            {
                if (useLocalSpace)
                {
                    go.transform.localRotation = Quaternion.Euler(env.fx, env.fy, env.fz);
                }
                else
                {
                    go.transform.rotation = Quaternion.Euler(env.fx, env.fy, env.fz);
                }
            }
            else if (env.hasRotation || HasAxisRotationFlags(env))
            {
                Vector3 rot = useLocalSpace ? go.transform.localEulerAngles : go.transform.eulerAngles;
                if (env.hasFx)
                {
                    rot.x = env.fx;
                }

                if (env.hasFy)
                {
                    rot.y = env.fy;
                }

                if (env.hasFz)
                {
                    rot.z = env.fz;
                }

                if (useLocalSpace)
                {
                    go.transform.localRotation = Quaternion.Euler(rot);
                }
                else
                {
                    go.transform.rotation = Quaternion.Euler(rot);
                }
            }

            if (legacySetTransform)
            {
                go.transform.localScale = ResolveScale(env);
            }
            else if (env.hasScale)
            {
                go.transform.localScale = ResolveChangeScale(go.transform.localScale, env);
            }

            if (hasParentCommand && env.worldPositionStays)
            {
                TryParentSpawned(go, env.stringValue, true);
            }
            return true;
        }

        private bool TryParentSpawned(GameObject child, string parentName, bool worldPositionStays = true)
        {
            if (child == null || string.IsNullOrWhiteSpace(parentName))
            {
                return false;
            }

            if (parentName.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                child.transform.SetParent(null, worldPositionStays);
                return true;
            }

            if (!ResolveObject(parentName, out GameObject parent))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] parent: parent not found (name='{parentName}')");
                return false;
            }

            child.transform.SetParent(parent.transform, worldPositionStays);
            return true;
        }

        private bool TryDestroy(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                UnityEngine.Object.Destroy(go);
            }

            return true;
        }

        private bool TryLoadScene(CoreAiWorldCommandEnvelope env)
        {
            string name = (env.sceneName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (_allowedScenes != null && !_allowedScenes.Contains(name))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] load_scene '{name}' rejected: not in the configured scene whitelist.");
                return false;
            }

            // Validate the scene is actually loadable BEFORE calling SceneManager.LoadScene: a scene
            // missing from (or disabled in) Build Settings logs a Unity error and leaves the current
            // scene running, but SceneManager.LoadScene itself has no return value — so without this
            // check the command sink would still report success and the caller could never self-correct.
            if (!IsSceneInBuildSettings(name))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] load_scene '{name}' rejected: scene is not present/enabled in Build Settings.");
                return false;
            }

            SceneManager.LoadScene(name);
            return true;
        }

        /// <summary>Checks whether a scene name is present and enabled in Build Settings.</summary>
        private static bool IsSceneInBuildSettings(string sceneName)
        {
            int count = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < count; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(fileName, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryReloadScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return false;
            }

            SceneManager.LoadScene(scene.name);
            return true;
        }

        private bool TrySetActive(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            go.SetActive(env.boolValue != 0);
            return true;
        }

        private bool TryParent(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject child))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] parent: child not found (name='{env.targetName}')");
                return false;
            }

            string parentName = (env.stringValue ?? "").Trim();
            if (string.IsNullOrEmpty(parentName) ||
                parentName.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                child.transform.SetParent(null, true);
                return true;
            }

            if (!ResolveObject(parentName, out GameObject parent))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] parent: parent not found (name='{env.stringValue}')");
                return false;
            }

            child.transform.SetParent(parent.transform, true);
            return true;
        }

        private bool TrySetScale(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_scale: object not found (name='{env.targetName}')");
                return false;
            }

            if (!IsFiniteScale(env))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_scale: non-finite scale rejected (name='{env.targetName}')");
                return false;
            }

            go.transform.localScale = ResolveScale(env);
            return true;
        }

        private static bool IsFiniteScale(CoreAiWorldCommandEnvelope env)
        {
            return float.IsFinite(env.floatValue) && float.IsFinite(env.scaleX) &&
                   float.IsFinite(env.scaleY) && float.IsFinite(env.scaleZ);
        }

        private static Vector3 ResolveScale(CoreAiWorldCommandEnvelope env)
        {
            float uniform = env.floatValue > 0f ? Mathf.Clamp(env.floatValue, 0.01f, 100f) : 1f;
            if (env.scaleX <= 0f && env.scaleY <= 0f && env.scaleZ <= 0f)
            {
                return Vector3.one * uniform;
            }

            return new Vector3(
                AxisScale(env.scaleX, uniform),
                AxisScale(env.scaleY, uniform),
                AxisScale(env.scaleZ, uniform));
        }

        private static float AxisScale(float axisValue, float fallback)
        {
            return axisValue > 0f ? Mathf.Clamp(axisValue, 0.01f, 100f) : fallback;
        }

        private static Vector3 ResolveChangeScale(Vector3 current, CoreAiWorldCommandEnvelope env)
        {
            Vector3 result = current;
            if (env.floatValue > 0f)
            {
                float uniform = Mathf.Clamp(env.floatValue, 0.01f, 100f);
                result = Vector3.one * uniform;
            }

            if (env.scaleX > 0f)
            {
                result.x = Mathf.Clamp(env.scaleX, 0.01f, 100f);
            }

            if (env.scaleY > 0f)
            {
                result.y = Mathf.Clamp(env.scaleY, 0.01f, 100f);
            }

            if (env.scaleZ > 0f)
            {
                result.z = Mathf.Clamp(env.scaleZ, 0.01f, 100f);
            }

            return result;
        }

        private static bool HasAxisPositionFlags(CoreAiWorldCommandEnvelope env)
        {
            return env.hasX || env.hasY || env.hasZ;
        }

        private static bool HasAxisRotationFlags(CoreAiWorldCommandEnvelope env)
        {
            return env.hasFx || env.hasFy || env.hasFz;
        }

        private bool TrySetColor(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_color: object not found (name='{env.targetName}')");
                return false;
            }

            return TrySetColorOnGameObject(go, env.stringValue);
        }

        /// <summary>Shared coloring logic used by <c>set_color</c> and per-item colors in <c>spawn_batch</c>.</summary>
        private bool TrySetColorOnGameObject(GameObject go, string htmlColor)
        {
            string colorText = NormalizeHtmlColor(htmlColor);
            if (!ColorUtility.TryParseHtmlString(colorText, out Color color))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_color: invalid html color '{htmlColor}'");
                return false;
            }

            int changed = 0;
            Renderer[] renderers = go.GetComponents<Renderer>();
            if (_sharedColorMpb == null)
            {
                _sharedColorMpb = new MaterialPropertyBlock();
            }

            foreach (Renderer renderer in renderers)
            {
                renderer.GetPropertyBlock(_sharedColorMpb);
                _sharedColorMpb.SetColor("_Color", color);
                _sharedColorMpb.SetColor("_BaseColor", color);
                renderer.SetPropertyBlock(_sharedColorMpb);
                changed++;
            }

            UnityEngine.UI.Graphic[] graphics = go.GetComponents<UnityEngine.UI.Graphic>();
            foreach (UnityEngine.UI.Graphic graphic in graphics)
            {
                graphic.color = color;
                changed++;
            }

            if (changed == 0)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_color: no Renderer or Graphic on '{go.name}'");
                return false;
            }

            return true;
        }

        private static string NormalizeHtmlColor(string htmlColor)
        {
            string value = (htmlColor ?? "").Trim();
            if (value.Length > 0 && value[0] != '#' && IsHexColorLength(value.Length) && IsHexColor(value))
            {
                return "#" + value;
            }

            return value;
        }

        private static bool IsHexColorLength(int length)
        {
            return length == 3 || length == 6 || length == 8;
        }

        private static bool IsHexColor(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') ||
                           (c >= 'a' && c <= 'f') ||
                           (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryPlayAnimation(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                return TryPlayAnimationOnGameObject(go, env.stringValue);
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] play_animation: object not found (name='{env.targetName}')");
            return false;
        }

        private bool TryPlayAnimationOnGameObject(GameObject go, string animationName)
        {
            if (string.IsNullOrEmpty(animationName))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_animation: animation name is empty");
                return false;
            }

            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.enabled)
            {
                if (TryGetAnimationState(animator, animationName, out string statePath))
                {
                    animator.Play(statePath);
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[World] play_animation: '{animationName}' on '{go.name}' (Animator)");
                    return true;
                }

                animator.Play(animationName);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] play_animation: '{animationName}' on '{go.name}' (Animator, state not verified)");
                return true;
            }

            Animation animation = go.GetComponent<Animation>();
            if (animation != null && animation.enabled)
            {
                if (animation.clip != null && animation.GetClip(animationName) != null)
                {
                    animation.Play(animationName);
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[World] play_animation: '{animationName}' on '{go.name}' (Animation)");
                    return true;
                }
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] play_animation: no Animator/Animation on '{go.name}'");
            return false;
        }

        private bool TryGetAnimationState(Animator animator, string animationName, out string statePath)
        {
            statePath = "";
            if (animator.runtimeAnimatorController == null)
            {
                return false;
            }

            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
            foreach (AnimationClip clip in clips)
            {
                if (clip.name.Equals(animationName, StringComparison.OrdinalIgnoreCase))
                {
                    statePath = animationName;
                    return true;
                }
            }

            return false;
        }

        private bool TryPlaySound(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                AudioSource[] audioSources = go.GetComponents<AudioSource>();
                if (audioSources == null || audioSources.Length == 0)
                {
                    _logger.LogWarning(GameLogFeature.MessagePipe,
                        $"[World] play_sound: no AudioSource on '{go.name}'");
                    return false;
                }

                string clipName = (env.stringValue ?? "").Trim();

                if (string.IsNullOrEmpty(clipName))
                {
                    foreach (AudioSource src in audioSources)
                    {
                        if (src.clip != null)
                        {
                            src.Play();
                            _logger.LogInfo(GameLogFeature.MessagePipe,
                                $"[World] play_sound: playing existing clip '{src.clip.name}' on '{go.name}'");
                            return true;
                        }
                    }

                    _logger.LogWarning(GameLogFeature.MessagePipe,
                        $"[World] play_sound: no predefined AudioClip found in any AudioSource on '{go.name}'");
                    return false;
                }

                // Iterate through the data sequence.
                foreach (AudioSource src in audioSources)
                {
                    if (src.clip != null && src.clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))
                    {
                        src.Play();
                        _logger.LogInfo(GameLogFeature.MessagePipe,
                            $"[World] play_sound: playing '{clipName}' on '{go.name}'");
                        return true;
                    }
                }

                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] play_sound: AudioClip '{clipName}' not found on '{go.name}'");
                return false;
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] play_sound: object not found (name='{env.targetName}')");
            return false;
        }

        /// <summary>
        /// Returns animation clip names exposed by Animator and legacy Animation components on the object.
        /// </summary>
        public string[] GetAvailableAnimations(GameObject go)
        {
            if (go == null)
            {
                return Array.Empty<string>();
            }

            List<string> animationsList = new();

            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
                foreach (AnimationClip clip in clips)
                {
                    if (!string.IsNullOrEmpty(clip.name))
                    {
                        animationsList.Add(clip.name);
                    }
                }
            }

            Animation anim = go.GetComponent<Animation>();
            if (anim != null)
            {
                foreach (AnimationState state in anim)
                {
                    if (!string.IsNullOrEmpty(state.clip.name) && !animationsList.Contains(state.clip.name))
                    {
                        animationsList.Add(state.clip.name);
                    }
                }
            }

            return animationsList.ToArray();
        }

        private bool TryListObjects(CoreAiWorldCommandEnvelope env)
        {
            string searchPattern = (env.stringValue ?? "").Trim().ToLowerInvariant();

            GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
            List<Dictionary<string, object>> results = new();

            foreach (GameObject root in rootObjects)
            {
                CollectObjectsRecursive(root, searchPattern, results);
            }

            LastListedObjects = results;

            _logger.LogInfo(GameLogFeature.MessagePipe, $"[World] list_objects: found {results.Count} objects");
            return true;
        }

        private void CollectObjectsRecursive(GameObject parent, string searchPattern,
            List<Dictionary<string, object>> results)
        {
            if (parent == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(searchPattern) ||
                parent.name.ToLowerInvariant().Contains(searchPattern))
            {
                results.Add(new Dictionary<string, object>
                {
                    { "name", parent.name },
                    { "active", parent.activeSelf },
                    {
                        "position",
                        new float[]
                        {
                            parent.transform.position.x, parent.transform.position.y, parent.transform.position.z
                        }
                    },
                    { "tag", parent.tag },
                    { "layer", parent.layer },
                    { "childCount", parent.transform.childCount }
                });
            }

            // Iterate through the data sequence.
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                CollectObjectsRecursive(parent.transform.GetChild(i).gameObject, searchPattern, results);
            }
        }

        private bool TryListAnimations(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] list_animations: object not found");
                return false;
            }

            string[] animations = GetAvailableAnimations(go);
            LastListedAnimations = animations;

            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] list_animations: found {animations.Length} animations on '{go.name}'");
            return true;
        }

        /// <summary>
        /// Resolves a scene object by its requested target name.
        /// </summary>
        private bool ResolveObject(string targetName, out GameObject gameObject)
        {
            gameObject = null;
            string name = (targetName ?? "").Trim();
            if (!string.IsNullOrEmpty(name))
            {
                gameObject = GameObject.Find(name);
                if (gameObject != null)
                {
                    return true;
                }

                Scene scene = SceneManager.GetActiveScene();
                if (scene.IsValid())
                {
                    GameObject[] rootObjects = scene.GetRootGameObjects();
                    for (int i = 0; i < rootObjects.Length; i++)
                    {
                        if (TryFindByNameIncludingInactive(rootObjects[i].transform, name, out gameObject))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryFindByNameIncludingInactive(Transform root, string name, out GameObject gameObject)
        {
            gameObject = null;
            if (root == null)
            {
                return false;
            }

            if (string.Equals(root.name, name, StringComparison.Ordinal))
            {
                gameObject = root.gameObject;
                return true;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                if (TryFindByNameIncludingInactive(root.GetChild(i), name, out gameObject))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Most recent object names returned by a list command.</summary>
        public List<Dictionary<string, object>> LastListedObjects { get; private set; } = new();

        /// <summary>Most recent animation names returned by a list command.</summary>
        public string[] LastListedAnimations { get; private set; } = Array.Empty<string>();

        /// <inheritdoc />
        public IReadOnlyList<string> LastListedPrefabKeys { get; private set; } = Array.Empty<string>();

        /// <inheritdoc />
        public string LastErrorMessage { get; private set; } = "";

        /// <inheritdoc />
        public CoreAiSpawnBatchResult LastSpawnBatchResult { get; private set; }
    }
}
