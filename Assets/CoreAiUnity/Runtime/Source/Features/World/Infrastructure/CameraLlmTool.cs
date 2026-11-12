using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Vision;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Scene-camera helpers for hosts: resolve a camera by name, capture it as JPEG bytes or as a MEAI
    /// <see cref="DataContent"/>, and read the legacy capture JSON that carried a base64 <c>dataUri</c>.
    /// <para>
    /// The LLM tool this class used to be (<c>camera_tool</c>, function <c>capture_camera</c>) was removed after its
    /// release of aliasing; the agent-vision <c>camera</c> tool (<see cref="CoreAI.Vision.CameraLlmTool"/>:
    /// <c>camera_capture</c>, <c>camera_look</c>, <c>camera_orbit</c>, <c>camera_list</c>) replaces it, and
    /// <c>CoreAi.RegisterCameraVisionTool</c> registers that one. A request allowlist that still names
    /// <c>camera_tool</c> or <c>capture_camera</c> reaches it (<c>AiOrchestrator</c> maps the old names).
    /// </para>
    /// </summary>
    public static class CameraLlmTool
    {
        /// <summary>The removed tool's registered name; an allowlist entry with it is read as <c>camera</c>.</summary>
        public const string RemovedToolName = "camera_tool";

        /// <summary>The removed tool's function name; an allowlist entry with it is read as <c>camera_capture</c>.</summary>
        public const string RemovedFunctionName = "capture_camera";

        /// <summary>
        /// Renders <paramref name="targetCam"/> to an offscreen target at the given size (clamped to
        /// 64..1024) and returns the frame encoded as JPEG bytes. Restores the camera target texture and
        /// the active render texture. Must run on the Unity main thread. Synchronous (one GPU stall); the
        /// <c>camera_capture</c> tool itself uses the non-blocking <see cref="CoreAI.Vision.ICameraFrameReadback"/>.
        /// </summary>
        public static byte[] CaptureCameraJpeg(Camera targetCam, int width, int height, int quality = 75)
        {
            if (targetCam == null)
            {
                throw new ArgumentNullException(nameof(targetCam));
            }

            width = Mathf.Clamp(width, 64, 1024);
            height = Mathf.Clamp(height, 64, 1024);

            // WHY: pooled render target + one cached readback texture instead of a new pair per capture.
            return AiAttachmentUnityExtensions.CaptureCamera(targetCam, width, height, CaptureImageFormat.Jpeg,
                quality);
        }

        /// <summary>
        /// Captures <paramref name="targetCam"/> and wraps the JPEG frame as a MEAI <see cref="DataContent"/>
        /// (<c>image/jpeg</c>). Attach it to a user <see cref="ChatMessage"/> so a vision-capable model
        /// receives the image — <see cref="MeaiOpenAiChatClient"/> serializes image content to OpenAI
        /// <c>image_url</c> parts.
        /// </summary>
        public static DataContent CaptureCameraImageContent(
            Camera targetCam,
            int width = 512,
            int height = 512,
            int quality = 75)
        {
            return new DataContent(CaptureCameraJpeg(targetCam, width, height, quality), "image/jpeg");
        }

        /// <summary>
        /// Resolves a scene camera by name. <c>"main"</c> (case-insensitive) or an empty/null name maps to
        /// <see cref="Camera.main"/>; otherwise the first GameObject named <paramref name="cameraName"/> with a
        /// <see cref="Camera"/> component is used. Falls back to the first active camera in the scene. Returns
        /// <c>null</c> when no camera exists. Must be called on the Unity main thread.
        /// </summary>
        public static Camera ResolveCamera(string cameraName = "main")
        {
            Camera targetCam = null;

            if (string.IsNullOrWhiteSpace(cameraName) ||
                string.Equals(cameraName, "main", StringComparison.OrdinalIgnoreCase))
            {
                targetCam = Camera.main;
            }
            else
            {
                GameObject camObj = GameObject.Find(cameraName);
                if (camObj != null)
                {
                    targetCam = camObj.GetComponent<Camera>();
                }
            }

            if (targetCam == null)
            {
                targetCam = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Exclude);
            }

            return targetCam;
        }

        /// <summary>
        /// Legacy helper: parses a camera result JSON that still carries a <c>data:image/...;base64,</c>
        /// <c>dataUri</c> (the shape <c>capture_camera</c> returned before it switched to
        /// <see cref="LlmToolImageResult"/>, or a host tool of the same shape) into a MEAI <see cref="DataContent"/>.
        /// Returns <c>false</c> for non-image, failed or unparseable results.
        /// </summary>
        public static bool TryExtractImageContentFromResult(string toolResultJson, out DataContent imageContent)
        {
            imageContent = null;
            if (string.IsNullOrWhiteSpace(toolResultJson))
            {
                return false;
            }

            string dataUri;
            try
            {
                CaptureCameraResult parsed = JsonConvert.DeserializeObject<CaptureCameraResult>(toolResultJson);
                if (parsed == null || !parsed.success || string.IsNullOrWhiteSpace(parsed.dataUri))
                {
                    return false;
                }

                dataUri = parsed.dataUri;
            }
            catch (JsonException)
            {
                return false;
            }

            return TryParseImageDataUri(dataUri, out imageContent);
        }

        /// <summary>
        /// Parses a <c>data:image/&lt;type&gt;;base64,&lt;payload&gt;</c> URI into a MEAI
        /// <see cref="DataContent"/>. Returns <c>false</c> for non-image or malformed data URIs.
        /// </summary>
        public static bool TryParseImageDataUri(string dataUri, out DataContent imageContent)
        {
            imageContent = null;
            if (string.IsNullOrWhiteSpace(dataUri) ||
                !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int comma = dataUri.IndexOf(',');
            int semicolon = dataUri.IndexOf(';');
            if (comma < 0 || semicolon < 0 || semicolon >= comma)
            {
                return false;
            }

            // WHY: "data:" prefix is 5 chars; media type spans up to the first ';'.
            string mediaType = dataUri.Substring(5, semicolon - 5);
            string base64 = dataUri.Substring(comma + 1);
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                imageContent = new DataContent(bytes, mediaType);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // WHY: Mirrors the legacy capture shape; only the fields needed for the lift.
        private sealed class CaptureCameraResult
        {
            public bool success { get; set; }
            public string dataUri { get; set; }
        }
    }
}
