namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>What a character's body is doing, as the built-in poser animates it.</summary>
    public enum RbxRigPoseKind
    {
        /// <summary>Standing: a slow breath and a slight arm sway.</summary>
        Idle,

        /// <summary>Walking: arms and legs swing opposite, stride matched to the speed.</summary>
        Walk,

        /// <summary>Rising from a jump: arms up.</summary>
        Jump,

        /// <summary>Falling: arms up and out, legs apart.</summary>
        Fall,

        /// <summary>The frame the ground is regained: a short dip, then idle or walk.</summary>
        Land,

        /// <summary>Free flight: leaning into the direction of travel, or hovering.</summary>
        Fly,

        /// <summary>Seated: legs forward.</summary>
        Sit,

        /// <summary>Dead: the last pose is held.</summary>
        Dead
    }

    /// <summary>
    /// Maps a Humanoid's state and running speed to the pose the built-in poser plays, the role the
    /// <c>Animate</c> script's state handlers have in Roblox.
    /// </summary>
    public static class RbxRigPoseSelector
    {
        /// <summary>
        /// Speed, in studs per second, a standing character must reach to start the walk cycle; the same line
        /// the Humanoid uses before it reports <c>Running</c> as moving.
        /// </summary>
        public const double WalkStartSpeedStuds = RbxHumanoid.RunningStartSpeedStuds;

        /// <summary>Speed under which a walking character goes back to idle; the Humanoid's stop line.</summary>
        public const double WalkStopSpeedStuds = RbxHumanoid.RunningStopSpeedStuds;

        /// <summary>
        /// The pose for a Humanoid in <paramref name="state"/> moving at <paramref name="speedStuds"/>.
        /// </summary>
        /// <param name="state">The Humanoid's state.</param>
        /// <param name="speedStuds">Horizontal speed in studs per second, as <c>Humanoid.Running</c> reports it.</param>
        /// <param name="rootAnchored">True when the root part is anchored: the character stands, whatever it reports.</param>
        /// <param name="seated">True while the character sits.</param>
        /// <param name="wasWalking">True when the previous pose was the walk, for the start/stop hysteresis.</param>
        /// <remarks>
        /// WHY an anchored root stands: an anchored root cannot move, yet a Humanoid with no body still accepts
        /// a jump (the null motor takes it) and a script may still report a speed; a walk or a jump played there
        /// would be a character running on the spot.
        /// </remarks>
        public static RbxRigPoseKind Select(RbxHumanoidState state, double speedStuds, bool rootAnchored,
            bool seated, bool wasWalking)
        {
            if (state == RbxHumanoidState.Dead)
            {
                return RbxRigPoseKind.Dead;
            }

            if (seated)
            {
                return RbxRigPoseKind.Sit;
            }

            if (rootAnchored)
            {
                return RbxRigPoseKind.Idle;
            }

            switch (state)
            {
                case RbxHumanoidState.Jumping:
                    return RbxRigPoseKind.Jump;
                case RbxHumanoidState.Freefall:
                    return RbxRigPoseKind.Fall;
                case RbxHumanoidState.Flying:
                    return RbxRigPoseKind.Fly;
                case RbxHumanoidState.Landed:
                    return RbxRigPoseKind.Land;
            }

            double threshold = wasWalking ? WalkStopSpeedStuds : WalkStartSpeedStuds;
            return speedStuds >= threshold ? RbxRigPoseKind.Walk : RbxRigPoseKind.Idle;
        }
    }
}
