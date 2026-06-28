using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// LLM tool that exposes camera control operations.
    /// </summary>
    public sealed class CameraLlmTool : IAIFunctionsLlmTool
    {
        public string Name => "camera_tool";
        public string Description => "Access scene cameras to take screenshots for visual analysis.";
        public bool AllowDuplicates => false;
        public string ParametersSchema => "{}"; // managed by AIFunctionFactory

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            yield return AIFunctionFactory.Create(
                (Func<string, int, int, CancellationToken, Task<string>>)CaptureCameraAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "capture_camera",
                    Description =
                        "Take a screenshot from a specific camera (or 'main') and return it as a JPEG Base64 string."
                } // arguments: cameraName, width, height
            );
        }

        private async Task<string> CaptureCameraAsync(
            string cameraName = "main",
            int width = 512,
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
                string base64 = Convert.ToBase64String(jpgBytes);

                // Return as Data URI so that if the user wants to append it as an ImageContent, they can parse it easily
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    resolution = $"{width}x{height}",
                    camera = targetCam.name,
                    dataUri = $"data:image/jpeg;base64,{base64}"
                });
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
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

            RenderTexture rt = new(width, height, 24);
            RenderTexture previousTarget = targetCam.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                targetCam.targetTexture = rt;
                targetCam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                return tex.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            }
            finally
            {
                targetCam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }

                UnityEngine.Object.Destroy(rt);
            }
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
        /// Autonomous-tool follow-up lift: parses a <c>capture_camera</c> tool result (the JSON produced by
        /// this tool, carrying a <c>data:image/...;base64,</c> <c>dataUri</c>) into a MEAI
        /// <see cref="DataContent"/>. OpenAI tool-result messages cannot carry images, so after the model
        /// invokes <c>capture_camera</c> the host lifts the returned image into a follow-up <b>user</b>
        /// message (subscribe to <c>CoreAi.OnToolCallCompleted</c>, match
        /// <c>LlmToolCallInfo.ToolName</c> == <c>"capture_camera"</c>, call this on
        /// <c>ResultJson</c>) so the next model call receives the screenshot as an <c>image_url</c> part.
        /// Returns <c>false</c> for non-image / failed / unparseable results.
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
                var parsed = JsonConvert.DeserializeObject<CaptureCameraResult>(toolResultJson);
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

            // "data:" prefix is 5 chars; media type spans up to the first ';'.
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

        // Mirrors the shape serialized by CaptureCameraAsync; only fields needed for the lift.
        private sealed class CaptureCameraResult
        {
            public bool success { get; set; }
            public string dataUri { get; set; }
        }
    }
}