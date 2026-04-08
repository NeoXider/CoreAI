using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// ILlmTool реализация для WorldTool — позволяет LLM вызывать world commands.
    /// Включает логику выполнения (ExecuteAsync) и метаданные инструмента.
    /// 
    /// Примечание: этот класс находится в CoreAiUnity потому что зависит от Unity и WorldCommand.
    /// Для других движков нужно создать аналогичный инструмент.
    /// </summary>
    public sealed class WorldLlmTool : LlmToolBase
    {
        private readonly ICoreAiWorldCommandExecutor _executor;

        public WorldLlmTool(ICoreAiWorldCommandExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public override string Name => "world_command";

        public override string Description =>
            "Execute world commands to manipulate the game world. " +
            "Actions: spawn, move, destroy, load_scene, reload_scene, bind_by_name, " +
            "set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects. " +
            "Use 'spawn' to create objects, 'move' to reposition, 'destroy' to remove, " +
            "'play_animation' to play animations, 'list_animations' to get available animations, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'show_text' to display notifications. " +
            "Objects can be targeted by 'instanceId' or 'targetName'.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Command: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects"),
            ("instanceId", "string", false, "Instance ID of the target object"),
            ("targetName", "string", false, "Object name to target (alternative to instanceId, works with move, destroy, set_active, play_animation, list_animations, etc.)"),
            ("x", "number", false, "X coordinate (for spawn, move, apply_force)"),
            ("y", "number", false, "Y coordinate (for spawn, move, apply_force)"),
            ("z", "number", false, "Z coordinate (for spawn, move, apply_force)"),
            ("prefabKey", "string", false, "Prefab key for spawn command"),
            ("stringValue", "string", false, "String value: animation name for play_animation, text for show_text, or search pattern for list_objects"),
            ("volume", "number", false, "Reserved for future use")
        );

        public AIFunction CreateAIFunction()
        {
            Func<string, string?, float, float, float, string?, string?, string?, float, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, Name, Description);
        }

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
                    _ => null
                };

                if (envelope == null)
                {
                    // For unknown action, return error properly
                    if (action != "spawn" && action != "move" && action != "destroy" && action != "load_scene" && action != "reload_scene" && action != "bind_by_name" && action != "set_active" && action != "play_animation" && action != "list_animations" && action != "show_text" && action != "apply_force" && action != "spawn_particles" && action != "list_objects")
                        throw new ArgumentException($"Unknown action: '{action}'. Valid actions: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects");
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
