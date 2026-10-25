using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Pure-spec Roblox UDim: relative scale + pixel offset (offset is an integer).</summary>
    public readonly struct RbxUDim : IEquatable<RbxUDim>
    {
        public float Scale { get; }
        public int Offset { get; }

        public RbxUDim(float scale, int offset)
        {
            Scale = scale;
            Offset = offset;
        }

        public static RbxUDim operator +(RbxUDim a, RbxUDim b)
        {
            return new RbxUDim(a.Scale + b.Scale, a.Offset + b.Offset);
        }

        public static RbxUDim operator -(RbxUDim a, RbxUDim b)
        {
            return new RbxUDim(a.Scale - b.Scale, a.Offset - b.Offset);
        }

        public static RbxUDim operator -(RbxUDim a)
        {
            return new RbxUDim(-a.Scale, -a.Offset);
        }

        public static bool operator ==(RbxUDim a, RbxUDim b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxUDim a, RbxUDim b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxUDim other)
        {
            return Scale == other.Scale && Offset == other.Offset;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxUDim u && Equals(u);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Scale, Offset);
        }

        /// <summary>Roblox tostring format: "{scale, offset}".</summary>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{{{0}, {1}}}", Scale, Offset);
        }
    }

    /// <summary>Pure-spec Roblox UDim2: a UDim per screen axis.</summary>
    public readonly struct RbxUDim2 : IEquatable<RbxUDim2>
    {
        public RbxUDim X { get; }
        public RbxUDim Y { get; }

        /// <summary>Width — alias of X (Roblox parity).</summary>
        public RbxUDim Width => X;

        /// <summary>Height — alias of Y (Roblox parity).</summary>
        public RbxUDim Height => Y;

        public RbxUDim2(RbxUDim x, RbxUDim y)
        {
            X = x;
            Y = y;
        }

        public RbxUDim2(float xScale, int xOffset, float yScale, int yOffset)
            : this(new RbxUDim(xScale, xOffset), new RbxUDim(yScale, yOffset))
        {
        }

        public static RbxUDim2 FromScale(float xScale, float yScale)
        {
            return new RbxUDim2(xScale, 0, yScale, 0);
        }

        public static RbxUDim2 FromOffset(int xOffset, int yOffset)
        {
            return new RbxUDim2(0f, xOffset, 0f, yOffset);
        }

        /// <summary>
        /// UDim2:Lerp interpolates offsets in double precision and rounds to the nearest even
        /// integer. An offset outside the signed 32-bit range raises BAD_ARGUMENT.
        /// </summary>
        public RbxUDim2 Lerp(RbxUDim2 goal, float alpha)
        {
            return new RbxUDim2(
                X.Scale + (goal.X.Scale - X.Scale) * alpha,
                InterpolateOffset(X.Offset, goal.X.Offset, alpha),
                Y.Scale + (goal.Y.Scale - Y.Scale) * alpha,
                InterpolateOffset(Y.Offset, goal.Y.Offset, alpha));
        }

        private static int InterpolateOffset(int start, int end, float alpha)
        {
            double offset = Math.Round(start + ((double)end - start) * alpha);
            if (double.IsNaN(offset) || offset < int.MinValue || offset > int.MaxValue)
            {
                throw RbxApiStubException.BadArgument(
                    "UDim2:Lerp produced an offset outside the signed 32-bit range",
                    "use a finite alpha whose interpolated offsets fit signed 32-bit integers");
            }

            return (int)offset;
        }

        public static RbxUDim2 operator +(RbxUDim2 a, RbxUDim2 b)
        {
            return new RbxUDim2(a.X + b.X, a.Y + b.Y);
        }

        public static RbxUDim2 operator -(RbxUDim2 a, RbxUDim2 b)
        {
            return new RbxUDim2(a.X - b.X, a.Y - b.Y);
        }

        public static RbxUDim2 operator -(RbxUDim2 a)
        {
            return new RbxUDim2(-a.X, -a.Y);
        }

        public static bool operator ==(RbxUDim2 a, RbxUDim2 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxUDim2 a, RbxUDim2 b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxUDim2 other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxUDim2 u && Equals(u);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y);
        }

        /// <summary>Roblox tostring format: "{xScale, xOffset}, {yScale, yOffset}".</summary>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{0}, {1}", X, Y);
        }
    }
}
