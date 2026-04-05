using System.Collections.Generic;
using System.Linq;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Config
{
    /// <summary>
    /// Unity-реализация хранилища конфигов на ScriptableObject.
    /// Каждая SO должна иметь поле ConfigKey для идентификации.
    /// </summary>
    public sealed class UnityGameConfigStore : IGameConfigStore
    {
        private readonly Dictionary<string, ScriptableObject> _configsByKey = new();
        private readonly IGameLogger _logger;

        public UnityGameConfigStore(IGameLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Регистрирует ScriptableObject как конфиг с указанным ключом.
        /// </summary>
        public void Register(string key, ScriptableObject config)
        {
            if (string.IsNullOrEmpty(key) || config == null)
            {
                return;
            }

            _configsByKey[key] = config;
            _logger.LogInfo(GameLogFeature.Core,
                $"[GameConfig] Registered config key: {key} (type: {config.GetType().Name})");
        }

        /// <inheritdoc />
        public bool TryLoad(string key, out string json)
        {
            if (_configsByKey.TryGetValue(key, out ScriptableObject so) && so != null)
            {
                json = JsonUtility.ToJson(so);
                return true;
            }

            json = null;
            return false;
        }

        /// <inheritdoc />
        public bool TrySave(string key, string json)
        {
            if (!_configsByKey.TryGetValue(key, out ScriptableObject so) || so == null)
            {
                _logger.LogWarning(GameLogFeature.Core, $"[GameConfig] Config key not found: {key}");
                return false;
            }

            try
            {
                // JsonUtility не поддерживает десериализацию напрямую в существующий объект
                // Поэтому использу временный объект и копируем поля
                JsonUtility.FromJsonOverwrite(json, so);
                _logger.LogInfo(GameLogFeature.Core, $"[GameConfig] Updated config key: {key}");
#if UNITY_EDITOR
                // В Editor сохраняем изменения в ассет
                UnityEditor.EditorUtility.SetDirty(so);
#endif
                return true;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(GameLogFeature.Core, $"[GameConfig] Failed to update config '{key}': {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc />
        public string[] GetKnownKeys()
        {
            return _configsByKey.Keys.ToArray();
        }
    }
}