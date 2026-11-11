using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// The built-in procedural animation of one R6 character: from the pose kind and the running speed it
    /// eases a handful of channels (each arm and leg forward, arm and leg spread, torso lean and bob, head nod)
    /// and turns them into the six joint transforms of <see cref="RbxR6Rig.Joints"/>.
    /// </summary>
    /// <remarks>
    /// WHY procedural and not keyframed: CoreAI has no <c>Animation</c>, <c>Animator</c> or
    /// <c>AnimationTrack</c> yet (all loud stubs, planned for MVP17), and the classic R6 <c>Animate</c> script
    /// was itself a sine swing of the four limb motors. What this writes is exactly what an AnimationTrack will
    /// write later — a <c>Motor6D.Transform</c> per joint — so tracks replace the channels, not the joints.
    /// <para>
    /// Every channel moves toward its target at a capped rate, so a change of pose (walk to jump, a landing)
    /// blends instead of snapping, and the result depends on elapsed time, not on the frame rate. A step
    /// allocates nothing.
    /// </para>
    /// </remarks>
    public sealed class RbxRigPoser
    {
        /// <summary>The speed the walk is tuned for: the mirror's default <c>WalkSpeed</c>.</summary>
        public const double WalkReferenceSpeedStuds = RbxHumanoid.DefaultWalkSpeed;

        /// <summary>Leg swing at <see cref="WalkReferenceSpeedStuds"/>, in radians either side of hanging.</summary>
        public const double WalkSwingRadians = 0.9d;

        /// <summary>The smallest fraction of <see cref="WalkSwingRadians"/> a walk swings, however slow.</summary>
        public const double WalkSwingMinScale = 0.2d;

        /// <summary>The largest fraction of <see cref="WalkSwingRadians"/> a fast walk swings.</summary>
        public const double WalkSwingMaxScale = 1.25d;

        /// <summary>Arm swing as a fraction of the leg swing.</summary>
        public const double ArmSwingRatio = 0.85d;

        /// <summary>From the hip pivot to the sole, in studs (the R6 leg length).</summary>
        public const double LegReachStuds = 2d;

        /// <summary>
        /// The share of the ground speed the feet keep up with. 1 would plant the feet exactly; less lets them
        /// slide a little, which keeps the step rate of a fast walk from turning frantic.
        /// </summary>
        public const double StrideCoverage = 0.75d;

        /// <summary>Slowest gait, in radians of cycle per second (a creep still shuffles visibly).</summary>
        public const double MinGaitRate = 4d;

        /// <summary>Fastest gait, in radians of cycle per second (about 3.5 steps a second each foot).</summary>
        public const double MaxGaitRate = 22d;

        /// <summary>Where the arms point while jumping, in radians forward from hanging (straight up).</summary>
        public const double JumpArmRadians = Math.PI;

        /// <summary>Flight speed, in studs per second, at which the flying lean is full.</summary>
        public const double FlyReferenceSpeedStuds = 24d;

        /// <summary>How long the landing dip lasts, in seconds.</summary>
        public const double LandingSeconds = 0.22d;

        /// <summary>Fastest an arm or a leg swings to a new target, in radians per second.</summary>
        public const double LimbRate = 14d;

        private const double SpreadRate = 5d;
        private const double LeanRate = 3d;
        private const double BobRate = 4d;
        private const double NodRate = 3d;
        private const double TwoPi = 2d * Math.PI;

        private readonly RbxCFrame[] _transforms = new RbxCFrame[RbxR6Rig.JointCount];
        private double _armRight;
        private double _armLeft;
        private double _armSpread;
        private double _legRight;
        private double _legLeft;
        private double _legSpread;
        private double _lean;
        private double _bob;
        private double _nod;
        private double _gaitPhase;
        private double _time;
        private double _landing;
        private RbxRigPoseKind _kind = RbxRigPoseKind.Idle;

        /// <summary>A poser standing at rest.</summary>
        public RbxRigPoser()
        {
            BuildTransforms();
        }

        /// <summary>The pose played by the last step.</summary>
        public RbxRigPoseKind Kind => _kind;

        /// <summary>True while the walk cycle plays.</summary>
        public bool IsWalking => _kind == RbxRigPoseKind.Walk;

        /// <summary>Right arm forward of hanging, in radians (up is pi).</summary>
        public double ArmForwardRight => _armRight;

        /// <summary>Left arm forward of hanging, in radians.</summary>
        public double ArmForwardLeft => _armLeft;

        /// <summary>Both arms away from the body, in radians.</summary>
        public double ArmSpread => _armSpread;

        /// <summary>Right leg forward of hanging, in radians.</summary>
        public double LegForwardRight => _legRight;

        /// <summary>Left leg forward of hanging, in radians.</summary>
        public double LegForwardLeft => _legLeft;

        /// <summary>Torso tilt forward, in radians.</summary>
        public double Lean => _lean;

        /// <summary>Torso rise over its rest height, in studs (negative while it dips).</summary>
        public double Bob => _bob;

        /// <summary>Where the gait is in its cycle, in radians [0, 2 pi).</summary>
        public double GaitPhase => _gaitPhase;

        /// <summary>Seconds of landing dip left.</summary>
        public double LandingRemaining => _landing;

        /// <summary>The current transform of <paramref name="joint"/> (an index into <see cref="RbxR6Rig.Joints"/>).</summary>
        public RbxCFrame GetTransform(int joint)
        {
            return _transforms[joint];
        }

        /// <summary>
        /// Advances the animation by <paramref name="deltaSeconds"/> playing <paramref name="kind"/> at
        /// <paramref name="speedStuds"/>. A zero step changes nothing; <see cref="RbxRigPoseKind.Dead"/> holds
        /// the last pose.
        /// </summary>
        public void Step(double deltaSeconds, RbxRigPoseKind kind, double speedStuds)
        {
            double dt = double.IsNaN(deltaSeconds) || deltaSeconds < 0d ? 0d : deltaSeconds;
            double speed = double.IsNaN(speedStuds) || double.IsInfinity(speedStuds) || speedStuds < 0d
                ? 0d
                : speedStuds;
            RbxRigPoseKind previous = _kind;
            _kind = kind;
            if (kind == RbxRigPoseKind.Dead)
            {
                return;
            }

            bool wasAirborne = previous == RbxRigPoseKind.Jump || previous == RbxRigPoseKind.Fall;
            bool grounded = kind == RbxRigPoseKind.Idle || kind == RbxRigPoseKind.Walk || kind == RbxRigPoseKind.Land;
            if ((wasAirborne && grounded) || (kind == RbxRigPoseKind.Land && previous != RbxRigPoseKind.Land))
            {
                _landing = LandingSeconds;
            }

            if (kind == RbxRigPoseKind.Walk && previous != RbxRigPoseKind.Walk)
            {
                _gaitPhase = 0d;
            }

            _time += dt;
            _landing = Math.Max(0d, _landing - dt);

            double armRight = 0d;
            double armLeft = 0d;
            double armSpread = 0d;
            double legRight = 0d;
            double legLeft = 0d;
            double legSpread = 0d;
            double lean = 0d;
            double bob = 0d;
            double nod = 0d;
            switch (kind)
            {
                case RbxRigPoseKind.Walk:
                {
                    double scale = Clamp(speed / WalkReferenceSpeedStuds, WalkSwingMinScale, WalkSwingMaxScale);
                    double swing = WalkSwingRadians * scale;
                    _gaitPhase = (_gaitPhase + GaitRate(speed, swing) * dt) % TwoPi;
                    double wave = Math.Sin(_gaitPhase);
                    legRight = -swing * wave;
                    legLeft = swing * wave;
                    armRight = swing * ArmSwingRatio * wave;
                    armLeft = -swing * ArmSwingRatio * wave;
                    armSpread = 0.05d;
                    bob = -0.15d * scale * wave * wave;
                    lean = 0.06d * scale;
                    nod = -0.6d * lean;
                    break;
                }
                case RbxRigPoseKind.Jump:
                    armRight = JumpArmRadians;
                    armLeft = JumpArmRadians;
                    armSpread = 0.1d;
                    legRight = 0.12d;
                    legLeft = 0.12d;
                    lean = -0.05d;
                    break;
                case RbxRigPoseKind.Fall:
                    armRight = 2.7d;
                    armLeft = 2.7d;
                    armSpread = 0.45d;
                    legRight = 0.15d;
                    legLeft = -0.15d;
                    legSpread = 0.08d;
                    break;
                case RbxRigPoseKind.Fly:
                {
                    double travel = Clamp(speed / FlyReferenceSpeedStuds, 0d, 1d);
                    double hover = 1d - travel;
                    double wave = Math.Sin(_time * TwoPi / 1.6d);
                    lean = 0.55d * travel;
                    armRight = -0.45d * travel + 0.15d * hover;
                    armLeft = armRight;
                    armSpread = 0.18d + 0.3d * hover + 0.05d * wave * hover;
                    legRight = -0.25d * travel + 0.08d * wave * hover;
                    legLeft = -0.25d * travel - 0.08d * wave * hover;
                    legSpread = 0.06d;
                    bob = 0.12d * wave * hover;
                    nod = -0.4d * lean;
                    break;
                }
                case RbxRigPoseKind.Sit:
                    legRight = Math.PI / 2d;
                    legLeft = Math.PI / 2d;
                    armRight = 0.35d;
                    armLeft = 0.35d;
                    armSpread = 0.05d;
                    break;
                default:
                {
                    double breath = Math.Sin(_time * TwoPi / 3.2d);
                    double sway = Math.Sin(_time * TwoPi / 4.6d);
                    armRight = 0.05d * sway;
                    armLeft = -0.05d * sway;
                    armSpread = 0.06d + 0.03d * breath;
                    bob = 0.035d * breath;
                    nod = 0.025d * breath;
                    break;
                }
            }

            if (_landing > 0d)
            {
                double weight = _landing / LandingSeconds;
                bob -= 0.35d * weight;
                armRight += 0.45d * weight;
                armLeft += 0.45d * weight;
                armSpread += 0.2d * weight;
                lean += 0.15d * weight;
            }

            _armRight = Approach(_armRight, armRight, LimbRate * dt);
            _armLeft = Approach(_armLeft, armLeft, LimbRate * dt);
            _armSpread = Approach(_armSpread, armSpread, SpreadRate * dt);
            _legRight = Approach(_legRight, legRight, LimbRate * dt);
            _legLeft = Approach(_legLeft, legLeft, LimbRate * dt);
            _legSpread = Approach(_legSpread, legSpread, SpreadRate * dt);
            _lean = Approach(_lean, lean, LeanRate * dt);
            _bob = Approach(_bob, bob, BobRate * dt);
            _nod = Approach(_nod, nod, NodRate * dt);
            BuildTransforms();
        }

        /// <summary>
        /// The gait rate, in radians of cycle per second, at which feet swinging <paramref name="swing"/> keep
        /// up with <see cref="StrideCoverage"/> of <paramref name="speedStuds"/>: one cycle is two steps, each
        /// <c>2 * LegReach * sin(swing)</c> long.
        /// </summary>
        public static double GaitRate(double speedStuds, double swing)
        {
            double cycleStuds = 4d * LegReachStuds * Math.Sin(swing);
            if (cycleStuds <= 1e-6d)
            {
                return MinGaitRate;
            }

            return Clamp(StrideCoverage * TwoPi * speedStuds / cycleStuds, MinGaitRate, MaxGaitRate);
        }

        private void BuildTransforms()
        {
            _transforms[0] = RootTransform(_bob, _lean);
            _transforms[1] = RotationX(_nod);
            _transforms[2] = LimbTransform(_armRight, _armSpread);
            _transforms[3] = LimbTransform(-_armLeft, _armSpread);
            _transforms[4] = LimbTransform(_legRight, _legSpread);
            _transforms[5] = LimbTransform(-_legLeft, _legSpread);
        }

        /// <summary>
        /// A limb joint's transform: a swing of <paramref name="swing"/> about the joint's Z axis after a spread
        /// of <paramref name="spread"/> away from the body. Each side's joint frame mirrors the other, so a
        /// left limb passes its forward angle negated and the same spread.
        /// </summary>
        private static RbxCFrame LimbTransform(double swing, double spread)
        {
            return RotationZ(swing) * RotationX(-spread);
        }

        /// <summary>The RootJoint's transform: a rise along the joint's Z axis (up) and a lean about its X axis.</summary>
        private static RbxCFrame RootTransform(double bob, double lean)
        {
            float cos = (float)Math.Cos(lean);
            float sin = (float)Math.Sin(lean);
            return new RbxCFrame(0f, 0f, (float)bob, 1f, 0f, 0f, 0f, cos, -sin, 0f, sin, cos);
        }

        private static RbxCFrame RotationX(double angle)
        {
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return new RbxCFrame(0f, 0f, 0f, 1f, 0f, 0f, 0f, cos, -sin, 0f, sin, cos);
        }

        private static RbxCFrame RotationZ(double angle)
        {
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return new RbxCFrame(0f, 0f, 0f, cos, -sin, 0f, sin, cos, 0f, 0f, 0f, 1f);
        }

        private static double Approach(double current, double target, double maxStep)
        {
            double difference = target - current;
            if (Math.Abs(difference) <= maxStep)
            {
                return target;
            }

            return current + (difference > 0d ? maxStep : -maxStep);
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
