using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Mirror <c>Region3</c>: an axis-aligned box given by two corners. Immutable, like every Roblox
    /// datatype. The corners are kept exactly as passed to <c>Region3.new(min, max)</c>, so swapping
    /// them flips the sign of <see cref="Size"/> as the mirror documents ("It is possible to create a
    /// Region3 with a negative volume").
    /// </summary>
    public readonly struct RbxRegion3 : IEquatable<RbxRegion3>
    {
        /// <summary>Mirror <c>Region3.new(min, max)</c>.</summary>
        public RbxRegion3(RbxVector3 min, RbxVector3 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>The first corner, as passed to the constructor.</summary>
        public RbxVector3 Min { get; }

        /// <summary>The second corner, as passed to the constructor.</summary>
        public RbxVector3 Max { get; }

        /// <summary>Mirror <c>Region3.CFrame</c>: the centre, with no rotation.</summary>
        public RbxCFrame CFrame => RbxCFrame.FromPosition(
            (Min.X + Max.X) * 0.5f, (Min.Y + Max.Y) * 0.5f, (Min.Z + Max.Z) * 0.5f);

        /// <summary>Mirror <c>Region3.Size</c>: <c>max - min</c>, negative where the corners are swapped.</summary>
        public RbxVector3 Size => Max - Min;

        /// <summary>
        /// Mirror <c>Region3:ExpandToGrid(resolution)</c>: the first corner floored and the second
        /// ceiled to multiples of <paramref name="resolution"/>, so the result is never smaller than
        /// the original. A resolution that is not a positive finite number raises BAD_ARGUMENT.
        /// </summary>
        public RbxRegion3 ExpandToGrid(double resolution)
        {
            if (double.IsNaN(resolution) || double.IsInfinity(resolution) || resolution <= 0d)
            {
                throw RbxApiStubException.BadArgument(
                    "Region3:ExpandToGrid expects a positive resolution, got " + resolution,
                    "pass the Terrain grid resolution, e.g. region:ExpandToGrid(4)");
            }

            return new RbxRegion3(
                new RbxVector3(Floor(Min.X, resolution), Floor(Min.Y, resolution), Floor(Min.Z, resolution)),
                new RbxVector3(Ceil(Max.X, resolution), Ceil(Max.Y, resolution), Ceil(Max.Z, resolution)));
        }

        private static float Floor(float value, double resolution)
        {
            return (float)(Math.Floor(value / resolution) * resolution);
        }

        private static float Ceil(float value, double resolution)
        {
            return (float)(Math.Ceiling(value / resolution) * resolution);
        }

        public static bool operator ==(RbxRegion3 a, RbxRegion3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxRegion3 a, RbxRegion3 b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxRegion3 other)
        {
            return Min == other.Min && Max == other.Max;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxRegion3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Min, Max);
        }

        /// <summary>Roblox tostring format: the twelve CFrame components, "; ", then the size.</summary>
        public override string ToString()
        {
            return CFrame + "; " + Size;
        }
    }
}
