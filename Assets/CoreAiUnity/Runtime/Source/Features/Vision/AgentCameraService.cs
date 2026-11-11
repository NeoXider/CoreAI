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
        WrongRole,

        /// <summary>The free agent camera was asked for, but <see cref="AgentCameraOptions.AllowFreeCamera"/> is off.</summary>
        FreeCameraDisabled
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

        /// <summary>True for a free agent camera the service created on demand.</summary>
        public bool IsFree { get; set; }
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

        /// <summary>Which readback path produced <see cref="Bytes"/>.</summary>
        public CameraReadbackMode ReadbackMode { get; set; }

        /// <summary>Failure detail when <see cref="Ok"/> is false; null otherwise.</summary>
        public string Message { get; set; }
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
        /// <see cref="AgentCameraService"/> also understands the reserved names <c>main</c>, <c>agent</c> and
        /// <c>free</c>.
        /// </summary>
        Camera ResolveCaptureCamera(string cameraName, string agentRoleId, out bool isMain);

        /// <summary>
        /// Movement gate: outputs the camera the agent may move, or a typed <paramref name="denial"/>
        /// explaining why not (unmarked / movement disabled / wrong role / no camera).
        /// </summary>
        bool TryResolveMovableCamera(string cameraName, string agentRoleId, out Camera camera,
            out CameraMoveDenial denial);

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
    /// <remarks>
    /// <para>
    /// <b>Whose camera.</b> An agent's own camera is a camera a designer marked for it with
    /// <see cref="CoreAiAgentCamera"/>, else its free camera: a camera this service creates on demand the first time
    /// the agent asks to move its own camera (or names <c>agent</c>/<c>free</c>). The free camera starts at the main
    /// camera's pose and settings, stays disabled so it never draws to the screen and never becomes
    /// <see cref="Camera.main"/>, is scoped to the role, and is destroyed by <see cref="Dispose"/>. A camera nobody
    /// marked, the player's included, can be captured but never moved.
    /// </para>
    /// <para>
    /// <b>Reserved names.</b> <c>main</c> is <see cref="Camera.main"/>; <c>agent</c> is the agent's own camera
    /// (a marked one, else the free camera); <c>free</c> is always the free camera. An empty name means the own
    /// camera when one exists, else the main camera for a capture and the free camera for a move.
    /// </para>
    /// <para>
    /// Frames go through <see cref="ICameraFrameReadback"/> (async GPU readback where the platform has it), and
    /// look/orbit go through <see cref="IAgentCameraRig"/>; both are replaceable through the constructor.
    /// </para>
    /// </remarks>
    public sealed class AgentCameraService : IAgentCameraService, IAgentCameraRig, IDisposable
    {
        /// <summary>Default long-edge capture size in pixels.</summary>
        public const int DefaultMaxSize = 512;

        /// <summary>Minimum long-edge capture size in pixels.</summary>
        public const int MinSize = 64;

        /// <summary>Maximum long-edge capture size in pixels (memory + token cap).</summary>
        public const int MaxSize = 1024;

        /// <summary>Default minimum seconds between captures per agent.</summary>
        public const double DefaultMinCaptureIntervalSeconds = 1.0;

        /// <summary>Reserved camera name for <see cref="Camera.main"/>.</summary>
        public const string MainCameraAlias = "main";

        /// <summary>Reserved camera name for the agent's own camera (a marked camera, else the free camera).</summary>
        public const string AgentCameraAlias = "agent";

        /// <summary>Reserved camera name for the free agent camera, created on demand.</summary>
        public const string FreeCameraAlias = "free";

        private const int JpegQuality = 75;

        private readonly Func<double> _clockSeconds;
        private readonly AgentCameraOptions _options;
        private readonly ICameraFrameReadback _readback;
        private readonly IAgentCameraRig _rig;
        private readonly object _lock = new();
        private readonly Dictionary<string, double> _lastCaptureAtSeconds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Camera> _freeCameras = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Production constructor: monotonic <see cref="System.Diagnostics.Stopwatch"/> clock, 1s rate limit.</summary>
        public AgentCameraService()
            : this(null, null, null, null)
        {
        }

        /// <summary>Test/config constructor: inject a clock (seconds) and rate-limit interval.</summary>
        public AgentCameraService(Func<double> clockSeconds, double minCaptureIntervalSeconds)
            : this(new AgentCameraOptions { MinCaptureIntervalSeconds = minCaptureIntervalSeconds }, null, null,
                clockSeconds)
        {
        }

        /// <summary>The given rules with the default readback, rig and clock.</summary>
        public AgentCameraService(AgentCameraOptions options)
            : this(options, null, null, null)
        {
        }

        /// <summary>
        /// Every block injectable; null picks the default: <see cref="AgentCameraOptions.CreateDefault"/>,
        /// <see cref="CameraFrameReadback.CreateDefault"/>, an <see cref="AgentCameraRig"/> over the same options,
        /// and a monotonic clock in seconds.
        /// </summary>
        public AgentCameraService(AgentCameraOptions options, ICameraFrameReadback readback, IAgentCameraRig rig,
            Func<double> clockSeconds)
        {
            _options = options ?? AgentCameraOptions.CreateDefault();
            _readback = readback ?? CameraFrameReadback.CreateDefault();
            _rig = rig ?? new AgentCameraRig(_options, new SceneCameraTargetResolver());
            _clockSeconds = clockSeconds ?? DefaultStopwatchClock;
        }

        /// <summary>The rules this service applies; changes take effect on the next call.</summary>
        public AgentCameraOptions Options => _options;

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

            List<AgentCameraInfo> list = new(cameras.Length + _freeCameras.Count);
            HashSet<Camera> listed = new();
            foreach (Camera cam in cameras)
            {
                if (cam != null && listed.Add(cam))
                {
                    list.Add(Describe(cam, main, agentRoleId));
                }
            }

            // WHY: an edit-mode free camera is DontSave, which object searches can skip, so the tracked ones are added explicitly.
            foreach (Camera free in _freeCameras.Values)
            {
                if (free != null && free.gameObject.activeInHierarchy && listed.Add(free))
                {
                    list.Add(Describe(free, main, agentRoleId));
                }
            }

            return list;
        }

        public Camera ResolveCaptureCamera(string cameraName, string agentRoleId, out bool isMain)
        {
            Camera cam = ResolveCaptureCandidate(cameraName, agentRoleId);
            isMain = cam != null && cam == Camera.main;
            return cam;
        }

        public bool TryResolveMovableCamera(string cameraName, string agentRoleId, out Camera camera,
            out CameraMoveDenial denial)
        {
            camera = null;
            string trimmed = cameraName?.Trim();
            if (IsReserved(trimmed, FreeCameraAlias) && !_options.AllowFreeCamera)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.FreeCameraDisabled,
                    "This game does not give agents a free camera. Call camera_list and move a camera marked " +
                    "movable, if there is one.");
                return false;
            }

            Camera candidate = ResolveMovableCandidate(trimmed, agentRoleId);
            if (candidate == null)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.NoCamera,
                    string.IsNullOrWhiteSpace(cameraName)
                        ? "No camera found to move. Call camera_list to see the cameras."
                        : $"No camera named '{cameraName}' found. Call camera_list to see the cameras, or leave " +
                          "cameraName empty to move your own camera.");
                return false;
            }

            CoreAiAgentCamera marker = candidate.GetComponent<CoreAiAgentCamera>();
            if (marker == null)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.NotMarked,
                    $"Camera '{candidate.name}' is not agent-controllable, so it cannot be moved (the player's " +
                    "camera is protected). Leave cameraName empty or use 'agent' to move your own camera instead. " +
                    "A designer can make a scene camera movable by adding a CoreAiAgentCamera component with " +
                    "allowMove=true.");
                return false;
            }

            if (!marker.AllowMove)
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.MovementDisabled,
                    $"Camera '{candidate.name}' is marked but has allowMove=false, so it is capture-only." +
                    (_options.AllowFreeCamera ? " Use cameraName 'free' to move a free camera of your own." : ""));
                return false;
            }

            if (!marker.AppliesToRole(agentRoleId))
            {
                denial = new CameraMoveDenial(CameraMoveDenialReason.WrongRole,
                    $"Camera '{candidate.name}' is reserved for a different agent role. Leave cameraName empty to " +
                    "move your own camera.");
                return false;
            }

            camera = candidate;
            denial = CameraMoveDenial.Allowed;
            return true;
        }

        public bool TryApplyLook(Camera camera, float? px, float? py, float? pz, float? rx, float? ry, float? rz,
            string lookAtTarget, out string errorCode)
        {
            CameraRigResult result = _rig.Look(camera, new CameraLookRequest
            {
                PositionX = px,
                PositionY = py,
                PositionZ = pz,
                RotationX = rx,
                RotationY = ry,
                RotationZ = rz,
                LookAt = lookAtTarget
            });
            errorCode = result.Ok ? null : result.ErrorCode;
            return result.Ok;
        }

        /// <inheritdoc/>
        public CameraRigResult Look(Camera camera, CameraLookRequest request)
        {
            return _rig.Look(camera, request);
        }

        /// <inheritdoc/>
        public CameraRigResult Orbit(Camera camera, CameraOrbitRequest request)
        {
            return _rig.Orbit(camera, request);
        }

        public bool TryReserveCapture(string agentRoleId, out double retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            double interval = _options.MinCaptureIntervalSeconds;
            if (interval <= 0)
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
                    if (elapsed < interval)
                    {
                        retryAfterSeconds = interval - elapsed;
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
            if (camera == null)
            {
                return new CameraCaptureResult { Ok = false, Error = "no_camera" };
            }

            int longEdge = Mathf.Clamp(maxSize <= 0 ? DefaultMaxSize : maxSize, MinSize, MaxSize);
            ComputeSize(camera, longEdge, out int width, out int height);
            string cameraName = camera.name;
            CameraPose pose = CameraPose.FromCamera(camera);

            CameraFrameReadbackResult frame;
            try
            {
                frame = await _readback.CaptureAsync(camera, width, height, format, JpegQuality, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CameraCaptureResult
                {
                    Ok = false, Error = "capture_failed", CameraName = cameraName, Message = ex.Message
                };
            }

            byte[] bytes = frame?.Bytes;
            if (bytes == null || bytes.Length == 0)
            {
                return new CameraCaptureResult { Ok = false, Error = "capture_failed", CameraName = cameraName };
            }

            return new CameraCaptureResult
            {
                Ok = true,
                CameraName = cameraName,
                IsMain = isMain,
                Width = width,
                Height = height,
                SizeBytes = bytes.Length,
                Format = format == CaptureImageFormat.Png ? "png" : "jpg",
                Bytes = bytes,
                Pose = pose,
                ReadbackMode = frame.Mode
            };
        }

        /// <summary>
        /// The free agent camera of <paramref name="agentRoleId"/>. With <paramref name="create"/> it is created on
        /// demand when <see cref="AgentCameraOptions.AllowFreeCamera"/> is on; otherwise only an existing one is
        /// returned. Must run on the Unity main thread.
        /// </summary>
        public bool TryGetFreeCamera(string agentRoleId, bool create, out Camera camera)
        {
            string key = NormalizeRole(agentRoleId);
            if (_freeCameras.TryGetValue(key, out camera) && camera != null)
            {
                return true;
            }

            camera = null;
            if (!create || !_options.AllowFreeCamera)
            {
                return false;
            }

            camera = CreateFreeCamera(agentRoleId);
            _freeCameras[key] = camera;
            return true;
        }

        /// <summary>True when <paramref name="camera"/> is a free agent camera this service created.</summary>
        public bool IsFreeCamera(Camera camera)
        {
            if (camera == null)
            {
                return false;
            }

            foreach (Camera free in _freeCameras.Values)
            {
                if (free == camera)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Destroys every free agent camera this service created. Must run on the Unity main thread.</summary>
        public void Dispose()
        {
            foreach (Camera free in _freeCameras.Values)
            {
                if (free == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(free.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(free.gameObject);
                }
            }

            _freeCameras.Clear();
        }

        /// <summary>
        /// Renders <paramref name="camera"/> to an offscreen target sized so its long edge is
        /// <paramref name="maxSize"/> (clamped to <see cref="MinSize"/>..<see cref="MaxSize"/>), preserving
        /// aspect ratio, and returns the encoded bytes. Restores the camera target texture and the active
        /// render texture. Must run on the Unity main thread. Synchronous: <see cref="CaptureAsync"/> uses the
        /// non-blocking readback instead.
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

            // WHY: pooled render target + one cached readback texture instead of a new pair per capture.
            return AiAttachmentUnityExtensions.CaptureCamera(camera, width, height, format, JpegQuality);
        }

        private static void ComputeSize(Camera camera, int longEdge, out int width, out int height)
        {
            int pw = camera.pixelWidth;
            int ph = camera.pixelHeight;
            if (pw <= 0 || ph <= 0)
            {
                // WHY: an off-display or uninitialized camera reports 0x0 pixels; assume 16:9.
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

        private AgentCameraInfo Describe(Camera cam, Camera main, string agentRoleId)
        {
            CoreAiAgentCamera marker = cam.GetComponent<CoreAiAgentCamera>();
            return new AgentCameraInfo
            {
                Name = cam.name,
                IsMain = cam == main,
                IsMarked = marker != null,
                Movable = marker != null && marker.IsMovableBy(agentRoleId),
                Pose = CameraPose.FromCamera(cam),
                IsFree = IsFreeCamera(cam)
            };
        }

        /// <summary>Read-only resolution for capture. Unknown names fall back to the defaults rather than failing.</summary>
        private Camera ResolveCaptureCandidate(string cameraName, string agentRoleId)
        {
            string trimmed = cameraName?.Trim();
            if (IsReserved(trimmed, AgentCameraAlias))
            {
                Camera own = FindMarkedOwnCamera(agentRoleId) ?? GetFreeCamera(agentRoleId, true);
                if (own != null)
                {
                    return own;
                }
            }
            else if (IsReserved(trimmed, FreeCameraAlias))
            {
                Camera free = GetFreeCamera(agentRoleId, true);
                if (free != null)
                {
                    return free;
                }
            }
            else if (string.IsNullOrEmpty(trimmed))
            {
                Camera own = FindMarkedOwnCamera(agentRoleId) ?? GetFreeCamera(agentRoleId, false);
                if (own != null)
                {
                    return own;
                }
            }
            else if (!IsReserved(trimmed, MainCameraAlias))
            {
                Camera named = FindActiveCameraByName(trimmed);
                if (named != null)
                {
                    return named;
                }
            }

            Camera main = Camera.main;
            if (main != null)
            {
                return main;
            }

            return UnityEngine.Object.FindAnyObjectByType<Camera>(FindObjectsInactive.Exclude);
        }

        /// <summary>
        /// Resolution for movement: <c>main</c> → the main camera (so it can be denied); <c>free</c> → the free camera;
        /// a name → that camera; empty or <c>agent</c> → the own marked camera, else the free camera, else the main
        /// camera (denied).
        /// </summary>
        private Camera ResolveMovableCandidate(string trimmed, string agentRoleId)
        {
            if (IsReserved(trimmed, MainCameraAlias))
            {
                return Camera.main;
            }

            if (IsReserved(trimmed, FreeCameraAlias))
            {
                return GetFreeCamera(agentRoleId, true);
            }

            if (!string.IsNullOrEmpty(trimmed) && !IsReserved(trimmed, AgentCameraAlias))
            {
                return FindActiveCameraByName(trimmed);
            }

            return FindMarkedOwnCamera(agentRoleId) ?? GetFreeCamera(agentRoleId, true) ?? Camera.main;
        }

        private Camera GetFreeCamera(string agentRoleId, bool create)
        {
            return TryGetFreeCamera(agentRoleId, create, out Camera camera) ? camera : null;
        }

        private Camera CreateFreeCamera(string agentRoleId)
        {
            string role = string.IsNullOrWhiteSpace(agentRoleId) ? null : agentRoleId.Trim();
            string baseName = string.IsNullOrWhiteSpace(_options.FreeCameraName)
                ? AgentCameraOptions.DefaultFreeCameraName
                : _options.FreeCameraName.Trim();
            GameObject go = new(role == null ? baseName : $"{baseName} ({role})");
            if (!Application.isPlaying)
            {
                // WHY: an edit-mode agent must not leave its camera in a scene the user later saves.
                go.hideFlags = HideFlags.DontSave;
            }

            Camera camera = go.AddComponent<Camera>();
            Camera main = Camera.main;
            if (main != null)
            {
                // WHY: CopyFrom also copies the main camera's pose, so the first frame shows what the player sees.
                camera.CopyFrom(main);
                camera.targetTexture = null;
            }
            else
            {
                go.transform.SetPositionAndRotation(new Vector3(0f, 10f, -10f), Quaternion.Euler(45f, 0f, 0f));
            }

            // WHY: disabled, the camera never draws to the screen and never becomes Camera.main; captures call Render() on it explicitly.
            camera.enabled = false;
            go.tag = "Untagged";
            CoreAiAgentCamera marker = go.AddComponent<CoreAiAgentCamera>();
            marker.Configure(role, true);
            return camera;
        }

        private Camera FindActiveCameraByName(string cameraName)
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

            foreach (Camera free in _freeCameras.Values)
            {
                if (free != null && free.gameObject.activeInHierarchy &&
                    string.Equals(free.name, cameraName, StringComparison.Ordinal))
                {
                    return free;
                }
            }

            return null;
        }

        /// <summary>The first active designer-marked camera (not a free camera) that applies to <paramref name="agentRoleId"/>.</summary>
        private Camera FindMarkedOwnCamera(string agentRoleId)
        {
            CoreAiAgentCamera[] markers = UnityEngine.Object.FindObjectsByType<CoreAiAgentCamera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (CoreAiAgentCamera marker in markers)
            {
                if (marker != null && marker.AppliesToRole(agentRoleId) && marker.TargetCamera != null &&
                    !IsFreeCamera(marker.TargetCamera))
                {
                    return marker.TargetCamera;
                }
            }

            return null;
        }

        private static bool IsReserved(string trimmed, string reservedName)
        {
            return string.Equals(trimmed, reservedName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRole(string agentRoleId)
        {
            return string.IsNullOrWhiteSpace(agentRoleId) ? "*" : agentRoleId.Trim();
        }
    }
}
