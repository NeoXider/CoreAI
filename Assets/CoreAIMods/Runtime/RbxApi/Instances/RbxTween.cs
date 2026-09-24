using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Mirror <c>Enum.PlaybackState</c> item set, valued 1:1 (Begin 0 through Cancelled 5).
    /// The Lua layer maps these to the registry items; the engine-free driver compares the
    /// raw values so tween state never depends on a registry lookup per frame.
    /// </summary>
    public enum RbxTweenPlaybackState
    {
        Begin = 0,
        Delayed = 1,
        Playing = 2,
        Paused = 3,
        Completed = 4,
        Cancelled = 5
    }

    /// <summary>
    /// Trusted caller identity for one <see cref="RbxTweenService.Create"/> call or one tween
    /// control call (Play/Pause/Cancel): the durable actor id, the unrestricted flag, the world
    /// id, and the owning mod id, copied from the trusted <c>LuaCsRbxModContext</c> at the Lua
    /// boundary — never from a Lua argument, so a script cannot start a tween as another actor.
    /// </summary>
    public readonly struct TweenCaller
    {
        public TweenCaller(string actorId, bool isUnrestricted, string worldId)
            : this(actorId, isUnrestricted, worldId, null)
        {
        }

        public TweenCaller(string actorId, bool isUnrestricted, string worldId, string ownerModId)
        {
            ActorId = actorId;
            IsUnrestricted = isUnrestricted;
            WorldId = worldId;
            OwnerModId = string.IsNullOrWhiteSpace(ownerModId) ? null : ownerModId;
        }

        /// <summary>Durable actor id the tween writes are attributed to.</summary>
        public string ActorId { get; }

        /// <summary>Whether the creating actor holds the composition-issued host grant.</summary>
        public bool IsUnrestricted { get; }

        /// <summary>World the creating actor belongs to.</summary>
        public string WorldId { get; }

        /// <summary>
        /// Mod whose script created the tween; null for one-off consoles and host code. Recorded
        /// as the tween's teardown owner so unloading the mod destroys its tweens.
        /// </summary>
        public string OwnerModId { get; }
    }

    /// <summary>One goal property of a tween: the member name, the goal box, and the start box
    /// captured from the live property when playback (re)starts from the beginning.</summary>
    internal sealed class TweenGoal
    {
        public TweenGoal(string propertyName, object goal)
        {
            PropertyName = propertyName;
            Goal = goal;
        }

        public string PropertyName { get; }

        public object Goal { get; }

        public object Start { get; set; }

        public bool HasStart { get; set; }
    }

    /// <summary>
    /// Roblox Tween: controls the playback of one interpolation created by
    /// <see cref="RbxTweenService.Create"/>. Mirror-pinned semantics: <c>Play</c> on an
    /// already-playing (or delayed) tween has no effect; <c>Cancel</c> halts playback, resets
    /// the tween variables (a later <c>Play</c> restarts the FULL duration from the current
    /// values) but leaves the tweened properties where they are, and fires <c>Completed</c>
    /// with Cancelled; <c>Pause</c> works only from Playing, keeps progress, and fires nothing.
    /// Start values are captured at (re)start, never at creation.
    /// <para>
    /// Playback is counted in legs: one leg runs from the start values to the goals over
    /// <c>TweenInfo.Time</c>. With <c>Reverses</c> every repeat is a forward leg plus a reverse
    /// leg back to the start values (mirror <c>TweenInfo.reverses</c>: "reverse to the starting
    /// values once it reaches its targets"), so <c>RepeatCount = 0, Reverses = true</c> plays two
    /// legs and ends on the start values; without it every repeat is one forward leg that snaps
    /// back to the start values first. A run ends exactly on the goals, or exactly on the start
    /// values when it reverses.
    /// </para>
    /// </summary>
    public sealed class RbxTween : RbxInstance
    {
        private Func<RbxTweenPlaybackState, RbxEnumItem> _stateItemResolver;
        private double _delayRemaining;
        private double _elapsed;
        private long _legIndex;

        internal RbxTween(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Tween";
            // WHY a registered signal and not a free-standing field: Destroy disconnects exactly
            // the signals held in the instance's signal table, so a field-initialised Completed
            // kept delivering to handlers of a destroyed tween.
            Completed = GetOrCreateSignal("Completed");
        }

        /// <summary>Mirror <c>Tween.Completed(playbackState)</c>: fires once per run end —
        /// on natural finish (Completed) and on Cancel (Cancelled); Pause fires nothing.</summary>
        public RbxScriptSignal Completed { get; }

        /// <summary>Mirror <c>Tween.Instance</c> (read-only): the tweened instance.</summary>
        public RbxInstance Target { get; private set; }

        /// <summary>Mirror <c>Tween.TweenInfo</c> (read-only): the playback parameters.</summary>
        public RbxTweenInfo Info { get; private set; }

        /// <summary>Mirror <c>TweenBase.PlaybackState</c> (read-only).</summary>
        public RbxTweenPlaybackState PlaybackState { get; private set; } =
            RbxTweenPlaybackState.Begin;

        /// <summary>Caller identity stored at creation for authorization re-checks.</summary>
        internal TweenCaller Caller { get; private set; }

        /// <summary>Goal properties in creation order.</summary>
        internal IReadOnlyList<TweenGoal> Goals => _goals;

        /// <summary>Service that created this tween; routes conflict cancellation on Play.</summary>
        internal RbxTweenService Owner { get; set; }

        /// <summary>Position in the service's per-actor idle list; null while not idle.</summary>
        internal LinkedListNode<RbxTween> IdleNode { get; set; }

        internal bool IsInitialized => Target != null && Info != null;

        /// <summary>Active driver states: the tween holds properties while in one of these.</summary>
        internal bool IsActive => PlaybackState == RbxTweenPlaybackState.Delayed
            || PlaybackState == RbxTweenPlaybackState.Playing
            || PlaybackState == RbxTweenPlaybackState.Paused;

        private readonly List<TweenGoal> _goals = new();

        /// <summary>
        /// Completes construction for a service-created tween. The goals are already
        /// type-checked against the live properties by the service.
        /// </summary>
        internal void Initialize(RbxInstance target, RbxTweenInfo info,
            List<TweenGoal> goals, TweenCaller caller,
            Func<RbxTweenPlaybackState, RbxEnumItem> stateItemResolver)
        {
            Target = target;
            Info = info;
            Caller = caller;
            _stateItemResolver = stateItemResolver;
            _goals.AddRange(goals);
        }

        /// <summary>Binds the Completed signal to the driver scheduler.</summary>
        internal void BindHost(ModScheduler scheduler)
        {
            Completed.BindScheduler(scheduler);
        }

        /// <summary>
        /// Mirror <c>TweenBase:Play</c> under the creating actor's identity. See
        /// <see cref="Play(TweenCaller)"/>; prefer that overload wherever the calling actor is
        /// known, so the write is authorized for whoever starts the tween.
        /// </summary>
        public void Play()
        {
            PlayAs(Caller);
        }

        /// <summary>
        /// Mirror <c>TweenBase:Play</c>: starts playback, resumes a paused tween from its
        /// progress, or restarts a cancelled/finished tween for its full length. No effect on
        /// an already-delayed or already-playing tween. Starting cancels any other active
        /// tween on the same properties of the same instance (mirror conflict rule). The
        /// caller's write authority over the target is checked on every start AND resume,
        /// before any conflicting tween is cancelled, so a refused Play disturbs nothing.
        /// </summary>
        public void Play(TweenCaller caller)
        {
            PlayAs(caller);
        }

        /// <summary>Mirror <c>TweenBase:Pause</c> without a caller check (kept for C# hosts).</summary>
        public void Pause()
        {
            if (!IsInitialized || PlaybackState != RbxTweenPlaybackState.Playing)
            {
                return;
            }

            PlaybackState = RbxTweenPlaybackState.Paused;
        }

        /// <summary>
        /// Mirror <c>TweenBase:Pause</c>: halts playback keeping progress, so Play resumes
        /// where it paused. Only works from Playing; any other state is a no-op (mirror:
        /// a Delayed tween ignores Pause and still plays after its delay). A pause that would
        /// change state requires the caller's write authority over the target.
        /// </summary>
        public void Pause(TweenCaller caller)
        {
            if (!IsInitialized || PlaybackState != RbxTweenPlaybackState.Playing)
            {
                return;
            }

            Owner?.AuthorizeControl(this, caller, "pause a tween");
            Pause();
        }

        /// <summary>Mirror <c>TweenBase:Cancel</c> without a caller check (kept for C# hosts).</summary>
        public void Cancel()
        {
            if (!IsInitialized || !IsActive)
            {
                return;
            }

            PlaybackState = RbxTweenPlaybackState.Cancelled;
            Owner?.Deactivate(this);
            FireCompleted();
        }

        /// <summary>
        /// Mirror <c>TweenBase:Cancel</c>: halts playback and resets the tween variables — a
        /// later Play takes the full duration — but leaves the tweened properties where they
        /// are. Fires Completed with Cancelled. No-op on never-played, finished, or
        /// already-cancelled tweens (OURS — the mirror pins the fire only for stopped playback).
        /// A cancel that would change state requires the caller's write authority over the
        /// target.
        /// </summary>
        public void Cancel(TweenCaller caller)
        {
            if (!IsInitialized || !IsActive)
            {
                return;
            }

            Owner?.AuthorizeControl(this, caller, "cancel a tween");
            Cancel();
        }

        private void PlayAs(TweenCaller caller)
        {
            if (IsDestroyed)
            {
                throw new RbxError(RbxErrorCode.InstanceDestroyed,
                    "Tween:Play on destroyed Tween (id " + Id.Value + "): it was destroyed, or"
                    + " released after finishing because its actor already keeps "
                    + RbxTweenService.MaxIdleTweensPerActor + " newer finished tweens",
                    "create a new Tween with TweenService:Create instead of replaying a released one");
            }

            if (!IsInitialized)
            {
                throw RbxError.BadArgument(
                    "Tween:Play cannot start: this Tween was not created by TweenService:Create",
                    "create tweens only via TweenService:Create(instance, tweenInfo, propertyTable)");
            }

            if (Target.IsDestroyed)
            {
                throw RbxError.BadArgument(
                    "Tween:Play cannot start: its target " + Target.ClassName + " '"
                    + Target.GetFullName() + "' was destroyed",
                    "create a new Tween for a live instance");
            }

            if (PlaybackState == RbxTweenPlaybackState.Delayed
                || PlaybackState == RbxTweenPlaybackState.Playing)
            {
                return;
            }

            if (PlaybackState == RbxTweenPlaybackState.Paused)
            {
                Owner?.AuthorizePlay(this, caller);
                Owner?.CancelConflicts(this);
                PlaybackState = RbxTweenPlaybackState.Playing;
                Owner?.Activate(this);
                return;
            }

            ITweenPropertyHost host = RequireHost();
            Owner?.AuthorizePlay(this, caller);
            CaptureStarts(host);
            Owner?.CancelConflicts(this);
            _legIndex = 0;
            _elapsed = 0d;
            _delayRemaining = Info.DelayTime;
            PlaybackState = _delayRemaining > 0d
                ? RbxTweenPlaybackState.Delayed
                : RbxTweenPlaybackState.Playing;
            Owner?.Activate(this);
        }

        /// <summary>
        /// Advances playback by one scaled Heartbeat delta. A zero or negative delta is a
        /// no-op, so a paused world (the driver feeds delta 0) freezes the tween exactly like
        /// task.wait. The run's final leg writes the EXACT end value, never an approximation.
        /// Constant work per call whatever the duration or repeat count: crossed leg boundaries
        /// are counted arithmetically and the remainder carries modulo the duration, so one
        /// frame performs at most one write per goal.
        /// </summary>
        internal void Step(double deltaSeconds, ITweenPropertyHost host)
        {
            if (!IsInitialized || !(deltaSeconds > 0d) || double.IsInfinity(deltaSeconds))
            {
                return;
            }

            if (PlaybackState == RbxTweenPlaybackState.Delayed)
            {
                _delayRemaining -= deltaSeconds;
                if (_delayRemaining > 0d)
                {
                    return;
                }

                PlaybackState = RbxTweenPlaybackState.Playing;
                deltaSeconds = -_delayRemaining;
                _delayRemaining = 0d;
                if (deltaSeconds <= 0d)
                {
                    return;
                }
            }

            if (PlaybackState != RbxTweenPlaybackState.Playing)
            {
                return;
            }

            double time = Info.Time;
            if (time <= 0d)
            {
                // WHY: a zero-duration run is a single instant pass that lands where the whole
                // run would (OURS — the mirror does not specify zero durations, and looping
                // instantaneous repeats would never end).
                ApplyRunEnd(host);
                CompleteNaturally();
                return;
            }

            double total = _elapsed + deltaSeconds;
            if (total < time)
            {
                _elapsed = total;
                ApplyAlpha(total / time, host);
                return;
            }

            // WHY arithmetic instead of a per-boundary loop: a loop subtracted one duration per
            // pass, so a 1e-300 s repeating tween never left the loop (the subtraction is below
            // one ulp) and 1e-9 s cost millions of writes in one frame — C# work outside every
            // Lua budget, freezing the host thread. The remainder modulo the duration is exact
            // (IEEE fmod), and only the leg parity matters once a run repeats forever.
            double remainder = total % time;
            double crossed = Math.Round((total - remainder) / time);
            if (!(crossed >= 1d))
            {
                crossed = 1d;
            }

            long totalLegs = TotalLegs(Info);
            if (totalLegs >= 0L)
            {
                long remainingLegs = totalLegs - _legIndex;
                if (crossed >= remainingLegs)
                {
                    ApplyRunEnd(host);
                    CompleteNaturally();
                    return;
                }

                _legIndex += (long)crossed;
            }
            else if (Info.Reverses && !double.IsInfinity(crossed) && crossed % 2d != 0d)
            {
                _legIndex ^= 1L;
            }

            _elapsed = remainder;
            ApplyAlpha(remainder / time, host);
        }

        /// <summary>Silent drop when the target is destroyed mid-flight (OURS — the mirror
        /// does not specify it): no Completed fire, since handlers could no longer read a
        /// meaningful tween state for a gone instance.</summary>
        internal void DropForDestroyedTarget()
        {
            PlaybackState = RbxTweenPlaybackState.Cancelled;
            Owner?.Deactivate(this);
        }

        /// <summary>
        /// Stops a tween whose own instance was destroyed: it stops driving its properties at
        /// once and fires nothing more (mirror <c>Instance:Destroy</c> disconnects all
        /// connections).
        /// </summary>
        internal void StopForOwnDestruction()
        {
            if (IsActive)
            {
                PlaybackState = RbxTweenPlaybackState.Cancelled;
            }

            Completed.DisconnectAll();
        }

        /// <summary>
        /// Stops a tween whose step threw: it is cancelled so it never runs again, and its
        /// Completed fires with Cancelled so a script waiting on it is not left hanging.
        /// </summary>
        internal void StopForFault()
        {
            PlaybackState = RbxTweenPlaybackState.Cancelled;
            Owner?.Deactivate(this);
            FireCompleted();
        }

        /// <summary>Legs in one full run; -1 when the tween repeats forever.</summary>
        private static long TotalLegs(RbxTweenInfo info)
        {
            if (info.RepeatCount < 0)
            {
                return -1L;
            }

            long legsPerRepeat = info.Reverses ? 2L : 1L;
            return ((long)info.RepeatCount + 1L) * legsPerRepeat;
        }

        private bool IsForwardLeg => !Info.Reverses || (_legIndex & 1L) == 0L;

        private void CompleteNaturally()
        {
            PlaybackState = RbxTweenPlaybackState.Completed;
            Owner?.Deactivate(this);
            FireCompleted();
        }

        private void ApplyAlpha(double alpha, ITweenPropertyHost host)
        {
            double eased = RbxEasing.Evaluate(alpha, Info.EasingStyle, Info.EasingDirection);
            bool forward = IsForwardLeg;
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                object from = forward ? goal.Start : goal.Goal;
                object to = forward ? goal.Goal : goal.Start;
                host.Write(Target, goal.PropertyName, Interpolate(from, to, eased));
            }
        }

        /// <summary>
        /// Writes the exact end value of the whole run — the goal box, or the start box when the
        /// run reverses — so the property lands exactly, never approximately (styles like Sine
        /// evaluate to 0.99999999999999994 at alpha 1).
        /// </summary>
        private void ApplyRunEnd(ITweenPropertyHost host)
        {
            bool endsOnGoal = !Info.Reverses;
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                host.Write(Target, goal.PropertyName, endsOnGoal ? goal.Goal : goal.Start);
            }
        }

        private void CaptureStarts(ITweenPropertyHost host)
        {
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                TweenPropertySample sample = host.Sample(Target, goal.PropertyName);
                if (!sample.Found || !sample.Supported
                    || !SameBoxType(sample.Value, goal.Goal))
                {
                    throw RbxError.BadArgument(
                        "Tween:Play cannot start: property '" + goal.PropertyName + "' of "
                        + Target.ClassName + " changed type since creation",
                        "create a new Tween for the current property types");
                }

                goal.Start = sample.Value;
                goal.HasStart = true;
            }
        }

        private void FireCompleted()
        {
            if (!Completed.HasConnections)
            {
                return;
            }

            Func<RbxTweenPlaybackState, RbxEnumItem> resolver = _stateItemResolver;
            if (resolver == null)
            {
                throw RbxError.BadArgument(
                    "Tween.Completed fired with no PlaybackState item resolver",
                    "front the world with the scripted API composition so enums resolve");
            }

            Completed.Fire(resolver(PlaybackState));
        }

        private ITweenPropertyHost RequireHost()
        {
            RbxTweenService owner = Owner;
            if (owner == null || owner.PropertyHost == null)
            {
                throw RbxError.BadArgument(
                    "Tween:Play cannot start: the TweenService has no property host",
                    "front the world with the scripted API composition so tweens can write");
            }

            return owner.PropertyHost;
        }

        /// <summary>Interpolates two same-typed boxes; number, Vector3, CFrame, Color3, UDim2.</summary>
        internal static object Interpolate(object start, object goal, double alpha)
        {
            float blend = (float)alpha;
            if (start is double startNumber && goal is double goalNumber)
            {
                double difference = goalNumber - startNumber;
                if (double.IsInfinity(difference))
                {
                    // WHY: two finite ends far apart overflow their difference, and inf * 0
                    // is NaN at alpha 0; the weighted form stays finite for finite ends.
                    return (startNumber * (1d - alpha)) + (goalNumber * alpha);
                }

                return startNumber + (difference * alpha);
            }

            if (start is RbxVector3 startVector && goal is RbxVector3 goalVector)
            {
                return startVector.Lerp(goalVector, blend);
            }

            if (start is RbxCFrame startCFrame && goal is RbxCFrame goalCFrame)
            {
                return startCFrame.Lerp(goalCFrame, blend);
            }

            if (start is RbxColor3 startColor && goal is RbxColor3 goalColor)
            {
                return startColor.Lerp(goalColor, blend);
            }

            if (start is RbxUDim2 startUDim && goal is RbxUDim2 goalUDim)
            {
                return startUDim.Lerp(goalUDim, blend);
            }

            throw RbxError.BadArgument(
                "Tween cannot interpolate " + DescribeBox(start),
                "tween number, Vector3, CFrame, Color3, or UDim2 properties only");
        }

        internal static bool SameBoxType(object left, object right)
        {
            return left != null && right != null
                && left.GetType() == right.GetType();
        }

        private static string DescribeBox(object box)
        {
            if (box == null)
            {
                return "nil";
            }

            if (box is double)
            {
                return "number";
            }

            if (box is bool)
            {
                return "boolean";
            }

            return box.GetType().Name;
        }
    }
}
