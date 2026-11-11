using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Engine-free maths of the default camera (<c>CameraType.Custom</c>): look angles, the wheel-zoom
    /// curve, the zoom clamp with its first-person snap, the orbit pose and the collision pull-in. The
    /// numbers are those of Roblox's PlayerModule camera scripts (the mirror documents the behaviour, not
    /// the constants), so a host camera built on these functions zooms and turns like the default one.
    /// </summary>
    public static class RbxCameraOrbitMath
    {
        /// <summary>Subject distance in first person, studs.</summary>
        public const double FirstPersonDistance = 0.5d;

        /// <summary>A resolved zoom below this distance snaps to <see cref="FirstPersonDistance"/>.</summary>
        public const double FirstPersonThreshold = 1d;

        /// <summary>Subject distance of a fresh camera, studs.</summary>
        public const double DefaultZoomDistance = 12.5d;

        /// <summary>How strongly one wheel notch scales with the current distance.</summary>
        public const double ZoomSensitivityCurvature = 0.5d;

        /// <summary>Highest look elevation, degrees (lowest is its negative).</summary>
        public const double MaxPitchDegrees = 80d;

        private const double DegreesToRadians = Math.PI / 180d;

        /// <summary>Clamps a look elevation, radians, to [-<paramref name="maxPitchDegrees"/>, +<paramref name="maxPitchDegrees"/>].</summary>
        public static double ClampPitch(double pitchRadians, double maxPitchDegrees = MaxPitchDegrees)
        {
            double limit = Math.Abs(maxPitchDegrees) * DegreesToRadians;
            if (double.IsNaN(pitchRadians))
            {
                return 0d;
            }

            return Math.Max(-limit, Math.Min(limit, pitchRadians));
        }

        /// <summary>
        /// Turns the look by a pointer movement in pixels (x right, y down): moving right turns right and
        /// moving down looks down. The pitch is clamped; the yaw wraps freely.
        /// </summary>
        public static void ApplyLook(ref double yawRadians, ref double pitchRadians, RbxVector2 deltaPixels,
            double yawRadiansPerPixel, double pitchRadiansPerPixel, double maxPitchDegrees = MaxPitchDegrees)
        {
            if (float.IsNaN(deltaPixels.X) || float.IsNaN(deltaPixels.Y)
                || float.IsInfinity(deltaPixels.X) || float.IsInfinity(deltaPixels.Y))
            {
                return;
            }

            yawRadians = WrapYaw(yawRadians - deltaPixels.X * yawRadiansPerPixel);
            pitchRadians = ClampPitch(pitchRadians - deltaPixels.Y * pitchRadiansPerPixel, maxPitchDegrees);
        }

        /// <summary>
        /// One zoom step on the PlayerModule curve. <paramref name="zoomDelta"/> is positive to zoom out and
        /// negative to zoom in (a forward wheel notch is -1); far cameras move further per notch than near
        /// ones. The result still goes through <see cref="ResolveZoom"/>.
        /// </summary>
        public static double StepZoom(double currentDistance, double zoomDelta)
        {
            if (double.IsNaN(zoomDelta) || double.IsInfinity(zoomDelta) || zoomDelta == 0d)
            {
                return currentDistance;
            }

            if (zoomDelta > 0d)
            {
                double zoomedOut = currentDistance + zoomDelta * (1d + currentDistance * ZoomSensitivityCurvature);
                return Math.Max(zoomedOut, FirstPersonThreshold);
            }

            return (currentDistance + zoomDelta) / (1d - zoomDelta * ZoomSensitivityCurvature);
        }

        /// <summary>
        /// The subject distance a desired zoom resolves to: <c>LockFirstPerson</c> always gives
        /// <see cref="FirstPersonDistance"/>; otherwise the zoom is clamped to [min, max] and a result below
        /// <see cref="FirstPersonThreshold"/> snaps to first person.
        /// </summary>
        public static double ResolveZoom(double desiredDistance, double minZoomDistance, double maxZoomDistance,
            RbxCameraMode cameraMode)
        {
            if (cameraMode == RbxCameraMode.LockFirstPerson)
            {
                return FirstPersonDistance;
            }

            double min = IsFinite(minZoomDistance) ? Math.Max(0d, minZoomDistance) : FirstPersonDistance;
            double max = IsFinite(maxZoomDistance) ? Math.Max(min, maxZoomDistance) : min;
            double desired = IsFinite(desiredDistance) ? desiredDistance : DefaultZoomDistance;
            double clamped = Math.Max(min, Math.Min(max, desired));
            return clamped < FirstPersonThreshold ? FirstPersonDistance : clamped;
        }

        /// <summary>True when a resolved subject distance is first person.</summary>
        public static bool IsFirstPerson(double distance)
        {
            return distance < FirstPersonThreshold;
        }

        /// <summary>The unit look direction of a yaw and pitch: yaw 0 looks along -Z, positive pitch looks up.</summary>
        public static RbxVector3 LookVector(double yawRadians, double pitchRadians)
        {
            double cosPitch = Math.Cos(pitchRadians);
            return new RbxVector3(
                (float)(-Math.Sin(yawRadians) * cosPitch),
                (float)Math.Sin(pitchRadians),
                (float)(-Math.Cos(yawRadians) * cosPitch));
        }

        /// <summary>Recovers yaw and pitch from a look direction; a vertical look keeps yaw 0.</summary>
        public static void YawPitchFromLook(RbxVector3 look, out double yawRadians, out double pitchRadians)
        {
            double magnitude = look.Magnitude;
            if (!(magnitude > 1e-6d) || double.IsInfinity(magnitude))
            {
                yawRadians = 0d;
                pitchRadians = 0d;
                return;
            }

            double y = Math.Max(-1d, Math.Min(1d, look.Y / magnitude));
            pitchRadians = Math.Asin(y);
            double planar = Math.Sqrt(look.X * (double)look.X + look.Z * (double)look.Z);
            yawRadians = planar < 1e-6d ? 0d : Math.Atan2(-look.X, -look.Z);
        }

        /// <summary>
        /// The camera pose orbiting <paramref name="focus"/> at <paramref name="distance"/> studs, looking at
        /// it with no roll.
        /// </summary>
        public static RbxCFrame OrbitCFrame(RbxVector3 focus, double yawRadians, double pitchRadians, double distance)
        {
            RbxVector3 look = LookVector(yawRadians, pitchRadians);
            RbxVector3 position = focus - look * (float)distance;
            RbxCFrame rotation = RbxCFrame.FromEulerAnglesYXZ((float)pitchRadians, (float)yawRadians, 0f);
            return RbxCFrame.FromPosition(position) * rotation;
        }

        /// <summary>
        /// The distance the camera keeps from the focus when an obstruction lies
        /// <paramref name="obstructionDistance"/> studs away: <paramref name="clearance"/> short of it, never
        /// behind the focus and never further than <paramref name="desiredDistance"/>.
        /// </summary>
        public static double PullIn(double desiredDistance, double obstructionDistance, double clearance)
        {
            if (!IsFinite(obstructionDistance) || obstructionDistance >= desiredDistance)
            {
                return desiredDistance;
            }

            double safeClearance = IsFinite(clearance) ? Math.Max(0d, clearance) : 0d;
            return Math.Max(0d, Math.Min(desiredDistance, obstructionDistance - safeClearance));
        }

        private static double WrapYaw(double yawRadians)
        {
            double twoPi = 2d * Math.PI;
            double wrapped = yawRadians % twoPi;
            if (wrapped > Math.PI)
            {
                wrapped -= twoPi;
            }
            else if (wrapped < -Math.PI)
            {
                wrapped += twoPi;
            }

            return wrapped;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
