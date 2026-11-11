using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// What a character's body is in, as its motor senses it: water round its middle, water round its head, and
    /// the height of the water's surface over it. Engine-free; a motor with no water reports <see cref="Dry"/>.
    /// </summary>
    public readonly struct RbxWaterContact
    {
        /// <summary>Creates a contact.</summary>
        public RbxWaterContact(bool middleSubmerged, bool headSubmerged, double surfaceStuds)
        {
            MiddleSubmerged = middleSubmerged;
            HeadSubmerged = headSubmerged;
            SurfaceStuds = surfaceStuds;
        }

        /// <summary>No water anywhere on the body.</summary>
        public static RbxWaterContact Dry => new(false, false, double.NegativeInfinity);

        /// <summary>True while the body's middle (half way from the feet to the top of the head) is under water.</summary>
        public bool MiddleSubmerged { get; }

        /// <summary>True while the top of the head is under water.</summary>
        public bool HeadSubmerged { get; }

        /// <summary>The height of the water's surface over the body, in studs; negative infinity when dry.</summary>
        public double SurfaceStuds { get; }
    }

    /// <summary>
    /// When a Humanoid swims, as Roblox decides it: it starts swimming once its middle is under the surface,
    /// unless it stands on the bottom with its head out (water less than a body deep is waded through); it stops
    /// when its feet find ground with its head out, when it leaves the water, or when it jumps at the surface.
    /// Engine-free.
    /// </summary>
    /// <remarks>
    /// The speeds follow WalkSpeed. DEV candidate: the mirror documents the Swimming state but not its numbers,
    /// so a swimmer moves at <see cref="SwimSpeedShare"/> of WalkSpeed, rises at <see cref="RiseSpeedShare"/> of it
    /// while Jump is held, and sinks at <see cref="SinkStudsPerSecond"/> with no input.
    /// </remarks>
    public static class RbxSwimRules
    {
        /// <summary>Swim speed as a share of WalkSpeed.</summary>
        public const double SwimSpeedShare = 0.75d;

        /// <summary>Rising speed while Jump is held, as a share of WalkSpeed.</summary>
        public const double RiseSpeedShare = 0.6d;

        /// <summary>How fast a swimmer with no input sinks, in studs per second.</summary>
        public const double SinkStudsPerSecond = 1.5d;

        /// <summary>
        /// Whether the Humanoid swims after this step: <paramref name="swimming"/> is the state it is in,
        /// <paramref name="grounded"/> whether its feet stand on something.
        /// </summary>
        public static bool ShouldSwim(bool swimming, in RbxWaterContact water, bool grounded)
        {
            bool wading = grounded && !water.HeadSubmerged;
            if (!swimming)
            {
                return water.MiddleSubmerged && !wading;
            }

            if (wading)
            {
                return false;
            }

            return water.MiddleSubmerged || water.HeadSubmerged;
        }

        /// <summary>True when a swimmer's jump takes it out of the water rather than up through it: its head is out.</summary>
        public static bool JumpLeavesWater(in RbxWaterContact water)
        {
            return !water.HeadSubmerged;
        }

        /// <summary>
        /// The velocity a swimmer aims for, in studs per second: along <paramref name="moveDirection"/> (its
        /// vertical part included, so looking down while swimming forward dives) at the swim speed, rising while
        /// <paramref name="rise"/> is held, and sinking slowly with no input.
        /// </summary>
        public static RbxVector3 TargetVelocity(RbxVector3 moveDirection, bool rise, double walkSpeed)
        {
            double speed = Math.Max(0d, walkSpeed) * SwimSpeedShare;
            RbxVector3 target = moveDirection.Magnitude > 1e-4f
                ? moveDirection.Unit * (float)speed
                : RbxVector3.Zero;
            if (rise)
            {
                return new RbxVector3(target.X, (float)(Math.Max(0d, walkSpeed) * RiseSpeedShare), target.Z);
            }

            if (moveDirection.Magnitude <= 1e-4f)
            {
                return new RbxVector3(0f, (float)-SinkStudsPerSecond, 0f);
            }

            return target;
        }
    }
}
