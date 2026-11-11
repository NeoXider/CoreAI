using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Mirror <c>Ray</c>: a half-line from <see cref="Origin"/> along <see cref="Direction"/>. Immutable,
    /// like every Roblox datatype. <see cref="ClosestPoint"/> and <see cref="Distance"/> follow the mirror's
    /// note that they behave as expected only on a unit ray: the direction is used as given, never
    /// normalised behind the caller's back.
    /// </summary>
    public readonly struct RbxRay : IEquatable<RbxRay>
    {
        /// <summary>Mirror <c>Ray.new(Origin, Direction)</c>.</summary>
        public RbxRay(RbxVector3 origin, RbxVector3 direction)
        {
            Origin = origin;
            Direction = direction;
        }

        /// <summary>Mirror <c>Ray.Origin</c>.</summary>
        public RbxVector3 Origin { get; }

        /// <summary>Mirror <c>Ray.Direction</c>.</summary>
        public RbxVector3 Direction { get; }

        /// <summary>Mirror <c>Ray.Unit</c>: the same origin with a direction of magnitude 1.</summary>
        public RbxRay Unit => new(Origin, Direction.Unit);

        /// <summary>
        /// Mirror <c>Ray:ClosestPoint(point)</c>: the point projected onto the ray, or the origin when the
        /// point lies behind it.
        /// </summary>
        public RbxVector3 ClosestPoint(RbxVector3 point)
        {
            float along = (point - Origin).Dot(Direction);
            return along <= 0f ? Origin : Origin + Direction * along;
        }

        /// <summary>Mirror <c>Ray:Distance(point)</c>: studs from the point to <see cref="ClosestPoint"/>.</summary>
        public float Distance(RbxVector3 point)
        {
            return (point - ClosestPoint(point)).Magnitude;
        }

        public static bool operator ==(RbxRay a, RbxRay b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxRay a, RbxRay b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxRay other)
        {
            return Origin == other.Origin && Direction == other.Direction;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxRay other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Origin, Direction);
        }

        /// <summary>The engine's <c>tostring</c> format, <c>{ox, oy, oz}, {dx, dy, dz}</c>.</summary>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{{{0}}}, {{{1}}}", Origin, Direction);
        }
    }
}
