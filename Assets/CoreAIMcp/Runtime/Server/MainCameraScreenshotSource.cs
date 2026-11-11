using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Mcp.Tools;
using CoreAI.Vision;
using UnityEngine;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Captures a camera to a PNG for the <c>screenshot</c> MCP tool. Must be called on the Unity main
    /// thread. Renders <see cref="Camera.main"/> (falling back to any enabled camera), or the camera named
    /// by the caller, off-screen through the shared <see cref="AiAttachmentUnityExtensions"/> capture: one
    /// pooled render target and one cached readback texture, so a capture allocates only its encoded bytes
    /// and works in edit mode, play mode and a build alike.
    /// <para>
    /// WHY: the camera is resolved per call, never cached at startup - a server started from a bootstrap
    /// scene would otherwise decide "no camera" forever, and the tool would stay missing from
    /// <c>tools/list</c> for the whole session.
    /// </para>
    /// </summary>
    public sealed class MainCameraScreenshotSource : ICameraScreenshotSource
    {
        /// <inheritdoc />
        public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
        {
            base64Png = null;
            Camera camera = ResolveDefaultCamera();
            if (camera == null)
            {
                error = "no camera is active in the loaded scenes (Camera.main is null and " +
                        "Camera.allCameras is empty).";
                return false;
            }

            return Capture(camera, maxResolution, out base64Png, out error);
        }

        /// <inheritdoc />
        public bool TryCaptureBase64Png(string cameraName, int maxResolution, out string base64Png, out string error)
        {
            base64Png = null;
            Camera camera = FindActiveCamera(cameraName, out string available);
            if (camera == null)
            {
                error = $"no active camera is named '{cameraName}'. Active cameras: {available}.";
                return false;
            }

            return Capture(camera, maxResolution, out base64Png, out error);
        }

        private static bool Capture(Camera camera, int maxResolution, out string base64Png, out string error)
        {
            base64Png = null;
            error = null;
            try
            {
                byte[] png = camera.CaptureAiAttachment(Math.Max(0, maxResolution), CaptureImageFormat.Png).Data;
                if (png == null || png.Length == 0)
                {
                    error = $"EncodeToPNG produced no bytes for a capture of camera '{camera.name}'.";
                    return false;
                }

                base64Png = Convert.ToBase64String(png);
                return true;
            }
            catch (Exception ex)
            {
                // WHY: log the real exception AND hand its text to the agent, which is the only one that
                // can act on it — a generic "no active camera" here would misdiagnose a live camera.
                error = $"capture of camera '{camera.name}' failed: {ex.GetType().Name}: {ex.Message}";
                Log.Instance.Error($"[CoreAI MCP] screenshot {error}\n{ex}");
                return false;
            }
        }

        private static Camera ResolveDefaultCamera()
        {
            if (Camera.main != null)
            {
                return Camera.main;
            }

            Camera[] all = Camera.allCameras;
            return all != null && all.Length > 0 ? all[0] : null;
        }

        private static Camera FindActiveCamera(string cameraName, out string available)
        {
            Camera[] all = Camera.allCameras ?? Array.Empty<Camera>();
            Camera caseInsensitive = null;
            int caseInsensitiveMatches = 0;
            List<string> names = new(all.Length);
            foreach (Camera candidate in all)
            {
                if (candidate == null)
                {
                    continue;
                }

                names.Add(candidate == Camera.main ? candidate.name + " (main)" : candidate.name);
                if (string.Equals(candidate.name, cameraName, StringComparison.Ordinal))
                {
                    available = null;
                    return candidate;
                }

                if (string.Equals(candidate.name, cameraName, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitive = candidate;
                    caseInsensitiveMatches++;
                }
            }

            available = names.Count == 0 ? "none" : string.Join(", ", names);
            return caseInsensitiveMatches == 1 ? caseInsensitive : null;
        }
    }
}
