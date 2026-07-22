using System;
using CoreAI.Mcp.Tools;
using UnityEngine;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Captures the main camera to a PNG for the <c>screenshot</c> MCP tool. Must be called on the Unity
    /// main thread. Renders <see cref="Camera.main"/> (falling back to any enabled camera) into an
    /// off-screen <see cref="RenderTexture"/> so the capture works headless-of-display and in a build,
    /// then downscales to the requested cap.
    /// </summary>
    public sealed class MainCameraScreenshotSource : IScreenshotSource
    {
        /// <summary>True when a camera exists to capture from right now.</summary>
        public static bool HasCamera => ResolveCamera() != null;

        /// <inheritdoc />
        public string CaptureBase64Png(int maxResolution)
        {
            Camera camera = ResolveCamera();
            if (camera == null)
            {
                return null;
            }

            int width = Screen.width > 0 ? Screen.width : 1280;
            int height = Screen.height > 0 ? Screen.height : 720;
            if (maxResolution > 0)
            {
                float scale = Mathf.Min(1f, maxResolution / (float)Mathf.Max(width, height));
                width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            }

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false);

                byte[] png = texture.EncodeToPNG();
                return png != null ? Convert.ToBase64String(png) : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        private static Camera ResolveCamera()
        {
            if (Camera.main != null)
            {
                return Camera.main;
            }

            Camera[] all = Camera.allCameras;
            return all != null && all.Length > 0 ? all[0] : null;
        }
    }
}
