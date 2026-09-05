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
    /// Trusted caller identity for one <see cref="RbxTweenService.Create"/> call: the durable
    /// actor id, the unrestricted flag, and the world id, copied from the trusted
    /// <c>LuaCsRbxModContext.ActorContext</c> at the Lua boundary — never from a Lua argument,
    /// so a script cannot start a tween as another actor.
    /// </summary>
    public readonly struct TweenCaller
    {
        public TweenCaller(string actorId, bool isUnrestricted, string worldId)
        {
            ActorId = actorId;
            IsUnrestricted = isUnrestricted;
            WorldId = worldId;
        }

        /// <summary>Durable actor id the tween writes are attributed to.</summary>
        public string ActorId { get; }

        /// <summary>Whether the creating actor holds the composition-issued host grant.</summary>
        public bool IsUnrestricted { get; }

        /// <summary>World the creating actor belongs to.</summary>
        public string WorldId { get; }
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
    /// </summary>
    public sealed class RbxTween : RbxInstance
    {
        private Func<RbxTweenPlaybackState, RbxEnumItem> _stateItemResolver;
        private double _delayRemaining;
        private double _elapsed;
        private int _iteration;

        internal RbxTween(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Tween";
            Completed = new RbxScriptSignal("Tween.Completed");
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
        /// Mirror <c>TweenBase:Play</c>: starts playback, resumes a paused tween from its
        /// progress, or restarts a cancelled/finished tween for its full length. No effect on
        /// an already-delayed or already-playing tween. Starting cancels any other active
        /// tween on the same properties of the same instance (mirror conflict rule).
        /// </summary>
        public void Play()
        {
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

            switch (PlaybackState)
            {
                case RbxTweenPlaybackState.Delayed:
                case RbxTweenPlaybackState.Playing:
                    return;
                case RbxTweenPlaybackState.Paused:
                    Owner?.CancelConflicts(this);
                    PlaybackState = RbxTweenPlaybackState.Playing;
                    Owner?.Activate(this);
                    return;
                default:
                    break;
            }

            ITweenPropertyHost host = RequireHost();
            Owner?.CancelConflicts(this);
            Owner?.AuthorizePlay(this);
            CaptureStarts(host);
            _iteration = 0;
            _elapsed = 0d;
            _delayRemaining = Info.DelayTime;
            PlaybackState = _delayRemaining > 0d
                ? RbxTweenPlaybackState.Delayed
                : RbxTweenPlaybackState.Playing;
            Owner?.Activate(this);
        }

        /// <summary>
        /// Mirror <c>TweenBase:Pause</c>: halts playback keeping progress, so Play resumes
        /// where it paused. Only works from Playing; any other state is a no-op (mirror:
        /// a Delayed tween ignores Pause and still plays after its delay).
        /// </summary>
        public void Pause()
        {
            if (!IsInitialized || PlaybackState != RbxTweenPlaybackState.Playing)
            {
                return;
            }

            PlaybackState = RbxTweenPlaybackState.Paused;
        }

        /// <summary>
        /// Mirror <c>TweenBase:Cancel</c>: halts playback and resets the tween variables — a
        /// later Play takes the full duration — but leaves the tweened properties where they
        /// are. Fires Completed with Cancelled. No-op on never-played, finished, or
        /// already-cancelled tweens (OURS — the mirror pins the fire only for stopped playback).
        /// </summary>
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
        /// Advances playback by one scaled Heartbeat delta. A zero or negative delta is a
        /// no-op, so a paused world (the driver feeds delta 0) freezes the tween exactly like
        /// task.wait. Reaching an iteration end writes the EXACT goal value, never an
        /// approximation, so the property lands exactly on the goal.
        /// </summary>
        internal void Step(double deltaSeconds, ITweenPropertyHost host)
        {
            if (!IsInitialized || deltaSeconds <= 0d)
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
                // WHY: a zero-duration tween is a single instant pass (OURS — the mirror does
                // not specify repeats at zero duration, and looping them would spin forever).
                ApplyIterationEnd(host);
                CompleteNaturally();
                return;
            }

            double remaining = deltaSeconds;
            while (remaining > 0d)
            {
                double need = time - _elapsed;
                if (remaining < need)
                {
                    _elapsed += remaining;
                    ApplyAlpha(_elapsed / time, host);
                    return;
                }

                remaining -= need;
                _elapsed = 0d;
                // WHY: the exact goal box, not the eased blend at alpha 1 (styles like Sine
                // evaluate to 0.99999999999999994 there) — the property must land EXACTLY.
                ApplyIterationEnd(host);
                if (!AdvanceIteration(host))
                {
                    return;
                }
            }

            ApplyAlpha(_elapsed / time, host);
        }

        /// <summary>Silent drop when the target is destroyed mid-flight (OURS — the mirror
        /// does not specify it): no Completed fire, since handlers could no longer read a
        /// meaningful tween state for a gone instance.</summary>
        internal void DropForDestroyedTarget()
        {
            PlaybackState = RbxTweenPlaybackState.Cancelled;
            Owner?.Deactivate(this);
        }

        private bool AdvanceIteration(ITweenPropertyHost host)
        {
            _iteration++;
            if (Info.RepeatCount >= 0 && _iteration > Info.RepeatCount)
            {
                CompleteNaturally();
                return false;
            }

            if (!Info.Reverses)
            {
                // WHY: without Reverses each repeat restarts from the captured start values —
                // the property snaps back and animates again, matching Roblox repeats.
                ApplyStarts(host);
            }

            return true;
        }

        private void CompleteNaturally()
        {
            PlaybackState = RbxTweenPlaybackState.Completed;
            Owner?.Deactivate(this);
            FireCompleted();
        }

        private void ApplyAlpha(double alpha, ITweenPropertyHost host)
        {
            double eased = RbxEasing.Evaluate(alpha, Info.EasingStyle, Info.EasingDirection);
            bool forward = !Info.Reverses || (_iteration % 2) == 0;
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                object from = forward ? goal.Start : goal.Goal;
                object to = forward ? goal.Goal : goal.Start;
                host.Write(Target, goal.PropertyName, Interpolate(from, to, eased));
            }
        }

        private void ApplyStarts(ITweenPropertyHost host)
        {
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                host.Write(Target, goal.PropertyName, goal.Start);
            }
        }

        /// <summary>
        /// Writes the exact end value of the finishing iteration (the goal box forward, the
        /// start box on a reversing leg) so the property lands exactly, never approximately.
        /// </summary>
        private void ApplyIterationEnd(ITweenPropertyHost host)
        {
            bool forward = !Info.Reverses || (_iteration % 2) == 0;
            for (int index = 0; index < _goals.Count; index++)
            {
                TweenGoal goal = _goals[index];
                host.Write(Target, goal.PropertyName, forward ? goal.Goal : goal.Start);
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
                return startNumber + ((goalNumber - startNumber) * alpha);
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
