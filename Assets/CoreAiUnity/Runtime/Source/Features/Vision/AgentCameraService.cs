using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Vision
{
    /// <summary>Encoding for a captured camera frame.</summary>
    public enum CaptureImageFormat
    {
        /// <summary>Lossy JPEG (default) — best for photographic 3D frames / low token cost.</summary>
        Jpeg,

        /// <summary>Lossless PNG — best for crisp UI / pixel-art captures.</summary>
        Png
    }

    /// <summary>Why an agent may not move a camera.</summary>
    public enum CameraMoveDenialReason
    {
        /// <summary>Movement is permitted (no denial).</summary>
        None,

        /// <summary>No camera resolved for the request.</summary>
        NoCamera,

        /// <summary>The camera has no <see cref="CoreAiAgentCamera"/> marker (player cameras are protected).</summary>
        NotMarked,

        /// <summary>The camera is marked but <see cref="CoreAiAgentCamera.AllowMove"/> is false.</summary>
        MovementDisabled,

        /// <summary>The camera is reserved for a different agent role.</summary>
        WrongRole
    }

    /// <summary>A camera pose snapshot (world position, Euler rotation, vertical FOV).</summary>
    public readonly struct CameraPose
    {
        public CameraPose(Vector3 position, Vector3 eulerRotation, float fieldOfView)
        {
            Position = position;
            EulerRotation = eulerRotation;
            FieldOfView = fieldOfView;
        }

        public Vector3 Position { get; }
        public Vector3 EulerRotation { get; }
        public float FieldOfView { get; }

        public static CameraPose FromCamera(Camera cam)
        {
            Transform t = cam.transform;
            return new CameraPose(t.position, t.eulerAngles, cam.fieldOfView);
        }
    }

    /// <summary>Result of a movement gate check (denial reason + a human-readable message).</summary>
    public readonly struct CameraMoveDenial
    {
        public CameraMoveDenial(CameraMoveDenialReason reason, string message)
        {
            Reason = reason;
            Message = message;
        }

        public CameraMoveDenialReason Reason { get; }
        public string Message { get; }

        public static CameraMoveDenial Allowed => new(CameraMoveDenialReason.None, null);
    }

    /// <summary>Descriptor for one scene camera, evaluated for a specific agent role.</summary>
    public sealed class AgentCameraInfo
    {
        public string Name { get; set; }
        public bool IsMain { get; set; }
        public bool IsMarked { get; set; }
        public bool Movable { get; set; }
        public CameraPose Pose { get; set; }
    }

    /// <summary>Outcome of a camera capture.</summary>
    public sealed class CameraCaptureResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string CameraName { get; set; }
        public bool IsMain { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SizeBytes { get; set; }
        public string Format { get; set; }
        public byte[] Bytes { get; set; }
        public CameraPose Pose { get; set; }
    }

    /// <summary>
    /// Resolves cameras for agents, enforces the opt-in ownership/movement model, rate-limits captures, and
    /// renders camera frames to image bytes. See <c>Docs/CoreAI/agent-vision.md</c>.
    /// </summary>
    public interface IAgentCameraService
    {
        /// <summary>True when the active scene has finished loading (no capture mid scene-load).</summary>
        bool IsSceneReady { get; }

        /// <summary>Lists every active scene camera, evaluating <c>Movable</c> for <paramref name="agentRoleId"/>.</summary>
        IReadOnlyList<AgentCameraInfo> ListCameras(string agentRoleId);

        /// <summary>
        /// Read-only capture-target resolution: explicit <paramref name="cameraName"/> →
        /// the agent's own marked camera → <see cref="Camera.main"/> → first active camera. Returns null
        /// when no camera exists. Capture never moves the camera, so this is always safe.
        /// </summary>
        Camera ResolveCaptureCamera(string cameraName, string agentRoleId, out bool isMain);

        /// <summary>
        /// Movement gate: outputs the camera the agent may move, or a typed <paramref name="denial"/>
        /// explaining why not (unmarked / movement disabled / wrong role / no camera).
        /// </summary>
        bool TryResolveMovableCamera(string cameraName, string agentRoleId, out Camera camera, out CameraMoveDenial denial);

        /// <summary>
        /// Applies a move/rotate/look-at to an already-gated camera. <paramref name="errorCode"/> is null on
        /// success; otherwise <c>no_change</c> or <c>target_not_found</c>.
        /// </summary>
        bool TryApplyLook(Camera camera, float? px, float? py, float? pz, float? rx, float? ry, float? rz,
            string lookAtTarget, out string errorCode);

        /// <summary>
        /// Rate limit: reserves a capture slot for <paramref name="agentRoleId"/>. Returns false and the
        /// seconds until the next allowed capture when called too soon after the previous one.
        /// </summary>
        bool TryReserveCapture(string agentRoleId, out double retryAfterSeconds);

        /// <summary>Renders <paramref name="camera"/> to image bytes on the Unity main thread.</summary>
        UniTask<CameraCaptureResult> CaptureAsync(Camera camera, bool isMain, int maxSize, CaptureImageFormat format,
            CancellationToken cancellationToken);
    }

    /// <inheritdoc cref="IAgentCameraService"/>
    public sealed class AgentCameraService : IAgentCameraService
    {
        /// <summary>Default long-edge capture size in pixels.</summary>
        public const int DefaultMaxSize = 512;

        /// <summary>Minimum long-edge capture size in pixels.</summary>
        public const int MinSize = 64;

        /// <summary>Maximum long-edge capture size in pixels (memory + token cap).</summary>
        public const int MaxSize = 1024;

        /// <summary>Default minimum seconds between captures per agent.</summary>
        public const double DefaultMinCaptureIntervalSeconds = 1.0;

        private const int JpegQuality = 75;

        private readonly Func<double> _clockSeconds;
        private readonly double _minCaptureIntervalSeconds;
        private readonly object _lock = new();
        private readonly Dictionary<string, double> _lastCaptureAtSeconds = new(StringComparer.Ordinal);

        /// <summary>Production constructor: monotonic <see cref="System.Diagnostics.Stopwatch"/> clock, 1s rate limit.</summary>
        public AgentCameraService()
            : this(DefaultStopwatchClock, DefaultMinCaptureIntervalSeconds)
        {
        }

        /// <summary>Test/config constructor: inject a clock (seconds) and rate-limit interval.</summary>
        public AgentCameraService(Func<double> clockSeconds, double minCaptureIntervalSeconds)
        {
            _clockSeconds = clockSeconds ?? DefaultStopwatchClock;
            _minCaptureIntervalSeconds = minCaptureIntervalSeconds < 0 ? 0 : minCaptureIntervalSeconds;
        }

        private static double DefaultStopwatchClock()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        public bool IsSceneReady => SceneManager.GetActiveScene().isLoaded;

        public IReadOnlyList<AgentCameraInfo> ListCameras(string agentRoleId)
        {
            Camera main = Camera.main;
            Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            List<AgentCameraInfo> list = new(cameras.Length);
            foreach (Camera cam in cameras)
            {
                if (cam == null)
                {
                    continue;
                }

                CoreAiAgentCamera marker = cam.GetComponent<CoreAiAgentCamera>();
                list.Add(new AgentCameraInfo
                {
                    Name = cam.name,
                    IsMain = cam == main,
                    IsMarked = marker != null,
                    Movable = marker != null && marker.IsMovableBy(agentRoleId),
                    Pose = CameraPose.FromCamera(cam)
                });
            }

            return list;
        }

        public Camera ResolveCaptureCamera(string cameraName, string agentRoleId, out bool isMain)
        {
            Camera cam = ResolveCameraCandidate(cameraName, agentRoleId, preferOwn: true);
            isMain = cam != null && cam == Camera.main;
            return cam;
        }

        public bool TryResolveMovableCamera(string cameraName, string agentRoleId, out Camera camera,
            out CameraMoveDenial denial)
        {
            camera = null;
            Camera candidate = ResolveMovableCandidate(cameraName, agentRoleId);
            if (candidate == null)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.NoCamera,
                    string.IsNullOrWhiteSpace(cameraName)
                        ? "No camera found to move."
                        : $"No camera named '{cameraName}' found.");
                return false;
            }

            CoreAiAgentCamera marker = candidate.GetComponent<CoreAiAgentCamera>();
            if (marker == null)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.NotMarked,
                    $"Camera '{candidate.name}' is not agent-controllable, so it cannot be moved (the " +
                    "player's camera is protected by default). Add a CoreAiAgentCamera component with " +
                    "allowMove=true to a camera to let agents move it, then capture/list will report it as movable.");
                return false;
            }

            if (!marker.AllowMove)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.MovementDisabled,
                    $"Camera '{candidate.name}' is marked but has allowMove=false, so it is capture-only.");
                return false;
            }

            if (!marker.AppliesToRole(agentRoleId))
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.WrongRole,
                    $"Camera '{candidate.name}' is reserved for a different agent role.");
                return false;
            }

            camera = candidate;
            denial = CameraMoveDenial.Allowed;
            return true;
        }

        public bool TryApplyLook(Camera camera, float? px, float? py, float? pz, float? rx, float? ry, float? rz,
            string lookAtTarget, out string errorCode)
        {
            errorCode = null;
            if (camera == null)
            {
                errorCode = "no_camera";
                return false;
            }

            Transform t = camera.transform;
            Vector3 originalPosition = t.position;
            Vector3 pos = t.position;
            Vector3 euler = t.eulerAngles;
            bool hasLookAt = !string.IsNullOrWhiteSpace(lookAtTarget);
            bool hasPos = px.HasValue || py.HasValue || pz.HasValue;
            bool hasRot = rx.HasValue || ry.HasValue || rz.HasValue;

            if (!hasPos && !hasRot && !hasLookAt)
            {
                errorCode = "no_change";
                return false;
            }

            if (px.HasValue)
            {
                pos.x = px.Value;
            }

            if (py.HasValue)
            {
                pos.y = py.Value;
            }

            if (pz.HasValue)
            {
                pos.z = pz.Value;
            }

            // Apply position first so a look-at rotates from the final vantage point.
            t.position = pos;

            if (hasLookAt)
            {
                Transform target = FindActiveTransformByName(lookAtTarget.Trim());
                if (target == null)
                {
                    // Restore position: nothing meaningful was applied.
                    t.position = originalPosition;
                    errorCode = "target_not_found";
                    return false;
                }

                t.LookAt(target);
                return true;
            }

            if (rx.HasValue)
            {
                euler.x = rx.Value;
            }

            if (ry.HasValue)
            {
                euler.y = ry.Value;
            }

            if (rz.HasValue)
            {
                euler.z = rz.Value;
            }

            t.eulerAngles = euler;
            return true;
        }

        public bool TryReserveCapture(string agentRoleId, out double retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (_minCaptureIntervalSeconds <= 0)
            {
                return true;
            }

            string key = NormalizeRole(agentRoleId);
            double now = _clockSeconds();
            lock (_lock)
            {
                if (_lastCaptureAtSeconds.TryGetValue(key, out double last))
                {
                    double elapsed = now - last;
                    if (elapsed < _minCaptureIntervalSeconds)
                    {
                        retryAfterSeconds = _minCaptureIntervalSeconds - elapsed;
                        return false;
                    }
                }

                _lastCaptureAtSeconds[key] = now;
                return true;
            }
        }

        public async UniTask<CameraCaptureResult> CaptureAsync(Camera camera, bool isMain, int maxSize,
            CaptureImageFormat format, CancellationToken cancellationToken)
        {
            if (camera == null)
            {
                return new CameraCaptureResult { Ok = false, Error = "no_camera" };
            }

            await UniTask.SwitchToMainThread(cancellationToken);
            // Let the current frame finish so the capture reflects a fully drawn frame.
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, cancellationToken);

            byte[] bytes = CaptureToBytes(camera, maxSize, format, out int width, out int height);
            return new CameraCaptureResult
            {
                Ok = true,
                CameraName = camera.name,
                IsMain = isMain,
                Width = width,
                Height = height,
                SizeBytes = bytes?.Length ?? 0,
                Format = format == CaptureImageFormat.Png ? "png" : "jpg",
                Bytes = bytes,
                Pose = CameraPose.FromCamera(camera)
            };
        }

        /// <summary>
        /// Renders <paramref name="camera"/> to an offscreen target sized so its long edge is
        /// <paramref name="maxSize"/> (clamped to <see cref="MinSize"/>..<see cref="MaxSize"/>), preserving
        /// aspect ratio, and returns the encoded bytes. Restores the camera target texture and the active
        /// render texture. Must run on the Unity main thread.
        /// </summary>
        public static byte[] CaptureToBytes(Camera camera, int maxSize, CaptureImageFormat format,
            out int width, out int height)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            int longEdge = Mathf.Clamp(maxSize <= 0 ? DefaultMaxSize : maxSize, MinSize, MaxSize);
            ComputeSize(camera, longEdge, out width, out height);

            RenderTexture rt = new(width, height, 24);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                return format == CaptureImageFormat.Png ? tex.EncodeToPNG() : tex.EncodeToJPG(JpegQuality);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }

                UnityEngine.Object.Destroy(rt);
            }
        }

        private static void ComputeSize(Camera camera, int longEdge, out int width, out int height)
        {
            int pw = camera.pixelWidth;
            int ph = camera.pixelHeight;
            if (pw <= 0 || ph <= 0)
            {
                // Off-display / uninitialized camera: assume 16:9.
                pw = 1600;
                ph = 900;
            }

            float aspect = pw / (float)ph;
            if (aspect >= 1f)
            {
                width = longEdge;
                height = Mathf.Max(1, Mathf.RoundToInt(longEdge / aspect));
            }
            else
            {
                height = longEdge;
                width = Mathf.Max(1, Mathf.RoundToInt(longEdge * aspect));
            }
        }

        /// <summary>Read-only resolution shared by capture. Falls back to main then any active camera.</summary>
        private Camera ResolveCameraCandidate(string cameraName, string agentRoleId, bool preferOwn)
        {
            string trimmed = cameraName?.Trim();
            bool explicitMain = string.Equals(trimmed, "main", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(trimmed) && !explicitMain)
            {
                Camera named = FindActiveCameraByName(trimmed);
                if (named != null)
                {
                    return named;
                }
                // Named camera not found: fall through to defaults rather than failing capture.
            }

            if (preferOwn && string.IsNullOrEmpty(trimmed))
            {
                Camera own = FindOwnCamera(agentRoleId);
                if (own != null)
                {
                    return own;
                }
            }

            Camera main = Camera.main;
            if (main != null)
            {
                return main;
            }

            return UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Exclude);
        }

        /// <summary>Resolution for movement: explicit name → own movable camera → main (so it can be denied).</summary>
        private Camera ResolveMovableCandidate(string cameraName, string agentRoleId)
        {
            string trimmed = cameraName?.Trim();
            bool explicitMain = string.Equals(trimmed, "main", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(trimmed) && !explicitMain)
            {
                return FindActiveCameraByName(trimmed);
            }

            if (!explicitMain)
            {
                Camera own = FindOwnCamera(agentRoleId);
                if (own != null)
                {
                    return own;
                }
            }

            return Camera.main;
        }

        private static Camera FindActiveCameraByName(string cameraName)
        {
            Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Camera camera in cameras)
            {
                if (camera != null && string.Equals(camera.name, cameraName, StringComparison.Ordinal))
                {
                    return camera;
                }
            }

            return null;
        }

        private static Transform FindActiveTransformByName(string objectName)
        {
            Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Transform transform in transforms)
            {
                if (transform == null || !string.Equals(transform.name, objectName, StringComparison.Ordinal))
                {
                    continue;
                }

                Scene scene = transform.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded)
                {
                    return transform;
                }
            }

            return null;
        }

        /// <summary>The first active marked camera that applies to <paramref name="agentRoleId"/>.</summary>
        private static Camera FindOwnCamera(string agentRoleId)
        {
            CoreAiAgentCamera[] markers = UnityEngine.Object.FindObjectsByType<CoreAiAgentCamera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (CoreAiAgentCamera marker in markers)
            {
                if (marker != null && marker.AppliesToRole(agentRoleId) && marker.TargetCamera != null)
                {
                    return marker.TargetCamera;
                }
            }

            return null;
        }

        private static string NormalizeRole(string agentRoleId)
        {
            return string.IsNullOrWhiteSpace(agentRoleId) ? "*" : agentRoleId.Trim();
        }
    }
}
