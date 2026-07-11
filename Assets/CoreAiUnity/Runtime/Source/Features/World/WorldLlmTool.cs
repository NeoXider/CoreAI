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

        // WHY: World mutations must be idempotent across turns: an echoed or blind-retried identical spawn
        // would create a second object. AllowDuplicates=false lets ToolExecutionPolicy suppress only a
        // CROSS-TURN identical echo (structured no-op) while still allowing intra-turn repeats
        // ("spawn tree x3" in one turn) and never suppressing the retry of a FAILED call. A genuinely
        // repeated physics/score action stays legitimate because the model varies its arguments
        // (position, force) between real requests; a byte-identical repeat next turn is an echo.
        public override bool AllowDuplicates => false;

        public override string Description =>
            "Manipulate game world objects/scenes. 1 unit = 1 meter; cube/sphere unscaled=1m; " +
            "cylinder/capsule unscaled=2m TALL at 1m diameter (already twice a cube's height at the same scaleY). " +
            "Actions, params -> result (all results are compact JSON {success,message,action}; " +
            "message never echoes full inputs back):\n" +
            "spawn(prefabKey,targetName,x/y/z?,fx/fy/fz?,scale|scaleX/Y/Z?,stringValue=parent?,worldPositionStays=false?) -> ok, echoes applied transform. " +
            "prefabKey is a registered prefab key or a primitive (cube, sphere, cylinder, capsule, empty); " +
            "an unknown key's error lists available keys — call list_prefabs first if unsure.\n" +
            "spawn_batch(prefabKey?,targetName=namePrefix?,x/y/z/fx/fy/fz/scale*/stringValue=parent as per-item " +
            "defaults,itemsJson=JSON array of up to 100 {prefabKey?,name?,x,y,z,rx?,ry?,rz?,scale?|scaleX/Y/Z?," +
            "parent?,worldPositionStays?,color?}) -> ONE call spawns every item -> {ok,spawned,failed,names:[first few]}. " +
            "With a parent, transform coordinates are LOCAL by default; set worldPositionStays=true to preserve world space. " +
            "Without a parent, local and world coordinates are identical. Create the parent before its children.\n" +
            "For compound objects and related spawned parts, prefer a meaningful hierarchy: create one named empty root " +
            "(for example stall_root or well_root), then parent posts, roofs, props, and decoration under it instead of " +
            "leaving every piece flat at scene root.\n" +
            "list_prefabs() -> {prefabs:[registered keys],primitives:[built-in shapes]}.\n" +
            "change(targetName,x?/y?/z?,fx?/fy?/fz?,scale?|scaleX/Y/Z?,stringValue=parent?,worldPositionStays=false?) -> ok; only given fields change.\n" +
            "set_color(targetName,stringValue=htmlColor) -> ok. destroy(targetName) -> ok, removes the object.\n" +
            "load_scene(stringValue=sceneName) / reload_scene() -> ok. set_active(targetName) -> ok.\n" +
            "play_animation(targetName,animationName|stringValue) / stop_animation(targetName) -> ok; " +
            "list_animations(targetName) -> {animations:[...]}.\n" +
            "play_sound(targetName,stringValue=clipName,volume?) / set_volume(targetName,volume) -> ok.\n" +
            "show_text(targetName,textToDisplay|stringValue) / hide_panel(targetName) -> ok.\n" +
            "apply_force(targetName,fx,fy,fz) / set_velocity(targetName,fx,fy,fz) -> ok.\n" +
            "list_objects(stringValue=searchPattern?) -> {count,objects:[...]} (search by name).\n" +
            "Always pass targetName for play_animation/stop_animation/list_animations (e.g. targetName='Enemy'); " +
            "do not put the target object name only in prose.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true,
                "Command: spawn, spawn_batch, list_prefabs, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects"),
            ("targetName", "string", false,
                "Object name to target. Required for spawn and most object actions."),
            ("x", "number", false,
                "X coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit to leave unchanged on change."),
            ("y", "number", false,
                "Y coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit to leave unchanged on change."),
            ("z", "number", false,
                "Z coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit to leave unchanged on change."),
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
            ("worldPositionStays", "boolean", false,
                "Parenting transform policy for spawn/change. Default false: supplied position/rotation are local to parent. True: preserve world transform. Ignored when no parent is set."),
            ("volume", "number", false, "Volume level 0.0-1.0 for set_volume"),
            ("itemsJson", "string", false,
                "spawn_batch only: JSON array of up to 100 items, each " +
                "{prefabKey?,name?,x,y,z,rx?,ry?,rz?,scale?|scaleX/Y/Z?,parent?,worldPositionStays?,color?}. " +
                "Fields an item omits fall back to this call's prefabKey/x/y/z/fx/fy/fz/scale*/stringValue/worldPositionStays as defaults.")
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
                "Command: spawn, spawn_batch, list_prefabs, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects")]
            string action,
            [Description(
                "X coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit on change to leave X unchanged.")]
            float? x = null,
            [Description(
                "Y coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Y is height; omit on change to leave Y unchanged.")]
            float? y = null,
            [Description(
                "Z coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit on change to leave Z unchanged.")]
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
            [Description(
                "Parenting transform policy for spawn/change. Default false: coordinates are local to parent. True: preserve world transform. Ignored without parent.")]
            bool worldPositionStays = false,
            [Description("Name of the animation to play/stop")]
            string? animationName = null,
            [Description("Text for show_text")]
            string? textToDisplay = null,
            [Description("Volume level 0.0-1.0 for set_volume")]
            float volume = 1f,
            [Description(
                "spawn_batch only: JSON array of up to 100 items, each {prefabKey?,name?,x,y,z,rx?,ry?,rz?,scale?|scaleX/Y/Z?,parent?,worldPositionStays?,color?}.")]
            string? itemsJson = null,
            CancellationToken cancellationToken = default);

        public async Task<string> ExecuteAsync(
            [Description(
                "Command: spawn, spawn_batch, list_prefabs, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, set_velocity, list_objects")]
            string action,
            [Description(
                "X coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit on change to leave X unchanged.")]
            float? x = null,
            [Description(
                "Y coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Y is height; omit on change to leave Y unchanged.")]
            float? y = null,
            [Description(
                "Z coordinate in meters for spawn/change: local when parent is set and worldPositionStays=false; otherwise world. Omit on change to leave Z unchanged.")]
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
            [Description(
                "Parenting transform policy for spawn/change. Default false: coordinates are local to parent. True: preserve world transform. Ignored without parent.")]
            bool worldPositionStays = false,
            [Description("Name of the animation to play/stop")]
            string? animationName = null,
            [Description("Text for show_text")]
            string? textToDisplay = null,
            [Description("Volume level 0.0-1.0 for set_volume")]
            float volume = 1f,
            [Description(
                "spawn_batch only: JSON array of up to 100 items, each {prefabKey?,name?,x,y,z,rx?,ry?,rz?,scale?|scaleX/Y/Z?,parent?,worldPositionStays?,color?}.")]
            string? itemsJson = null,
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
                    args.Append($" worldPositionStays={worldPositionStays}");
                }

                if (!string.IsNullOrEmpty(animationName))
                {
                    args.Append($" animationName={animationName}");
                }

                if (!string.IsNullOrEmpty(textToDisplay))
                {
                    args.Append($" textToDisplay={textToDisplay}");
                }

                if (!string.IsNullOrEmpty(itemsJson))
                {
                    args.Append($" itemsJson.len={itemsJson.Length}");
                }

                if (args.Length > 0)
                {
                    _logger.LogInfo(GameLogFeature.MessagePipe, $"  args:{args}");
                }
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                if (action == "spawn_batch")
                {
                    return await ExecuteSpawnBatchAsync(prefabKey, targetName, x, y, z, fx, fy, fz, scale, scaleX,
                        scaleY, scaleZ, stringValue, worldPositionStays, itemsJson, cancellationToken);
                }

                if (action == "list_prefabs")
                {
                    return await ExecuteListPrefabsAsync(cancellationToken);
                }

                CoreAiWorldCommandEnvelope envelope = action switch
                {
                    "spawn" => CreateSpawnCommand(prefabKey, targetName, x, y, z, fx, fy, fz, scale, scaleX, scaleY,
                        scaleZ, stringValue, worldPositionStays),
                    "change" => CreateChangeCommand(targetName, x, y, z, fx, fy, fz, scale, scaleX, scaleY, scaleZ,
                        stringValue, worldPositionStays),
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

                // WHY: World executors commonly touch Unity APIs; always marshal to the Unity main thread.
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

                // WHY: For spawn, echo the actually-applied transform (incl. rotation/scale when the model passed
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

                // WHY: A failed spawn (most commonly an unknown prefabKey) carries a self-correcting detail —
                // e.g. the available registered/primitive keys — on the executor; surface it instead of the
                // generic message so the model can fix the call in one round without a blind retry.
                if (!success && action == "spawn" && !string.IsNullOrEmpty(_executor.LastErrorMessage))
                {
                    return SerializeResult(false, _executor.LastErrorMessage, action);
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

        /// <summary>
        /// Spawns every entry of <paramref name="itemsJson"/> in one dispatch. Missing per-item fields fall
        /// back to this call's top-level prefabKey/x/y/z/fx/fy/fz/scale*/stringValue defaults. Result is a
        /// compact summary, never a full echo of every spawned item — see TOOL_CALLING_BEST_PRACTICES.md.
        /// </summary>
        private async Task<string> ExecuteSpawnBatchAsync(
            string? prefabKey, string? namePrefix, float? x, float? y, float? z, float? fx, float? fy, float? fz,
            float? scale, float? scaleX, float? scaleY, float? scaleZ, string? parent, bool worldPositionStays,
            string? itemsJson,
            CancellationToken cancellationToken)
        {
            const string action = "spawn_batch";

            if (string.IsNullOrWhiteSpace(itemsJson))
            {
                return SerializeResult(false,
                    "Missing required parameters for action 'spawn_batch': itemsJson must be a JSON array with at least one item.",
                    action);
            }

            List<CoreAiSpawnBatchItem> items;
            try
            {
                Newtonsoft.Json.Linq.JArray rawItems = Newtonsoft.Json.Linq.JArray.Parse(itemsJson);
                items = rawItems.ToObject<List<CoreAiSpawnBatchItem>>() ?? new List<CoreAiSpawnBatchItem>();
                for (int i = 0; i < items.Count && i < rawItems.Count; i++)
                {
                    if (rawItems[i] is Newtonsoft.Json.Linq.JObject rawItem &&
                        rawItem.TryGetValue("worldPositionStays", StringComparison.OrdinalIgnoreCase, out _))
                    {
                        items[i].hasWorldPositionStays = true;
                    }
                }
            }
            catch (Exception ex)
            {
                return SerializeResult(false, $"spawn_batch: itemsJson is not a valid JSON array: {ex.Message}",
                    action);
            }

            if (items.Count == 0)
            {
                return SerializeResult(false,
                    "Missing required parameters for action 'spawn_batch': itemsJson must contain at least one item.",
                    action);
            }

            if (items.Count > CoreAiWorldCommandExecutor.MaxSpawnBatchSize)
            {
                return SerializeResult(false,
                    $"spawn_batch exceeds maximum of {CoreAiWorldCommandExecutor.MaxSpawnBatchSize} items ({items.Count} given).",
                    action);
            }

            CoreAiWorldCommandEnvelope envelope = CoreAiWorldCommandEnvelope.SpawnBatch(
                prefabKey ?? "",
                namePrefix ?? "",
                new Vector3(x ?? 0f, y ?? 0f, z ?? 0f),
                new Vector3(fx ?? 0f, fy ?? 0f, fz ?? 0f),
                Positive(scale) ? scale.Value : 0f,
                new Vector3(Positive(scaleX) ? scaleX.Value : 0f, Positive(scaleY) ? scaleY.Value : 0f,
                    Positive(scaleZ) ? scaleZ.Value : 0f),
                parent ?? "",
                items.ToArray(),
                worldPositionStays);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(envelope);
            cancellationToken.ThrowIfCancellationRequested();
            await UniTask.SwitchToMainThread(cancellationToken);
            bool dispatched = _executor.TryExecute(new CoreAI.Messaging.ApplyAiGameCommand
            {
                CommandTypeId = CoreAI.Messaging.AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json
            });

            if (_settings.LogToolCallResults)
            {
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[Tool Call] world_command: {(dispatched ? "SUCCESS" : "FAILED")} - {action}");
            }

            if (!dispatched)
            {
                return SerializeResult(false, "Failed to execute world command 'spawn_batch'", action);
            }

            CoreAiSpawnBatchResult result = _executor.LastSpawnBatchResult;
            int spawned = result?.Spawned ?? 0;
            int failed = result?.Failed ?? 0;
            List<string> names = result?.Names ?? new List<string>();
            bool ok = spawned > 0;
            string resultJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { ok, spawned, failed, names });
            return SerializeResult(ok, resultJson, action);
        }

        /// <summary>Lists registered prefab keys plus built-in primitive names, so a model can self-correct
        /// an unknown prefabKey without a trial-and-error spawn call.</summary>
        private async Task<string> ExecuteListPrefabsAsync(CancellationToken cancellationToken)
        {
            const string action = "list_prefabs";

            cancellationToken.ThrowIfCancellationRequested();
            await UniTask.SwitchToMainThread(cancellationToken);
            bool success = _executor.TryExecute(new CoreAI.Messaging.ApplyAiGameCommand
            {
                CommandTypeId = CoreAI.Messaging.AiGameCommandTypeIds.WorldCommand,
                JsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(CoreAiWorldCommandEnvelope.ListPrefabs())
            });

            if (_settings.LogToolCallResults)
            {
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[Tool Call] world_command: {(success ? "SUCCESS" : "FAILED")} - {action}");
            }

            if (!success)
            {
                return SerializeResult(false, "Failed to execute world command 'list_prefabs'", action);
            }

            IReadOnlyList<string> prefabs = _executor.LastListedPrefabKeys ?? Array.Empty<string>();
            string[] primitives = CoreAiPrimitiveFactory.SupportedKeys.Split(", ");
            string resultJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { prefabs, primitives });
            return SerializeResult(true, resultJson, action);
        }

        private CoreAiWorldCommandEnvelope CreateSpawnCommand(string? prefabKey, string? targetName, float? x,
            float? y, float? z, float? fx, float? fy, float? fz, float? scale, float? scaleX, float? scaleY,
            float? scaleZ,
            string? parentName,
            bool worldPositionStays)
        {
            if (string.IsNullOrEmpty(prefabKey) || string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            string name = targetName.Trim();

            Vector3 pos = new(x ?? 0f, y ?? 0f, z ?? 0f);

            // WHY: Only take the rotation+scale overload when the model actually asked for orientation or sizing,
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
                env.worldPositionStays = worldPositionStays;
                return env;
            }

            CoreAiWorldCommandEnvelope plain = CoreAiWorldCommandEnvelope.Spawn(prefabKey, name, pos);
            plain.stringValue = parentName ?? "";
            plain.worldPositionStays = worldPositionStays;
            return plain;
        }

        private static CoreAiWorldCommandEnvelope CreateChangeCommand(string? targetName, float? x, float? y, float? z,
            float? fx, float? fy, float? fz, float? scale, float? scaleX, float? scaleY, float? scaleZ,
            string? parentName,
            bool worldPositionStays)
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
                parentName,
                worldPositionStays);
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

            // WHY: Require at least one force component. A fully-omitted vector is a model mistake, not a zero-force
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

            // WHY: Require at least one velocity component. A fully-omitted vector is a model mistake; an explicit
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
            "spawn, spawn_batch, list_prefabs, change, set_color, destroy, load_scene, reload_scene, set_active, " +
            "play_animation, stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, " +
            "apply_force, set_velocity, list_objects";

        private static bool IsKnownWorldAction(string action)
        {
            return action switch
            {
                "spawn" or "spawn_batch" or "list_prefabs" or "change" or "set_color" or "destroy" or "load_scene" or
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
