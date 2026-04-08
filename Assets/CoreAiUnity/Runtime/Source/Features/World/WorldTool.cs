using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// AIFunction-обёртка для WorldCommand — позволяет LLM управлять миром (спавн, перемещение, удаление и т.д.).
    /// Используется в MEAI function calling pipeline (FunctionInvokingChatClient).
    /// 
    /// Примечание: этот класс находится в CoreAiUnity потому что зависит от Unity и WorldCommand.
    /// Для других движков нужно создать аналогичный инструмент.
    /// </summary>
    public sealed class WorldTool
    {
        private readonly ICoreAiWorldCommandExecutor _executor;

        public WorldTool(ICoreAiWorldCommandExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        /// <summary>
        /// Создать AIFunction для MEAI.
        /// Возвращает JSON строку с результатом выполнения команды.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string?, float, float, float, string, string?, string?, float, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(
                func,
                "world_command",
                "Execute world commands to manipulate the game world: spawn, move, destroy objects, load scenes, list hierarchy, show text notifications, etc.");
        }

        /// <summary>
        /// Выполнить world command.
        /// </summary>
        /// <param name="action">Команда: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, show_text, apply_force, spawn_particles, list_objects</param>
        /// <param name="instanceId">ID инстанса объекта (для move, destroy, set_active, apply_force, spawn_particles)</param>
        /// <param name="x">X координата (для spawn, move, apply_force)</param>
        /// <param name="y">Y координата (для spawn, move, apply_force)</param>
        /// <param name="z">Z координата (для spawn, move, apply_force)</param>
        /// <param name="prefabKey">Ключ префаба (для spawn)</param>
        /// <param name="targetName">Имя цели (для bind_by_name, show_text, move, destroy, set_active, apply_force, spawn_particles)</param>
        /// <param name="stringValue">Строковое значение: text, search pattern (для show_text, list_objects)</param>
        /// <param name="volume">Громкость 0-1 (резерв для будущего)</param>
        /// <param name="cancellationToken">Token отмены</param>
        public async Task<string> ExecuteAsync(
            string action,
            string? instanceId = null,
            float x = 0f,
            float y = 0f,
            float z = 0f,
            string? prefabKey = null,
            string? targetName = null,
            string? stringValue = null,
            float volume = 1f,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(false, "Action is required. Valid actions: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, show_text, apply_force, spawn_particles, list_objects");
            }

            if (CoreAISettings.LogToolCalls)
            {
                CoreAI.Logging.Log.Instance.Info($"[Tool Call] world_command: action={action}", CoreAI.Logging.LogTag.World);
            }
            if (CoreAISettings.LogToolCallArguments)
            {
                var args = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(instanceId)) args.Append($" instanceId={instanceId}");
                if (!string.IsNullOrEmpty(targetName)) args.Append($" targetName={targetName}");
                if (!string.IsNullOrEmpty(prefabKey)) args.Append($" prefabKey={prefabKey}");
                if (x != 0f || y != 0f || z != 0f) args.Append($" pos=({x},{y},{z})");
                if (!string.IsNullOrEmpty(stringValue)) args.Append($" stringValue={stringValue}");
                if (args.Length > 0) CoreAI.Logging.Log.Instance.Info($"  args:{args}", CoreAI.Logging.LogTag.World);
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                CoreAiWorldCommandEnvelope envelope = action switch
                {
                    "spawn" => CreateSpawnCommand(prefabKey, instanceId, x, y, z),
                    "move" => CreateMoveCommand(instanceId, targetName, x, y, z),
                    "destroy" => CreateDestroyCommand(instanceId, targetName),
                    "load_scene" => CreateLoadSceneCommand(stringValue),
                    "reload_scene" => CreateReloadSceneCommand(),
                    "bind_by_name" => CreateBindByNameCommand(targetName, instanceId),
                    "set_active" => CreateSetActiveCommand(instanceId, targetName, true),
                    "play_animation" => CreatePlayAnimationCommand(instanceId, targetName, stringValue),
                    "list_animations" => CreateListAnimationsCommand(instanceId, targetName),
                    "show_text" => CreateShowTextCommand(targetName, stringValue),
                    "apply_force" => CreateApplyForceCommand(instanceId, targetName, x, y, z),
                    "spawn_particles" => CreateSpawnParticlesCommand(instanceId, targetName, stringValue),
                    "list_objects" => CreateListObjectsCommand(stringValue),
                    _ => throw new ArgumentException($"Unknown action: '{action}'. Valid actions: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects")
                };

                if (envelope == null)
                {
                    return SerializeResult(false, $"Missing required parameters for action '{action}'");
                }

                // Выполняем команду через executor
                string json = JsonUtility.ToJson(envelope);
                bool success = await Task.Run(() => _executor.TryExecute(new CoreAI.Messaging.ApplyAiGameCommand
                {
                    CommandTypeId = CoreAI.Messaging.AiGameCommandTypeIds.WorldCommand,
                    JsonPayload = json
                }), cancellationToken);

                if (CoreAISettings.LogToolCallResults)
                {
                    CoreAI.Logging.Log.Instance.Info($"[Tool Call] world_command: {(success ? "SUCCESS" : "FAILED")} - {action}", CoreAI.Logging.LogTag.World);
                }

                return SerializeResult(success,
                    success ? $"World command '{action}' executed successfully" : $"Failed to execute world command '{action}'",
                    action);
            }
            catch (Exception ex)
            {
                if (CoreAISettings.LogToolCallResults)
                {
                    CoreAI.Logging.Log.Instance.Error($"[Tool Call] world_command: FAILED - {ex.Message}", CoreAI.Logging.LogTag.World);
                }
                
                return SerializeResult(false, $"World command failed: {ex.Message}", action);
            }
        }

        private static CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? instanceId, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(prefabKey)) return null;
            return CoreAiWorldCommandEnvelope.Spawn(prefabKey, instanceId ?? Guid.NewGuid().ToString("N"), new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateMoveCommand(string? instanceId, string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.Move(instanceId ?? "", new Vector3(x, y, z));
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreateDestroyCommand(string? instanceId, string? targetName)
        {
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.Destroy(instanceId ?? "");
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreateLoadSceneCommand(string? sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return null;
            return CoreAiWorldCommandEnvelope.LoadScene(sceneName);
        }

        private static CoreAiWorldCommandEnvelope CreateReloadSceneCommand()
        {
            return CoreAiWorldCommandEnvelope.ReloadScene();
        }

        private static CoreAiWorldCommandEnvelope CreateBindByNameCommand(string? targetName, string? instanceId)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.BindByName(targetName, instanceId ?? "");
        }

        private static CoreAiWorldCommandEnvelope CreateSetActiveCommand(string? instanceId, string? targetName, bool active)
        {
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.SetActive(instanceId ?? "", active);
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreatePlayAnimationCommand(string? instanceId, string? targetName, string? animationName)
        {
            if (string.IsNullOrEmpty(animationName)) return null;
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.PlayAnimation(instanceId ?? "", animationName);
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreateShowTextCommand(string? targetName, string? text)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(text)) return null;
            return CoreAiWorldCommandEnvelope.ShowText(targetName, text);
        }

        private static CoreAiWorldCommandEnvelope CreateApplyForceCommand(string? instanceId, string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.ApplyForce(instanceId ?? "", new Vector3(x, y, z));
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreateSpawnParticlesCommand(string? instanceId, string? targetName, string? effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return null;
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            var env = CoreAiWorldCommandEnvelope.SpawnParticles(instanceId ?? "", effectName);
            if (!string.IsNullOrEmpty(targetName)) env.targetName = targetName;
            return env;
        }

        private static CoreAiWorldCommandEnvelope CreateListObjectsCommand(string? searchPattern)
        {
            return CoreAiWorldCommandEnvelope.ListObjects(searchPattern ?? "");
        }

        private static CoreAiWorldCommandEnvelope CreateListAnimationsCommand(string? instanceId, string? targetName)
        {
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.ListAnimations(instanceId ?? "", targetName ?? "");
        }

        private static string SerializeResult(bool success, string message, string? action = null)
        {
            return JsonSerializer.Serialize(new WorldResult
            {
                Success = success,
                Message = message,
                Action = action ?? ""
            });
        }

        /// <summary>
        /// Результат выполнения world command.
        /// </summary>
        public sealed class WorldResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Action { get; set; }
        }
    }
}
