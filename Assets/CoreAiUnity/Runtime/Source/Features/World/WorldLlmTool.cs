using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// LLM tool that converts model requests into world commands.
    /// </summary>
    public sealed class WorldLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly ICoreAiWorldCommandExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly IGameLogger _logger;
        private readonly Func<string> _liveResultNote;

        // Monotonic counter for auto-naming unnamed spawns with a readable name (e.g. "cube_1") instead of
        // a GUID hash, so the hierarchy stays human-readable when the model omits targetName.
        private int _autoNameCounter;

        /// <param name="liveResultNote">
        /// Optional callback returning a short note appended to EVERY successful result message (e.g. a live
        /// "time remaining" countdown the benchmark feeds the model after each spawn). Null = no note.
        /// </param>
        public WorldLlmTool(ICoreAiWorldCommandExecutor executor, ICoreAISettings settings, IGameLogger logger,
            Func<string> liveResultNote = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _liveResultNote = liveResultNote;
        }

        public override string Name => "world_command";
        public override bool AllowDuplicates => false;

        public override string Description =>
            "Execute world commands to manipulate the game world. " +
            "Actions: spawn, move, rotate, set_scale, parent, destroy, load_scene, reload_scene, " +
            "set_active, play_animation, stop_animation, list_animations, show_text, " +
            "play_sound, set_volume, hide_panel, update_score, " +
            "apply_force, set_velocity, spawn_particles, list_objects. " +
            "Use 'spawn' to create objects (prefabKey can be a built-in primitive — " +
            "cube, sphere, cylinder, capsule, quad, empty — or a registered prefab key). " +
            "During spawn you can ALSO set rotation (fx/fy/fz degrees) and scale (uniform) in the same call, " +
            "or use separate 'rotate'/'set_scale' actions later. " +
            "'move' to reposition, 'rotate' to rotate (fx/fy/fz degrees), 'set_scale' to resize (uniform scale), " +
            "'parent' to attach a child to a parent (stringValue = parent name), 'destroy' to remove, " +
            "'play_animation'/'stop_animation' to control animations, 'list_animations' to get available animations, " +
            "'play_sound'/'set_volume' for audio, 'show_text'/'hide_panel'/'update_score' for UI, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'apply_force'/'set_velocity' for physics. " +
            "Objects are targeted by 'targetName'. For play_animation, stop_animation, and list_animations " +
            "always pass targetName (for example targetName='Enemy'); do not put the target object name only in prose.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true,
                "Command: spawn, move, rotate, set_scale, parent, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, update_score, apply_force, set_velocity, spawn_particles, list_objects"),
            ("targetName", "string", false,
                "Object name to target (required for move, rotate, set_scale, parent, destroy, set_active, play_animation, stop_animation, list_animations, etc). Used to set a name for spawned objects."),
            ("x", "number", false, "X coordinate (for spawn, move)"),
            ("y", "number", false, "Y coordinate (for spawn, move)"),
            ("z", "number", false, "Z coordinate (for spawn, move)"),
            ("fx", "number", false,
                "Rotation X in degrees. Works on 'rotate' AND directly on 'spawn' (spawn the object already turned). Also Force X for apply_force."),
            ("fy", "number", false,
                "Rotation Y in degrees. Works on 'rotate' AND directly on 'spawn'. Also Force Y for apply_force."),
            ("fz", "number", false,
                "Rotation Z in degrees. Works on 'rotate' AND directly on 'spawn'. Also Force Z for apply_force."),
            ("scale", "number", false,
                "Uniform scale. Works on 'set_scale' AND directly on 'spawn' (spawn the object already sized, e.g. 0.5 = half, 2 = double, 3 = a tall tower). Omit or 0 = default size."),
            ("prefabKey", "string", false,
                "What to spawn: a built-in primitive (cube, sphere, cylinder, capsule, quad, empty) or a registered prefab key"),
            ("animationName", "string", false, "Name of the animation to play/stop"),
            ("textToDisplay", "string", false, "Text for show_text / update_score"),
            ("stringValue", "string", false,
                "Generic string value (e.g. search pattern for list_objects, clip name for play_sound, parent object name for parent)"),
            ("volume", "number", false, "Volume level 0.0-1.0 for set_volume")
        );

        public AIFunction CreateAIFunction()
        {
            Func<string, float, float, float, float, float, float, float, string?, string?, string?, string?, string?,
                float,
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
            [Description(
                "Command: spawn, move, rotate, set_scale, parent, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, update_score, apply_force, set_velocity, spawn_particles, list_objects")]
            string action,
            [Description("X coordinate (for spawn, move)")]
            float x = 0f,
            [Description("Y coordinate (for spawn, move). Y is height: larger Y = higher; ground at y=0.")]
            float y = 0f,
            [Description("Z coordinate (for spawn, move)")]
            float z = 0f,
            [Description(
                "Rotation X in degrees. Works on 'rotate' AND directly on 'spawn' (the object is created already turned). Also Force X for apply_force. Vary it so objects are not all axis-aligned.")]
            float fx = 0f,
            [Description(
                "Rotation Y in degrees. Works on 'rotate' AND directly on 'spawn' (the object is created already turned, e.g. 45 for angled towers/roofs). Also Force Y for apply_force.")]
            float fy = 0f,
            [Description(
                "Rotation Z in degrees. Works on 'rotate' AND directly on 'spawn' (the object is created already turned). Also Force Z for apply_force.")]
            float fz = 0f,
            [Description(
                "Uniform size. Works on 'set_scale' AND directly on 'spawn' (the object is created already sized, e.g. 0.5 = half, 2 = double, 3 = a tall tower). Omit or 0 = default size. Vary it for differently sized pieces.")]
            float scale = 0f,
            [Description(
                "What to spawn: a built-in primitive (cube, sphere, cylinder, capsule, quad, empty) or a registered prefab key")]
            string? prefabKey = null,
            [Description(
                "Object name to target (required for move, rotate, set_scale, parent, destroy, set_active, play_animation, stop_animation, list_animations, etc). Used to set a name for spawned objects.")]
            string? targetName = null,
            [Description(
                "Generic string value (e.g. search pattern for list_objects, clip name for play_sound, parent object name for parent)")]
            string? stringValue = null,
            [Description("Name of the animation to play/stop")]
            string? animationName = null,
            [Description("Text for show_text / update_score")]
            string? textToDisplay = null,
            [Description("Volume level 0.0-1.0 for set_volume")]
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
                    "spawn" => CreateSpawnCommand(prefabKey, targetName, x, y, z, fx, fy, fz, scale),
                    "move" => CreateMoveCommand(targetName, x, y, z),
                    "rotate" => CreateRotateCommand(targetName, fx, fy, fz),
                    "set_scale" => CreateSetScaleCommand(targetName, scale),
                    "parent" => CreateParentCommand(targetName, stringValue),
                    "destroy" => CreateDestroyCommand(targetName),
                    "load_scene" => CreateLoadSceneCommand(stringValue),
                    "reload_scene" => CreateReloadSceneCommand(),
                    "set_active" => CreateSetActiveCommand(targetName, true),
                    "play_animation" => CreatePlayAnimationCommand(targetName, animationName ?? stringValue),
                    "stop_animation" => CreateStopAnimationCommand(targetName),
                    "list_animations" => CreateListAnimationsCommand(targetName),
                    "play_sound" => CreatePlaySoundCommand(targetName, stringValue, volume),
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
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[Tool Call] world_command: {(success ? "SUCCESS" : "FAILED")} - {action}");
                }

                if (success && action == "list_animations")
                {
                    string[] anims = _executor.LastListedAnimations ?? Array.Empty<string>();
                    return SerializeResult(true, $"Found {anims.Length} animations: {string.Join(", ", anims)}",
                        action);
                }

                if (success && action == "list_objects")
                {
                    List<Dictionary<string, object>> objs = _executor.LastListedObjects ??
                                                            new List<Dictionary<string, object>>();
                    return SerializeResult(true, $"Found {objs.Count} matching objects.\n" +
                                                 Newtonsoft.Json.JsonConvert.SerializeObject(objs), action);
                }

                // For spawn, echo the actually-applied transform (incl. rotation/scale when the model passed
                // them) into the result message, so the benchmark transcript records WHAT the model requested
                // — this is how we verify whether models use inline rotation/scale, not just that spawn ran.
                if (success && action == "spawn")
                {
                    bool hasRot = fx != 0f || fy != 0f || fz != 0f;
                    bool hasScale = scale > 0f;
                    string extra = $" at ({x:0.##},{y:0.##},{z:0.##})"
                                   + (hasRot ? $" rot=({fx:0.#},{fy:0.#},{fz:0.#})" : "")
                                   + (hasScale ? $" scale={scale:0.##}" : "");
                    return SerializeResult(true,
                        $"World command 'spawn' executed successfully{extra}{LiveNote()}", action);
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

        private CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? targetName, float x,
            float y, float z, float fx, float fy, float fz, float scale)
        {
            if (string.IsNullOrEmpty(prefabKey))
            {
                return null;
            }

            // Prefer the model-supplied name. When it is omitted, generate a READABLE name from the prefab
            // key plus a counter (e.g. "cube_1", "Enemy_2") rather than a GUID hash, so the scene hierarchy
            // stays legible. A leading digit or empty fallback is guarded with a generic "object_N".
            string name = targetName;
            if (string.IsNullOrWhiteSpace(name))
            {
                int n = ++_autoNameCounter;
                string stem = string.IsNullOrWhiteSpace(prefabKey) ? "object" : prefabKey.Trim();
                name = $"{stem}_{n}";
            }

            Vector3 pos = new(x, y, z);

            // Only take the rotation+scale overload when the model actually asked for orientation or sizing,
            // so a plain spawn keeps the default rotation/scale (uniformScale <= 0 = leave at default).
            bool hasRotation = fx != 0f || fy != 0f || fz != 0f;
            bool hasScale = scale > 0f;
            if (hasRotation || hasScale)
            {
                return CoreAiWorldCommandEnvelope.Spawn(prefabKey, name, pos, new Vector3(fx, fy, fz), scale);
            }

            return CoreAiWorldCommandEnvelope.Spawn(prefabKey, name, pos);
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

        private static CoreAiWorldCommandEnvelope CreateRotateCommand(string? targetName, float fx, float fy, float fz)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Rotate(targetName, new Vector3(fx, fy, fz));
        }

        private static CoreAiWorldCommandEnvelope CreateSetScaleCommand(string? targetName, float scale)
        {
            if (string.IsNullOrEmpty(targetName) || scale <= 0f)
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SetScale(targetName, scale);
        }

        private static CoreAiWorldCommandEnvelope CreateParentCommand(string? childName, string? parentName)
        {
            if (string.IsNullOrEmpty(childName) || string.IsNullOrEmpty(parentName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Parent(childName, parentName);
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
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.StopAnimation(targetName);
        }

        private static CoreAiWorldCommandEnvelope CreatePlaySoundCommand(string? targetName, string? clipName,
            float volume)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(clipName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.PlaySound(targetName, clipName, volume);
        }

        private static CoreAiWorldCommandEnvelope CreateSetVolumeCommand(string? targetName, float volume)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SetVolume(targetName, volume);
        }

        private static CoreAiWorldCommandEnvelope CreateHidePanelCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.HidePanel(targetName);
        }

        private static CoreAiWorldCommandEnvelope CreateUpdateScoreCommand(string? targetName, string? text)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(text))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.UpdateScore(targetName, text);
        }

        private static CoreAiWorldCommandEnvelope CreateSetVelocityCommand(string? targetName, float fx, float fy,
            float fz)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

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
            "spawn, move, rotate, set_scale, parent, destroy, load_scene, reload_scene, set_active, " +
            "play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, " +
            "update_score, apply_force, set_velocity, spawn_particles, list_objects";

        private static bool IsKnownWorldAction(string action)
        {
            return action switch
            {
                "spawn" or "move" or "rotate" or "set_scale" or "parent" or "destroy" or "load_scene" or
                    "reload_scene" or "set_active" or "play_animation" or "stop_animation" or "list_animations" or
                    "play_sound" or "set_volume" or "show_text" or "hide_panel" or "update_score" or "apply_force" or
                    "set_velocity" or "spawn_particles" or "list_objects" => true,
                _ => false
            };
        }

        private static string MissingRequiredParametersMessage(string action)
        {
            return action switch
            {
                "spawn" =>
                    "Missing required parameters for action 'spawn': prefabKey is required; targetName is recommended.",
                "move" => "Missing required parameters for action 'move': targetName is required.",
                "rotate" => "Missing required parameters for action 'rotate': targetName and fx/fy/fz (degrees) are required.",
                "set_scale" => "Missing required parameters for action 'set_scale': targetName and a positive scale are required.",
                "parent" => "Missing required parameters for action 'parent': targetName (child) and stringValue (parent name) are required.",
                "destroy" => "Missing required parameters for action 'destroy': targetName is required.",
                "load_scene" =>
                    "Missing required parameters for action 'load_scene': stringValue must be the scene name.",
                "set_active" => "Missing required parameters for action 'set_active': targetName is required.",
                "play_animation" =>
                    "Missing required parameters for action 'play_animation': targetName and animationName (or stringValue) are required.",
                "stop_animation" => "Missing required parameters for action 'stop_animation': targetName is required.",
                "list_animations" =>
                    "Missing required parameters for action 'list_animations': targetName is required (for example targetName='Enemy').",
                "play_sound" =>
                    "Missing required parameters for action 'play_sound': targetName and stringValue are required.",
                "set_volume" => "Missing required parameters for action 'set_volume': targetName is required.",
                "show_text" =>
                    "Missing required parameters for action 'show_text': targetName and textToDisplay (or stringValue) are required.",
                "hide_panel" => "Missing required parameters for action 'hide_panel': targetName is required.",
                "update_score" =>
                    "Missing required parameters for action 'update_score': targetName and textToDisplay (or stringValue) are required.",
                "apply_force" =>
                    "Missing required parameters for action 'apply_force': targetName and force components are required.",
                "set_velocity" =>
                    "Missing required parameters for action 'set_velocity': targetName and velocity components are required.",
                "set_color" =>
                    "Missing required parameters for action 'set_color': targetName and stringValue (an HTML colour like #88aa33) are required.",
                "spawn_particles" =>
                    "Missing required parameters for action 'spawn_particles': targetName and stringValue are required.",
                "list_objects" => "Missing required parameters for action 'list_objects'.",
                _ => $"Missing required parameters for action '{action}'."
            };
        }

        /// <summary>Live note (e.g. time-remaining) appended to a successful result, or "" when none set.</summary>
        private string LiveNote()
        {
            if (_liveResultNote == null)
            {
                return "";
            }

            try
            {
                string note = _liveResultNote();
                return string.IsNullOrWhiteSpace(note) ? "" : " — " + note.Trim();
            }
            catch
            {
                return "";
            }
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
        /// World Result component used by CoreAI.
        /// </summary>
        public sealed class WorldResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Action { get; set; }
        }
    }
}
