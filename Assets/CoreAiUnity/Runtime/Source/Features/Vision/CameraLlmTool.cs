using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Vision
{
    /// <summary>
    /// Agent-vision tool: lets an LLM agent SEE the game and move its OWN camera. One <see cref="ILlmTool"/> named
    /// <c>camera</c> expanding to five native MEAI functions:
    /// <list type="bullet">
    /// <item><description><c>camera_capture</c> (alias <c>screenshot</c>) — capture a named camera, the agent's own
    /// camera or the free agent camera; never moves a camera.</description></item>
    /// <item><description><c>camera_look</c> — position, rotation, an object or point to aim at, field of view.</description></item>
    /// <item><description><c>camera_orbit</c> — yaw, pitch and distance around an object or a point.</description></item>
    /// <item><description><c>camera_list</c> — every camera with its pose and whether the agent may move it.</description></item>
    /// </list>
    /// Movement goes through the ownership gate of <see cref="IAgentCameraService"/>: the agent moves a camera marked
    /// for it (<see cref="CoreAiAgentCamera"/>) or its free camera, created on demand; the player's camera never moves.
    /// See <c>Docs/CoreAI/agent-vision.md</c>.
    /// </summary>
    public sealed class CameraLlmTool : IAIFunctionsLlmTool, ILlmToolCallEffects
    {
        private readonly IAgentCameraService _service;
        private readonly IAgentCameraRig _rig;
        private readonly string _agentRoleId;

        /// <summary>
        /// The tool for <paramref name="agentRoleId"/> over <paramref name="service"/>. Look and orbit use the service's
        /// own rig when it implements <see cref="IAgentCameraRig"/> (as <see cref="AgentCameraService"/> does), else a
        /// default <see cref="AgentCameraRig"/>.
        /// </summary>
        public CameraLlmTool(IAgentCameraService service, string agentRoleId)
            : this(service, agentRoleId, null)
        {
        }

        /// <summary>The tool with an explicit <paramref name="rig"/>; null picks the default described above.</summary>
        public CameraLlmTool(IAgentCameraService service, string agentRoleId, IAgentCameraRig rig)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _agentRoleId = agentRoleId;
            _rig = rig ?? service as IAgentCameraRig ?? new AgentCameraRig();
        }

        public string Name => "camera";

        /// <inheritdoc />
        /// <remarks>
        /// <c>camera_look</c> and <c>camera_orbit</c> move the agent's camera, so a capture repeated after them in the
        /// same request takes a new picture; capturing and listing only read.
        /// </remarks>
        public bool ChangesState(string functionName, IDictionary<string, object> arguments)
        {
            return string.Equals(functionName, "camera_look", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(functionName, "camera_orbit", StringComparison.OrdinalIgnoreCase);
        }

        public string Description =>
            "See the game through a camera: capture a screenshot, aim or orbit your own camera to look around, " +
            "or list available cameras.";

        public bool AllowDuplicates => false;

        // WHY: the wrapper expands into native MEAI functions, so the aggregate schema stays empty; each
        // AIFunction.JsonSchema (built from the [Description]-annotated parameters) is authoritative.
        public string ParametersSchema => "{}";

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            yield return AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<object>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_capture",
                    Description =
                        "Take a screenshot of a game camera to SEE the scene and check your work; the image is " +
                        "attached for you. Defaults to your own camera, else the main one. Moves no camera.",
                    MarshalResult = LlmToolImageResult.PreserveResult
                });

            // WHY: models that search tools by the literal word "screenshot" miss camera_capture, so this
            // alias exposes the identical capture behavior under a name that matches that search intent.
            yield return AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<object>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "screenshot",
                    Description =
                        "Take a screenshot to see the scene: the same as camera_capture.",
                    MarshalResult = LlmToolImageResult.PreserveResult
                });

            yield return AIFunctionFactory.Create(
                (Func<string, float?, float?, float?, float?, float?, float?, string, float?, float?, float?, float?,
                    CancellationToken, Task<string>>)LookAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_look",
                    Description =
                        "Move and aim YOUR OWN camera: position, rotation, an object (lookAt) or point " +
                        "(targetX/Y/Z) to look at, field of view. Your free camera starts at the main camera's " +
                        "view; the player's camera never moves. Then call camera_capture."
                });

            yield return AIFunctionFactory.Create(
                (Func<string, string, float?, float?, float?, float?, float?, float?, float?, CancellationToken,
                    Task<string>>)OrbitAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_orbit",
                    Description =
                        "Circle YOUR OWN camera around an object or point and aim at it (yaw, pitch, distance); " +
                        "omitted values and target stay as they were. Then call camera_capture."
                });

            yield return AIFunctionFactory.Create(
                (Func<CancellationToken, Task<string>>)ListAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_list",
                    Description =
                        "List cameras: name, main or not, movable by you, your free camera, pose."
                });
        }

        /// <summary>
        /// Captures a frame and returns it as an <see cref="LlmToolImageResult"/>: the JPEG/PNG bytes travel
        /// out-of-band to the model's image channel, the tool message is only the JSON summary. WHY: the former
        /// base64 data URL inside the JSON cost several full-size string copies plus a parse and a decode per
        /// capture before the image reached the model. Failures are plain JSON strings.
        /// </summary>
        private async Task<object> CaptureCameraAsync(
            [Description("Empty = yours, else the main one; 'main', 'agent' or a name from camera_list.")]
            string cameraName = "",
            [Description("Long edge in pixels, 64..1024 (default 512).")]
            int maxSize = 512,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                if (!_service.IsSceneReady)
                {
                    return Error("scene_loading", "The scene is still loading; try again shortly.");
                }

                Camera cam = _service.ResolveCaptureCamera(cameraName, _agentRoleId, out bool isMain);
                if (cam == null)
                {
                    return Error("no_camera",
                        string.IsNullOrWhiteSpace(cameraName)
                            ? "No camera found in the scene."
                            : $"No camera named '{cameraName}' and no fallback camera found. Call camera_list.");
                }

                if (!_service.TryReserveCapture(_agentRoleId, out double retryAfterSeconds))
                {
                    int retryMs = Mathf.CeilToInt((float)(retryAfterSeconds * 1000.0));
                    return JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = "rate_limited",
                        message = "Captures are rate limited; wait before capturing again.",
                        retryAfterMs = retryMs
                    });
                }

                CameraCaptureResult result =
                    await _service.CaptureAsync(cam, isMain, maxSize, CaptureImageFormat.Jpeg, cancellationToken);
                if (!result.Ok || result.Bytes == null || result.Bytes.Length == 0)
                {
                    return Error(result.Error ?? "capture_failed",
                        "Failed to capture the camera frame." +
                        (string.IsNullOrEmpty(result.Message) ? "" : " " + result.Message));
                }

                string mime = result.Format == "png" ? "image/png" : "image/jpeg";
                Vector3 p = result.Pose.Position;
                Vector3 r = result.Pose.EulerRotation;

                string summaryJson = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    summary =
                        $"Captured '{result.CameraName}'{(result.IsMain ? " (main camera)" : " (agent camera)")} " +
                        $"at {result.Width}x{result.Height}, pos ({F(p.x)}, {F(p.y)}, {F(p.z)}), " +
                        $"rot ({F(r.x)}, {F(r.y)}, {F(r.z)}).",
                    camera = result.CameraName,
                    isMain = result.IsMain,
                    width = result.Width,
                    height = result.Height,
                    format = result.Format,
                    sizeBytes = result.SizeBytes,
                    pose = PoseJson(result.Pose),
                    imageAttached = true
                });
                return new LlmToolImageResult(summaryJson, AiAttachment.Image(result.Bytes, mime));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                // WHY: WebGL has no ThreadPool, so awaiting SwitchToThreadPool there never resumes and the
                // whole turn hangs until the request timeout.
                await UniTask.SwitchToThreadPool();
#endif
            }
        }

        private async Task<string> LookAsync(
            [Description("Empty or 'agent' = yours, 'free' = your free camera, else a movable camera's name.")]
            string cameraName = "",
            [Description("Position X; omit to keep.")]
            float? posX = null,
            [Description("Position Y; omit to keep.")]
            float? posY = null,
            [Description("Position Z; omit to keep.")]
            float? posZ = null,
            [Description("Rotation X in degrees, ignored with a target; omit to keep.")]
            float? rotX = null,
            [Description("Rotation Y in degrees, ignored with a target; omit to keep.")]
            float? rotY = null,
            [Description("Rotation Z in degrees, ignored with a target; omit to keep.")]
            float? rotZ = null,
            [Description("Object to aim at; wins over the rotation and the point.")]
            string lookAt = null,
            [Description("Point to aim at, X (give X, Y and Z).")]
            float? targetX = null,
            [Description("Point to aim at, Y.")]
            float? targetY = null,
            [Description("Point to aim at, Z.")]
            float? targetZ = null,
            [Description("Vertical field of view in degrees (10..120); omit to keep.")]
            float? fov = null,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                if (!_service.TryResolveMovableCamera(cameraName, _agentRoleId, out Camera cam,
                        out CameraMoveDenial denial))
                {
                    return Denied(denial);
                }

                CameraRigResult result = _rig.Look(cam, new CameraLookRequest
                {
                    PositionX = posX,
                    PositionY = posY,
                    PositionZ = posZ,
                    RotationX = rotX,
                    RotationY = rotY,
                    RotationZ = rotZ,
                    LookAt = lookAt,
                    TargetX = targetX,
                    TargetY = targetY,
                    TargetZ = targetZ,
                    FieldOfView = fov
                });
                if (!result.Ok)
                {
                    return Error(result.ErrorCode ?? "look_failed", result.Message ?? "Failed to move the camera.");
                }

                CameraPose pose = CameraPose.FromCamera(cam);
                Vector3 p = pose.Position;
                Vector3 r = pose.EulerRotation;
                string aimed = result.HasTarget
                    ? result.TargetName != null
                        ? $", looking at '{result.TargetName}'"
                        : $", looking at ({F(result.TargetPoint.x)}, {F(result.TargetPoint.y)}, {F(result.TargetPoint.z)})"
                    : "";
                return JsonConvert.SerializeObject(new
                {
                    ok = true,
                    summary = $"Moved '{cam.name}' to pos ({F(p.x)}, {F(p.y)}, {F(p.z)}), " +
                              $"rot ({F(r.x)}, {F(r.y)}, {F(r.z)}), fov {F(pose.FieldOfView)}{aimed}. " +
                              "Call camera_capture to see the new view.",
                    camera = cam.name,
                    pose = PoseJson(pose)
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                await UniTask.SwitchToThreadPool();
#endif
            }
        }

        private async Task<string> OrbitAsync(
            [Description("Empty or 'agent' = yours, 'free' = your free camera.")]
            string cameraName = "",
            [Description("Object to circle (its centre); wins over the point.")]
            string target = null,
            [Description("Point to circle, X (give X, Y and Z).")]
            float? targetX = null,
            [Description("Point to circle, Y.")]
            float? targetY = null,
            [Description("Point to circle, Z.")]
            float? targetZ = null,
            [Description("Heading in degrees: 0 looks along +Z, 90 along +X; omit to keep.")]
            float? yaw = null,
            [Description("Degrees above the target: 0 level, 89 top-down; omit to keep.")]
            float? pitch = null,
            [Description("Distance in world units; omit to keep.")]
            float? distance = null,
            [Description("Vertical field of view in degrees (10..120); omit to keep.")]
            float? fov = null,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                if (!_service.TryResolveMovableCamera(cameraName, _agentRoleId, out Camera cam,
                        out CameraMoveDenial denial))
                {
                    return Denied(denial);
                }

                CameraRigResult result = _rig.Orbit(cam, new CameraOrbitRequest
                {
                    Target = target,
                    TargetX = targetX,
                    TargetY = targetY,
                    TargetZ = targetZ,
                    Yaw = yaw,
                    Pitch = pitch,
                    Distance = distance,
                    FieldOfView = fov
                });
                if (!result.Ok)
                {
                    return Error(result.ErrorCode ?? "orbit_failed", result.Message ?? "Failed to orbit the camera.");
                }

                CameraPose pose = CameraPose.FromCamera(cam);
                Vector3 pivot = result.TargetPoint;
                string around = result.TargetName != null
                    ? $"'{result.TargetName}'"
                    : $"({F(pivot.x)}, {F(pivot.y)}, {F(pivot.z)})";
                return JsonConvert.SerializeObject(new
                {
                    ok = true,
                    summary = $"Orbited '{cam.name}' around {around}: yaw {F(result.Yaw)}, pitch {F(result.Pitch)}, " +
                              $"distance {F(result.Distance)}. Call camera_capture to see the view.",
                    camera = cam.name,
                    target = result.TargetName,
                    pivot = new { x = pivot.x, y = pivot.y, z = pivot.z },
                    yaw = Round(result.Yaw),
                    pitch = Round(result.Pitch),
                    distance = Round(result.Distance),
                    pose = PoseJson(pose)
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                await UniTask.SwitchToThreadPool();
#endif
            }
        }

        private async Task<string> ListAsync(CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                IReadOnlyList<AgentCameraInfo> cameras = _service.ListCameras(_agentRoleId);
                List<object> items = new(cameras.Count);
                foreach (AgentCameraInfo info in cameras)
                {
                    items.Add(new
                    {
                        name = info.Name,
                        isMain = info.IsMain,
                        marked = info.IsMarked,
                        movable = info.Movable,
                        free = info.IsFree,
                        pose = PoseJson(info.Pose)
                    });
                }

                return JsonConvert.SerializeObject(new { ok = true, count = items.Count, cameras = items });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                await UniTask.SwitchToThreadPool();
#endif
            }
        }

        private static string Error(string code, string message)
        {
            return JsonConvert.SerializeObject(new { ok = false, error = code, message });
        }

        private static string Denied(CameraMoveDenial denial)
        {
            return JsonConvert.SerializeObject(new
            {
                ok = false,
                error = DenialCode(denial.Reason),
                message = denial.Message
            });
        }

        private static string DenialCode(CameraMoveDenialReason reason)
        {
            return reason switch
            {
                CameraMoveDenialReason.NoCamera => "no_camera",
                _ => "camera_not_movable"
            };
        }

        private static object PoseJson(CameraPose pose)
        {
            return new
            {
                position = new { x = pose.Position.x, y = pose.Position.y, z = pose.Position.z },
                rotation = new { x = pose.EulerRotation.x, y = pose.EulerRotation.y, z = pose.EulerRotation.z },
                fieldOfView = pose.FieldOfView
            };
        }

        private static float Round(float v)
        {
            return (float)Math.Round(v, 2);
        }

        private static string F(float v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Host lift helper: extracts the <c>dataUrl</c> from a successful <c>camera_capture</c> result. Since
        /// OpenAI tool-result messages cannot carry images, the host lifts this into a follow-up user message
        /// so a vision model receives the screenshot as an <c>image_url</c> part. Returns false for
        /// failed/non-image/unparseable results.
        /// </summary>
        public static bool TryGetImageDataUrl(string toolResultJson, out string dataUrl)
        {
            dataUrl = null;
            if (string.IsNullOrWhiteSpace(toolResultJson))
            {
                return false;
            }

            try
            {
                CaptureResultShape parsed = JsonConvert.DeserializeObject<CaptureResultShape>(toolResultJson);
                if (parsed == null || !parsed.ok || string.IsNullOrWhiteSpace(parsed.dataUrl))
                {
                    return false;
                }

                dataUrl = parsed.dataUrl;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Parses a <c>data:image/&lt;type&gt;;base64,&lt;payload&gt;</c> URL into a MEAI
        /// <see cref="DataContent"/> for attaching to a user message. Returns false for non-image or
        /// malformed data URLs.
        /// </summary>
        public static bool TryParseImageDataUrl(string dataUrl, out DataContent imageContent)
        {
            imageContent = null;
            if (string.IsNullOrWhiteSpace(dataUrl) ||
                !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int comma = dataUrl.IndexOf(',');
            int semicolon = dataUrl.IndexOf(';');
            if (comma < 0 || semicolon < 0 || semicolon >= comma)
            {
                return false;
            }

            // WHY: "data:" is 5 chars and the media type runs up to the first ';'.
            string mediaType = dataUrl.Substring(5, semicolon - 5);
            string base64 = dataUrl.Substring(comma + 1);
            try
            {
                imageContent = new DataContent(Convert.FromBase64String(base64), mediaType);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // WHY: mirrors only the fields the host image lift needs.
        private sealed class CaptureResultShape
        {
            public bool ok { get; set; }
            public string dataUrl { get; set; }
        }
    }
}
