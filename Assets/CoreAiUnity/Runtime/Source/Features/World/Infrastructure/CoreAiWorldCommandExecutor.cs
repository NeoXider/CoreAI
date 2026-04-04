using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Минимальный исполнитель: spawn/move/destroy/load_scene. Всё через whitelist registry.</summary>
    public sealed class CoreAiWorldCommandExecutor : ICoreAiWorldCommandExecutor
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiPrefabRegistryAsset _prefabRegistry;
        private readonly Dictionary<string, GameObject> _instances = new(StringComparer.Ordinal);

        public CoreAiWorldCommandExecutor(IGameLogger logger, CoreAiPrefabRegistryAsset prefabRegistry = null)
        {
            _logger = logger;
            _prefabRegistry = prefabRegistry;
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

            switch (env.action.Trim())
            {
                case "spawn":
                    return TrySpawn(env);
                case "move":
                    return TryMove(env);
                case "destroy":
                    return TryDestroy(env);
                case "bind_by_name":
                    return TryBindByName(env);
                case "set_active":
                    return TrySetActive(env);
                case "load_scene":
                    return TryLoadScene(env);
                case "reload_scene":
                    return TryReloadScene();
                default:
                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] unknown action '{env.action}'");
                    return false;
            }
        }

        private bool TrySpawn(CoreAiWorldCommandEnvelope env)
        {
            if (_prefabRegistry == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] prefab registry not assigned");
                return false;
            }

            string key = (env.prefabKeyOrName ?? "").Trim();
            if (!_prefabRegistry.TryResolve(key, out GameObject prefab) || prefab == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] prefab not found: '{key}'");
                return false;
            }

            string id = (env.instanceId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] spawn missing instanceId");
                return false;
            }

            Vector3 pos = new(env.px, env.py, env.pz);
            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            go.name = $"{prefab.name}#{id}";
            _instances[id] = go;
            return true;
        }

        private bool TryMove(CoreAiWorldCommandEnvelope env)
        {
            string id = (env.instanceId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (!_instances.TryGetValue(id, out GameObject go) || go == null)
            {
                return false;
            }

            go.transform.position = new Vector3(env.mx, env.my, env.mz);
            return true;
        }

        private bool TryDestroy(CoreAiWorldCommandEnvelope env)
        {
            string id = (env.instanceId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (_instances.TryGetValue(id, out GameObject go))
            {
                _instances.Remove(id);
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
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

            SceneManager.LoadScene(name);
            return true;
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

        private bool TryBindByName(CoreAiWorldCommandEnvelope env)
        {
            string name = (env.targetName ?? "").Trim();
            string id = (env.instanceId ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
            {
                return false;
            }

            GameObject go = GameObject.Find(name);
            if (go == null)
            {
                return false;
            }

            _instances[id] = go;
            return true;
        }

        private bool TrySetActive(CoreAiWorldCommandEnvelope env)
        {
            string id = (env.instanceId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (!_instances.TryGetValue(id, out GameObject go) || go == null)
            {
                return false;
            }

            go.SetActive(env.boolValue != 0);
            return true;
        }
    }
}