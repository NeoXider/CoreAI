using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Applies curated CoreAI component commands to Unity scene objects.</summary>
    public sealed class CoreAiComponentCommandExecutor : ICoreAiComponentCommandExecutor
    {
        private readonly IGameLogger _logger;

        public CoreAiComponentCommandExecutor(IGameLogger logger)
        {
            _logger = logger;
        }

        public bool TryExecute(ApplyAiGameCommand cmd)
        {
            return TryExecute(cmd, out _);
        }

        public bool TryExecute(ApplyAiGameCommand cmd, out List<string> listedComponents)
        {
            listedComponents = new List<string>();
            if (cmd == null || !string.Equals(cmd.CommandTypeId, AiGameCommandTypeIds.ComponentCommand,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string json = cmd.JsonPayload ?? "";
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            CoreAiComponentCommandEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CoreAiComponentCommandEnvelope>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[Component] bad json: {ex.Message}");
                return false;
            }

            if (env == null || string.IsNullOrWhiteSpace(env.action))
            {
                return false;
            }

            switch (env.action.Trim())
            {
                case "add":
                    return TryAdd(env);
                case "remove":
                    return TryRemove(env);
                case "set":
                    return TrySet(env);
                case "list_components":
                    return TryListComponents(env, out listedComponents);
                default:
                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[Component] unknown action '{env.action}'");
                    return false;
            }
        }

        private bool TryAdd(CoreAiComponentCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] add: object not found (name='{env.targetName}')");
                return false;
            }

            if (!CoreAiComponentCatalog.TryGet(env.componentType, out CoreAiComponentCatalog.Entry entry))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] add: unsupported component type '{env.componentType}'");
                return false;
            }

            if (entry.Get(go) == null)
            {
                entry.Add(go);
            }

            return true;
        }

        private bool TryRemove(CoreAiComponentCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] remove: object not found (name='{env.targetName}')");
                return false;
            }

            if (!CoreAiComponentCatalog.TryGet(env.componentType, out CoreAiComponentCatalog.Entry entry))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] remove: unsupported component type '{env.componentType}'");
                return false;
            }

            Component component = entry.Get(go);
            if (component != null)
            {
                UnityEngine.Object.Destroy(component);
            }

            return true;
        }

        private bool TrySet(CoreAiComponentCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] set: object not found (name='{env.targetName}')");
                return false;
            }

            if (!CoreAiComponentCatalog.TryGet(env.componentType, out CoreAiComponentCatalog.Entry entry))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] set: unsupported component type '{env.componentType}'");
                return false;
            }

            string propertyName = (env.propertyName ?? "").Trim().ToLowerInvariant();
            if (!entry.Setters.TryGetValue(propertyName, out Action<Component, CoreAiComponentCommandEnvelope> setter))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] set: unsupported property '{env.propertyName}' for '{env.componentType}'");
                return false;
            }

            Component component = entry.Get(go);
            if (component == null)
            {
                component = entry.Add(go);
            }

            setter(component, env);
            return true;
        }

        private bool TryListComponents(CoreAiComponentCommandEnvelope env, out List<string> listedComponents)
        {
            listedComponents = new List<string>();
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[Component] list_components: object not found (name='{env.targetName}')");
                return false;
            }

            Component[] components = go.GetComponents<Component>();
            List<string> names = new(components.Length);
            foreach (Component component in components)
            {
                if (component != null)
                {
                    names.Add(component.GetType().Name);
                }
            }

            listedComponents = names;
            return true;
        }

        private bool ResolveObject(string targetName, out GameObject gameObject)
        {
            gameObject = null;
            string name = (targetName ?? "").Trim();
            if (!string.IsNullOrEmpty(name))
            {
                gameObject = GameObject.Find(name);
                return gameObject != null;
            }

            return false;
        }
    }
}
