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
    /// Инструмент MEAI, позволяющий агенту делать снимки (скриншоты) с камер(ы) в игре.
    /// Возвращает Base64-строку изображения (или сохраняет на диск), чтобы модель могла его проанализировать, 
    /// если она поддерживает зрение (Vision).
    /// </summary>
    public sealed class CameraLlmTool : ILlmTool
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

                // Clamp resolution to avoid memory overflow (vision models rarely need > 1024)
                width = Mathf.Clamp(width, 64, 1024);
                height = Mathf.Clamp(height, 64, 1024);

                RenderTexture rt = new(width, height, 24);
                RenderTexture previousRt = targetCam.targetTexture;

                targetCam.targetTexture = rt;
                targetCam.Render();
                targetCam.targetTexture = previousRt;

                RenderTexture.active = rt;
                Texture2D tex = new(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                byte[] jpgBytes = tex.EncodeToJPG(75); // 75% quality to save tokens/memory

                UnityEngine.Object.Destroy(tex);
                UnityEngine.Object.Destroy(rt);

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

        private string SerializeError(string error)
        {
            return JsonConvert.SerializeObject(new { success = false, error });
        }
    }
}