using System;
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
            "Actions: spawn, move, destroy, load_scene, reload_scene, " +
            "set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects. " +
            "Use 'spawn' to create objects, 'move' to reposition, 'destroy' to remove, " +
            "'play_animation' to play animations, 'list_animations' to get available animations, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'show_text' to display notifications. " +
            "Objects are targeted by 'targetName'.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Command: spawn, move, destroy, load_scene, reload_scene, set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects"),
            ("targetName", "string", false, "Object name to target (required for move, destroy, set_active, play_animation, etc). Used to set a name for spawned objects."),
            ("x", "number", false, "X coordinate (for spawn, move)"),
            ("y", "number", false, "Y coordinate (for spawn, move)"),
            ("z", "number", false, "Z coordinate (for spawn, move)"),
            ("fx", "number", false, "Force X (for apply_force)"),
            ("fy", "number", false, "Force Y (for apply_force)"),
            ("fz", "number", false, "Force Z (for apply_force)"),
            ("prefabKey", "string", false, "Prefab key for spawn command"),
            ("stringValue", "string", false, "String value: animation name for play_animation, text for show_text, or search pattern for list_objects"),
            ("volume", "number", false, "Reserved for future use")
        );

        public AIFunction CreateAIFunction()
        {
            Func<string, float, float, float, float, float, float, string?, string?, string?, float, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, Name, Description);
        }

        public async Task<string> ExecuteAsync(
            string action,
            float x = 0f,
            float y = 0f,
            float z = 0f,
            float fx = 0f,
            float fy = 0f,
            float fz = 0f,
            string? prefabKey = null,
            string? targetName = null,
            string? stringValue = null,
            float volume = 1f,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(false, "Action is required. Valid actions: spawn, move, destroy, load_scene, reload_scene, set_active, show_text, apply_force, spawn_particles, list_objects");
            }

            if (CoreAISettings.LogToolCalls)
            {
                CoreAI.Logging.Log.Instance.Info($"[Tool Call] world_command: action={action}", CoreAI.Logging.LogTag.World);
            }
            if (CoreAISettings.LogToolCallArguments)
            {
                var args = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(targetName)) args.Append($" targetName={targetName}");
                if (!string.IsNullOrEmpty(prefabKey)) args.Append($" prefabKey={prefabKey}");
                if (x != 0f || y != 0f || z != 0f) args.Append($" pos=({x},{y},{z})");
                if (fx != 0f || fy != 0f || fz != 0f) args.Append($" force=({fx},{fy},{fz})");
                if (!string.IsNullOrEmpty(stringValue)) args.Append($" stringValue={stringValue}");
                if (args.Length > 0) CoreAI.Logging.Log.Instance.Info($"  args:{args}", CoreAI.Logging.LogTag.World);
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                CoreAiWorldCommandEnvelope envelope = action switch
                {
                    "spawn" => CreateSpawnCommand(prefabKey, targetName, x, y, z),
                    "move" => CreateMoveCommand(targetName, x, y, z),
                    "destroy" => CreateDestroyCommand(targetName),
                    "load_scene" => CreateLoadSceneCommand(stringValue),
                    "reload_scene" => CreateReloadSceneCommand(),
                    "set_active" => CreateSetActiveCommand(targetName, true),
                    "play_animation" => CreatePlayAnimationCommand(targetName, stringValue),
                    "list_animations" => CreateListAnimationsCommand(targetName),
                    "show_text" => CreateShowTextCommand(targetName, stringValue),
                    "apply_force" => CreateApplyForceCommand(targetName, fx, fy, fz),
                    "spawn_particles" => CreateSpawnParticlesCommand(targetName, stringValue),
                    "list_objects" => CreateListObjectsCommand(stringValue),
                    _ => null
                };

                if (envelope == null)
                {
                    if (action != "spawn" && action != "move" && action != "destroy" && action != "load_scene" && action != "reload_scene" && action != "set_active" && action != "play_animation" && action != "list_animations" && action != "show_text" && action != "apply_force" && action != "spawn_particles" && action != "list_objects")
                        throw new ArgumentException($"Unknown action: '{action}'. Valid actions: spawn, move, destroy, load_scene, reload_scene, set_active, play_animation, list_animations, show_text, apply_force, spawn_particles, list_objects");
                    return SerializeResult(false, $"Missing required parameters for action '{action}'");
                }

                // Выполняем команду через executor
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(envelope);
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

        private static CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(prefabKey)) return null;
            return CoreAiWorldCommandEnvelope.Spawn(prefabKey, targetName ?? Guid.NewGuid().ToString("N"), new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateMoveCommand(string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.Move(targetName, new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateDestroyCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.Destroy(targetName);
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

        private static CoreAiWorldCommandEnvelope CreateSetActiveCommand(string? targetName, bool active)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.SetActive(targetName, active);
        }

        private static CoreAiWorldCommandEnvelope CreatePlayAnimationCommand(string? targetName, string? animationName)
        {
            if (string.IsNullOrEmpty(animationName)) return null;
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.PlayAnimation(targetName, animationName);
        }

        private static CoreAiWorldCommandEnvelope CreateShowTextCommand(string? targetName, string? text)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(text)) return null;
            return CoreAiWorldCommandEnvelope.ShowText(targetName, text);
        }

        private static CoreAiWorldCommandEnvelope CreateApplyForceCommand(string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.ApplyForce(targetName, new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateSpawnParticlesCommand(string? targetName, string? effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return null;
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.SpawnParticles(targetName, effectName);
        }

        private static CoreAiWorldCommandEnvelope CreateListObjectsCommand(string? searchPattern)
        {
            return CoreAiWorldCommandEnvelope.ListObjects(searchPattern ?? "");
        }

        private static CoreAiWorldCommandEnvelope CreateListAnimationsCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.ListAnimations(targetName);
        }

        private static string SerializeResult(bool success, string message, string? action = null)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(new WorldResult
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
