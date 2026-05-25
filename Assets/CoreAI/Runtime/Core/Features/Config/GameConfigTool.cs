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
    /// Host-side implementation of game configuration tool operations.
    /// </summary>
    public sealed class GameConfigTool
    {
        private readonly IGameConfigStore _store;
        private readonly GameConfigPolicy _policy;
        private readonly string _roleId;
        private readonly ICoreAISettings _settings;

        public GameConfigTool(IGameConfigStore store, GameConfigPolicy policy, string roleId,
            ICoreAISettings settings = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }

        /// <summary>
        /// Creates the MEAI function exposed to the model for reading or updating game config.
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
        /// Handles a game-config tool action and returns a serialized <see cref="GameConfigResult"/>.
        /// </summary>
        /// <param name="action">Tool action: <c>read</c> or <c>update</c>.</param>
        /// <param name="content">JSON object used by the <c>update</c> action.</param>
        /// <param name="cancellationToken">Cancellation token for future async stores.</param>
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

            if ((_settings?.LogToolCallArguments ?? CoreAISettings.LogToolCallArguments) &&
                !string.IsNullOrEmpty(content))
            {
                Log.Instance.Info($"  content length={content.Length}", LogTag.Config);
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "read":
                        return await ExecuteReadAsync(cancellationToken).ConfigureAwait(false);

                    case "update":
                        return await ExecuteUpdateAsync(content, cancellationToken).ConfigureAwait(false);

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

            content = content.Trim();
            if (!content.StartsWith("{") || !content.EndsWith("}"))
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = "Content must be a valid JSON object."
                });
            }

            string[] allowedKeys = _policy.GetAllowedKeys(_roleId);
            if (allowedKeys.Length == 0)
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = false,
                    Error = $"Role '{_roleId}' is not allowed to modify any config keys."
                });
            }

            string primarykey = allowedKeys[0];

            if (_policy.TryApplyChanges(_roleId, content, out string[] appliedKeys, out string error))
            {
                return SerializeResult(new GameConfigResult
                {
                    Success = true,
                    Message = $"Config updated for keys: {string.Join(", ", appliedKeys)}",
                    ConfigJson = content
                });
            }

            _store.TrySave(primarykey, content);
            return SerializeResult(new GameConfigResult
            {
                Success = true,
                Message = $"Config updated for key: {primarykey}",
                ConfigJson = content
            });
        }

        /// <summary>
        /// Combines per-key JSON payloads into one object keyed by config name.
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
                // Stored values are already JSON payloads, so write them as raw object values.
                sb.Append($"\"{kvp.Key}\":{kvp.Value}");
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Structured response returned by the game configuration tool.
        /// </summary>
        public sealed class GameConfigResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }

            /// <summary>Serialized configuration JSON returned by the tool.</summary>
            public string ConfigJson { get; set; }
        }
    }
}
