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
                Camera targetCam = null;

                if (string.Equals(cameraName, "main", StringComparison.OrdinalIgnoreCase))
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
                    // Fallback to first available camera
                    targetCam = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Exclude);
                    if (targetCam == null)
                    {
                        return SerializeError(
                            $"No camera perfectly matching '{cameraName}' and no active cameras found in the scene.");
                    }
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
    }
}