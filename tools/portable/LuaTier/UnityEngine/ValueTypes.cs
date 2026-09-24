using System;

namespace UnityEngine
{
    /// <summary>
    /// Data twin of <c>UnityEngine.Vector3</c>: the fields, constructors, axis constants, equality rule
    /// (squared distance below 1e-5 squared), magnitude, normalization, dot and cross products,
    /// subtraction and scalar multiplication/division, each with the body of Unity's managed
    /// implementation. No other math is provided; code that needs it fails to compile.
    /// </summary>
    [Serializable]
    public struct Vector3 : IEquatable<Vector3>
    {
        public const float kEpsilon = 0.00001F;

        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3(float x, float y)
        {
            this.x = x;
            this.y = y;
            z = 0F;
        }

        public const float kEpsilonNormalSqrt = 1e-15F;

        public static Vector3 zero => new Vector3(0F, 0F, 0F);

        public static Vector3 one => new Vector3(1F, 1F, 1F);

        public static Vector3 down => new Vector3(0F, -1F, 0F);

        public static Vector3 right => new Vector3(1F, 0F, 0F);

        public static Vector3 forward => new Vector3(0F, 0F, 1F);

        public static float Dot(Vector3 lhs, Vector3 rhs)
        {
            return lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z;
        }

        public static Vector3 Cross(Vector3 lhs, Vector3 rhs)
        {
            return new Vector3(
                lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.z * rhs.x - lhs.x * rhs.z,
                lhs.x * rhs.y - lhs.y * rhs.x);
        }

        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);

        public float sqrMagnitude => x * x + y * y + z * z;

        public Vector3 normalized
        {
            get
            {
                float mag = magnitude;
                return mag > kEpsilonNormalSqrt ? this / mag : zero;
            }
        }

        public static Vector3 operator /(Vector3 a, float d)
        {
            return new Vector3(a.x / d, a.y / d, a.z / d);
        }

        public static Vector3 operator *(Vector3 a, float d)
        {
            return new Vector3(a.x * d, a.y * d, a.z * d);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static bool operator ==(Vector3 lhs, Vector3 rhs)
        {
            float diffX = lhs.x - rhs.x;
            float diffY = lhs.y - rhs.y;
            float diffZ = lhs.z - rhs.z;
            float sqrmag = diffX * diffX + diffY * diffY + diffZ * diffZ;
            return sqrmag < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector3 lhs, Vector3 rhs)
        {
            return !(lhs == rhs);
        }

        public bool Equals(Vector3 other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public override bool Equals(object other)
        {
            return other is Vector3 vector && Equals(vector);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        }
    }

    /// <summary>Data twin of <c>UnityEngine.Vector2</c> (fields, constructor, equality rule).</summary>
    [Serializable]
    public struct Vector2 : IEquatable<Vector2>
    {
        public const float kEpsilon = 0.00001F;

        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new Vector2(0F, 0F);

        public static bool operator ==(Vector2 lhs, Vector2 rhs)
        {
            float diffX = lhs.x - rhs.x;
            float diffY = lhs.y - rhs.y;
            return diffX * diffX + diffY * diffY < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector2 lhs, Vector2 rhs)
        {
            return !(lhs == rhs);
        }

        public bool Equals(Vector2 other)
        {
            return x == other.x && y == other.y;
        }

        public override bool Equals(object other)
        {
            return other is Vector2 vector && Equals(vector);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2);
        }
    }

    /// <summary>Data twin of <c>UnityEngine.Vector4</c> (fields, constructor, equality rule).</summary>
    [Serializable]
    public struct Vector4 : IEquatable<Vector4>
    {
        public const float kEpsilon = 0.00001F;

        public float x;
        public float y;
        public float z;
        public float w;

        public Vector4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Vector4 zero => new Vector4(0F, 0F, 0F, 0F);

        public static bool operator ==(Vector4 lhs, Vector4 rhs)
        {
            float diffX = lhs.x - rhs.x;
            float diffY = lhs.y - rhs.y;
            float diffZ = lhs.z - rhs.z;
            float diffW = lhs.w - rhs.w;
            float sqrmag = diffX * diffX + diffY * diffY + diffZ * diffZ + diffW * diffW;
            return sqrmag < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector4 lhs, Vector4 rhs)
        {
            return !(lhs == rhs);
        }

        public bool Equals(Vector4 other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        public override bool Equals(object other)
        {
            return other is Vector4 vector && Equals(vector);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        }
    }

    /// <summary>
    /// Data twin of <c>UnityEngine.Quaternion</c>: fields, constructor, identity, Unity's equality rule
    /// (dot product above 1 - 1e-6), and the managed bodies of <c>Dot</c>, <c>Angle</c> and
    /// quaternion-times-vector rotation. <c>Quaternion.Euler</c> is native in Unity and is refused.
    /// </summary>
    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public const float kEpsilon = 0.000001F;

        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0F, 0F, 0F, 1F);

        public static Quaternion Euler(float x, float y, float z)
        {
            throw PortableEngine.Unavailable("Quaternion.Euler");
        }

        public static Quaternion Euler(Vector3 euler)
        {
            throw PortableEngine.Unavailable("Quaternion.Euler");
        }

        public static float Dot(Quaternion a, Quaternion b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Min(Mathf.Abs(Dot(a, b)), 1.0F);
            return dot > 1.0f - kEpsilon ? 0.0f : Mathf.Acos(dot) * 2.0F * Mathf.Rad2Deg;
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float x = rotation.x * 2F;
            float y = rotation.y * 2F;
            float z = rotation.z * 2F;
            float xx = rotation.x * x;
            float yy = rotation.y * y;
            float zz = rotation.z * z;
            float xy = rotation.x * y;
            float xz = rotation.x * z;
            float yz = rotation.y * z;
            float wx = rotation.w * x;
            float wy = rotation.w * y;
            float wz = rotation.w * z;

            Vector3 res;
            res.x = (1F - (yy + zz)) * point.x + (xy - wz) * point.y + (xz + wy) * point.z;
            res.y = (xy + wz) * point.x + (1F - (xx + zz)) * point.y + (yz - wx) * point.z;
            res.z = (xz - wy) * point.x + (yz + wx) * point.y + (1F - (xx + yy)) * point.z;
            return res;
        }

        public static bool operator ==(Quaternion lhs, Quaternion rhs)
        {
            return Dot(lhs, rhs) > 1.0f - kEpsilon;
        }

        public static bool operator !=(Quaternion lhs, Quaternion rhs)
        {
            return !(lhs == rhs);
        }

        public bool Equals(Quaternion other)
        {
            return x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
        }

        public override bool Equals(object other)
        {
            return other is Quaternion quaternion && Equals(quaternion);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        }
    }

    /// <summary>Data twin of <c>UnityEngine.Color</c> (fields, constructors, component equality).</summary>
    [Serializable]
    public struct Color : IEquatable<Color>
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public Color(float r, float g, float b)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            a = 1F;
        }

        public static Color white => new Color(1F, 1F, 1F, 1F);

        public bool Equals(Color other)
        {
            return r.Equals(other.r) && g.Equals(other.g) && b.Equals(other.b) && a.Equals(other.a);
        }

        public override bool Equals(object other)
        {
            return other is Color color && Equals(color);
        }

        public override int GetHashCode()
        {
            return r.GetHashCode() ^ (g.GetHashCode() << 2) ^ (b.GetHashCode() >> 2) ^ (a.GetHashCode() >> 1);
        }
    }

    /// <summary>Data twin of <c>UnityEngine.Color32</c>, including Unity's rounding conversion from Color.</summary>
    [Serializable]
    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;

        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static implicit operator Color32(Color c)
        {
            return new Color32(
                (byte)Math.Round(Mathf.Clamp01(c.r) * 255F),
                (byte)Math.Round(Mathf.Clamp01(c.g) * 255F),
                (byte)Math.Round(Mathf.Clamp01(c.b) * 255F),
                (byte)Math.Round(Mathf.Clamp01(c.a) * 255F));
        }
    }

    /// <summary>Data twin of <c>UnityEngine.Rect</c> (position and size fields).</summary>
    [Serializable]
    public struct Rect
    {
        private float m_XMin;
        private float m_YMin;
        private float m_Width;
        private float m_Height;

        public Rect(float x, float y, float width, float height)
        {
            m_XMin = x;
            m_YMin = y;
            m_Width = width;
            m_Height = height;
        }

        public float x => m_XMin;

        public float y => m_YMin;

        public float width => m_Width;

        public float height => m_Height;
    }

    /// <summary>Data twin of <c>UnityEngine.Bounds</c> (center and extents, built from center and size).</summary>
    [Serializable]
    public struct Bounds
    {
        private Vector3 m_Center;
        private Vector3 m_Extents;

        public Bounds(Vector3 center, Vector3 size)
        {
            m_Center = center;
            m_Extents = size * 0.5F;
        }

        public Vector3 center => m_Center;

        public Vector3 extents => m_Extents;

        public Vector3 size => m_Extents * 2.0F;
    }

    /// <summary>Compile-time surface of <c>UnityEngine.ColorUtility</c>; its parser is native and refused.</summary>
    public static class ColorUtility
    {
        public static bool TryParseHtmlString(string htmlString, out Color color)
        {
            throw PortableEngine.Unavailable("ColorUtility.TryParseHtmlString");
        }
    }
}
