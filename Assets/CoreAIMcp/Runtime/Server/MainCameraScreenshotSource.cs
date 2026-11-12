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
    public sealed class MainCameraScreenshotSource : ICameraScreenshotSource, IScreenCaptureSource
    {
        /// <summary>Seconds a screen capture waits for a frame to end before it fails.</summary>
        public const float ScreenCaptureTimeoutSeconds = 5f;

        private readonly MonoBehaviour _coroutineHost;

        /// <summary>A source that renders cameras only (no screen capture).</summary>
        public MainCameraScreenshotSource() : this(null)
        {
        }

        /// <param name="coroutineHost">
        /// The component whose coroutines wait for the end of a frame for <see cref="CaptureScreen"/> (the MCP server
        /// component); null leaves screen capture unavailable.
        /// </param>
        public MainCameraScreenshotSource(MonoBehaviour coroutineHost)
        {
            _coroutineHost = coroutineHost;
        }

        /// <summary>
        /// Captures the displayed frame, screen-space UI included, with <c>ScreenCapture.CaptureScreenshotAsTexture</c>
        /// after <c>WaitForEndOfFrame</c>. Refuses at once where no frame is drawn (batch mode, <c>-nographics</c>, edit
        /// mode) and fails after <see cref="ScreenCaptureTimeoutSeconds"/> when no frame ends (a paused game).
        /// </summary>
        public void CaptureScreen(int maxResolution, Action<string, string> done)
        {
            if (done == null) throw new ArgumentNullException(nameof(done));
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                done(null, "this run draws no screen (batch mode or -nographics), so there is nothing to capture; omit " +
                           "'screen' to render a camera offscreen.");
                return;
            }

            if (!Application.isPlaying)
            {
                done(null, "the screen is captured only in play mode and in players.");
                return;
            }

            if (_coroutineHost == null || !_coroutineHost.isActiveAndEnabled)
            {
                done(null, "no active component can wait for the end of a frame.");
                return;
            }

            ScreenCaptureRequest request = new(Math.Max(0, maxResolution), done);
            _coroutineHost.StartCoroutine(CaptureAtEndOfFrame(request));
            _coroutineHost.StartCoroutine(FailAfterDeadline(request));
        }

        private static System.Collections.IEnumerator CaptureAtEndOfFrame(ScreenCaptureRequest request)
        {
            yield return new WaitForEndOfFrame();
            if (request.Finished) yield break;
            Texture2D shot = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                byte[] png = shot.ToAiAttachment(request.MaxResolution, CaptureImageFormat.Png).Data;
                request.Finish(png == null || png.Length == 0 ? null : Convert.ToBase64String(png),
                    "the screen capture produced no bytes.");
            }
            catch (Exception ex)
            {
                Log.Instance.Error($"[CoreAI MCP] screen capture failed: {ex}");
                request.Finish(null, $"screen capture failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (shot != null) UnityEngine.Object.Destroy(shot);
            }
        }

        private static System.Collections.IEnumerator FailAfterDeadline(ScreenCaptureRequest request)
        {
            yield return new WaitForSecondsRealtime(ScreenCaptureTimeoutSeconds);
            request.Finish(null, $"no frame ended within {ScreenCaptureTimeoutSeconds:0} s (the game is paused, or " +
                                 "its window is not drawn).");
        }

        private sealed class ScreenCaptureRequest
        {
            private readonly Action<string, string> _done;

            public ScreenCaptureRequest(int maxResolution, Action<string, string> done)
            {
                MaxResolution = maxResolution;
                _done = done;
            }

            public int MaxResolution { get; }

            public bool Finished { get; private set; }

            public void Finish(string base64, string error)
            {
                if (Finished) return;
                Finished = true;
                _done(base64, base64 == null ? error : null);
            }
        }
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
