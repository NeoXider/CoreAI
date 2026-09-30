using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Vision;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Scene-camera tool registered by <c>CoreAi.RegisterCameraVisionTool</c>: one native function,
    /// <c>capture_camera</c>, that screenshots a named camera (or the main one). The frame is returned as an
    /// <see cref="LlmToolImageResult"/> — a small JSON summary for the tool message plus the JPEG as an image
    /// part — and the tool loop lifts it into the model's image channel automatically, like the agent-vision
    /// <c>camera_capture</c> tool (<see cref="CoreAI.Vision.CameraLlmTool"/>).
    /// </summary>
    public sealed class CameraLlmTool : IAIFunctionsLlmTool
    {
        public string Name => "camera_tool";
        public string Description => "Access scene cameras to take screenshots for visual analysis.";

        public bool AllowDuplicates => false;

        // WHY: This wrapper expands into native MEAI functions. The aggregate ILlmTool schema is intentionally
        // empty because each AIFunction.JsonSchema from CreateAIFunctions is authoritative.
        public string ParametersSchema => "{}";

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            yield return AIFunctionFactory.Create(
                (Func<string, int, int, CancellationToken, Task<object>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "capture_camera",
                    Description =
                        "Take a screenshot from a specific camera (or 'main') to SEE the scene. Returns a compact " +
                        "JSON summary; the image itself is attached for you to look at.",
                    MarshalResult = LlmToolImageResult.PreserveResult
                }
            );
        }

        /// <summary>
        /// Captures the camera and returns an <see cref="LlmToolImageResult"/>: the tool message is only the JSON
        /// summary (<c>success</c>, <c>resolution</c>, <c>camera</c>, <c>format</c>, <c>sizeBytes</c>,
        /// <c>imageAttached</c>), the JPEG travels out-of-band. WHY: the former base64 <c>dataUri</c> inside the JSON
        /// was never lifted, so the default result-size cut sent the model thousands of base64 characters and never
        /// the picture. Failures stay plain JSON strings.
        /// </summary>
        private async Task<object> CaptureCameraAsync(
            [Description("Camera GameObject name, or 'main' for the main camera. Default 'main'.")]
            string cameraName = "main",
            [Description("Screenshot width in pixels (clamped 64..1024). Default 512.")]
            int width = 512,
            [Description("Screenshot height in pixels (clamped 64..1024). Default 512.")]
            int height = 512,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                Camera targetCam = ResolveCamera(cameraName);
                if (targetCam == null)
                {
                    return SerializeError(
                        $"No camera perfectly matching '{cameraName}' and no active cameras found in the scene.");
                }

                // Clamp resolution to avoid memory overflow (vision models rarely need > 1024).
                width = Mathf.Clamp(width, 64, 1024);
                height = Mathf.Clamp(height, 64, 1024);

                byte[] jpgBytes = CaptureCameraJpeg(targetCam, width, height);
                string summaryJson = JsonConvert.SerializeObject(new
                {
                    success = true,
                    resolution = $"{width}x{height}",
                    camera = targetCam.name,
                    format = "jpeg",
                    sizeBytes = jpgBytes.Length,
                    imageAttached = true
                });
                return new LlmToolImageResult(summaryJson, AiAttachment.Image(jpgBytes, "image/jpeg"));
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
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

        /// <summary>
        /// Renders <paramref name="targetCam"/> to an offscreen target at the given size (clamped to
        /// 64..1024) and returns the frame encoded as JPEG bytes. Restores the camera target texture and
        /// the active render texture. Must run on the Unity main thread.
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

        private string SerializeError(string error)
        {
            return JsonConvert.SerializeObject(new { success = false, error });
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
        /// Today's <c>capture_camera</c> result has no <c>dataUri</c> — the tool loop delivers its image to the model
        /// automatically, and <c>CoreAi.OnToolExecuted</c> hands hosts the <see cref="LlmToolImageResult"/> whose
        /// <c>Images</c> hold the frame — so this returns <c>false</c> for it, as it does for non-image, failed or
        /// unparseable results.
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

        // WHY: Mirrors the shape serialized by CaptureCameraAsync; only fields needed for the lift.
        private sealed class CaptureCameraResult
        {
            public bool success { get; set; }
            public string dataUri { get; set; }
        }
    }
}
