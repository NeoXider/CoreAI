using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Pure-spec Roblox Vector3 (right-handed coordinate system; ROBLOX_API_ROADMAP.md D1).
    /// Components are 32-bit floats like Roblox's native type. No Unity types appear here —
    /// conversion happens only in the RbxSpace adapter.
    /// </summary>
    public readonly struct RbxVector3 : IEquatable<RbxVector3>
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public RbxVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static RbxVector3 Zero => new RbxVector3(0f, 0f, 0f);
        public static RbxVector3 One => new RbxVector3(1f, 1f, 1f);
        public static RbxVector3 XAxis => new RbxVector3(1f, 0f, 0f);
        public static RbxVector3 YAxis => new RbxVector3(0f, 1f, 0f);
        public static RbxVector3 ZAxis => new RbxVector3(0f, 0f, 1f);

        /// <summary>Vector3.FromNormalId — Roblox face directions (Front = -Z, right-handed).</summary>
        public static RbxVector3 FromNormalId(RbxEnumItem normalId)
        {
            if (normalId == null || normalId.EnumType.Name != "NormalId")
            {
                throw RbxApiStubException.BadArgument(
                    "Vector3.FromNormalId expects an Enum.NormalId.",
                    "pass Enum.NormalId.Front (or another NormalId item) at argument 1");
            }

            switch (normalId.Name)
            {
                case "Right": return XAxis;
                case "Top": return YAxis;
                case "Back": return ZAxis;
                case "Left": return new RbxVector3(-1f, 0f, 0f);
                case "Bottom": return new RbxVector3(0f, -1f, 0f);
                case "Front": return new RbxVector3(0f, 0f, -1f);
                default:
                    throw RbxApiStubException.BadArgument(
                        $"Unknown NormalId '{normalId.Name}'.",
                        "use one of Right/Top/Back/Left/Bottom/Front");
            }
        }

        /// <summary>Vector3.FromAxis — unit vector along Enum.Axis.</summary>
        public static RbxVector3 FromAxis(RbxEnumItem axis)
        {
            if (axis == null || axis.EnumType.Name != "Axis")
            {
                throw RbxApiStubException.BadArgument(
                    "Vector3.FromAxis expects an Enum.Axis.",
                    "pass Enum.Axis.X (or Y/Z) at argument 1");
            }

            switch (axis.Name)
            {
                case "X": return XAxis;
                case "Y": return YAxis;
                case "Z": return ZAxis;
                default:
                    throw RbxApiStubException.BadArgument(
                        $"Unknown Axis '{axis.Name}'.",
                        "use Enum.Axis.X, Enum.Axis.Y or Enum.Axis.Z");
            }
        }

        public float Magnitude => MathF.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Normalized copy. WHY: Roblox returns (nan, nan, nan) for the zero vector — mirrored.</summary>
        public RbxVector3 Unit
        {
            get
            {
                float m = Magnitude;
                return new RbxVector3(X / m, Y / m, Z / m);
            }
        }

        public float Dot(RbxVector3 other) => X * other.X + Y * other.Y + Z * other.Z;

        public RbxVector3 Cross(RbxVector3 other) => new RbxVector3(
            Y * other.Z - Z * other.Y,
            Z * other.X - X * other.Z,
            X * other.Y - Y * other.X);

        public RbxVector3 Lerp(RbxVector3 goal, float alpha) => new RbxVector3(
            X + (goal.X - X) * alpha,
            Y + (goal.Y - Y) * alpha,
            Z + (goal.Z - Z) * alpha);

        /// <summary>Angle in radians between the vectors; signed around <paramref name="axis"/> when provided.</summary>
        public float Angle(RbxVector3 other, RbxVector3? axis = null)
        {
            RbxVector3 cross = Cross(other);
            float angle = MathF.Atan2(cross.Magnitude, Dot(other));
            if (axis.HasValue && cross.Dot(axis.Value) < 0f)
            {
                angle = -angle;
            }

            return angle;
        }

        public bool FuzzyEq(RbxVector3 other, float epsilon = 1e-5f) =>
            MathF.Abs(X - other.X) <= epsilon &&
            MathF.Abs(Y - other.Y) <= epsilon &&
            MathF.Abs(Z - other.Z) <= epsilon;

        public RbxVector3 Abs() => new RbxVector3(MathF.Abs(X), MathF.Abs(Y), MathF.Abs(Z));
        public RbxVector3 Ceil() => new RbxVector3(MathF.Ceiling(X), MathF.Ceiling(Y), MathF.Ceiling(Z));
        public RbxVector3 Floor() => new RbxVector3(MathF.Floor(X), MathF.Floor(Y), MathF.Floor(Z));
        public RbxVector3 Sign() => new RbxVector3(MathF.Sign(X), MathF.Sign(Y), MathF.Sign(Z));

        public RbxVector3 Max(params RbxVector3[] others)
        {
            var result = this;
            foreach (RbxVector3 v in others)
            {
                result = new RbxVector3(
                    MathF.Max(result.X, v.X), MathF.Max(result.Y, v.Y), MathF.Max(result.Z, v.Z));
            }

            return result;
        }

        public RbxVector3 Min(params RbxVector3[] others)
        {
            var result = this;
            foreach (RbxVector3 v in others)
            {
                result = new RbxVector3(
                    MathF.Min(result.X, v.X), MathF.Min(result.Y, v.Y), MathF.Min(result.Z, v.Z));
            }

            return result;
        }

        public static RbxVector3 operator +(RbxVector3 a, RbxVector3 b) =>
            new RbxVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static RbxVector3 operator -(RbxVector3 a, RbxVector3 b) =>
            new RbxVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static RbxVector3 operator -(RbxVector3 a) => new RbxVector3(-a.X, -a.Y, -a.Z);

        /// <summary>Component-wise product (Roblox Vector3 * Vector3 semantics).</summary>
        public static RbxVector3 operator *(RbxVector3 a, RbxVector3 b) =>
            new RbxVector3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

        public static RbxVector3 operator *(RbxVector3 a, float s) => new RbxVector3(a.X * s, a.Y * s, a.Z * s);
        public static RbxVector3 operator *(float s, RbxVector3 a) => a * s;

        public static RbxVector3 operator /(RbxVector3 a, RbxVector3 b) =>
            new RbxVector3(a.X / b.X, a.Y / b.Y, a.Z / b.Z);

        public static RbxVector3 operator /(RbxVector3 a, float s) => new RbxVector3(a.X / s, a.Y / s, a.Z / s);

        /// <summary>Lua `//` floor division against a scalar (marshaller maps the metamethod here).</summary>
        public RbxVector3 FloorDivide(float s) =>
            new RbxVector3(MathF.Floor(X / s), MathF.Floor(Y / s), MathF.Floor(Z / s));

        /// <summary>Lua `//` floor division component-wise.</summary>
        public RbxVector3 FloorDivide(RbxVector3 b) =>
            new RbxVector3(MathF.Floor(X / b.X), MathF.Floor(Y / b.Y), MathF.Floor(Z / b.Z));

        public static bool operator ==(RbxVector3 a, RbxVector3 b) => a.Equals(b);
        public static bool operator !=(RbxVector3 a, RbxVector3 b) => !a.Equals(b);

        public bool Equals(RbxVector3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is RbxVector3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        /// <summary>Roblox tostring format: "x, y, z" — corpus scripts string-match on it.</summary>
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "{0}, {1}, {2}", X, Y, Z);
    }
}
