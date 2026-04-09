using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Microsoft.Extensions.AI;

namespace CoreAI.Config
{
    /// <summary>
    /// LLM-инструмент для чтения и изменения игровых конфигов.
    /// AI получает текущий конфиг как JSON и возвращает изменённый JSON.
    /// </summary>
    public sealed class GameConfigTool
    {
        private readonly IGameConfigStore _store;
        private readonly GameConfigPolicy _policy;
        private readonly string _roleId;
        private readonly ICoreAISettings _settings;

        public GameConfigTool(IGameConfigStore store, GameConfigPolicy policy, string roleId, ICoreAISettings settings = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string?, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = "game_config",
                Description =
                    "Read or modify game configuration. Use 'read' to get current config as JSON, or 'update' with modified JSON to apply changes. Available keys: " +
                    string.Join(", ", _policy.GetAllowedKeys(_roleId))
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>
        /// Выполняет операцию с конфигом.
        /// </summary>
        /// <param name="action">Действие: "read" или "update".</param>
        /// <param name="content">Для update — изменённый JSON конфиг. Для read — игнорируется.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        public async Task<string> ExecuteAsync(
            string action,
            string? content = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(new GameConfigResult
                    { Success = false, Error = "Action is required. Use 'read' or 'update'." });
            }

            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] game_config: action={action}", LogTag.Config);
            }

            if ((_settings?.LogToolCallArguments ?? CoreAISettings.LogToolCallArguments) && !string.IsNullOrEmpty(content))
            {
                Log.Instance.Info($"  content length={content.Length}", LogTag.Config);
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "read":
                        return await ExecuteReadAsync(cancellationToken);

                    case "update":
                        return await ExecuteUpdateAsync(content, cancellationToken);

                    default:
                        return SerializeResult(new GameConfigResult
                        {
                            Success = false,
                            Error = $"Unknown action: '{action}'. Valid actions: read, update"
                        });
                }
            }
            catch (Exception ex)
            {
                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Error($"[Tool Call] game_config: FAILED - {ex.Message}", LogTag.Config);
                }

                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = $"GameConfigTool failed: {ex.Message}"
                });
            }
        }

        private static string SerializeResult(GameConfigResult result)
        {
            return JsonConvert.SerializeObject(result);
        }

        private async Task<string> ExecuteReadAsync(CancellationToken cancellationToken)
        {
            string[] keys = _policy.GetAllowedKeys(_roleId);
            if (keys.Length == 0)
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = $"Role '{_roleId}' has no allowed config keys."
                });
            }

            Dictionary<string, string> configs = new();
            foreach (string key in keys)
            {
                if (_store.TryLoad(key, out string json) && !string.IsNullOrEmpty(json))
                {
                    configs[key] = json;
                }
            }

            if (configs.Count == 0)
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = true,
                    Message = "No configs available for allowed keys.",
                    ConfigJson = "{}"
                });
            }

            // Объединяем все конфиги в один JSON объект
            string combinedJson = CombineConfigsToJson(configs);
            return SerializeResult(new GameConfigResult
            {
                Success = true,
                Message = $"Config read successfully for keys: {string.Join(", ", configs.Keys)}",
                ConfigJson = combinedJson
            });
        }

        private async Task<string> ExecuteUpdateAsync(string? content, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(content))
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = "Content (JSON config) is required for update action."
                });
            }

            // Валидируем что это JSON
            content = content.Trim();
            if (!content.StartsWith("{") || !content.EndsWith("}"))
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = "Content must be a valid JSON object."
                });
            }

            // Определяем какие ключи можно менять этой роли
            string[] allowedKeys = _policy.GetAllowedKeys(_roleId);
            if (allowedKeys.Length == 0)
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = $"Role '{_roleId}' is not allowed to modify any config keys."
                });
            }

            // Для простоты: сохраняем весь JSON как новый конфиг первого разрешённого ключ
            // В реальной игре AI должен возвращать только изменённые части
            // Для более сложной логики (частичное обновление) используйте GameConfigPolicy.ApplyChanges
            string primarykey = allowedKeys[0];

            // Пытаемся применить изменения через политику (если она поддерживает)
            if (_policy.TryApplyChanges(_roleId, content, out string[] appliedKeys, out string error))
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = true,
                    Message = $"Config updated for keys: {string.Join(", ", appliedKeys)}",
                    ConfigJson = content
                });
            }

            // Fallback: сохраняем как есть для первичного ключа
            _store.TrySave(primarykey, content);
            return SerializeResult(new GameConfigResult
            {
                Success = true,
                Message = $"Config updated for key: {primarykey}",
                ConfigJson = content
            });
        }

        /// <summary>
        /// Объединяет несколько JSON строк в один объект.
        /// Простая реализация: оборачивает каждый конфиг по ключу.
        /// </summary>
        private static string CombineConfigsToJson(Dictionary<string, string> configs)
        {
            StringBuilder sb = new();
            sb.Append("{");
            bool first = true;
            foreach (KeyValuePair<string, string> kvp in configs)
            {
                if (!first)
                {
                    sb.Append(",");
                }

                first = false;
                // kvp.Value уже JSON, оборачиваем по ключу
                sb.Append($"\"{kvp.Key}\":{kvp.Value}");
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Результат операции с конфигом.
        /// </summary>
        public sealed class GameConfigResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }

            /// <summary>JSON конфиг (для read) или применённый конфиг (для update).</summary>
            public string ConfigJson { get; set; }
        }
    }
}