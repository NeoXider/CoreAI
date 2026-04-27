using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using Cysharp.Threading.Tasks;
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
        private readonly ICoreAISettings _settings;
        private readonly IGameLogger _logger;

        public WorldLlmTool(ICoreAiWorldCommandExecutor executor, ICoreAISettings settings, IGameLogger logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override string Name => "world_command";
        public override bool AllowDuplicates => true;

        public override string Description =>
            "Execute world commands to manipulate the game world. " +
            "Actions: spawn, move, destroy, load_scene, reload_scene, " +
            "set_active, play_animation, stop_animation, list_animations, show_text, " +
            "play_sound, set_volume, hide_panel, update_score, " +
            "apply_force, set_velocity, spawn_particles, list_objects. " +
            "Use 'spawn' to create objects, 'move' to reposition, 'destroy' to remove, " +
            "'play_animation'/'stop_animation' to control animations, 'list_animations' to get available animations, " +
            "'play_sound'/'set_volume' for audio, 'show_text'/'hide_panel'/'update_score' for UI, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'apply_force'/'set_velocity' for physics. " +
            "Objects are targeted by 'targetName'. For play_animation, stop_animation, and list_animations " +
            "always pass targetName (for example targetName='Enemy'); do not put the target object name only in prose.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true,
                "Command: spawn, move, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, update_score, apply_force, set_velocity, spawn_particles, list_objects"),
            ("targetName", "string", false,
                "Object name to target (required for move, destroy, set_active, play_animation, stop_animation, list_animations, etc). Used to set a name for spawned objects."),
            ("x", "number", false, "X coordinate (for spawn, move)"),
            ("y", "number", false, "Y coordinate (for spawn, move)"),
            ("z", "number", false, "Z coordinate (for spawn, move)"),
            ("fx", "number", false, "Force X (for apply_force)"),
            ("fy", "number", false, "Force Y (for apply_force)"),
            ("fz", "number", false, "Force Z (for apply_force)"),
            ("prefabKey", "string", false, "Prefab key for spawn command"),
            ("animationName", "string", false, "Name of the animation to play/stop"),
            ("textToDisplay", "string", false, "Text for show_text / update_score"),
            ("stringValue", "string", false, "Generic string value (e.g. search pattern for list_objects, clip name for play_sound)"),
            ("volume", "number", false, "Volume level 0.0-1.0 for set_volume")
        );

        public AIFunction CreateAIFunction()
        {
            Func<string, float, float, float, float, float, float, string?, string?, string?, string?, string?, float,
                CancellationToken,
                Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
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
            string? animationName = null,
            string? textToDisplay = null,
            float volume = 1f,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(false,
                    $"Action is required. Valid actions: {ValidActionsText}");
            }

            if (_settings.LogToolCalls)
            {
                _logger.LogInfo(GameLogFeature.MessagePipe, $"[Tool Call] world_command: action={action}");
            }

            if (_settings.LogToolCallArguments)
            {
                StringBuilder args = new();
                if (!string.IsNullOrEmpty(targetName))
                {
                    args.Append($" targetName={targetName}");
                }

                if (!string.IsNullOrEmpty(prefabKey))
                {
                    args.Append($" prefabKey={prefabKey}");
                }

                if (x != 0f || y != 0f || z != 0f)
                {
                    args.Append($" pos=({x},{y},{z})");
                }

                if (fx != 0f || fy != 0f || fz != 0f)
                {
                    args.Append($" force=({fx},{fy},{fz})");
                }

                if (!string.IsNullOrEmpty(stringValue))
                {
                    args.Append($" stringValue={stringValue}");
                }

                if (!string.IsNullOrEmpty(animationName))
                {
                    args.Append($" animationName={animationName}");
                }

                if (!string.IsNullOrEmpty(textToDisplay))
                {
                    args.Append($" textToDisplay={textToDisplay}");
                }

                if (args.Length > 0)
                {
                    _logger.LogInfo(GameLogFeature.MessagePipe, $"  args:{args}");
                }
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
                    "play_animation" => CreatePlayAnimationCommand(targetName, animationName ?? stringValue),
                    "stop_animation" => CreateStopAnimationCommand(targetName),
                    "list_animations" => CreateListAnimationsCommand(targetName),
                    "play_sound" => CreatePlaySoundCommand(targetName, stringValue),
                    "set_volume" => CreateSetVolumeCommand(targetName, volume),
                    "show_text" => CreateShowTextCommand(targetName, textToDisplay ?? stringValue),
                    "hide_panel" => CreateHidePanelCommand(targetName),
                    "update_score" => CreateUpdateScoreCommand(targetName, textToDisplay ?? stringValue),
                    "apply_force" => CreateApplyForceCommand(targetName, fx, fy, fz),
                    "set_velocity" => CreateSetVelocityCommand(targetName, fx, fy, fz),
                    "spawn_particles" => CreateSpawnParticlesCommand(targetName, stringValue),
                    "list_objects" => CreateListObjectsCommand(stringValue),
                    _ => null
                };

                if (envelope == null)
                {
                    if (!IsKnownWorldAction(action))
                    {
                        throw new ArgumentException(
                            $"Unknown action: '{action}'. Valid actions: {ValidActionsText}");
                    }

                    return SerializeResult(false, MissingRequiredParametersMessage(action), action);
                }

                // World executors commonly touch Unity APIs; always marshal to the Unity main thread.
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(envelope);
                cancellationToken.ThrowIfCancellationRequested();
                await UniTask.SwitchToMainThread(cancellationToken);
                bool success = _executor.TryExecute(new CoreAI.Messaging.ApplyAiGameCommand
                {
                    CommandTypeId = CoreAI.Messaging.AiGameCommandTypeIds.WorldCommand,
                    JsonPayload = json
                });

                if (_settings.LogToolCallResults)
                {
                    _logger.LogInfo(GameLogFeature.MessagePipe, $"[Tool Call] world_command: {(success ? "SUCCESS" : "FAILED")} - {action}");
                }

                if (success && action == "list_animations")
                {
                    string[] anims = _executor.LastListedAnimations ?? Array.Empty<string>();
                    return SerializeResult(true, $"Found {anims.Length} animations: {string.Join(", ", anims)}", action);
                }

                if (success && action == "list_objects")
                {
                    var objs = _executor.LastListedObjects ?? new List<Dictionary<string, object>>();
                    return SerializeResult(true, $"Found {objs.Count} matching objects.\n" + 
                                                 Newtonsoft.Json.JsonConvert.SerializeObject(objs), action);
                }

                return SerializeResult(success,
                    success
                        ? $"World command '{action}' executed successfully"
                        : $"Failed to execute world command '{action}'",
                    action);
            }
            catch (Exception ex)
            {
                if (_settings.LogToolCallResults)
                {
                    _logger.LogError(GameLogFeature.MessagePipe, $"[Tool Call] world_command: FAILED - {ex.Message}");
                }

                return SerializeResult(false, $"World command failed: {ex.Message}", action);
            }
        }

        private static CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? targetName, float x,
            float y, float z)
        {
            if (string.IsNullOrEmpty(prefabKey))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Spawn(prefabKey, targetName ?? Guid.NewGuid().ToString("N"),
                new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateMoveCommand(string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Move(targetName, new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateDestroyCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Destroy(targetName);
        }

        private static CoreAiWorldCommandEnvelope CreateLoadSceneCommand(string? sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.LoadScene(sceneName);
        }

        private static CoreAiWorldCommandEnvelope CreateReloadSceneCommand()
        {
            return CoreAiWorldCommandEnvelope.ReloadScene();
        }

        private static CoreAiWorldCommandEnvelope CreateSetActiveCommand(string? targetName, bool active)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SetActive(targetName, active);
        }

        private static CoreAiWorldCommandEnvelope CreatePlayAnimationCommand(string? targetName, string? animationName)
        {
            if (string.IsNullOrEmpty(animationName))
            {
                return null;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.PlayAnimation(targetName, animationName);
        }

        private static CoreAiWorldCommandEnvelope CreateShowTextCommand(string? targetName, string? text)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(text))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.ShowText(targetName, text);
        }

        private static CoreAiWorldCommandEnvelope CreateApplyForceCommand(string? targetName, float x, float y, float z)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.ApplyForce(targetName, new Vector3(x, y, z));
        }

        private static CoreAiWorldCommandEnvelope CreateSpawnParticlesCommand(string? targetName, string? effectName)
        {
            if (string.IsNullOrEmpty(effectName))
            {
                return null;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SpawnParticles(targetName, effectName);
        }

        private static CoreAiWorldCommandEnvelope CreateListObjectsCommand(string? searchPattern)
        {
            return CoreAiWorldCommandEnvelope.ListObjects(searchPattern ?? "");
        }

        private static CoreAiWorldCommandEnvelope CreateStopAnimationCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.StopAnimation(targetName);
        }

        private static CoreAiWorldCommandEnvelope CreatePlaySoundCommand(string? targetName, string? clipName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.PlaySound(targetName, clipName ?? "", 1f);
        }

        private static CoreAiWorldCommandEnvelope CreateSetVolumeCommand(string? targetName, float volume)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.SetVolume(targetName, volume);
        }

        private static CoreAiWorldCommandEnvelope CreateHidePanelCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.HidePanel(targetName);
        }

        private static CoreAiWorldCommandEnvelope CreateUpdateScoreCommand(string? targetName, string? text)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(text)) return null;
            return CoreAiWorldCommandEnvelope.UpdateScore(targetName, text);
        }

        private static CoreAiWorldCommandEnvelope CreateSetVelocityCommand(string? targetName, float fx, float fy, float fz)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            return CoreAiWorldCommandEnvelope.SetVelocity(targetName, new Vector3(fx, fy, fz));
        }

        private static CoreAiWorldCommandEnvelope CreateListAnimationsCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.ListAnimations(targetName);
        }

        private const string ValidActionsText =
            "spawn, move, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, " +
            "list_animations, play_sound, set_volume, show_text, hide_panel, update_score, apply_force, " +
            "set_velocity, spawn_particles, list_objects";

        private static bool IsKnownWorldAction(string action)
        {
            return action switch
            {
                "spawn" or "move" or "destroy" or "load_scene" or "reload_scene" or "set_active" or
                    "play_animation" or "stop_animation" or "list_animations" or "play_sound" or
                    "set_volume" or "show_text" or "hide_panel" or "update_score" or "apply_force" or
                    "set_velocity" or "spawn_particles" or "list_objects" => true,
                _ => false
            };
        }

        private static string MissingRequiredParametersMessage(string action)
        {
            return action switch
            {
                "spawn" => "Missing required parameters for action 'spawn': prefabKey is required; targetName is recommended.",
                "move" => "Missing required parameters for action 'move': targetName is required.",
                "destroy" => "Missing required parameters for action 'destroy': targetName is required.",
                "load_scene" => "Missing required parameters for action 'load_scene': stringValue must be the scene name.",
                "set_active" => "Missing required parameters for action 'set_active': targetName is required.",
                "play_animation" => "Missing required parameters for action 'play_animation': targetName and animationName (or stringValue) are required.",
                "stop_animation" => "Missing required parameters for action 'stop_animation': targetName is required.",
                "list_animations" => "Missing required parameters for action 'list_animations': targetName is required (for example targetName='Enemy').",
                "play_sound" => "Missing required parameters for action 'play_sound': targetName and stringValue are required.",
                "set_volume" => "Missing required parameters for action 'set_volume': targetName is required.",
                "show_text" => "Missing required parameters for action 'show_text': targetName and textToDisplay (or stringValue) are required.",
                "hide_panel" => "Missing required parameters for action 'hide_panel': targetName is required.",
                "update_score" => "Missing required parameters for action 'update_score': targetName and textToDisplay (or stringValue) are required.",
                "apply_force" => "Missing required parameters for action 'apply_force': targetName and force components are required.",
                "set_velocity" => "Missing required parameters for action 'set_velocity': targetName and velocity components are required.",
                "spawn_particles" => "Missing required parameters for action 'spawn_particles': targetName and stringValue are required.",
                "list_objects" => "Missing required parameters for action 'list_objects'.",
                _ => $"Missing required parameters for action '{action}'."
            };
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