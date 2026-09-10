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

        /// <summary>Requests one jump and reports whether the controller took it.</summary>
        /// <remarks>
        /// WHY a refusal is an answer and not an error: a controller may legitimately decline — no
        /// head clearance, mid-animation, on a ladder — and the Humanoid then has to leave its state
        /// machine where it was rather than announce a Jumping state the character never enters
        /// (every airborne sample after that read as Freefall, even while ascending). WHY a default
        /// body that forwards to <see cref="Jump"/> and reports acceptance, the same pattern as
        /// <see cref="Step"/>: added after the seam shipped, so an external motor implementing only
        /// the original members keeps compiling and keeps today's rule that every request is a jump.
        /// </remarks>
        bool TryJump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
            Jump(jumpPower, jumpHeight, useJumpPower);
            return true;
        }

        /// <summary>Walks toward a world point, in studs. A null target stops the walk.</summary>
        void MoveTo(RbxVector3? targetStuds);

        /// <summary>Where the character is now, in studs.</summary>
        RbxVector3 Position { get; }

        /// <summary>Unit direction the character is moving in; zero when standing still.</summary>
        RbxVector3 MoveDirection { get; }

        /// <summary>
        /// Horizontal speed the character is actually covering, in studs per second, or null when
        /// this motor cannot measure it.
        /// </summary>
        /// <remarks>
        /// WHY measured and not configured: <c>Humanoid.Running(speed)</c> is the mirror's report of
        /// the speed the character is running at, and a controller with acceleration, a wall in the
        /// way, or a WalkSpeed it has not reached yet is not moving at that number.
        /// <see cref="MoveDirection"/> is a unit vector, so nothing about the rate can be recovered
        /// from it. WHY null rather than a value computed here: the only fallback available —
        /// direction magnitude times the configured WalkSpeed — needs the WalkSpeed the Humanoid
        /// holds, so the Humanoid derives it; an external motor implementing only the original
        /// members keeps compiling and keeps reporting that derived number.
        /// </remarks>
        double? MeasuredSpeed => null;

        /// <summary>True while the character stands on something.</summary>
        bool IsGrounded { get; }

        /// <summary>Whether this motor can still drive its character.</summary>
        /// <remarks>
        /// WHY the pipeline has to ask: a script writing <c>Anchored</c> on the root part destroys
        /// its body and clearing it builds a new one, so a motor holding the old body is dead and
        /// has to be rebuilt. That check used to recognise only CoreAI's own motor by concrete type,
        /// which left a host motor holding a destroyed body forever — the character simply stopped
        /// moving, or threw on the next step. Default is true: a motor that cannot go stale, such as
        /// one that resolves its body on every call, has nothing to answer.
        /// </remarks>
        bool IsAvailable => true;

        /// <summary>
        /// Advances an in-progress <see cref="MoveTo"/> by one fixed step, in seconds.
        /// </summary>
        /// <remarks>
        /// WHY this is on the interface with a no-op default rather than left to the host: the
        /// fixed-step pump used to advance only CoreAI's own motor, by concrete type, so a host
        /// motor's MoveTo never progressed and the character stood still while the Humanoid waited
        /// out its arrival timeout. A controller that drives itself from its own FixedUpdate can
        /// leave this empty; one that wants CoreAI's step cadence implements it.
        /// </remarks>
        void Step(double deltaSeconds)
        {
        }

        /// <summary>
        /// Retires this motor. The pipeline that built it (through
        /// <c>IRbxCharacterMotorProvider.TryCreate</c> or CoreAI's own factory) calls this exactly
        /// once, on whichever path stops using the motor for good, and only after the Humanoid it
        /// drove no longer points at it.
        /// </summary>
        /// <remarks>
        /// WHY on the interface with a no-op default, the same pattern as <see cref="Step"/> and
        /// <see cref="IsAvailable"/>, rather than requiring every implementer to be
        /// <see cref="System.IDisposable"/>: nothing in the pipeline ever called Dispose on a motor
        /// before this existed, so implementing IDisposable alone would have changed nothing —
        /// the release point has to be a method something actually calls. CoreAI's own motor holds
        /// only a Rigidbody Unity destroys anyway, so the default empty body is already correct for
        /// it. A host motor overrides this to stop reading input on its own, drop whatever it
        /// registered (a controller-registry slot, a subscription, an animation rig), and end its
        /// own in-flight walk before its last reference goes away — stopping the character is part
        /// of releasing it.
        /// </remarks>
        void Release()
        {
        }
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
        /// <remarks>
        /// WHY this body is empty yet the inherited <see cref="IRbxCharacterMotor.TryJump"/> still
        /// reports the jump as taken: a headless Humanoid runs its state machine with nothing to
        /// refuse on behalf of, and the frozen Tier-A fixture TBC-010-gravity-low-jump creates one
        /// with no body and asserts that <c>Jumping(true)</c> fires. Refusing here would break that
        /// corpus.
        /// </remarks>
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

        /// <summary>
        /// Smallest change in running speed, in studs per second, that <see cref="Running"/> reports
        /// while the character is moving.
        /// </summary>
        /// <remarks>
        /// OURS — the mirror does not publish a resolution. WHY 0.1 stud/s (2.8 cm/s): a velocity-
        /// driven body reads solver jitter of a few millimetres per second, about 0.01–0.03 stud/s,
        /// and reporting each of those would fire Running every frame at a steady walk; at the
        /// default 16 stud/s, 0.1 is under one percent of full speed, below anything an animation
        /// blend or footstep cadence can show.
        /// </remarks>
        public const double RunningSpeedResolutionStuds = 0.1d;

        /// <summary>
        /// Speed, in studs per second, under which a moving character is reported as stopped —
        /// the mirror's "fires with a speed of 0".
        /// </summary>
        /// <remarks>
        /// OURS. WHY 0.1 stud/s: it is the resting jitter floor. A body standing on a physics
        /// contact still reads 0.01–0.03 stud/s, and the mirror promises an exact 0 for a character
        /// that has stopped, not almost 0.
        /// </remarks>
        public const double RunningStopSpeedStuds = 0.1d;

        /// <summary>
        /// Speed, in studs per second, a stopped character has to reach before it is reported as
        /// moving again.
        /// </summary>
        /// <remarks>
        /// OURS. WHY a second line one resolution step above <see cref="RunningStopSpeedStuds"/>
        /// rather than the same number: with a single cutoff a body hovering at it (0.099, 0.101,
        /// 0.099 — a real change of 0.002) flipped between 0 and 0.101 on every step, each flip a
        /// queued signal, so the zero boundary was fifty times more sensitive than any other speed.
        /// A band exactly one resolution wide means no change under the resolution — by definition
        /// not one the signal reports — can cross both lines, while a character setting off toward
        /// the default 16 stud/s is past 0.2 within a frame or two. WHY not wider: every stud/s of
        /// band is a creep speed a script never hears about.
        /// </remarks>
        public const double RunningStartSpeedStuds =
            RunningStopSpeedStuds + RunningSpeedResolutionStuds;

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
        private double _reportedRunningSpeed;

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
            if (_walkTarget.HasValue)
            {
                _motor.MoveTo(_walkTarget);
            }
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

            // WHY the state machine stays put on a refusal: the controller declining is a legitimate
            // answer (see IRbxCharacterMotor.TryJump), and Jumping is the state of a character that
            // is actually rising, not of one that asked to.
            if (!_motor.TryJump(_jumpPower, _jumpHeight, _useJumpPower))
            {
                return;
            }

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
            if (!_motor.IsGrounded)
            {
                if (_state != RbxHumanoidState.Freefall)
                {
                    EnterState(RbxHumanoidState.Freefall);
                    FreeFalling.Fire(true);
                }

                return;
            }

            if (_state == RbxHumanoidState.Freefall || _state == RbxHumanoidState.Jumping)
            {
                EnterState(RbxHumanoidState.Landed);
                return;
            }

            if (_state == RbxHumanoidState.Running)
            {
                ReportRunningSpeed(force: false);
                return;
            }

            EnterState(RbxHumanoidState.Running);
        }

        /// <summary>
        /// Fires <see cref="Running"/> with the speed the motor measures now. Called only while the
        /// state is Running; every exit from that state reports through
        /// <see cref="ReportRunningStopped"/> instead.
        /// </summary>
        /// <param name="force">
        /// Report even when the value equals the last one reported. Entering the Running state
        /// passes true so a landing is always announced (see <see cref="EnterState"/>).
        /// </param>
        /// <remarks>
        /// WHY on every step rather than once per state entry: the mirror says the signal fires
        /// "when the speed at which a Humanoid is running changes" and "with a speed of 0" when it
        /// stops, so a footstep or animation script reads it as a rate, not as a state entry.
        /// WHY the stopped/moving flip is always reported and a change while moving only past the
        /// resolution: the two lines around zero (see <see cref="RunningStartSpeedStuds"/>) keep a
        /// hovering body from flipping, and a flip that does happen is the mirror's documented 0 or
        /// the end of it — never something a resolution check may swallow.
        /// </remarks>
        private void ReportRunningSpeed(bool force)
        {
            bool wasMoving = _reportedRunningSpeed > 0d;
            double speed = _motor.MeasuredSpeed ?? _motor.MoveDirection.Magnitude * _walkSpeed;
            if (speed < (wasMoving ? RunningStopSpeedStuds : RunningStartSpeedStuds))
            {
                speed = 0d;
            }

            bool isMoving = speed > 0d;
            if (!force
                && isMoving == wasMoving
                && Math.Abs(speed - _reportedRunningSpeed) < RunningSpeedResolutionStuds)
            {
                return;
            }

            _reportedRunningSpeed = speed;
            Running.Fire(speed);
        }

        /// <summary>
        /// Fires <see cref="Running"/> with the mirror's stop value, 0, if the last report said the
        /// character was moving; a character already reported stopped gets nothing.
        /// </summary>
        private void ReportRunningStopped()
        {
            if (_reportedRunningSpeed <= 0d)
            {
                return;
            }

            _reportedRunningSpeed = 0d;
            Running.Fire(0d);
        }

        /// <summary>
        /// Moves the state machine and keeps <see cref="Running"/> bracketed inside the Running
        /// state: a stop is the last thing reported before leaving it, a speed the first thing
        /// reported after entering it.
        /// </summary>
        /// <remarks>
        /// WHY the stop goes BEFORE <see cref="StateChanged"/> and before the airborne signal the
        /// caller fires next: leaving Running is what ends the walk, and the mirror's default
        /// character animation script picks a pose per signal — FreeFalling picks the fall,
        /// Running(0) picks the idle, and the last one to arrive wins. A stop heard after the fall
        /// announcement leaves the character standing in mid-air; the same holds for a stop heard
        /// after Jumping(true), and for one heard after a StateChanged(Dead) handler has started a
        /// death animation.
        /// WHY entering Running always reports, even when the value is unchanged: the stop reported
        /// on the way out is 0, an idle character measures 0 on landing, and a change-only report
        /// would then say nothing — yet Running is the only signal that takes that animation script
        /// out of the falling pose. Both halves live here, in the one place a transition happens,
        /// so no call site can reorder them back.
        /// </remarks>
        private void EnterState(RbxHumanoidState next)
        {
            if (_state == next)
            {
                return;
            }

            if (next != RbxHumanoidState.Running)
            {
                ReportRunningStopped();
            }

            RbxHumanoidState previous = _state;
            _state = next;
            StateChanged.Fire(previous, next);
            if (next == RbxHumanoidState.Running)
            {
                ReportRunningSpeed(force: true);
            }
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
            // WHY the state changes before Died fires: Advance refuses a dead Humanoid, so this is
            // the last time the running speed is looked at, and a walk cycle driven by Running
            // alone would otherwise keep the last rate forever with auto-respawn off. EnterState
            // reports that stop — once, and only for a character that was moving — ahead of its
            // StateChanged, so Died is the last thing a listener hears about this character and
            // whatever its handler starts — a death animation, a ragdoll, a respawn timer — is not
            // followed by a stale Running(0) telling an animation script to blend back to idle.
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
