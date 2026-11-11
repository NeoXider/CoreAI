using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Engine-free maths of character control: the world direction <c>Humanoid:Move</c> resolves, and
    /// the flight integrator a motor uses while <c>Flying</c>. Every motor shares it, so a host motor
    /// moves the way CoreAI's own does.
    /// </summary>
    public static class RbxCharacterControlMath
    {
        /// <summary>Default rate, in studs per second squared, at which flight reaches its target velocity.</summary>
        public const double DefaultFlightAccelerationStuds = 120d;

        /// <summary>
        /// Resolves a <c>Humanoid:Move</c> argument to a world direction of length at most 1.
        /// <paramref name="relativeToCamera"/> reads <paramref name="moveDirection"/> in the camera's
        /// frame flattened to the ground plane (negative Z is forward, as in the mirror), so looking
        /// down never slows walking; <paramref name="keepVertical"/> keeps the world-space Y component
        /// (flight), otherwise it is dropped.
        /// </summary>
        public static RbxVector3 ResolveMoveDirection(RbxVector3 moveDirection, bool relativeToCamera,
            RbxCFrame? camera, bool keepVertical)
        {
            if (!IsFinite(moveDirection))
            {
                return RbxVector3.Zero;
            }

            RbxVector3 world = moveDirection;
            if (relativeToCamera && camera.HasValue)
            {
                RbxVector3 forward = Flatten(camera.Value.LookVector, fallback: Flatten(-camera.Value.UpVector,
                    fallback: new RbxVector3(0f, 0f, -1f)));
                RbxVector3 right = new(-forward.Z, 0f, forward.X);
                world = right * moveDirection.X + forward * -moveDirection.Z
                        + new RbxVector3(0f, moveDirection.Y, 0f);
            }

            if (!keepVertical)
            {
                world = new RbxVector3(world.X, 0f, world.Z);
            }

            float magnitude = world.Magnitude;
            return magnitude > 1f ? world * (1f / magnitude) : world;
        }

        /// <summary>
        /// One flight step: moves <paramref name="current"/> toward <paramref name="target"/> (both in
        /// studs per second) by at most <paramref name="accelerationStuds"/> times
        /// <paramref name="deltaSeconds"/>, so letting go of the keys brakes to a hover without gravity.
        /// </summary>
        public static RbxVector3 StepFlightVelocity(RbxVector3 current, RbxVector3 target,
            double accelerationStuds, double deltaSeconds)
        {
            if (!IsFinite(current))
            {
                current = RbxVector3.Zero;
            }

            if (!IsFinite(target) || deltaSeconds <= 0d || accelerationStuds <= 0d)
            {
                return IsFinite(target) && accelerationStuds <= 0d ? target : current;
            }

            RbxVector3 difference = target - current;
            float distance = difference.Magnitude;
            float maxStep = (float)(accelerationStuds * deltaSeconds);
            return distance <= maxStep ? target : current + difference * (maxStep / distance);
        }

        private static RbxVector3 Flatten(RbxVector3 vector, RbxVector3 fallback)
        {
            RbxVector3 planar = new(vector.X, 0f, vector.Z);
            float magnitude = planar.Magnitude;
            return magnitude < 1e-4f ? fallback : planar * (1f / magnitude);
        }

        private static bool IsFinite(RbxVector3 vector)
        {
            return !float.IsNaN(vector.X) && !float.IsInfinity(vector.X)
                   && !float.IsNaN(vector.Y) && !float.IsInfinity(vector.Y)
                   && !float.IsNaN(vector.Z) && !float.IsInfinity(vector.Z);
        }
    }
}
