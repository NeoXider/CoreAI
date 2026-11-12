using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
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
    /// <para>
    /// The body's rotation is frozen against physics and turned by the motor instead: with
    /// <c>Humanoid.AutoRotate</c> on (the default) each fixed step turns it toward the direction it is told to
    /// walk or fly in (<see cref="RbxCharacterFacing"/>), yaw only, so a character faces where it goes; a
    /// character standing still keeps its heading.
    /// </para>
    /// <para>
    /// A bound character root (a part whose collider is its box) gets the body capsule of
    /// <see cref="RbxCharacterBodyShape"/> with a frictionless material, and its box is switched off: the box was
    /// two studs tall under a five-stud body, caught the top edge of a block with its square bottom, and its
    /// friction against a wall it was pushed into ate a jump's rise, so a character could not jump onto one
    /// block. The capsule covers the body, slides over edges and along walls, and grows with the root's size
    /// (<c>Model:ScaleTo</c>). Without friction a character would drift once it stops, on a slope or a block's
    /// rim, so a grounded character with nothing to walk to grips: its capsule takes a material with friction
    /// until it walks, jumps or leaves the ground. Its velocity is never written for that, so a script that
    /// moves it by velocity, or a part it stands on, still moves it, against ordinary friction.
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

        private static PhysicsMaterial _characterMaterial;
        private static PhysicsMaterial _gripMaterial;

        private readonly Rigidbody _body;
        private readonly float _groundProbeOrigin;
        private readonly BoxCollider _rootBox;
        private readonly CapsuleCollider _shape;
        private Vector3 _shapedForScale;
        private RbxCharacterBodyShape _bodyShape;
        private RbxCharacterWater _water;
        private bool _swimming;
        private bool _rise;
        private double _blockedSeconds;
        private readonly Func<float> _worldGravityMetresPerSecondSquared;
        private float _walkSpeedMetres = RbxSpace.LengthToUnity((float)RbxHumanoid.DefaultWalkSpeed);
        private double _walkSpeedStuds = RbxHumanoid.DefaultWalkSpeed;
        private Vector3? _targetMetres;
        private RbxVector3 _moveDirectionStuds = RbxVector3.Zero;
        private bool _flying;
        private bool _noclip;
        private bool _autoRotate = true;

        /// <summary>
        /// How fast the character turns toward its heading, per second (see
        /// <see cref="RbxCharacterFacing.DefaultTurnResponsePerSecond"/>).
        /// </summary>
        public double TurnResponsePerSecond { get; set; } = RbxCharacterFacing.DefaultTurnResponsePerSecond;

        /// <summary>Mirror <c>Humanoid.AutoRotate</c> as this motor last heard it.</summary>
        public bool AutoRotate => _autoRotate;

        /// <summary>Flight speed as a multiple of <c>WalkSpeed</c> (default 2, creative-mode feel).</summary>
        public double FlySpeedMultiplier { get; set; } = 2d;

        /// <summary>How fast flight reaches its target velocity, in studs per second squared.</summary>
        public double FlightAccelerationStuds { get; set; } = RbxCharacterControlMath.DefaultFlightAccelerationStuds;

        /// <inheritdoc />
        public bool IsFlying => _flying;

        /// <inheritdoc />
        public double WalkBlockedSeconds => _blockedSeconds;

        /// <summary>True while the motor walks toward a MoveTo target.</summary>
        public bool HasMoveTarget => _targetMetres.HasValue;

        /// <summary>True while the motor swims (see <see cref="SetSwimming"/>).</summary>
        public bool IsSwimming => _swimming;

        /// <inheritdoc />
        public RbxWaterContact Water
        {
            get
            {
                if (_water == null && _body != null)
                {
                    _body.TryGetComponent(out _water);
                }

                return _water != null ? _water.Contact : RbxWaterContact.Dry;
            }
        }

        /// <inheritdoc />
        public bool SetSwimming(bool swimming)
        {
            _swimming = swimming;
            if (!swimming)
            {
                _rise = false;
            }

            return true;
        }

        /// <inheritdoc />
        public void SwimRise()
        {
            _rise = true;
        }

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
            // WHY continuous: a long fall reaches tens of metres per second, and a discrete body passes through
            // a terrain chunk's mesh collider in one step (seen: a 90-stud drop left the world).
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _shape = InstallBodyShape(_body, out _rootBox);
            FitShape();
        }

        /// <summary>
        /// The frictionless material of a character's body capsule: it slides along walls and over block edges
        /// instead of sticking to what it is pushed into.
        /// </summary>
        public static PhysicsMaterial CharacterMaterial
        {
            get
            {
                if (_characterMaterial == null)
                {
                    _characterMaterial = new PhysicsMaterial("CoreAI Character")
                    {
                        dynamicFriction = 0f,
                        staticFriction = 0f,
                        bounciness = 0f,
                        frictionCombine = PhysicsMaterialCombine.Minimum,
                        bounceCombine = PhysicsMaterialCombine.Minimum,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                return _characterMaterial;
            }
        }

        /// <summary>
        /// The material of a character's body capsule while it stands on the ground with nothing to walk to: it
        /// holds on a slope or a block's rim, and a part it stands on carries it.
        /// </summary>
        public static PhysicsMaterial GripMaterial
        {
            get
            {
                if (_gripMaterial == null)
                {
                    _gripMaterial = new PhysicsMaterial("CoreAI Character Grip")
                    {
                        dynamicFriction = 1f,
                        staticFriction = 1f,
                        bounciness = 0f,
                        frictionCombine = PhysicsMaterialCombine.Maximum,
                        bounceCombine = PhysicsMaterialCombine.Minimum,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                return _gripMaterial;
            }
        }

        /// <summary>True while the body capsule grips (see <see cref="GripMaterial"/>).</summary>
        public bool IsGripping => _shape != null && _shape.sharedMaterial == _gripMaterial && _gripMaterial != null;

        /// <summary>The body capsule this motor gave its root; null for a body that brought its own collider.</summary>
        public CapsuleCollider BodyShape => _shape;

        /// <summary>
        /// True when <paramref name="body"/> carries a character's body capsule; <paramref name="feetY"/> is then
        /// the world height of its bottom, in metres.
        /// </summary>
        public static bool TryGetFeet(Rigidbody body, out float feetY)
        {
            feetY = 0f;
            if (body == null || !body.TryGetComponent(out CapsuleCollider shape) || !shape.enabled
                || shape.sharedMaterial == null
                || (shape.sharedMaterial != _characterMaterial && shape.sharedMaterial != _gripMaterial))
            {
                return false;
            }

            feetY = shape.bounds.min.y;
            return true;
        }

        private static CapsuleCollider InstallBodyShape(Rigidbody body, out BoxCollider rootBox)
        {
            GameObject gameObject = body.gameObject;
            if (!gameObject.TryGetComponent(out rootBox) || gameObject.TryGetComponent(out CapsuleCollider _))
            {
                rootBox = null;
                return null;
            }

            CapsuleCollider shape = gameObject.AddComponent<CapsuleCollider>();
            shape.direction = 1;
            shape.sharedMaterial = CharacterMaterial;
            shape.isTrigger = rootBox.isTrigger;
            rootBox.enabled = false;
            return shape;
        }

        /// <summary>
        /// The walk velocity <paramref name="planar"/> with the part that would push another character taken out:
        /// the capsule is swept one step ahead, and for every other character's capsule in the way the push along its
        /// normal is removed, so the walker stops against it or slides along it. A MoveTo walker
        /// (<paramref name="waits"/>) stops and waits instead, until the other moves or its MoveTo times out.
        /// </summary>
        /// <remarks>
        /// WHY no pushing between characters: two bodies driven by velocity push each other by their solver contacts,
        /// harder than any grip, so a villager walking into the player shoved him off a ledge or into a block, and
        /// how far depended on the frame. The rule now is the same every time: characters block each other, and only
        /// their own input moves them.
        /// </remarks>
        private Vector3 BlockedByCharacters(Vector3 planar, double deltaSeconds, bool waits)
        {
            if (_shape == null || planar.sqrMagnitude <= 1e-8f)
            {
                return planar;
            }

            float speed = planar.magnitude;
            float distance = speed * (float)deltaSeconds + RbxSpace.LengthToUnity(0.5f);
            RaycastHit[] found = _body.SweepTestAll(planar / speed, distance, QueryTriggerInteraction.Ignore);
            Vector3 result = planar;
            for (int index = 0; index < found.Length; index++)
            {
                Collider other = found[index].collider;
                if (other == null || other.attachedRigidbody == _body || !IsCharacterShape(other))
                {
                    continue;
                }

                Vector3 normal = found[index].normal;
                normal.y = 0f;
                if (normal.sqrMagnitude <= 1e-6f)
                {
                    continue;
                }

                normal /= Mathf.Sqrt(normal.sqrMagnitude);
                float into = Vector3.Dot(result, normal);
                if (into >= 0f)
                {
                    continue;
                }

                if (waits)
                {
                    return Vector3.zero;
                }

                result -= normal * into;
            }

            return result;
        }

        private static bool IsCharacterShape(Collider collider)
        {
            PhysicsMaterial material = collider.sharedMaterial;
            return material != null && (material == _characterMaterial || material == _gripMaterial);
        }

        /// <summary>
        /// Swaps the body capsule between the frictionless material (walking, jumping, in the air) and the grip
        /// material (standing on the ground with nothing to walk to).
        /// </summary>
        private void Grip(bool grip)
        {
            if (_shape == null)
            {
                return;
            }

            PhysicsMaterial wanted = grip ? GripMaterial : CharacterMaterial;
            if (_shape.sharedMaterial != wanted)
            {
                _shape.sharedMaterial = wanted;
            }
        }

        /// <summary>Sizes the capsule to the root's current size; a no-op while the size has not changed.</summary>
        private void FitShape()
        {
            if (_shape == null || _body == null)
            {
                return;
            }

            if (_rootBox != null && _shape.isTrigger != _rootBox.isTrigger)
            {
                _shape.isTrigger = _rootBox.isTrigger;
            }

            Vector3 scale = _body.transform.lossyScale;
            if (scale == _shapedForScale)
            {
                return;
            }

            _shapedForScale = scale;
            _bodyShape = RbxCharacterBodyShape.ForR6Root(RbxSpace.SizeFromUnity(scale));
            _shape.height = _bodyShape.LocalHeight;
            _shape.radius = _bodyShape.LocalRadius;
            _shape.center = new Vector3(0f, _bodyShape.LocalCentreY, 0f);
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
        /// <remarks>
        /// WHY a sphere for the body capsule: a single ray from the centre missed the floor as soon as the centre
        /// stood past a block's edge, so a character on the rim of a step could not jump. The sphere is four fifths
        /// of the capsule's width, starts at the root's centre and reaches just below the feet.
        /// </remarks>
        public bool IsGrounded
        {
            get
            {
                if (_body == null)
                {
                    return false;
                }

                if (_shape == null)
                {
                    return Physics.Raycast(_body.position, Vector3.down, _groundProbeOrigin + GroundProbeMetres,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                }

                // WHY the centre ray as well: a cast ignores every collider its sphere starts inside, and one Terrain
                // chunk collider holds both the floor and the wall a body is pressed against, so a sphere that
                // starts touching that wall would miss the floor under it.
                float radius = RbxSpace.LengthToUnity(_bodyShape.Radius) * 0.8f;
                float feet = RbxSpace.LengthToUnity(_bodyShape.BottomBelowRoot);
                float distance = Mathf.Max(0f, feet - radius) + GroundProbeMetres;
                return Physics.SphereCast(_body.position, radius, Vector3.down, out RaycastHit _, distance,
                           Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                       || Physics.Raycast(_body.position, Vector3.down, feet + GroundProbeMetres,
                           Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
        }

        /// <inheritdoc />
        public void SetWalkSpeed(double studsPerSecond)
        {
            _walkSpeedStuds = studsPerSecond;
            _walkSpeedMetres = RbxSpace.LengthToUnity((float)studsPerSecond);
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY a direction cancels the walk target: Move is the player's input and MoveTo a scripted
        /// destination; the Humanoid already ends the MoveTo, and the motor must not keep chasing it.
        /// </remarks>
        public void Move(RbxVector3 directionStuds)
        {
            bool wasMoving = _moveDirectionStuds.Magnitude > 1e-4f;
            _moveDirectionStuds = directionStuds;
            bool moving = directionStuds.Magnitude > 1e-4f;
            if (moving)
            {
                _targetMetres = null;
                return;
            }

            if (wasMoving && !_flying && _body != null && !_body.isKinematic)
            {
                Vector3 stopped = _body.linearVelocity;
                stopped.x = 0f;
                stopped.z = 0f;
                _body.linearVelocity = stopped;
            }
        }

        /// <inheritdoc />
        public bool SetFlying(bool flying)
        {
            _flying = flying;
            if (!flying)
            {
                SetNoclip(false);
            }

            return true;
        }

        /// <inheritdoc />
        public void SetNoclip(bool noclip)
        {
            _noclip = noclip && _flying;
            if (_body != null)
            {
                _body.detectCollisions = !_noclip;
            }
        }

        /// <inheritdoc />
        public void SetAutoRotate(bool autoRotate)
        {
            _autoRotate = autoRotate;
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
            // WHY a swimmer with its head out may jump: it pushes off the water to climb onto a bank.
            bool fromWater = _swimming && RbxSwimRules.JumpLeavesWater(Water);
            if (_body == null || _body.isKinematic || (!IsGrounded && !fromWater))
            {
                return false;
            }

            _swimming = false;

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
            _blockedSeconds = 0d;
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
        /// WHY the step length only times the turn: this motor drives velocity rather than integrating a
        /// position, so the physics step itself does the integrating; the AutoRotate turn is the one thing
        /// the motor eases on its own clock.
        /// </remarks>
        public void Step(double deltaSeconds)
        {
            StandUpright();
            if (_flying)
            {
                StepFlight(deltaSeconds);
                return;
            }

            if (_swimming)
            {
                StepSwim(deltaSeconds);
                return;
            }

            StepWalk(deltaSeconds);
        }

        /// <summary>The tilt (degrees) a character body stands back up from.</summary>
        internal const float UprightToleranceDegrees = 0.5f;

        /// <summary>
        /// Stands a tilted body back up, keeping its heading: a Humanoid's character stays upright, as in Roblox.
        /// </summary>
        /// <remarks>
        /// WHY here: rotation is frozen against physics, so nothing undid a tilt given from outside (a Hub move of
        /// a villager, a script's PivotTo with a rotation), and the motor only turns the yaw while walking; the
        /// owner moved villagers and they stayed lying on the grass.
        /// </remarks>
        private void StandUpright()
        {
            if (_body == null || _body.isKinematic)
            {
                return;
            }

            Transform bodyTransform = _body.transform;
            if (Vector3.Angle(bodyTransform.up, Vector3.up) <= UprightToleranceDegrees)
            {
                return;
            }

            double yaw = RbxCharacterFacing.YawOf(RbxSpace.RotationFromUnity(bodyTransform.rotation).LookVector);
            bodyTransform.rotation = RbxSpace.ToUnity(RbxCFrame.Angles(0f, (float)yaw, 0f));
        }

        /// <summary>Advances the walk by one fixed step. Call from the fixed-step pump.</summary>
        public void Step()
        {
            Step(Time.fixedDeltaTime);
        }

        private void StepWalk(double deltaSeconds)
        {
            if (_body == null || _body.isKinematic)
            {
                return;
            }

            FitShape();
            bool idle = _moveDirectionStuds.Magnitude <= 1e-4f && !_targetMetres.HasValue;
            Grip(idle && _shape != null && IsGrounded);
            if (_moveDirectionStuds.Magnitude > 1e-4f)
            {
                RbxVector3 heading = new(_moveDirectionStuds.X, 0f, _moveDirectionStuds.Z);
                Vector3 direction = RbxSpace.DirectionToUnity(heading);
                Vector3 walking = _body.linearVelocity;
                Vector3 planarWalk = BlockedByCharacters(new Vector3(direction.x, 0f, direction.z) * _walkSpeedMetres,
                    deltaSeconds, false);
                walking.x = planarWalk.x;
                walking.z = planarWalk.z;
                _body.linearVelocity = walking;
                FaceAlong(heading, deltaSeconds);
                return;
            }

            if (!_targetMetres.HasValue)
            {
                return;
            }

            Vector3 delta = _targetMetres.Value - _body.position;
            delta.y = 0f;
            Vector3 velocity = _body.linearVelocity;
            Vector3 planar = delta.sqrMagnitude <= 1e-6f
                ? Vector3.zero
                : BlockedByCharacters(delta.normalized * _walkSpeedMetres, deltaSeconds, true);
            _blockedSeconds = delta.sqrMagnitude > 1e-6f && planar.sqrMagnitude <= 1e-8f
                ? _blockedSeconds + deltaSeconds
                : 0d;
            velocity.x = planar.x;
            velocity.z = planar.z;
            _body.linearVelocity = velocity;
            FaceAlong(RbxSpace.DirectionFromUnity(delta), deltaSeconds);
        }

        /// <summary>
        /// Turns the body one step toward facing <paramref name="headingStuds"/> (a Roblox-space direction; only
        /// its horizontal part counts), when AutoRotate is on.
        /// </summary>
        /// <remarks>
        /// WHY the commanded direction and not the measured velocity: the mirror's character faces
        /// <c>MoveDirection</c>, the input, so one walking into a wall still faces the wall instead of turning
        /// along whatever the solver let it slide. WHY the Transform and not a Rigidbody rotation write: rotation
        /// is frozen against physics, the motor steps right before the physics step that syncs transforms, and
        /// the Transform is the member every CoreAI build target exposes for it.
        /// </remarks>
        private void FaceAlong(RbxVector3 headingStuds, double deltaSeconds)
        {
            if (!_autoRotate || _body == null
                || !RbxCharacterFacing.TryYawOfDirection(headingStuds, out double targetYaw))
            {
                return;
            }

            Transform bodyTransform = _body.transform;
            double currentYaw = RbxCharacterFacing.YawOf(RbxSpace.RotationFromUnity(bodyTransform.rotation).LookVector);
            double nextYaw = RbxCharacterFacing.StepYaw(currentYaw, targetYaw, deltaSeconds, TurnResponsePerSecond);
            if (Math.Abs(RbxCharacterFacing.WrapAngle(nextYaw - currentYaw)) < RbxCharacterFacing.SnapRadians * 0.01d)
            {
                return;
            }

            bodyTransform.rotation = RbxSpace.ToUnity(RbxCFrame.Angles(0f, (float)nextYaw, 0f));
        }

        /// <summary>
        /// One swimming step: the velocity eases toward <see cref="RbxSwimRules.TargetVelocity"/> (along the Move
        /// direction, tilted by the camera's pitch while swimming forward, so looking down dives; rising while Jump
        /// is held; sinking slowly with no input), and the world's gravity on the body is cancelled, as in flight.
        /// </summary>
        private void StepSwim(double deltaSeconds)
        {
            if (_body == null || _body.isKinematic)
            {
                return;
            }

            FitShape();
            Grip(false);
            RbxVector3 heading = new(_moveDirectionStuds.X, 0f, _moveDirectionStuds.Z);
            RbxVector3 direction = heading;
            Camera eye = Camera.main;
            if (eye != null && heading.Magnitude > 1e-4f)
            {
                Vector3 look = eye.transform.forward;
                Vector3 flatLook = new(look.x, 0f, look.z);
                Vector3 flatMove = RbxSpace.DirectionToUnity(heading.Unit);
                float along = flatLook.sqrMagnitude > 1e-6f ? Vector3.Dot(flatMove, flatLook.normalized) : 0f;
                if (along > 0f)
                {
                    direction = new RbxVector3(heading.Unit.X, look.y * along, heading.Unit.Z);
                }
            }

            RbxVector3 target = RbxSwimRules.TargetVelocity(direction, _rise, _walkSpeedStuds);
            _rise = false;
            RbxVector3 current = RbxSpace.VelocityFromUnity(_body.linearVelocity);
            RbxVector3 next = RbxCharacterControlMath.StepFlightVelocity(current, target, FlightAccelerationStuds,
                deltaSeconds);
            _body.linearVelocity = RbxSpace.VelocityToUnity(next);
            float gravity = _worldGravityMetresPerSecondSquared();
            _body.AddForce(new Vector3(0f, Mathf.Abs(gravity), 0f), ForceMode.Acceleration);
            FaceAlong(heading, deltaSeconds);
        }

        /// <summary>
        /// One flight step: the velocity eases toward the Move direction times the flight speed, and
        /// the world's gravity on this body is cancelled for the step.
        /// </summary>
        /// <remarks>
        /// WHY gravity is cancelled with an opposite acceleration instead of being skipped: the
        /// physics port adds gravity to every bound body in the same fixed step, in an order this
        /// motor does not control; equal and opposite accelerations sum to zero whichever runs first.
        /// </remarks>
        private void StepFlight(double deltaSeconds)
        {
            if (_body == null || _body.isKinematic)
            {
                return;
            }

            Grip(false);
            float flySpeed = (float)(_walkSpeedStuds * FlySpeedMultiplier);
            RbxVector3 current = RbxSpace.VelocityFromUnity(_body.linearVelocity);
            RbxVector3 next = RbxCharacterControlMath.StepFlightVelocity(
                current, _moveDirectionStuds * flySpeed, FlightAccelerationStuds, deltaSeconds);
            _body.linearVelocity = RbxSpace.VelocityToUnity(next);
            float gravity = _worldGravityMetresPerSecondSquared();
            _body.AddForce(new Vector3(0f, Mathf.Abs(gravity), 0f), ForceMode.Acceleration);
            FaceAlong(_moveDirectionStuds, deltaSeconds);
        }
    }
}
