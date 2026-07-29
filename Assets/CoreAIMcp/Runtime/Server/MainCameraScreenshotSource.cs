using System;
using CoreAI.Logging;
using CoreAI.Mcp.Tools;
using UnityEngine;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Captures the main camera to a PNG for the <c>screenshot</c> MCP tool. Must be called on the Unity
    /// main thread. Renders <see cref="Camera.main"/> (falling back to any enabled camera) into an
    /// off-screen <see cref="RenderTexture"/> so the capture works headless-of-display and in a build,
    /// then downscales to the requested cap.
    /// <para>
    /// WHY: the camera is resolved per call, never cached at startup - a server started from a bootstrap
    /// scene would otherwise decide "no camera" forever, and the tool would stay missing from
    /// <c>tools/list</c> for the whole session.
    /// </para>
    /// </summary>
    public sealed class MainCameraScreenshotSource : IScreenshotSource
    {
        /// <inheritdoc />
        public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
        {
            base64Png = null;
            error = null;

            Camera camera = ResolveCamera();
            if (camera == null)
            {
                error = "no camera is active in the loaded scenes (Camera.main is null and " +
                        "Camera.allCameras is empty).";
                return false;
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
                if (png == null || png.Length == 0)
                {
                    error = $"EncodeToPNG produced no bytes for a {width}x{height} capture of camera " +
                            $"'{camera.name}'.";
                    return false;
                }

                base64Png = Convert.ToBase64String(png);
                return true;
            }
            catch (Exception ex)
            {
                // WHY: swallowing this used to report "no active camera" for a live camera - log the real
                // exception AND hand its text to the agent, which is the only one that can act on it.
                error = $"capture of camera '{camera.name}' at {width}x{height} failed: " +
                        $"{ex.GetType().Name}: {ex.Message}";
                Log.Instance.Error($"[CoreAI MCP] screenshot {error}\n{ex}");
                return false;
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
