using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// CoreAI's own minimal character controller: the metric half of <c>Humanoid</c>.
    /// </summary>
    /// <remarks>
    /// WHY CoreAI ships its own instead of adapting an existing controller: <c>Humanoid</c> has an
    /// exact metric contract — <c>WalkSpeed</c> in studs per second at 0.28 m/stud, <c>JumpPower</c>
    /// as an upward impulse or <c>JumpHeight</c> as a target height, a grounded flag that decides the
    /// state machine. Adapting a general-purpose controller means re-deriving each of those numbers
    /// from someone else's tuning, and none of them could then be asserted. A host that prefers its
    /// own controller implements <see cref="IRbxCharacterMotor"/> and keeps every Lua-visible rule.
    /// <para>
    /// Velocity is driven directly rather than through forces: a walking character is not a physical
    /// body being pushed, and force-driven walking makes speed depend on mass and friction — the two
    /// things a scripted <c>WalkSpeed</c> must not depend on.
    /// </para>
    /// </remarks>
    public sealed class UnityRbxCharacterMotor : IRbxCharacterMotor
    {
        /// <summary>How far below the capsule counts as standing on something, in metres.</summary>
        private const float GroundProbeMetres = 0.12f;

        /// <summary>
        /// Fallback world acceleration for a motor built with no live gravity source, in metres/s².
        /// </summary>
        /// <remarks>
        /// WHY <see cref="RbxWorldPhysics.DefaultGravity"/> and not <c>Physics.gravity</c>: bound
        /// bodies never use the engine's own gravity (their <c>Rigidbody.useGravity</c> is off; see
        /// <see cref="UnityRbxPhysicsPort"/>), so falling back to it would still answer with a number
        /// the world's own acceleration has no connection to.
        /// </remarks>
        private static float DefaultWorldGravityMetresPerSecondSquared() =>
            RbxSpace.AccelerationToUnity((float)RbxWorldPhysics.DefaultGravity);

        private readonly Rigidbody _body;
        private readonly float _groundProbeOrigin;
        private readonly Func<float> _worldGravityMetresPerSecondSquared;
        private float _walkSpeedMetres = RbxSpace.LengthToUnity((float)RbxHumanoid.DefaultWalkSpeed);
        private Vector3? _targetMetres;

        /// <summary>Drives an existing Rigidbody as a character.</summary>
        /// <param name="body">The Rigidbody this motor moves.</param>
        /// <param name="capsuleHalfHeightMetres">Half-height used by the ground probe.</param>
        /// <param name="worldGravityMetresPerSecondSquared">
        /// Reads the acceleration actually applied to <paramref name="body"/> by the world (see
        /// <see cref="UnityRbxPhysicsPort.GravityMetresPerSecondSquared"/>), so a height-based jump
        /// tracks live changes to <c>Workspace.Gravity</c> instead of the engine's own
        /// <c>Physics.gravity</c>, which these bodies do not use. Defaults to the mirror's documented
        /// gravity when the caller has no live source to hand it.
        /// </param>
        public UnityRbxCharacterMotor(
            Rigidbody body,
            float capsuleHalfHeightMetres = 0.5f,
            Func<float> worldGravityMetresPerSecondSquared = null)
        {
            _body = body != null ? body : throw new ArgumentNullException(nameof(body));
            _groundProbeOrigin = capsuleHalfHeightMetres;
            _worldGravityMetresPerSecondSquared =
                worldGravityMetresPerSecondSquared ?? DefaultWorldGravityMetresPerSecondSquared;
            _body.freezeRotation = true;
        }

        /// <inheritdoc />
        public RbxVector3 Position => _body == null ? RbxVector3.Zero : RbxSpace.FromUnity(_body.position);

        /// <summary>
        /// False once the Rigidbody this motor drives has been destroyed by Unity.
        /// </summary>
        /// <remarks>
        /// WHY public rather than internal: the composition that decides when to rebuild a motor
        /// lives in <c>CoreAI.Mods</c>, a different assembly, so an internal member is invisible
        /// exactly where the question is asked. A destroyed body is a normal end of life — a
        /// character was despawned — not an error, so the answer is a property, not a throw.
        /// </remarks>
        public bool IsAvailable => _body != null;

        /// <inheritdoc />
        public RbxVector3 MoveDirection
        {
            get
            {
                if (_body == null)
                {
                    return RbxVector3.Zero;
                }

                Vector3 planar = new(_body.linearVelocity.x, 0f, _body.linearVelocity.z);
                return planar.sqrMagnitude <= 1e-6f
                    ? RbxVector3.Zero
                    : RbxSpace.DirectionFromUnity(planar.normalized);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY the solver-resolved velocity and not the speed <see cref="Step()"/> commanded: the
        /// physics step decides how fast the body actually moved — a wall it walks into leaves the
        /// commanded WalkSpeed in place and the resolved velocity at zero — and it is the same vector
        /// <see cref="MoveDirection"/> reads, so direction and speed can never disagree.
        /// WHY null and not 0 once the body is destroyed: the seam defines null as "this motor
        /// cannot measure", and a vanished body is a character that no longer exists, not one that
        /// has stopped. Answering 0 would present a despawn as a genuine stop to the Humanoid while
        /// <see cref="IsAvailable"/> is already telling the pipeline to rebuild this motor.
        /// </remarks>
        public double? MeasuredSpeed
        {
            get
            {
                if (_body == null)
                {
                    return null;
                }

                Vector3 planar = new(_body.linearVelocity.x, 0f, _body.linearVelocity.z);
                return RbxSpace.LengthFromUnity(planar.magnitude);
            }
        }

        /// <inheritdoc />
        public bool IsGrounded =>
            _body != null && Physics.Raycast(_body.position, Vector3.down, _groundProbeOrigin + GroundProbeMetres);

        /// <inheritdoc />
        public void SetWalkSpeed(double studsPerSecond)
        {
            _walkSpeedMetres = RbxSpace.LengthToUnity((float)studsPerSecond);
        }

        /// <inheritdoc />
        public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
            TryJump(jumpPower, jumpHeight, useJumpPower);
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY refused off the ground and on a kinematic body: an airborne character has nothing to
        /// push against, and a kinematic body ignores velocity writes, so reporting acceptance would
        /// put the Humanoid into Jumping for a jump that never happened.
        /// </remarks>
        public bool TryJump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
            if (_body == null || _body.isKinematic || !IsGrounded)
            {
                return false;
            }

            // WHY two formulas: the mirror treats JumpPower as an upward impulse and JumpHeight as
            // the height actually reached, so the second has to be solved against current gravity
            // (v = sqrt(2·g·h)) rather than used as a velocity. WHY the injected source and not
            // Physics.gravity: this body has gravity disabled and falls under whatever
            // Workspace.Gravity the physics port is applying (DEV-6) — the engine's own global plays
            // no part in how far it falls, so solving against it would answer a question about a
            // different, unrelated acceleration.
            float gravity = _worldGravityMetresPerSecondSquared();
            float upward = useJumpPower
                ? RbxSpace.LengthToUnity((float)jumpPower)
                : Mathf.Sqrt(2f * Mathf.Abs(gravity == 0f ? DefaultWorldGravityMetresPerSecondSquared() : gravity)
                             * RbxSpace.LengthToUnity((float)jumpHeight));

            Vector3 velocity = _body.linearVelocity;
            velocity.y = upward;
            _body.linearVelocity = velocity;
            return true;
        }

        /// <inheritdoc />
        public void MoveTo(RbxVector3? targetStuds)
        {
            _targetMetres = targetStuds.HasValue ? RbxSpace.ToUnity(targetStuds.Value) : null;
            if (_body != null && !_body.isKinematic && !_targetMetres.HasValue)
            {
                Vector3 stopped = _body.linearVelocity;
                stopped.x = 0f;
                stopped.z = 0f;
                _body.linearVelocity = stopped;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY the step length is ignored: this motor drives velocity rather than integrating a
        /// position, so the physics step itself does the integrating. The parameter exists for
        /// host motors that do integrate.
        /// </remarks>
        public void Step(double deltaSeconds) => Step();

        /// <summary>Advances the walk by one fixed step. Call from the fixed-step pump.</summary>
        public void Step()
        {
            if (_body == null || _body.isKinematic || !_targetMetres.HasValue)
            {
                return;
            }

            Vector3 delta = _targetMetres.Value - _body.position;
            delta.y = 0f;
            Vector3 velocity = _body.linearVelocity;
            Vector3 planar = delta.sqrMagnitude <= 1e-6f
                ? Vector3.zero
                : delta.normalized * _walkSpeedMetres;
            velocity.x = planar.x;
            velocity.z = planar.z;
            _body.linearVelocity = velocity;
        }
    }
}
