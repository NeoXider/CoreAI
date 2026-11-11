using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace CoreAI.Vision
{
    /// <summary>What <c>camera_look</c> asks for. Every field is optional; at least one must be set.</summary>
    public sealed class CameraLookRequest
    {
        /// <summary>New world position X; null keeps it.</summary>
        public float? PositionX { get; set; }

        /// <summary>New world position Y; null keeps it.</summary>
        public float? PositionY { get; set; }

        /// <summary>New world position Z; null keeps it.</summary>
        public float? PositionZ { get; set; }

        /// <summary>New Euler rotation X in degrees; ignored when a target is set.</summary>
        public float? RotationX { get; set; }

        /// <summary>New Euler rotation Y in degrees; ignored when a target is set.</summary>
        public float? RotationY { get; set; }

        /// <summary>New Euler rotation Z in degrees; ignored when a target is set.</summary>
        public float? RotationZ { get; set; }

        /// <summary>Name of an object to look at; wins over <see cref="TargetX"/>/<see cref="TargetY"/>/<see cref="TargetZ"/>.</summary>
        public string LookAt { get; set; }

        /// <summary>X of a world point to look at; all three of X, Y and Z must be set.</summary>
        public float? TargetX { get; set; }

        /// <summary>Y of a world point to look at.</summary>
        public float? TargetY { get; set; }

        /// <summary>Z of a world point to look at.</summary>
        public float? TargetZ { get; set; }

        /// <summary>New vertical field of view in degrees, clamped by <see cref="AgentCameraOptions.ClampFieldOfView"/>.</summary>
        public float? FieldOfView { get; set; }
    }

    /// <summary>What <c>camera_orbit</c> asks for. Omitted values keep the camera's current ones.</summary>
    public sealed class CameraOrbitRequest
    {
        /// <summary>Name of an object to orbit around; wins over the point.</summary>
        public string Target { get; set; }

        /// <summary>X of a world point to orbit around; all three of X, Y and Z must be set.</summary>
        public float? TargetX { get; set; }

        /// <summary>Y of a world point to orbit around.</summary>
        public float? TargetY { get; set; }

        /// <summary>Z of a world point to orbit around.</summary>
        public float? TargetZ { get; set; }

        /// <summary>Heading in degrees (0 looks along +Z, 90 along +X); null keeps the current one.</summary>
        public float? Yaw { get; set; }

        /// <summary>Degrees above the pivot (positive looks down at it); null keeps the current one.</summary>
        public float? Pitch { get; set; }

        /// <summary>Distance from the pivot in world units; null keeps the current one.</summary>
        public float? Distance { get; set; }

        /// <summary>New vertical field of view in degrees.</summary>
        public float? FieldOfView { get; set; }
    }

    /// <summary>Outcome of a <see cref="IAgentCameraRig"/> move: success with the applied pose, or a code and a next step.</summary>
    public sealed class CameraRigResult
    {
        private CameraRigResult(bool ok, string errorCode, string message)
        {
            Ok = ok;
            ErrorCode = errorCode;
            Message = message;
        }

        /// <summary>True when the camera was moved.</summary>
        public bool Ok { get; }

        /// <summary>Stable error code (<c>no_change</c>, <c>target_not_found</c>, <c>invalid_target</c>, <c>no_target</c>, <c>no_camera</c>); null on success.</summary>
        public string ErrorCode { get; }

        /// <summary>Human-readable failure message that names the next step; null on success.</summary>
        public string Message { get; }

        /// <summary>Name of the object the camera aims or orbits at; null for a point or no target.</summary>
        public string TargetName { get; private set; }

        /// <summary>True when the camera was aimed at an object or a point.</summary>
        public bool HasTarget { get; private set; }

        /// <summary>The aim point or orbit pivot, when <see cref="HasTarget"/>.</summary>
        public Vector3 TargetPoint { get; private set; }

        /// <summary>True for an orbit; <see cref="Yaw"/>, <see cref="Pitch"/> and <see cref="Distance"/> are then set.</summary>
        public bool IsOrbit { get; private set; }

        /// <summary>Applied orbit heading in degrees, <c>[0, 360)</c>, measured from the camera's final position.</summary>
        public float Yaw { get; private set; }

        /// <summary>Applied orbit pitch in degrees, measured from the camera's final position.</summary>
        public float Pitch { get; private set; }

        /// <summary>Applied orbit distance in world units, measured from the camera's final position.</summary>
        public float Distance { get; private set; }

        /// <summary>A failure with a stable code and a message naming the next step.</summary>
        public static CameraRigResult Failure(string errorCode, string message)
        {
            return new CameraRigResult(false, errorCode, message);
        }

        /// <summary>A successful look, optionally aimed at <paramref name="targetPoint"/>.</summary>
        public static CameraRigResult Looked(bool hasTarget, string targetName, Vector3 targetPoint)
        {
            return new CameraRigResult(true, null, null)
            {
                HasTarget = hasTarget,
                TargetName = targetName,
                TargetPoint = targetPoint
            };
        }

        /// <summary>A successful orbit around <paramref name="pivot"/>.</summary>
        public static CameraRigResult Orbited(string targetName, Vector3 pivot, float yaw, float pitch, float distance)
        {
            return new CameraRigResult(true, null, null)
            {
                HasTarget = true,
                TargetName = targetName,
                TargetPoint = pivot,
                IsOrbit = true,
                Yaw = yaw,
                Pitch = pitch,
                Distance = distance
            };
        }
    }

    /// <summary>
    /// Moves an agent camera that already passed the ownership gate
    /// (<see cref="IAgentCameraService.TryResolveMovableCamera"/>): <c>look</c> (position, rotation, an object or
    /// point to aim at, field of view) and <c>orbit</c> (yaw, pitch and distance around an object or a point).
    /// Never decides WHICH camera may move. Must run on the Unity main thread. The default is
    /// <see cref="AgentCameraRig"/>; <see cref="AgentCameraService"/> implements it too, by delegation.
    /// </summary>
    public interface IAgentCameraRig
    {
        /// <summary>Applies a look request to <paramref name="camera"/>; on failure the camera is left as it was.</summary>
        CameraRigResult Look(Camera camera, CameraLookRequest request);

        /// <summary>Applies an orbit request to <paramref name="camera"/>; on failure the camera is left as it was.</summary>
        CameraRigResult Orbit(Camera camera, CameraOrbitRequest request);
    }

    /// <inheritdoc cref="IAgentCameraRig"/>
    /// <remarks>
    /// Remembers the last pivot per camera (the last orbit target or look target), so
    /// <c>camera_orbit(yaw: 90)</c> after <c>camera_orbit(target: "Castle")</c> or
    /// <c>camera_look(lookAt: "Castle")</c> keeps circling the castle. Limits come from
    /// <see cref="AgentCameraOptions"/>; object names resolve through <see cref="ICameraTargetResolver"/>.
    /// </remarks>
    public sealed class AgentCameraRig : IAgentCameraRig
    {
        private readonly AgentCameraOptions _options;
        private readonly ICameraTargetResolver _targets;
        private readonly Dictionary<Camera, CameraTarget> _lastPivots = new();

        /// <summary>The shipped rules (<see cref="AgentCameraOptions.CreateDefault"/>) and scene name lookup.</summary>
        public AgentCameraRig()
            : this(null, null)
        {
        }

        /// <summary>A rig with the given rules and target resolver; null picks the default for either.</summary>
        public AgentCameraRig(AgentCameraOptions options, ICameraTargetResolver targets)
        {
            _options = options ?? AgentCameraOptions.CreateDefault();
            _targets = targets ?? new SceneCameraTargetResolver();
        }

        /// <inheritdoc/>
        public CameraRigResult Look(Camera camera, CameraLookRequest request)
        {
            if (camera == null)
            {
                return CameraRigResult.Failure("no_camera", "No camera to move. Call camera_list to see the cameras.");
            }

            request ??= new CameraLookRequest();
            bool hasPosition = request.PositionX.HasValue || request.PositionY.HasValue || request.PositionZ.HasValue;
            bool hasRotation = request.RotationX.HasValue || request.RotationY.HasValue || request.RotationZ.HasValue;
            bool hasObject = !string.IsNullOrWhiteSpace(request.LookAt);
            int pointParts = Count(request.TargetX, request.TargetY, request.TargetZ);
            bool hasFov = request.FieldOfView.HasValue;
            if (!hasPosition && !hasRotation && !hasObject && pointParts == 0 && !hasFov)
            {
                return CameraRigResult.Failure("no_change",
                    "Nothing to change. Give a position (posX/posY/posZ), a rotation (rotX/rotY/rotZ), lookAt (an " +
                    "object name), targetX/targetY/targetZ (a point to look at) or fov.");
            }

            if (!hasObject && pointParts is > 0 and < 3)
            {
                return CameraRigResult.Failure("invalid_target",
                    "A point target needs all three of targetX, targetY and targetZ; or pass lookAt with an object name.");
            }

            Transform t = camera.transform;
            Vector3 position = t.position;
            if (request.PositionX.HasValue)
            {
                position.x = request.PositionX.Value;
            }

            if (request.PositionY.HasValue)
            {
                position.y = request.PositionY.Value;
            }

            if (request.PositionZ.HasValue)
            {
                position.z = request.PositionZ.Value;
            }

            Quaternion rotation = t.rotation;
            bool hasTarget = false;
            string targetName = null;
            Vector3 targetPoint = default;
            if (hasObject)
            {
                if (!_targets.TryResolve(request.LookAt, camera, out CameraTarget target))
                {
                    return CameraRigResult.Failure("target_not_found",
                        $"No active object named '{request.LookAt.Trim()}' was found to look at. Use an exact object " +
                        "name, or targetX/targetY/targetZ for a point.");
                }

                hasTarget = true;
                targetName = target.Name;
                targetPoint = target.Point;
            }
            else if (pointParts == 3)
            {
                hasTarget = true;
                targetPoint = new Vector3(request.TargetX.Value, request.TargetY.Value, request.TargetZ.Value);
            }

            if (hasTarget)
            {
                // WHY: position first, then aim, so the camera looks at the target from its final vantage point.
                if (!CameraPoseMath.TryAim(ToNumerics(position), ToNumerics(targetPoint), out float pitch,
                        out float yaw))
                {
                    return CameraRigResult.Failure("invalid_target",
                        "The target is at the camera position, so there is no direction to look in. Move the " +
                        "camera away first or pick another target.");
                }

                rotation = Quaternion.Euler(pitch, yaw, 0f);
            }
            else if (hasRotation)
            {
                Vector3 euler = t.eulerAngles;
                euler.x = request.RotationX ?? euler.x;
                euler.y = request.RotationY ?? euler.y;
                euler.z = request.RotationZ ?? euler.z;
                rotation = Quaternion.Euler(euler);
            }

            t.SetPositionAndRotation(position, rotation);
            if (hasFov)
            {
                camera.fieldOfView = _options.ClampFieldOfView(request.FieldOfView.Value);
            }

            if (hasTarget)
            {
                RememberPivot(camera, new CameraTarget(targetName, targetPoint, 0f));
            }

            return CameraRigResult.Looked(hasTarget, targetName, targetPoint);
        }

        /// <inheritdoc/>
        public CameraRigResult Orbit(Camera camera, CameraOrbitRequest request)
        {
            if (camera == null)
            {
                return CameraRigResult.Failure("no_camera", "No camera to move. Call camera_list to see the cameras.");
            }

            request ??= new CameraOrbitRequest();
            int pointParts = Count(request.TargetX, request.TargetY, request.TargetZ);
            bool hasObject = !string.IsNullOrWhiteSpace(request.Target);
            if (!hasObject && pointParts is > 0 and < 3)
            {
                return CameraRigResult.Failure("invalid_target",
                    "A point target needs all three of targetX, targetY and targetZ; or pass target with an object name.");
            }

            CameraTarget pivot;
            if (hasObject)
            {
                if (!_targets.TryResolve(request.Target, camera, out pivot))
                {
                    return CameraRigResult.Failure("target_not_found",
                        $"No active object named '{request.Target.Trim()}' was found to orbit around. Use an exact " +
                        "object name, or targetX/targetY/targetZ for a point.");
                }
            }
            else if (pointParts == 3)
            {
                pivot = new CameraTarget(null,
                    new Vector3(request.TargetX.Value, request.TargetY.Value, request.TargetZ.Value), 0f);
            }
            else if (!TryGetLastPivot(camera, out pivot))
            {
                return CameraRigResult.Failure("no_target",
                    "Nothing to orbit around yet. Give target (an object name) or targetX/targetY/targetZ (a point).");
            }

            Transform t = camera.transform;
            NVector3 pivotPoint = ToNumerics(pivot.Point);
            bool described = CameraPoseMath.TryDescribeOrbit(pivotPoint, ToNumerics(t.position),
                out float currentYaw, out float currentPitch, out float currentDistance);
            if (!described)
            {
                currentYaw = CameraPoseMath.NormalizeYaw(t.eulerAngles.y);
                currentPitch = 0f;
                currentDistance = pivot.Radius > 0f ? pivot.Radius * 2.5f : _options.DefaultOrbitDistance;
            }

            float yaw = CameraPoseMath.NormalizeYaw(request.Yaw ?? currentYaw);
            float pitchDegrees = _options.ClampPitch(request.Pitch ?? currentPitch);
            float distance = _options.ClampOrbitDistance(request.Distance ?? currentDistance);
            NVector3 position = CameraPoseMath.OrbitPosition(pivotPoint, yaw, pitchDegrees, distance);
            t.SetPositionAndRotation(ToUnity(position), Quaternion.Euler(pitchDegrees, yaw, 0f));
            if (request.FieldOfView.HasValue)
            {
                camera.fieldOfView = _options.ClampFieldOfView(request.FieldOfView.Value);
            }

            RememberPivot(camera, pivot);
            CameraPoseMath.TryDescribeOrbit(pivotPoint, ToNumerics(t.position), out float appliedYaw,
                out float appliedPitch, out float appliedDistance);
            return CameraRigResult.Orbited(pivot.Name, pivot.Point, appliedYaw, appliedPitch, appliedDistance);
        }

        private bool TryGetLastPivot(Camera camera, out CameraTarget pivot)
        {
            return _lastPivots.TryGetValue(camera, out pivot);
        }

        private void RememberPivot(Camera camera, CameraTarget pivot)
        {
            List<Camera> destroyed = null;
            foreach (Camera key in _lastPivots.Keys)
            {
                if (key == null)
                {
                    (destroyed ??= new List<Camera>()).Add(key);
                }
            }

            if (destroyed != null)
            {
                foreach (Camera key in destroyed)
                {
                    _lastPivots.Remove(key);
                }
            }

            _lastPivots[camera] = pivot;
        }

        private static int Count(float? x, float? y, float? z)
        {
            return (x.HasValue ? 1 : 0) + (y.HasValue ? 1 : 0) + (z.HasValue ? 1 : 0);
        }

        private static NVector3 ToNumerics(Vector3 v)
        {
            return new NVector3(v.x, v.y, v.z);
        }

        private static Vector3 ToUnity(NVector3 v)
        {
            return new Vector3(v.X, v.Y, v.Z);
        }
    }
}
