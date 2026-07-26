using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Pure-spec Roblox Vector2 (float components, right-handed 2D conventions).</summary>
    public readonly struct RbxVector2 : IEquatable<RbxVector2>
    {
        public float X { get; }
        public float Y { get; }

        public RbxVector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static RbxVector2 Zero => new(0f, 0f);
        public static RbxVector2 One => new(1f, 1f);
        public static RbxVector2 XAxis => new(1f, 0f);
        public static RbxVector2 YAxis => new(0f, 1f);

        public float Magnitude => MathF.Sqrt(X * X + Y * Y);

        public RbxVector2 Unit
        {
            get
            {
                float m = Magnitude;
                return new RbxVector2(X / m, Y / m);
            }
        }

        /// <summary>2D cross product — the z component of the 3D cross (a scalar, per Roblox docs).</summary>
        public float Cross(RbxVector2 other)
        {
            return X * other.Y - Y * other.X;
        }

        public float Dot(RbxVector2 other)
        {
            return X * other.X + Y * other.Y;
        }

        public RbxVector2 Lerp(RbxVector2 goal, float alpha)
        {
            return new RbxVector2(
                X + (goal.X - X) * alpha,
                Y + (goal.Y - Y) * alpha);
        }

        /// <summary>Angle in radians between vectors; negative allowed when <paramref name="isSigned"/>.</summary>
        public float Angle(RbxVector2 other, bool isSigned = false)
        {
            float angle = MathF.Atan2(Cross(other), Dot(other));
            return isSigned ? angle : MathF.Abs(angle);
        }

        public bool FuzzyEq(RbxVector2 other, float epsilon = 1e-5f)
        {
            return MathF.Abs(X - other.X) <= epsilon && MathF.Abs(Y - other.Y) <= epsilon;
        }

        public RbxVector2 Abs()
        {
            return new RbxVector2(MathF.Abs(X), MathF.Abs(Y));
        }

        public RbxVector2 Ceil()
        {
            return new RbxVector2(MathF.Ceiling(X), MathF.Ceiling(Y));
        }

        public RbxVector2 Floor()
        {
            return new RbxVector2(MathF.Floor(X), MathF.Floor(Y));
        }

        public RbxVector2 Sign()
        {
            return new RbxVector2(MathF.Sign(X), MathF.Sign(Y));
        }

        public RbxVector2 Max(params RbxVector2[] others)
        {
            RbxVector2 result = this;
            foreach (RbxVector2 v in others)
            {
                result = new RbxVector2(MathF.Max(result.X, v.X), MathF.Max(result.Y, v.Y));
            }

            return result;
        }

        public RbxVector2 Min(params RbxVector2[] others)
        {
            RbxVector2 result = this;
            foreach (RbxVector2 v in others)
            {
                result = new RbxVector2(MathF.Min(result.X, v.X), MathF.Min(result.Y, v.Y));
            }

            return result;
        }

        public static RbxVector2 operator +(RbxVector2 a, RbxVector2 b)
        {
            return new RbxVector2(a.X + b.X, a.Y + b.Y);
        }

        public static RbxVector2 operator -(RbxVector2 a, RbxVector2 b)
        {
            return new RbxVector2(a.X - b.X, a.Y - b.Y);
        }

        public static RbxVector2 operator -(RbxVector2 a)
        {
            return new RbxVector2(-a.X, -a.Y);
        }

        public static RbxVector2 operator *(RbxVector2 a, RbxVector2 b)
        {
            return new RbxVector2(a.X * b.X, a.Y * b.Y);
        }

        public static RbxVector2 operator *(RbxVector2 a, float s)
        {
            return new RbxVector2(a.X * s, a.Y * s);
        }

        public static RbxVector2 operator *(float s, RbxVector2 a)
        {
            return a * s;
        }

        public static RbxVector2 operator /(RbxVector2 a, RbxVector2 b)
        {
            return new RbxVector2(a.X / b.X, a.Y / b.Y);
        }

        public static RbxVector2 operator /(RbxVector2 a, float s)
        {
            return new RbxVector2(a.X / s, a.Y / s);
        }

        public static bool operator ==(RbxVector2 a, RbxVector2 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxVector2 a, RbxVector2 b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxVector2 other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxVector2 v && Equals(v);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y);
        }

        /// <summary>Roblox tostring format: "x, y".</summary>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}, {1}", X, Y);
        }
    }
}
