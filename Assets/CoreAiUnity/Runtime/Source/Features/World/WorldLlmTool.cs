using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

        // Multi-action tool: identical repeats are legitimate (apply_force/set_velocity/show_text etc.).
        // The original duplicate-SPAWN spam is now prevented at the root by reasoning being enabled and the
        // self-describing [Description] schema (models emit distinct args), so blanket tool-level dedup is the
        // wrong layer — it would wrongly skip valid repeated physics/score calls.
        public override bool AllowDuplicates => true;

        public override string Description =>
            "Execute world commands to manipulate the game world. " +
            "Actions: spawn, change, set_color, destroy, load_scene, reload_scene, " +
            "set_active, play_animation, stop_animation, list_animations, show_text, " +
            "play_sound, set_volume, hide_panel, apply_force, set_velocity, list_objects. " +
            "Use 'spawn' to create objects (prefabKey can be a built-in primitive — " +
            "cube, sphere, cylinder, capsule, empty — or a registered prefab key). " +
            "For spawn, targetName and prefabKey are required; x/y/z, fx/fy/fz, scale/scaleX/scaleY/scaleZ, " +
            "and stringValue parent name are optional. One Unity unit is one meter. " +
            "Unscaled primitive sizes differ by shape: cube/sphere are 1m; cylinder and capsule are " +
            "2m TALL (not 1m) at 1m diameter — a plain cylinder is already twice as tall as a cube of the " +
            "same scaleY, so account for that when sizing towers, pillars, or trunks. " +
            "Use 'change' to update any subset of position, rotation, scale, and parent on an existing object. " +
            "Use 'set_color' to tint an object with an HTML color in stringValue. " +
            "'destroy' to remove, " +
            "'play_animation'/'stop_animation' to control animations, 'list_animations' to get available animations, " +
            "'play_sound'/'set_volume' for audio, 'show_text'/'hide_panel' for UI, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'apply_force'/'set_velocity' for physics. " +
            "Objects are targeted by 'targetName'. For play_animation, stop_animation, and list_animations " +
            "always pass targetName (for example targetName='Enemy'); do not put the target object name only in prose.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true,
                "Command: spawn, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects"),
            ("targetName", "string", false,
                "Object name to target. Required for spawn and most object actions."),
            ("x", "number", false, "World X coordinate in meters for spawn/change; omit to leave unchanged on change."),
            ("y", "number", false, "World Y coordinate in meters for spawn/change; omit to leave unchanged on change."),
            ("z", "number", false, "World Z coordinate in meters for spawn/change; omit to leave unchanged on change."),
            ("fx", "number", false,
                "Rotation X in degrees for spawn/change. Also Force X for apply_force."),
            ("fy", "number", false,
                "Rotation Y in degrees for spawn/change. Also Force Y for apply_force."),
            ("fz", "number", false,
                "Rotation Z in degrees for spawn/change. Also Force Z for apply_force."),
            ("scale", "number", false,
                "Uniform local scale for spawn/change. Omit or 0 = default on spawn, unchanged on change."),
            ("scaleX", "number", false,
                "Optional local X size/scale for non-uniform pieces. Use for wall length or platform width."),
            ("scaleY", "number", false,
                "Optional local Y size/scale for non-uniform pieces. Use for height."),
            ("scaleZ", "number", false,
                "Optional local Z size/scale for non-uniform pieces. Use for wall thickness/depth."),
            ("prefabKey", "string", false,
                "What to spawn: a built-in primitive (cube, sphere, cylinder, capsule, empty) or a registered prefab key. " +
                "Unscaled sizes: cube/sphere are 1m; cylinder/capsule are 2m tall (not 1m) at 1m diameter."),
            ("animationName", "string", false, "Name of the animation to play/stop"),
            ("textToDisplay", "string", false, "Text for show_text"),
            ("stringValue", "string", false,
                "Generic string value (search pattern for list_objects, clip name for play_sound, parent object name for change/spawn, HTML color for set_color)"),
            ("volume", "number", false, "Volume level 0.0-1.0 for set_volume")
        );

        public AIFunction CreateAIFunction()
        {
            ExecuteWorldCommandDelegate func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        private delegate Task<string> ExecuteWorldCommandDelegate(
            [Description(
                "Command: spawn, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects")]
            string action,
            [Description("World X coordinate in meters for spawn/change. Omit on change to leave X unchanged.")]
            float? x = null,
            [Description(
                "World Y coordinate in meters for spawn/change. Y is height; omit on change to leave Y unchanged.")]
            float? y = null,
            [Description("World Z coordinate in meters for spawn/change. Omit on change to leave Z unchanged.")]
            float? z = null,
            [Description(
                "Rotation X in degrees for spawn/change. Also Force X for apply_force.")]
            float? fx = null,
            [Description(
                "Rotation Y in degrees for spawn/change. Also Force Y for apply_force.")]
            float? fy = null,
            [Description(
                "Rotation Z in degrees for spawn/change. Also Force Z for apply_force.")]
            float? fz = null,
            [Description(
                "Uniform size for spawn/change. Omit or 0 = default on spawn, unchanged on change.")]
            float? scale = null,
            [Description("Optional local X size/scale for non-uniform objects.")]
            float? scaleX = null,
            [Description("Optional local Y size/scale for non-uniform objects.")]
            float? scaleY = null,
            [Description("Optional local Z size/scale for non-uniform objects.")]
            float? scaleZ = null,
            [Description(
                "What to spawn: a built-in primitive (cube, sphere, cylinder, capsule, empty) or a registered prefab key. " +
                "Unscaled sizes: cube/sphere are 1m; cylinder/capsule are 2m tall (not 1m) at 1m diameter.")]
            string? prefabKey = null,
            [Description("Object name to target or spawn.")]
            string? targetName = null,
            [Description(
                "Generic string value. For spawn/change this is the parent object name; for set_color it is an HTML color; for list_objects it is the search pattern.")]
            string? stringValue = null,
            [Description("Name of the animation to play/stop")]
            string? animationName = null,
            [Description("Text for show_text")] string? textToDisplay = null,
            [Description("Volume level 0.0-1.0 for set_volume")]
            float volume = 1f,
            CancellationToken cancellationToken = default);

        public async Task<string> ExecuteAsync(
            [Description(
                "Command: spawn, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects")]
            string action,
            [Description("World X coordinate in meters for spawn/change. Omit on change to leave X unchanged.")]
            float? x = null,
            [Description(
                "World Y coordinate in meters for spawn/change. Y is height; omit on change to leave Y unchanged.")]
            float? y = null,
            [Description("World Z coordinate in meters for spawn/change. Omit on change to leave Z unchanged.")]
            float? z = null,
            [Description(
                "Rotation X in degrees for spawn/change. Also Force X for apply_force.")]
            float? fx = null,
            [Description(
                "Rotation Y in degrees for spawn/change. Also Force Y for apply_force.")]
            float? fy = null,
            [Description(
                "Rotation Z in degrees for spawn/change. Also Force Z for apply_force.")]
            float? fz = null,
            [Description(
                "Uniform local size for spawn/change. Omit or 0 = default on spawn, unchanged on change.")]
            float? scale = null,
            [Description(
                "Optional local X size/scale for non-uniform objects. Use for wall length, bridge width, or platform width.")]
            float? scaleX = null,
            [Description(
                "Optional local Y size/scale for non-uniform objects. Use for height.")]
            float? scaleY = null,
            [Description(
                "Optional local Z size/scale for non-uniform objects. Use for wall thickness/depth.")]
            float? scaleZ = null,
            [Description(
                "What to spawn: a built-in primitive (cube, sphere, cylinder, capsule, empty) or a registered prefab key. " +
                "Unscaled sizes: cube/sphere are 1m; cylinder/capsule are 2m tall (not 1m) at 1m diameter.")]
            string? prefabKey = null,
            [Description(
                "Object name to target or create. Required for spawn and most object actions.")]
            string? targetName = null,
            [Description(
                "Generic string value (search pattern for list_objects, clip name for play_sound, parent object name for spawn/change, HTML color for set_color)")]
            string? stringValue = null,
            [Description("Name of the animation to play/stop")]
            string? animationName = null,
            [Description("Text for show_text")] string? textToDisplay = null,
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

                if (x.HasValue || y.HasValue || z.HasValue)
                {
                    args.Append($" pos=({x},{y},{z})");
                }

                if (fx.HasValue || fy.HasValue || fz.HasValue)
                {
                    args.Append($" force=({fx},{fy},{fz})");
                }

                if (Positive(scale) || Positive(scaleX) || Positive(scaleY) || Positive(scaleZ))
                {
                    args.Append($" scale=({scale},{scaleX},{scaleY},{scaleZ})");
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
                    "spawn" => CreateSpawnCommand(prefabKey, targetName, x, y, z, fx, fy, fz, scale, scaleX, scaleY,
                        scaleZ, stringValue),
                    "change" => CreateChangeCommand(targetName, x, y, z, fx, fy, fz, scale, scaleX, scaleY, scaleZ,
                        stringValue),
                    "set_color" => CreateSetColorCommand(targetName, stringValue),
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
                    "apply_force" => CreateApplyForceCommand(targetName, fx, fy, fz),
                    "set_velocity" => CreateSetVelocityCommand(targetName, fx, fy, fz),
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
                    bool hasRot = fx.HasValue || fy.HasValue || fz.HasValue;
                    bool hasScale = Positive(scale);
                    bool hasAxisScale = Positive(scaleX) || Positive(scaleY) || Positive(scaleZ);
                    string extra = string.Format(CultureInfo.InvariantCulture, " at ({0:0.##},{1:0.##},{2:0.##})",
                                       x ?? 0f, y ?? 0f, z ?? 0f)
                                   + (hasRot
                                       ? string.Format(CultureInfo.InvariantCulture, " rot=({0:0.#},{1:0.#},{2:0.#})",
                                           fx ?? 0f, fy ?? 0f, fz ?? 0f)
                                       : "")
                                   + (hasScale
                                       ? string.Format(CultureInfo.InvariantCulture, " scale={0:0.##}", scale ?? 0f)
                                       : "")
                                   + (hasAxisScale
                                       ? string.Format(CultureInfo.InvariantCulture,
                                           " scaleXYZ=({0:0.##},{1:0.##},{2:0.##})", scaleX ?? 0f, scaleY ?? 0f,
                                           scaleZ ?? 0f)
                                       : "");
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

        private CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? targetName, float? x,
            float? y, float? z, float? fx, float? fy, float? fz, float? scale, float? scaleX, float? scaleY,
            float? scaleZ,
            string? parentName)
        {
            if (string.IsNullOrEmpty(prefabKey) || string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            string name = targetName.Trim();

            Vector3 pos = new(x ?? 0f, y ?? 0f, z ?? 0f);

            // Only take the rotation+scale overload when the model actually asked for orientation or sizing,
            // so a plain spawn keeps the default rotation/scale (uniformScale <= 0 = leave at default).
            bool hasRotation = fx.HasValue || fy.HasValue || fz.HasValue;
            bool hasScale = Positive(scale) || Positive(scaleX) || Positive(scaleY) || Positive(scaleZ);
            if (hasRotation || hasScale)
            {
                CoreAiWorldCommandEnvelope env = CoreAiWorldCommandEnvelope.Spawn(
                    prefabKey,
                    name,
                    pos,
                    new Vector3(fx ?? 0f, fy ?? 0f, fz ?? 0f),
                    Positive(scale) ? scale.Value : 0f,
                    new Vector3(Positive(scaleX) ? scaleX.Value : 0f, Positive(scaleY) ? scaleY.Value : 0f,
                        Positive(scaleZ) ? scaleZ.Value : 0f));
                env.stringValue = parentName ?? "";
                return env;
            }

            CoreAiWorldCommandEnvelope plain = CoreAiWorldCommandEnvelope.Spawn(prefabKey, name, pos);
            plain.stringValue = parentName ?? "";
            return plain;
        }

        private static CoreAiWorldCommandEnvelope CreateChangeCommand(string? targetName, float? x, float? y, float? z,
            float? fx, float? fy, float? fz, float? scale, float? scaleX, float? scaleY, float? scaleZ,
            string? parentName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            bool hasPosition = x.HasValue || y.HasValue || z.HasValue;
            bool hasRotation = fx.HasValue || fy.HasValue || fz.HasValue;
            bool hasScale = Positive(scale) || Positive(scaleX) || Positive(scaleY) || Positive(scaleZ);
            bool hasParent = !string.IsNullOrWhiteSpace(parentName);
            if (!hasPosition && !hasRotation && !hasScale && !hasParent)
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.Change(
                targetName,
                new Vector3(x ?? 0f, y ?? 0f, z ?? 0f),
                hasPosition,
                x.HasValue,
                y.HasValue,
                z.HasValue,
                new Vector3(fx ?? 0f, fy ?? 0f, fz ?? 0f),
                hasRotation,
                fx.HasValue,
                fy.HasValue,
                fz.HasValue,
                Positive(scale) ? scale.Value : 0f,
                new Vector3(Positive(scaleX) ? scaleX.Value : 0f, Positive(scaleY) ? scaleY.Value : 0f,
                    Positive(scaleZ) ? scaleZ.Value : 0f),
                hasScale,
                parentName);
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

        private static CoreAiWorldCommandEnvelope CreateApplyForceCommand(string? targetName, float? x, float? y,
            float? z)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            // Require at least one force component. A fully-omitted vector is a model mistake, not a zero-force
            // request; an explicit 0 (e.g. fx=0) still counts as provided and is honored.
            if (x is null && y is null && z is null)
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.ApplyForce(targetName, new Vector3(x ?? 0f, y ?? 0f, z ?? 0f));
        }

        private static CoreAiWorldCommandEnvelope CreateSetColorCommand(string? targetName, string? htmlColor)
        {
            if (string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(htmlColor))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SetColor(targetName, htmlColor);
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

        private static CoreAiWorldCommandEnvelope CreateSetVelocityCommand(string? targetName, float? fx, float? fy,
            float? fz)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            // Require at least one velocity component. A fully-omitted vector is a model mistake; an explicit
            // 0 on any axis still counts as provided, so an intentional stop (fx=0, fy=0, fz=0) is honored.
            if (fx is null && fy is null && fz is null)
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.SetVelocity(targetName, new Vector3(fx ?? 0f, fy ?? 0f, fz ?? 0f));
        }

        private static CoreAiWorldCommandEnvelope CreateListAnimationsCommand(string? targetName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            return CoreAiWorldCommandEnvelope.ListAnimations(targetName);
        }

        private static bool Positive(float? value)
        {
            return value.HasValue && value.Value > 0f;
        }

        private const string ValidActionsText =
            "spawn, change, set_color, destroy, load_scene, reload_scene, set_active, " +
            "play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, " +
            "apply_force, set_velocity, list_objects";

        private static bool IsKnownWorldAction(string action)
        {
            return action switch
            {
                "spawn" or "change" or "set_color" or "destroy" or "load_scene" or
                    "reload_scene" or "set_active" or "play_animation" or "stop_animation" or "list_animations" or
                    "play_sound" or "set_volume" or "show_text" or "hide_panel" or "apply_force" or
                    "set_velocity" or "list_objects" => true,
                _ => false
            };
        }

        private static string MissingRequiredParametersMessage(string action)
        {
            return action switch
            {
                "spawn" =>
                    "Missing required parameters for action 'spawn': prefabKey and targetName are required.",
                "change" =>
                    "Missing required parameters for action 'change': targetName and at least one optional transform/parent value are required.",
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
                "apply_force" =>
                    "Missing required parameters for action 'apply_force': targetName and force components are required.",
                "set_velocity" =>
                    "Missing required parameters for action 'set_velocity': targetName and velocity components are required.",
                "set_color" =>
                    "Missing required parameters for action 'set_color': targetName and stringValue (an HTML colour like #88aa33) are required.",
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