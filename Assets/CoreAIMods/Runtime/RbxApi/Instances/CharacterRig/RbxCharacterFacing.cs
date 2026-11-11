using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// The turn a character makes toward the direction it moves in, behind <c>Humanoid.AutoRotate</c>: yaw
    /// only, in Roblox space, where <c>CFrame.Angles(0, yaw, 0).LookVector</c> is <c>(-sin yaw, 0, -cos yaw)</c>.
    /// </summary>
    /// <remarks>
    /// The mirror says the character "will gradually turn to face their movement direction" and publishes no
    /// rate. OURS: an exponential approach at <see cref="DefaultTurnResponsePerSecond"/> — a half turn is
    /// nine tenths done in about 0.16 s, quick enough to read as the Roblox snap and soft enough not to pop,
    /// and frame-rate independent, so a 30 Hz and a 120 Hz step turn the same way.
    /// </remarks>
    public static class RbxCharacterFacing
    {
        /// <summary>The default turn response, per second (see the class remarks).</summary>
        public const double DefaultTurnResponsePerSecond = 14d;

        /// <summary>Horizontal length under which a direction has no heading to turn to.</summary>
        public const double MinimumHeadingLength = 1e-4d;

        /// <summary>A remaining turn smaller than this, in radians, finishes at once.</summary>
        public const double SnapRadians = 1e-4d;

        /// <summary>The yaw of a look direction; a vertical or zero direction answers 0.</summary>
        public static double YawOf(RbxVector3 lookVector)
        {
            return Math.Atan2(-lookVector.X, -lookVector.Z);
        }

        /// <summary>The yaw a character moving along <paramref name="direction"/> faces; false when it has no horizontal part.</summary>
        public static bool TryYawOfDirection(RbxVector3 direction, out double yaw)
        {
            double x = direction.X;
            double z = direction.Z;
            if (x * x + z * z < MinimumHeadingLength * MinimumHeadingLength)
            {
                yaw = 0d;
                return false;
            }

            yaw = Math.Atan2(-x, -z);
            return true;
        }

        /// <summary>
        /// One step of the turn from <paramref name="currentYaw"/> toward <paramref name="targetYaw"/>, the short
        /// way round, never past it. A zero or negative step leaves the yaw where it is.
        /// </summary>
        public static double StepYaw(double currentYaw, double targetYaw, double deltaSeconds,
            double responsePerSecond = DefaultTurnResponsePerSecond)
        {
            double remaining = WrapAngle(targetYaw - currentYaw);
            if (Math.Abs(remaining) <= SnapRadians)
            {
                return WrapAngle(targetYaw);
            }

            if (deltaSeconds <= 0d || responsePerSecond <= 0d)
            {
                return currentYaw;
            }

            double fraction = 1d - Math.Exp(-responsePerSecond * deltaSeconds);
            return WrapAngle(currentYaw + remaining * fraction);
        }

        /// <summary>An angle in radians wrapped into (-pi, pi].</summary>
        public static double WrapAngle(double radians)
        {
            double wrapped = Math.IEEERemainder(radians, 2d * Math.PI);
            return wrapped <= -Math.PI ? wrapped + 2d * Math.PI : wrapped;
        }
    }
}
