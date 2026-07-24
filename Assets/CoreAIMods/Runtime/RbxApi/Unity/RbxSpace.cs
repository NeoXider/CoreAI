using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Spatial
{
    /// <summary>
    /// THE single conversion boundary between Roblox space and Unity space
    /// (ROBLOX_API_ROADMAP.md D2/D3, LOCKED). Nothing else in the Roblox API layer may
    /// convert — enforced by RbxSpaceUsageLintTests.
    ///
    /// Mapping: Roblox is right-handed with LookVector = -Z; Unity is left-handed with
    /// forward = +Z. The bridge is the Z-mirror S = diag(1, 1, -1):
    ///   position:  unity = (x, y, -z) * MetersPerStud
    ///   rotation:  R_unity = S * R_rbx * S, i.e. quaternion (qx, qy, qz, qw) -> (-qx, -qy, qz, qw)
    /// Documented visible artifact: mod-space z = -Unity z.
    /// </summary>
    public static class RbxSpace
    {
        /// <summary>Default scale: 1 stud = 0.28 m (D3, LOCKED — Roblox game-feel parity).</summary>
        public const float DefaultMetersPerStud = 0.28f;

        private static float _metersPerStud = DefaultMetersPerStud;
        private static bool _configured;

        /// <summary>Meters per stud. Set once at host bootstrap via Configure; constant per session.</summary>
        public static float MetersPerStud => _metersPerStud;

        /// <summary>Studs per meter — the inverse used when reading meter-authored host objects.</summary>
        public static float StudsPerMeter => 1f / _metersPerStud;

        /// <summary>
        /// Configures the session scale. May be called once (host profile bootstrap);
        /// a second call with a different value throws — the scale is constant per session.
        /// </summary>
        public static void Configure(float metersPerStud)
        {
            if (metersPerStud <= 0f || float.IsNaN(metersPerStud) || float.IsInfinity(metersPerStud))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(metersPerStud), metersPerStud, "MetersPerStud must be a positive finite number.");
            }

            if (_configured && !ScaleMath.Approximately(_metersPerStud, metersPerStud))
            {
                throw new InvalidOperationException(
                    $"RbxSpace scale is already configured to {_metersPerStud} m/stud for this " +
                    "session; changing it mid-session would mis-scale every live instance.");
            }

            _metersPerStud = metersPerStud;
            _configured = true;
        }

        /// <summary>
        /// Test-only reset for the dual-scale EditMode runs (§5.1.1). Production keeps the
        /// constant-per-session rule; only CoreAI.Mods.Tests can see this.
        /// </summary>
        internal static void ResetForTests(float metersPerStud = DefaultMetersPerStud)
        {
            _metersPerStud = metersPerStud;
            _configured = false;
        }

        // ---- Positions (scaled) ------------------------------------------------------

        public static UnityEngine.Vector3 ToUnity(RbxVector3 position) => new UnityEngine.Vector3(
            position.X * _metersPerStud,
            position.Y * _metersPerStud,
            -position.Z * _metersPerStud);

        public static RbxVector3 FromUnity(UnityEngine.Vector3 position) => new RbxVector3(
            position.x * StudsPerMeter,
            position.y * StudsPerMeter,
            -position.z * StudsPerMeter);

        // ---- Rotations (unscaled) ----------------------------------------------------

        /// <summary>Rotation of a CFrame as a Unity quaternion (handedness-flipped).</summary>
        public static UnityEngine.Quaternion ToUnity(in RbxCFrame cf)
        {
            (float qx, float qy, float qz, float qw) = cf.ToQuaternion();
            // WHY: conjugation by the Z-mirror S: q' = (-qx, -qy, qz, qw). Identity maps
            // Roblox LookVector (0,0,-1) onto Unity forward (0,0,1).
            return new UnityEngine.Quaternion(-qx, -qy, qz, qw);
        }

        /// <summary>Unity rotation as a rotation-only CFrame (position zero).</summary>
        public static RbxCFrame RotationFromUnity(UnityEngine.Quaternion q)
        {
            return RbxCFrame.FromQuaternion(0f, 0f, 0f, -q.x, -q.y, q.z, q.w);
        }

        // ---- Full CFrame <-> pose ----------------------------------------------------

        public static (UnityEngine.Vector3 position, UnityEngine.Quaternion rotation) ToUnityPose(
            in RbxCFrame cf)
        {
            return (ToUnity(cf.Position), ToUnity(cf));
        }

        public static RbxCFrame FromUnity(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation)
        {
            RbxVector3 pos = FromUnity(position);
            return RbxCFrame.FromPosition(pos) * RotationFromUnity(rotation);
        }

        // ---- Directions / velocities (scaled, no translation) ------------------------

        public static UnityEngine.Vector3 VelocityToUnity(RbxVector3 v) => ToUnity(v);

        public static RbxVector3 VelocityFromUnity(UnityEngine.Vector3 v) => FromUnity(v);

        /// <summary>Direction conversion: mirror only, no scale (unit vectors stay unit).</summary>
        public static UnityEngine.Vector3 DirectionToUnity(RbxVector3 d) =>
            new UnityEngine.Vector3(d.X, d.Y, -d.Z);

        public static RbxVector3 DirectionFromUnity(UnityEngine.Vector3 d) =>
            new RbxVector3(d.x, d.y, -d.z);

        // ---- Scalars -----------------------------------------------------------------

        /// <summary>studs/s^2 -> m/s^2 (gravity etc.; per-body application per DEV-6).</summary>
        public static float AccelerationToUnity(float studsPerSecSq) => studsPerSecSq * _metersPerStud;

        public static float AccelerationFromUnity(float metersPerSecSq) => metersPerSecSq * StudsPerMeter;

        /// <summary>Scalar length studs -> meters (part sizes: localScale = Size * MetersPerStud).</summary>
        public static float LengthToUnity(float studs) => studs * _metersPerStud;

        public static float LengthFromUnity(float meters) => meters * StudsPerMeter;

        /// <summary>Size conversion: scale each axis, no mirror (sizes are extents, not positions).</summary>
        public static UnityEngine.Vector3 SizeToUnity(RbxVector3 size) => new UnityEngine.Vector3(
            size.X * _metersPerStud, size.Y * _metersPerStud, size.Z * _metersPerStud);

        public static RbxVector3 SizeFromUnity(UnityEngine.Vector3 size) => new RbxVector3(
            size.x * StudsPerMeter, size.y * StudsPerMeter, size.z * StudsPerMeter);
    }

    /// <summary>
    /// WHY: UnityEngine.Mathf.Approximately's epsilon depends on magnitudes; the scale
    /// comparison needs a plain relative check, so it lives here explicitly.
    /// </summary>
    internal static class ScaleMath
    {
        public static bool Approximately(float a, float b) =>
            Math.Abs(a - b) <= 1e-6f * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
