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
    /// Agent-vision tool: lets an LLM agent SEE the game (<c>camera_capture</c>, aliased as <c>screenshot</c>
    /// for discoverability), move its OWN camera (<c>camera_look</c>), and enumerate cameras
    /// (<c>camera_list</c>). One <see cref="ILlmTool"/> named <c>camera</c> expanding to four native MEAI
    /// functions. Capture is always read-only-safe on any camera; movement is gated by the opt-in
    /// <see cref="CoreAiAgentCamera"/> marker so the player's camera is never hijacked. See
    /// <c>Docs/CoreAI/agent-vision.md</c>.
    /// </summary>
    public sealed class CameraLlmTool : IAIFunctionsLlmTool
    {
        private readonly IAgentCameraService _service;
        private readonly string _agentRoleId;

        public CameraLlmTool(IAgentCameraService service, string agentRoleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _agentRoleId = agentRoleId;
        }

        public string Name => "camera";

        public string Description =>
            "See the game through a camera: capture a screenshot, move your own camera to look around, " +
            "or list available cameras.";

        public bool AllowDuplicates => false;

        // Wrapper expands into native MEAI functions; the aggregate schema is intentionally empty because
        // each AIFunction.JsonSchema (built from the [Description]-annotated parameters) is authoritative.
        public string ParametersSchema => "{}";

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            yield return AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<string>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_capture",
                    Description =
                        "Take a screenshot of the game camera to SEE the current scene and verify your work. " +
                        "Returns a compact JSON result with a text summary and a base64 image data URL. " +
                        "Defaults to your own camera, else the main camera. Does NOT move the camera."
                });

            // WHY: models that search tools by the literal word "screenshot" miss camera_capture, so this
            // alias exposes the identical capture behavior under a name that matches that search intent.
            yield return AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<string>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "screenshot",
                    Description =
                        "Take a screenshot of the game camera to SEE the current scene and verify your work. " +
                        "Alias for camera_capture: returns a compact JSON result with a text summary and a " +
                        "base64 image data URL. Defaults to your own camera, else the main camera. Does NOT " +
                        "move the camera."
                });

            yield return AIFunctionFactory.Create(
                (Func<string, float?, float?, float?, float?, float?, float?, string, CancellationToken, Task<string>>)
                LookAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_look",
                    Description =
                        "Move/rotate YOUR OWN camera to look around. Only works on a camera marked " +
                        "agent-controllable (CoreAiAgentCamera with allowMove); the player's camera cannot be moved."
                });

            yield return AIFunctionFactory.Create(
                (Func<CancellationToken, Task<string>>)ListAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "camera_list",
                    Description =
                        "List scene cameras with their name, whether they are the main camera, whether they are " +
                        "movable by you, and their current pose."
                });
        }

        private async Task<string> CaptureCameraAsync(
            [Description("Camera GameObject name, or 'main' for the main camera. Leave empty to use your own " +
                         "camera if you have one, otherwise the main camera.")]
            string cameraName = "",
            [Description("Long-edge size of the screenshot in pixels (clamped 64..1024). Default 512.")]
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
                            : $"No camera named '{cameraName}' and no fallback camera found.");
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
                    return Error(result.Error ?? "capture_failed", "Failed to capture the camera frame.");
                }

                string mime = result.Format == "png" ? "image/png" : "image/jpeg";
                string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(result.Bytes)}";
                Vector3 p = result.Pose.Position;
                Vector3 r = result.Pose.EulerRotation;

                return JsonConvert.SerializeObject(new
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
                    dataUrl
                });
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private async Task<string> LookAsync(
            [Description("Camera GameObject name, or 'main'. Leave empty to use your own agent camera.")]
            string cameraName = "",
            [Description("New world position X. Omit to leave unchanged.")]
            float? posX = null,
            [Description("New world position Y. Omit to leave unchanged.")]
            float? posY = null,
            [Description("New world position Z. Omit to leave unchanged.")]
            float? posZ = null,
            [Description("New Euler rotation X in degrees. Ignored when lookAt is set. Omit to leave unchanged.")]
            float? rotX = null,
            [Description("New Euler rotation Y in degrees. Ignored when lookAt is set. Omit to leave unchanged.")]
            float? rotY = null,
            [Description("New Euler rotation Z in degrees. Ignored when lookAt is set. Omit to leave unchanged.")]
            float? rotZ = null,
            [Description("Name of a GameObject to orient the camera toward. Overrides rotX/rotY/rotZ.")]
            string lookAt = null,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                if (!_service.TryResolveMovableCamera(cameraName, _agentRoleId, out Camera cam,
                        out CameraMoveDenial denial))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = DenialCode(denial.Reason),
                        message = denial.Message
                    });
                }

                if (!_service.TryApplyLook(cam, posX, posY, posZ, rotX, rotY, rotZ, lookAt, out string errorCode))
                {
                    return Error(errorCode ?? "look_failed",
                        errorCode == "no_change"
                            ? "Provide a position, rotation, or lookAt target to move the camera."
                            : errorCode == "target_not_found"
                                ? $"No GameObject named '{lookAt}' was found to look at."
                                : "Failed to move the camera.");
                }

                CameraPose pose = CameraPose.FromCamera(cam);
                Vector3 p = pose.Position;
                Vector3 r = pose.EulerRotation;
                return JsonConvert.SerializeObject(new
                {
                    ok = true,
                    summary = $"Moved '{cam.name}' to pos ({F(p.x)}, {F(p.y)}, {F(p.z)}), " +
                              $"rot ({F(r.x)}, {F(r.y)}, {F(r.z)}).",
                    camera = cam.name,
                    pose = PoseJson(pose)
                });
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
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
                        pose = PoseJson(info.Pose)
                    });
                }

                return JsonConvert.SerializeObject(new { ok = true, count = items.Count, cameras = items });
            }
            catch (Exception ex)
            {
                return Error("exception", ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private static string Error(string code, string message)
        {
            return JsonConvert.SerializeObject(new { ok = false, error = code, message });
        }

        private static string DenialCode(CameraMoveDenialReason reason)
        {
            return reason switch
            {
                CameraMoveDenialReason.NoCamera => "no_camera",
                CameraMoveDenialReason.NotMarked => "camera_not_movable",
                CameraMoveDenialReason.MovementDisabled => "camera_not_movable",
                CameraMoveDenialReason.WrongRole => "camera_not_movable",
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

            // "data:" prefix is 5 chars; media type spans up to the first ';'.
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

        // Mirrors only the fields needed for the host image lift.
        private sealed class CaptureResultShape
        {
            public bool ok { get; set; }
            public string dataUrl { get; set; }
        }
    }
}
