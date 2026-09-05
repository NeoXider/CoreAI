using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The <c>Enum.HumanoidStateType</c> items CoreAI's state machine actually produces.
    /// </summary>
    /// <remarks>
    /// WHY a subset and not all seventeen: the mirror's enum covers ragdoll, swimming, climbing,
    /// seats and physics states that need a character rig CoreAI does not have. Registering items a
    /// state machine can never enter would let a script write a state check that silently never runs;
    /// the enum itself ships the mirror's full item set, and the states outside this subset raise the
    /// loud stub when a script tries to force them.
    /// </remarks>
    public enum RbxHumanoidState
    {
        /// <summary>Mirror value 3: rising from a jump.</summary>
        Jumping = 3,

        /// <summary>Mirror value 5: airborne and falling.</summary>
        Freefall = 5,

        /// <summary>Mirror value 7: the frame contact with the ground is regained.</summary>
        Landed = 7,

        /// <summary>Mirror value 8: on the ground, moving or standing.</summary>
        Running = 8,

        /// <summary>Mirror value 15: Health reached zero.</summary>
        Dead = 15
    }

    /// <summary>
    /// The engine seam a <c>Humanoid</c> drives: one character's movement, in Roblox units.
    /// </summary>
    /// <remarks>
    /// WHY the Humanoid does not move anything itself: <c>CoreAI.RbxApi.Instances</c> is engine-free,
    /// and the metric contract (studs per second at 0.28 m/stud, an upward impulse, a grounded flag)
    /// is the only thing a controller must honour. A host that prefers its own character controller
    /// implements this and keeps every Lua-visible rule below unchanged.
    /// </remarks>
    public interface IRbxCharacterMotor
    {
        /// <summary>Walk speed in studs per second; the motor converts to its own units.</summary>
        void SetWalkSpeed(double studsPerSecond);

        /// <summary>Requests one jump using the currently configured power or height.</summary>
        void Jump(double jumpPower, double jumpHeight, bool useJumpPower);

        /// <summary>Walks toward a world point, in studs. A null target stops the walk.</summary>
        void MoveTo(RbxVector3? targetStuds);

        /// <summary>Where the character is now, in studs.</summary>
        RbxVector3 Position { get; }

        /// <summary>Unit direction the character is moving in; zero when standing still.</summary>
        RbxVector3 MoveDirection { get; }

        /// <summary>True while the character stands on something.</summary>
        bool IsGrounded { get; }
    }

    /// <summary>A motor for a world with no character: it stands still and never lands.</summary>
    /// <remarks>
    /// WHY it exists: a Humanoid can be created and scripted in a headless world (tests, the world
    /// package tools, a dedicated server before its scene binds). Health, damage and the Died signal
    /// are all meaningful there; only movement is not.
    /// </remarks>
    public sealed class NullRbxCharacterMotor : IRbxCharacterMotor
    {
        /// <summary>Shared instance; the type holds no state.</summary>
        public static readonly NullRbxCharacterMotor Instance = new();

        /// <inheritdoc />
        public RbxVector3 Position => RbxVector3.Zero;

        /// <inheritdoc />
        public RbxVector3 MoveDirection => RbxVector3.Zero;

        /// <inheritdoc />
        public bool IsGrounded => true;

        /// <inheritdoc />
        public void SetWalkSpeed(double studsPerSecond)
        {
        }

        /// <inheritdoc />
        public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
        }

        /// <inheritdoc />
        public void MoveTo(RbxVector3? targetStuds)
        {
        }
    }

    /// <summary>
    /// Mirror <c>Humanoid</c>: health, movement parameters, and the state machine over a character.
    /// </summary>
    /// <remarks>
    /// Mirror-pinned defaults: <c>MaxHealth</c> 100, <c>WalkSpeed</c> 16 studs/s,
    /// <c>JumpPower</c> 50, <c>JumpHeight</c> 7.2 studs, <c>UseJumpPower</c> true.
    /// <para>
    /// Passive health regeneration is deliberately NOT here. The mirror says a regeneration SCRIPT is
    /// inserted into humanoids, and that adding an empty <c>Script</c> named <c>Health</c> disables
    /// it — so regeneration belongs to the character template, not to this class. Baking it in would
    /// look identical in a kill-brick fixture, diverge in every damage-over-time one, and make the
    /// documented opt-out impossible to honour.
    /// </para>
    /// </remarks>
    public sealed class RbxHumanoid : RbxInstance
    {
        /// <summary>Mirror default: 100 health.</summary>
        public const double DefaultMaxHealth = 100d;

        /// <summary>Mirror default: 16 studs per second.</summary>
        public const double DefaultWalkSpeed = 16d;

        /// <summary>Mirror default: 50.</summary>
        public const double DefaultJumpPower = 50d;

        /// <summary>Mirror default: 7.2 studs.</summary>
        public const double DefaultJumpHeight = 7.2d;

        /// <summary>Mirror: MoveTo gives up after eight seconds and reports reached = false.</summary>
        public const double MoveToTimeoutSeconds = 8d;

        /// <summary>How close, in studs, counts as having arrived.</summary>
        /// <remarks>
        /// OURS — the mirror does not publish the arrival radius. Two studs is roughly a character's
        /// own width, which is what "reached the point" means for something that has a body.
        /// </remarks>
        public const double ArrivalRadiusStuds = 2d;

        private IRbxCharacterMotor _motor = NullRbxCharacterMotor.Instance;
        private ModScheduler _scheduler;
        private double _maxHealth = DefaultMaxHealth;
        private double _health = DefaultMaxHealth;
        private double _walkSpeed = DefaultWalkSpeed;
        private double _jumpPower = DefaultJumpPower;
        private double _jumpHeight = DefaultJumpHeight;
        private bool _useJumpPower = true;
        private bool _died;
        private RbxHumanoidState _state = RbxHumanoidState.Running;
        private RbxVector3? _walkTarget;
        private double _walkElapsed;

        /// <summary>Constructed by the class catalog for <c>Humanoid</c>.</summary>
        protected internal RbxHumanoid(ClassDescriptor descriptor) : base(descriptor)
        {
        }

        /// <summary>Mirror <c>Humanoid.Died</c>, fired once when Health reaches zero.</summary>
        public RbxScriptSignal Died => GetOrCreateSignal("Died");

        /// <summary>Mirror <c>Humanoid.HealthChanged(health)</c>.</summary>
        public RbxScriptSignal HealthChanged => GetOrCreateSignal("HealthChanged");

        /// <summary>Mirror <c>Humanoid.MoveToFinished(reached)</c>.</summary>
        public RbxScriptSignal MoveToFinished => GetOrCreateSignal("MoveToFinished");

        /// <summary>Mirror <c>Humanoid.Running(speed)</c>.</summary>
        public RbxScriptSignal Running => GetOrCreateSignal("Running");

        /// <summary>Mirror <c>Humanoid.Jumping(active)</c>.</summary>
        public RbxScriptSignal Jumping => GetOrCreateSignal("Jumping");

        /// <summary>Mirror <c>Humanoid.FreeFalling(active)</c>.</summary>
        public RbxScriptSignal FreeFalling => GetOrCreateSignal("FreeFalling");

        /// <summary>Mirror <c>Humanoid.StateChanged(old, new)</c>.</summary>
        public RbxScriptSignal StateChanged => GetOrCreateSignal("StateChanged");

        /// <summary>Mirror <c>Humanoid.DisplayName</c>: the name shown above the character.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>The motor currently moving this character.</summary>
        public IRbxCharacterMotor Motor => _motor;

        /// <summary>Mirror <c>Humanoid.Health</c>, clamped to [0, MaxHealth].</summary>
        public double Health
        {
            get => _health;
            set => SetHealth(value);
        }

        /// <summary>Mirror <c>Humanoid.MaxHealth</c>. Lowering it clamps Health with it.</summary>
        public double MaxHealth
        {
            get => _maxHealth;
            set
            {
                RequireFinite(value, "Humanoid.MaxHealth");
                _maxHealth = value < 0d ? 0d : value;
                if (_health > _maxHealth)
                {
                    SetHealth(_maxHealth);
                }
            }
        }

        /// <summary>Mirror <c>Humanoid.WalkSpeed</c>, in studs per second.</summary>
        public double WalkSpeed
        {
            get => _walkSpeed;
            set
            {
                RequireFinite(value, "Humanoid.WalkSpeed");
                _walkSpeed = value < 0d ? 0d : value;
                _motor.SetWalkSpeed(_walkSpeed);
            }
        }

        /// <summary>Mirror <c>Humanoid.JumpPower</c>: the upward force used when UseJumpPower.</summary>
        public double JumpPower
        {
            get => _jumpPower;
            set
            {
                RequireFinite(value, "Humanoid.JumpPower");
                _jumpPower = value < 0d ? 0d : value;
            }
        }

        /// <summary>Mirror <c>Humanoid.JumpHeight</c>, in studs, used when UseJumpPower is false.</summary>
        public double JumpHeight
        {
            get => _jumpHeight;
            set
            {
                RequireFinite(value, "Humanoid.JumpHeight");
                _jumpHeight = value < 0d ? 0d : value;
            }
        }

        /// <summary>Mirror <c>Humanoid.UseJumpPower</c>: JumpPower (true) or JumpHeight (false).</summary>
        public bool UseJumpPower
        {
            get => _useJumpPower;
            set => _useJumpPower = value;
        }

        /// <summary>Mirror <c>Humanoid.MoveDirection</c>: read-only, from the motor.</summary>
        public RbxVector3 MoveDirection => _motor.MoveDirection;

        /// <summary>Mirror <c>Humanoid.RootPart</c>: the character's driving part, or null.</summary>
        public RbxInstance RootPart { get; private set; }

        /// <summary>True once Health has reached zero; a dead Humanoid stays dead.</summary>
        public bool IsDead => _died;

        /// <summary>Attaches the motor and the scheduler that drives MoveTo and state changes.</summary>
        public void AttachHost(ModScheduler scheduler, IRbxCharacterMotor motor, RbxInstance rootPart)
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnPhaseReached;
            }

            _scheduler = scheduler;
            _motor = motor ?? NullRbxCharacterMotor.Instance;
            RootPart = rootPart;
            _motor.SetWalkSpeed(_walkSpeed);
            if (_scheduler != null)
            {
                _scheduler.PhaseReached += OnPhaseReached;
                // WHY bound here and not on first read: a Humanoid's signals are the ones a HOST
                // wants (a game's own UI listens for Died and HealthChanged, not only Lua), and a
                // signal that gets its scheduler from whoever reads it first refuses every C#
                // listener in a world where no mod happened to touch it.
                BindSignals(_scheduler);
            }
        }

        /// <summary>Detaches the motor; the Humanoid keeps its health and stops moving.</summary>
        public void DetachHost()
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnPhaseReached;
                _scheduler = null;
            }

            _motor = NullRbxCharacterMotor.Instance;
            _walkTarget = null;
        }

        /// <summary>Mirror <c>Humanoid:TakeDamage(amount)</c>. A negative amount heals.</summary>
        public void TakeDamage(double amount)
        {
            RequireFinite(amount, "Humanoid:TakeDamage amount");
            SetHealth(_health - amount);
        }

        /// <summary>Mirror <c>Humanoid:GetState()</c>.</summary>
        public RbxHumanoidState GetState()
        {
            return _state;
        }

        /// <summary>
        /// Mirror <c>Humanoid.Jump = true</c>: requests one jump. Refused while dead.
        /// </summary>
        public void RequestJump()
        {
            if (_died)
            {
                return;
            }

            _motor.Jump(_jumpPower, _jumpHeight, _useJumpPower);
            EnterState(RbxHumanoidState.Jumping);
            Jumping.Fire(true);
        }

        /// <summary>
        /// Mirror <c>Humanoid:MoveTo(location)</c>: walks toward a point and reports the outcome
        /// through <see cref="MoveToFinished"/> — reached within eight seconds, or false at eight.
        /// </summary>
        public void MoveTo(RbxVector3 location)
        {
            if (_died)
            {
                return;
            }

            _walkTarget = location;
            _walkElapsed = 0d;
            _motor.MoveTo(location);
        }

        /// <summary>
        /// Advances the walk timer and the grounded-state machine by one scaled step.
        /// </summary>
        /// <remarks>
        /// WHY it is driven and not polled: MoveToFinished has to fire at eight seconds of SCALED
        /// time, the same clock task.wait uses, so a paused world pauses the timeout too. Reading a
        /// wall clock here would make a paused game give up on its own walk.
        /// </remarks>
        public void Advance(double deltaSeconds)
        {
            if (_died || deltaSeconds < 0d)
            {
                return;
            }

            UpdateGroundedState();
            if (!_walkTarget.HasValue)
            {
                return;
            }

            _walkElapsed += deltaSeconds;
            RbxVector3 delta = _walkTarget.Value - _motor.Position;
            if (delta.Magnitude <= ArrivalRadiusStuds)
            {
                FinishWalk(reached: true);
                return;
            }

            if (_walkElapsed >= MoveToTimeoutSeconds)
            {
                FinishWalk(reached: false);
            }
        }

        private void BindSignals(ModScheduler scheduler)
        {
            Died.BindScheduler(scheduler);
            HealthChanged.BindScheduler(scheduler);
            MoveToFinished.BindScheduler(scheduler);
            Running.BindScheduler(scheduler);
            Jumping.BindScheduler(scheduler);
            FreeFalling.BindScheduler(scheduler);
            StateChanged.BindScheduler(scheduler);
        }

        private void OnPhaseReached(SchedulerPhase phase, double delta)
        {
            // WHY Heartbeat: that phase's delta IS the scaled frame time, so a paused world pauses
            // the MoveTo timeout too — a walk cannot time out while the game is not running.
            if (phase == SchedulerPhase.Heartbeat)
            {
                Advance(delta);
            }
        }

        private void FinishWalk(bool reached)
        {
            _walkTarget = null;
            _walkElapsed = 0d;
            _motor.MoveTo(null);
            MoveToFinished.Fire(reached);
        }

        private void UpdateGroundedState()
        {
            if (_motor.IsGrounded)
            {
                if (_state == RbxHumanoidState.Freefall || _state == RbxHumanoidState.Jumping)
                {
                    EnterState(RbxHumanoidState.Landed);
                    return;
                }

                if (_state != RbxHumanoidState.Running)
                {
                    EnterState(RbxHumanoidState.Running);
                    Running.Fire(_motor.MoveDirection.Magnitude * _walkSpeed);
                }

                return;
            }

            if (_state != RbxHumanoidState.Freefall)
            {
                EnterState(RbxHumanoidState.Freefall);
                FreeFalling.Fire(true);
            }
        }

        private void EnterState(RbxHumanoidState next)
        {
            if (_state == next)
            {
                return;
            }

            RbxHumanoidState previous = _state;
            _state = next;
            StateChanged.Fire(previous, next);
        }

        private void SetHealth(double value)
        {
            RequireFinite(value, "Humanoid.Health");
            if (_died)
            {
                // The mirror: "if the humanoid is dead, this property is continually set to 0".
                // Healing a corpse back to life is a resurrection the mirror does not describe.
                return;
            }

            double clamped = value < 0d ? 0d : value > _maxHealth ? _maxHealth : value;
            if (Math.Abs(clamped - _health) < double.Epsilon)
            {
                return;
            }

            _health = clamped;
            HealthChanged.Fire(_health);
            if (_health > 0d)
            {
                return;
            }

            _died = true;
            _walkTarget = null;
            EnterState(RbxHumanoidState.Dead);
            Died.Fire();
        }

        private static void RequireFinite(double value, string what)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw RbxError.BadArgument(
                    what + " must be a finite number",
                    "check the arithmetic that produced the value for a division by zero");
            }
        }
    }
}
