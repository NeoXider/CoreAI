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

        private readonly Rigidbody _body;
        private readonly float _groundProbeOrigin;
        private float _walkSpeedMetres = RbxSpace.LengthToUnity((float)RbxHumanoid.DefaultWalkSpeed);
        private Vector3? _targetMetres;

        /// <summary>Drives an existing Rigidbody as a character.</summary>
        public UnityRbxCharacterMotor(Rigidbody body, float capsuleHalfHeightMetres = 0.5f)
        {
            _body = body != null ? body : throw new ArgumentNullException(nameof(body));
            _groundProbeOrigin = capsuleHalfHeightMetres;
            _body.freezeRotation = true;
        }

        /// <inheritdoc />
        public RbxVector3 Position => RbxSpace.FromUnity(_body.position);

        /// <inheritdoc />
        public RbxVector3 MoveDirection
        {
            get
            {
                Vector3 planar = new(_body.linearVelocity.x, 0f, _body.linearVelocity.z);
                return planar.sqrMagnitude <= 1e-6f
                    ? RbxVector3.Zero
                    : RbxSpace.DirectionFromUnity(planar.normalized);
            }
        }

        /// <inheritdoc />
        public bool IsGrounded =>
            Physics.Raycast(_body.position, Vector3.down, _groundProbeOrigin + GroundProbeMetres);

        /// <inheritdoc />
        public void SetWalkSpeed(double studsPerSecond)
        {
            _walkSpeedMetres = RbxSpace.LengthToUnity((float)studsPerSecond);
        }

        /// <inheritdoc />
        public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
            if (!IsGrounded)
            {
                return;
            }

            // WHY two formulas: the mirror treats JumpPower as an upward impulse and JumpHeight as
            // the height actually reached, so the second has to be solved against current gravity
            // (v = sqrt(2·g·h)) rather than used as a velocity.
            float upward = useJumpPower
                ? RbxSpace.LengthToUnity((float)jumpPower)
                : Mathf.Sqrt(2f * Mathf.Abs(Physics.gravity.y == 0f ? 9.81f : Physics.gravity.y)
                             * RbxSpace.LengthToUnity((float)jumpHeight));

            Vector3 velocity = _body.linearVelocity;
            velocity.y = upward;
            _body.linearVelocity = velocity;
        }

        /// <inheritdoc />
        public void MoveTo(RbxVector3? targetStuds)
        {
            _targetMetres = targetStuds.HasValue ? RbxSpace.ToUnity(targetStuds.Value) : null;
            if (!_targetMetres.HasValue)
            {
                Vector3 stopped = _body.linearVelocity;
                stopped.x = 0f;
                stopped.z = 0f;
                _body.linearVelocity = stopped;
            }
        }

        /// <summary>Advances the walk by one fixed step. Call from the fixed-step pump.</summary>
        public void Step()
        {
            if (!_targetMetres.HasValue)
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
