using System;
using System.Numerics;

namespace CoreAI.Vision
{
    /// <summary>
    /// Engine-free pose math for the agent camera, in Unity's axes (left-handed, +Y up, +Z forward) and degrees.
    /// A camera pose here has no roll: its rotation is the Euler rotation <c>(pitch, yaw, 0)</c>, where
    /// <c>yaw</c> is the heading about +Y (0 looks along +Z, 90 along +X) and a positive <c>pitch</c> looks down.
    /// An orbit places the camera <c>distance</c> away from a pivot so that it looks straight at the pivot:
    /// a positive orbit pitch puts the camera above the pivot.
    /// <para>
    /// Kept free of <c>UnityEngine</c> so the math is deterministic and testable outside the engine; the Unity
    /// rig converts at the boundary (<see cref="AgentCameraRig"/>).
    /// </para>
    /// </summary>
    public static class CameraPoseMath
    {
        /// <summary>Below this length two points count as the same point (no direction between them).</summary>
        public const float Epsilon = 1e-5f;

        private const double DegreesToRadians = Math.PI / 180.0;
        private const double RadiansToDegrees = 180.0 / Math.PI;

        /// <summary>The unit forward vector of a camera whose rotation is the Euler rotation <c>(pitch, yaw, 0)</c>.</summary>
        public static Vector3 Forward(float pitchDegrees, float yawDegrees)
        {
            double pitch = pitchDegrees * DegreesToRadians;
            double yaw = yawDegrees * DegreesToRadians;
            double cosPitch = Math.Cos(pitch);
            return new Vector3(
                (float)(cosPitch * Math.Sin(yaw)),
                (float)-Math.Sin(pitch),
                (float)(cosPitch * Math.Cos(yaw)));
        }

        /// <summary>
        /// The camera position of an orbit around <paramref name="pivot"/>: <paramref name="distance"/> away,
        /// looking at the pivot with heading <paramref name="yawDegrees"/> and pitch <paramref name="pitchDegrees"/>
        /// (positive = above the pivot, looking down). The camera rotation is <c>(pitch, yaw, 0)</c>.
        /// </summary>
        public static Vector3 OrbitPosition(Vector3 pivot, float yawDegrees, float pitchDegrees, float distance)
        {
            return pivot - Forward(pitchDegrees, yawDegrees) * distance;
        }

        /// <summary>
        /// The heading that looks from <paramref name="from"/> toward <paramref name="to"/>, as the Euler rotation
        /// <c>(pitch, yaw, 0)</c> with yaw in <c>[0, 360)</c>. Returns false (and zero angles) when the two points
        /// are closer than <see cref="Epsilon"/>, because there is no direction to look in.
        /// </summary>
        public static bool TryAim(Vector3 from, Vector3 to, out float pitchDegrees, out float yawDegrees)
        {
            Vector3 direction = to - from;
            float length = direction.Length();
            if (length < Epsilon)
            {
                pitchDegrees = 0f;
                yawDegrees = 0f;
                return false;
            }

            double sinPitch = Math.Max(-1.0, Math.Min(1.0, -direction.Y / length));
            pitchDegrees = (float)(Math.Asin(sinPitch) * RadiansToDegrees);
            yawDegrees = NormalizeYaw((float)(Math.Atan2(direction.X, direction.Z) * RadiansToDegrees));
            return true;
        }

        /// <summary>
        /// The inverse of <see cref="OrbitPosition"/>: describes <paramref name="cameraPosition"/> as an orbit around
        /// <paramref name="pivot"/> (yaw in <c>[0, 360)</c>, pitch in <c>[-90, 90]</c>, distance). Returns false when
        /// the camera sits on the pivot, where yaw and pitch are undefined; the distance is still reported.
        /// </summary>
        public static bool TryDescribeOrbit(Vector3 pivot, Vector3 cameraPosition, out float yawDegrees,
            out float pitchDegrees, out float distance)
        {
            distance = Vector3.Distance(pivot, cameraPosition);
            return TryAim(cameraPosition, pivot, out pitchDegrees, out yawDegrees);
        }

        /// <summary>Wraps a heading into <c>[0, 360)</c>.</summary>
        public static float NormalizeYaw(float yawDegrees)
        {
            if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees))
            {
                return 0f;
            }

            double wrapped = yawDegrees % 360.0;
            if (wrapped < 0.0)
            {
                wrapped += 360.0;
            }

            return wrapped >= 360.0 ? 0f : (float)wrapped;
        }

        /// <summary>The signed smallest difference <c>b - a</c> between two headings, in <c>(-180, 180]</c>.</summary>
        public static float DeltaYaw(float aDegrees, float bDegrees)
        {
            double delta = (bDegrees - aDegrees) % 360.0;
            if (delta > 180.0)
            {
                delta -= 360.0;
            }
            else if (delta <= -180.0)
            {
                delta += 360.0;
            }

            return (float)delta;
        }
    }
}
