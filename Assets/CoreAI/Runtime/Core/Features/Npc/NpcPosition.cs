using System;

namespace CoreAI.Ai.Npc
{
    /// <summary>A point in the game's world space, in whatever unit the game measures distance in.</summary>
    public readonly struct NpcPosition
    {
        /// <summary>Creates a point.</summary>
        public NpcPosition(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>X coordinate.</summary>
        public double X { get; }

        /// <summary>Y coordinate.</summary>
        public double Y { get; }

        /// <summary>Z coordinate.</summary>
        public double Z { get; }

        /// <summary>Straight-line distance to <paramref name="other"/>.</summary>
        public double DistanceTo(NpcPosition other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            double dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "(" + X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                   Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                   Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// Whether a speaker can see an NPC, for <see cref="NpcAddressingRules.RequireLineOfSight"/>. The default
    /// answers true; a game with physics plugs in a raycast.
    /// </summary>
    public interface INpcLineOfSight
    {
        /// <summary>True when nothing blocks the straight line between the two points.</summary>
        bool CanSee(NpcPosition from, NpcPosition to);
    }

    /// <summary>A line-of-sight query that never finds an obstacle.</summary>
    public sealed class OpenNpcLineOfSight : INpcLineOfSight
    {
        /// <summary>Shared instance; the type has no state.</summary>
        public static readonly OpenNpcLineOfSight Instance = new();

        /// <inheritdoc />
        public bool CanSee(NpcPosition from, NpcPosition to)
        {
            return true;
        }
    }

    /// <summary>Monotonic time source of the NPC block, in seconds; a test passes a manual clock.</summary>
    public interface INpcClock
    {
        /// <summary>Seconds since an arbitrary start, never going backwards and unaffected by game time scale.</summary>
        double NowSeconds { get; }
    }

    /// <summary>The default clock: a <see cref="System.Diagnostics.Stopwatch"/> started on construction.</summary>
    public sealed class StopwatchNpcClock : INpcClock
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

        /// <inheritdoc />
        public double NowSeconds => _stopwatch.Elapsed.TotalSeconds;
    }
}
