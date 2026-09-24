using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox TweenService: creates <see cref="RbxTween"/>s that interpolate instance
    /// properties, plus the pure <c>GetValue</c> easing math. Mirror-pinned semantics: when
    /// two tweens target the same property of the same instance, the initial tween is
    /// cancelled (firing its Completed with Cancelled) and overwritten by the most recent
    /// tween. The driver advances from the scheduler Heartbeat phase on the SCALED clock —
    /// a paused world (the frame driver feeds delta 0) freezes tweens exactly like task.wait
    /// (roadmap D9: tween durations are scaled game time).
    /// <para>
    /// Lifetime (OURS — Roblox garbage-collects a finished, unreferenced tween): every tween is
    /// Owned by the creating actor and charged to that actor's instance quota, never to the
    /// shared host bucket. Each actor keeps at most <see cref="MaxIdleTweensPerActor"/> finished
    /// (Completed or Cancelled) tweens; finishing one more destroys the one that finished
    /// longest ago, so "create, play, forget" stays bounded while replaying a recently finished
    /// tween keeps working. A mod's tweens are destroyed when the mod unloads.
    /// </para>
    /// </summary>
    public sealed class RbxTweenService : RbxInstance
    {
        /// <summary>Finished tweens an actor keeps before the oldest one is destroyed.</summary>
        public const int MaxIdleTweensPerActor = 256;

        private readonly HashSet<RbxTween> _active = new();
        private readonly HashSet<RbxTween> _tracked = new();
        private readonly Dictionary<InstanceId, List<RbxTween>> _byTarget = new();
        private readonly Dictionary<string, LinkedList<RbxTween>> _idleByActor =
            new(StringComparer.Ordinal);
        private ModScheduler _scheduler;
        private ITweenPropertyHost _propertyHost;
        private Func<RbxTweenPlaybackState, RbxEnumItem> _stateItemResolver;
        private InstanceRegistry _subscribedRegistry;
        private Action<string> _log;

        internal RbxTweenService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "TweenService";
        }

        /// <summary>Property IO behind per-frame writes; null until the host attaches.</summary>
        internal ITweenPropertyHost PropertyHost => _propertyHost;

        /// <summary>Currently driven tweens (Delayed, Playing, or Paused).</summary>
        internal int ActiveTweenCount => _active.Count;

        /// <summary>Live (not destroyed) tweens this service created, in any state.</summary>
        internal int LiveTweenCount => _tracked.Count;

        /// <summary>Tweens dropped because their step threw; each one is logged once.</summary>
        internal int FaultedTweenCount { get; private set; }

        /// <summary>Finished tweens currently kept for the actor.</summary>
        internal int IdleTweenCount(string actorId)
        {
            return _idleByActor.TryGetValue(ActorKey(actorId), out LinkedList<RbxTween> idle)
                ? idle.Count
                : 0;
        }

        /// <summary>
        /// Mirror <c>TweenService:GetValue</c>: pure easing math — the clamped alpha remapped
        /// through the style and direction. Never touches tween state.
        /// </summary>
        public static double GetValue(double alpha, RbxEasingStyle easingStyle,
            RbxEasingDirection easingDirection)
        {
            return RbxEasing.Evaluate(alpha, easingStyle, easingDirection);
        }

        /// <summary>
        /// Mirror <c>TweenService:Create</c>: validates the target, the info, and every goal
        /// against the LIVE property types, authorizes the write at call time, and returns a
        /// Begin-state tween (playback starts only on Play, which captures start values).
        /// Re-creating for the same property does not disturb a running tween — only Play
        /// triggers the conflict rule. The tween is Owned by the caller's actor and charged to
        /// that actor's quota.
        /// </summary>
        public RbxTween Create(RbxInstance target, RbxTweenInfo info,
            IReadOnlyList<KeyValuePair<string, object>> goals, TweenCaller caller)
        {
            if (target == null)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create expects an Instance at argument 1",
                    "pass the Instance whose properties are to be tweened at argument 1");
            }

            if (info == null)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create expects a TweenInfo at argument 2",
                    "pass TweenInfo.new(...) at argument 2");
            }

            if (target.IsDestroyed)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create cannot tween destroyed instance " + target.Name,
                    "tween a live instance instead");
            }

            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create cannot create: the TweenService is not attached to a world",
                    "resolve it via game:GetService(\"TweenService\")");
            }

            if (_scheduler == null || _propertyHost == null)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create cannot create: the TweenService has no scheduler host",
                    "front the world with the scripted API composition so tweens can play");
            }

            WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted,
                caller.WorldId, target, WorldAclDecision.WriteProperty, "tween properties");

            List<TweenGoal> checkedGoals = CheckGoals(target, goals);

            // WHY Owned by the caller's actor and not runtime infrastructure: infrastructure is
            // charged to nobody and an ownerless record is charged to the shared "host/system"
            // bucket, which every create-play-forget tween used to fill until Instance.new failed
            // world-wide. Charged to its own actor, a runaway script exhausts only its own quota,
            // and Owned lets that actor (and only it) destroy its tween.
            RbxTween tween = (RbxTween)registry.Create("Tween", caller.OwnerModId, null,
                InstanceIdAuthority.Server, caller.ActorId.Trim(), InstanceAccessScope.Owned,
                false);
            tween.Owner = this;
            tween.Initialize(target, info, checkedGoals, caller, _stateItemResolver);
            tween.BindHost(_scheduler);
            Track(tween);
            return tween;
        }

        /// <summary>
        /// Attaches the Heartbeat driver and the property host; safe to call again (a snapshot
        /// restore replaces the service instance, and the next Create re-attaches through here).
        /// A null <paramref name="log"/> keeps the sink already attached.
        /// </summary>
        internal void AttachHost(ModScheduler scheduler, ITweenPropertyHost propertyHost,
            Func<RbxTweenPlaybackState, RbxEnumItem> stateItemResolver,
            Action<string> log = null)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException(nameof(scheduler));
            }

            if (propertyHost == null)
            {
                throw new ArgumentNullException(nameof(propertyHost));
            }

            InstanceRegistry registry = Registry;
            if (!ReferenceEquals(_scheduler, scheduler))
            {
                if (_scheduler != null)
                {
                    _scheduler.PhaseReached -= OnSchedulerPhase;
                }

                _scheduler = scheduler;
                scheduler.PhaseReached += OnSchedulerPhase;
            }

            if (registry != null && !ReferenceEquals(_subscribedRegistry, registry))
            {
                if (_subscribedRegistry != null)
                {
                    _subscribedRegistry.Unregistered -= OnInstanceUnregistered;
                }

                registry.Unregistered += OnInstanceUnregistered;
                _subscribedRegistry = registry;
            }

            _propertyHost = propertyHost;
            _stateItemResolver = stateItemResolver;
            if (log != null)
            {
                _log = log;
            }
        }

        /// <summary>Attaches when the scheduler host is missing or replaced; otherwise a no-op.</summary>
        internal void EnsureHost(ModScheduler scheduler, ITweenPropertyHost propertyHost,
            Func<RbxTweenPlaybackState, RbxEnumItem> stateItemResolver,
            Action<string> log = null)
        {
            if (_scheduler == null || !ReferenceEquals(_scheduler, scheduler)
                || _subscribedRegistry == null || _propertyHost == null)
            {
                AttachHost(scheduler, propertyHost, stateItemResolver, log);
                return;
            }

            _propertyHost = propertyHost;
            _stateItemResolver = stateItemResolver;
            if (log != null)
            {
                _log = log;
            }
        }

        /// <summary>Releases the driver subscriptions; in-flight tweens freeze until re-attached.</summary>
        internal void DetachHost()
        {
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnSchedulerPhase;
                _scheduler = null;
            }

            if (_subscribedRegistry != null)
            {
                _subscribedRegistry.Unregistered -= OnInstanceUnregistered;
                _subscribedRegistry = null;
            }

            _propertyHost = null;
        }

        /// <summary>
        /// Destroys every live tween created by <paramref name="ownerModId"/>'s scripts. Playing
        /// ones stop where they are and fire nothing (the mod's handlers are going away with
        /// it). Call it when a mod unloads or is killed; returns the number destroyed.
        /// </summary>
        public int CancelAndReleaseOwnedBy(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId) || _tracked.Count == 0)
            {
                return 0;
            }

            List<RbxTween> owned = null;
            foreach (RbxTween tween in _tracked)
            {
                if (string.Equals(tween.Caller.OwnerModId, ownerModId, StringComparison.Ordinal))
                {
                    owned ??= new List<RbxTween>();
                    owned.Add(tween);
                }
            }

            if (owned == null)
            {
                return 0;
            }

            for (int index = 0; index < owned.Count; index++)
            {
                RbxTween tween = owned[index];
                tween.StopForOwnDestruction();
                Release(tween);
                DestroyQuietly(tween, "its mod '" + ownerModId + "' was torn down");
            }

            return owned.Count;
        }

        /// <summary>Tracks a tween the driver must step; called by the tween on (re)start.</summary>
        internal void Activate(RbxTween tween)
        {
            RemoveIdle(tween);
            _active.Add(tween);
        }

        /// <summary>
        /// Releases a tween from the driver; called by the tween on terminal states. The tween
        /// joins its actor's idle list, which destroys the actor's longest-idle tween once the
        /// list holds more than <see cref="MaxIdleTweensPerActor"/>.
        /// </summary>
        internal void Deactivate(RbxTween tween)
        {
            _active.Remove(tween);
            if (tween.IsDestroyed || !_tracked.Contains(tween))
            {
                return;
            }

            AddIdle(tween);
        }

        /// <summary>
        /// Re-checks the creator's write authorization when playback starts (ownership may have
        /// changed between Create and Play).
        /// </summary>
        internal void AuthorizePlay(RbxTween tween)
        {
            AuthorizePlay(tween, tween.Caller);
        }

        /// <summary>
        /// Checks that <paramref name="caller"/> may write the tween's target before playback
        /// starts or resumes (ownership may have changed since Create, and the caller may not be
        /// the creator). Uses the bare Demand like Create — never the enveloped
        /// AuthorizeMutation — because Play runs outside the per-call mutation envelope by
        /// design. Per-frame writes are NOT re-checked (OURS): they converge to the goal values
        /// authorized here.
        /// </summary>
        internal void AuthorizePlay(RbxTween tween, TweenCaller caller)
        {
            AuthorizeControl(tween, caller, "tween properties");
        }

        /// <summary>
        /// Checks that <paramref name="caller"/> may write the tween's target; Pause and Cancel
        /// stop another actor's writes, so they need the same authority Play does.
        /// </summary>
        internal void AuthorizeControl(RbxTween tween, TweenCaller caller, string operation)
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "Tween cannot " + operation + ": the TweenService is not attached to a world",
                    "resolve it via game:GetService(\"TweenService\")");
            }

            WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted,
                caller.WorldId, tween.Target, WorldAclDecision.WriteProperty, operation);
        }

        /// <summary>
        /// Mirror conflict rule: starting this tween cancels every other active tween that
        /// targets an overlapping property of the same instance. Each cancelled tween fires
        /// its own Completed with Cancelled.
        /// </summary>
        internal void CancelConflicts(RbxTween incoming)
        {
            List<RbxTween> conflicts = null;
            foreach (RbxTween active in _active)
            {
                if (ReferenceEquals(active, incoming)
                    || active.Target == null || incoming.Target == null
                    || active.Target.Id != incoming.Target.Id)
                {
                    continue;
                }

                if (Overlaps(active, incoming))
                {
                    conflicts ??= new List<RbxTween>();
                    conflicts.Add(active);
                }
            }

            if (conflicts == null)
            {
                return;
            }

            for (int index = 0; index < conflicts.Count; index++)
            {
                conflicts[index].Cancel();
            }
        }

        private static bool Overlaps(RbxTween left, RbxTween right)
        {
            IReadOnlyList<TweenGoal> leftGoals = left.Goals;
            IReadOnlyList<TweenGoal> rightGoals = right.Goals;
            for (int leftIndex = 0; leftIndex < leftGoals.Count; leftIndex++)
            {
                for (int rightIndex = 0; rightIndex < rightGoals.Count; rightIndex++)
                {
                    if (string.Equals(leftGoals[leftIndex].PropertyName,
                            rightGoals[rightIndex].PropertyName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private List<TweenGoal> CheckGoals(RbxInstance target,
            IReadOnlyList<KeyValuePair<string, object>> goals)
        {
            if (goals == null || goals.Count == 0)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create expects a property table with at least one goal at argument 3",
                    "pass a table like {Transparency = 1} at argument 3");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            List<TweenGoal> checkedGoals = new(goals.Count);
            for (int index = 0; index < goals.Count; index++)
            {
                string propertyName = goals[index].Key;
                object goal = goals[index].Value;
                if (string.IsNullOrEmpty(propertyName))
                {
                    throw RbxError.BadArgument(
                        "TweenService:Create expects string property names in the property table",
                        "pass a dictionary like {Transparency = 1}");
                }

                if (!seen.Add(propertyName))
                {
                    throw RbxError.BadArgument(
                        "TweenService:Create got a duplicate goal for property '"
                        + propertyName + "'",
                        "list each property once in the property table");
                }

                TweenPropertySample sample = _propertyHost.Sample(target, propertyName);
                if (!sample.Found)
                {
                    throw RbxError.BadArgument(
                        propertyName + " is not a valid member of " + target.ClassName + " \""
                        + target.GetFullName() + "\"",
                        "tween a property that exists on " + target.ClassName);
                }

                if (!sample.Supported)
                {
                    // WHY the loud stub and not BAD_ARGUMENT: the member exists and the script
                    // is valid Roblox; only CoreAI cannot tween its type yet, and the stub code
                    // is what tells an author (and the stub counter) "not yet", not "wrong".
                    throw RbxError.NotImplemented(
                        "TweenService:Create tweening " + target.ClassName + "." + propertyName
                        + " (" + sample.TypeName + ")",
                        "the MVP-later tweenable backlog",
                        "tween a number, Vector3, CFrame, Color3, or UDim2 property instead;"
                        + " boolean, EnumItem, Rect, UDim, Vector2 and Vector2int16 values are"
                        + " not tweenable yet");
                }

                string goalType = DescribeGoal(goal);
                if (goal == null || !RbxTween.SameBoxType(sample.Value, goal))
                {
                    throw RbxError.BadArgument(
                        "TweenService:Create goal for '" + propertyName + "' expects "
                        + sample.TypeName + ", got " + goalType,
                        "pass a " + sample.TypeName + " goal for '" + propertyName + "'");
                }

                string nonFinite = DescribeNonFinite(goal);
                if (nonFinite != null)
                {
                    // WHY refused here and not at the write: a NaN or infinite goal interpolates
                    // to NaN/inf on every frame, and the setter behind the write (IntValue,
                    // Humanoid) refuses it from inside Heartbeat, where no script can catch it.
                    throw RbxError.BadArgument(
                        "TweenService:Create goal for '" + propertyName + "' must be finite, got "
                        + nonFinite,
                        "pass a finite " + sample.TypeName + " goal for '" + propertyName + "'");
                }

                checkedGoals.Add(new TweenGoal(propertyName, goal));
            }

            return checkedGoals;
        }

        /// <summary>Names the first non-finite part of a goal box, or null when all are finite.</summary>
        private static string DescribeNonFinite(object goal)
        {
            switch (goal)
            {
                case double number:
                    return DescribeNonFiniteNumber(number);
                case RbxVector3 vector:
                    return FirstNonFinite(vector.X, vector.Y, vector.Z);
                case RbxColor3 color:
                    return FirstNonFinite(color.R, color.G, color.B);
                case RbxUDim2 udim2:
                    return FirstNonFinite(udim2.X.Scale, udim2.Y.Scale, 0f);
                case RbxCFrame cframe:
                    float[] components = cframe.GetComponents();
                    for (int index = 0; index < components.Length; index++)
                    {
                        string described = DescribeNonFiniteNumber(components[index]);
                        if (described != null)
                        {
                            return "a CFrame component " + described;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        private static string FirstNonFinite(float first, float second, float third)
        {
            string described = DescribeNonFiniteNumber(first)
                ?? DescribeNonFiniteNumber(second)
                ?? DescribeNonFiniteNumber(third);
            return described == null ? null : "a component " + described;
        }

        private static string DescribeNonFiniteNumber(double number)
        {
            if (double.IsNaN(number))
            {
                return "nan";
            }

            if (double.IsPositiveInfinity(number))
            {
                return "inf";
            }

            return double.IsNegativeInfinity(number) ? "-inf" : null;
        }

        private static string DescribeGoal(object goal)
        {
            if (goal == null)
            {
                return "nil";
            }

            if (goal is double)
            {
                return "number";
            }

            if (goal is bool)
            {
                return "boolean";
            }

            if (goal is string)
            {
                return "string";
            }

            if (goal is RbxVector3)
            {
                return "Vector3";
            }

            if (goal is RbxCFrame)
            {
                return "CFrame";
            }

            if (goal is RbxColor3)
            {
                return "Color3";
            }

            if (goal is RbxUDim2)
            {
                return "UDim2";
            }

            return goal.GetType().Name;
        }

        private void OnSchedulerPhase(SchedulerPhase phase, double deltaSeconds)
        {
            // WHY: the Heartbeat phase delta IS the scaled frame time (the driver feeds the
            // already-scaled host delta), so stepping here — and only here — freezes tweens
            // when the world pauses, exactly like task.wait. Wall time is never consulted.
            if (phase != SchedulerPhase.Heartbeat)
            {
                return;
            }

            ITweenPropertyHost host = _propertyHost;
            if (host == null || _active.Count == 0)
            {
                return;
            }

            RbxTween[] snapshot = new RbxTween[_active.Count];
            _active.CopyTo(snapshot);
            for (int index = 0; index < snapshot.Length; index++)
            {
                RbxTween tween = snapshot[index];
                if (tween.IsDestroyed || !tween.IsActive)
                {
                    _active.Remove(tween);
                    continue;
                }

                if (tween.Target == null || tween.Target.IsDestroyed)
                {
                    tween.DropForDestroyedTarget();
                    continue;
                }

                try
                {
                    tween.Step(deltaSeconds, host);
                }
                catch (Exception ex)
                {
                    // WHY contained per tween: this handler runs inside the scheduler's Heartbeat,
                    // and one tween whose write throws used to escape it on every frame — the
                    // later Heartbeat subscribers (RunService.Heartbeat, Humanoids, the mod
                    // tick) never ran again. The faulting tween is dropped so it is reported once.
                    DropFaulted(tween, ex);
                    continue;
                }

                if (!tween.IsActive)
                {
                    _active.Remove(tween);
                }
            }
        }

        private void DropFaulted(RbxTween tween, Exception fault)
        {
            FaultedTweenCount++;
            string report = "[CoreAI.RbxApi] TweenService stopped a Tween of "
                + DescribeTarget(tween) + " (actor '" + tween.Caller.ActorId
                + "') because writing its properties failed: " + fault.Message;
            try
            {
                tween.StopForFault();
            }
            catch (Exception completedFault)
            {
                report += "; firing its Completed also failed: " + completedFault.Message;
            }

            _active.Remove(tween);
            _log?.Invoke(report);
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (record.Instance is RbxTween ownTween && ReferenceEquals(ownTween.Owner, this))
            {
                ownTween.StopForOwnDestruction();
                Release(ownTween);
                return;
            }

            if (!_byTarget.TryGetValue(record.Id, out List<RbxTween> targeting))
            {
                return;
            }

            RbxTween[] snapshot = targeting.ToArray();
            for (int index = 0; index < snapshot.Length; index++)
            {
                RbxTween tween = snapshot[index];
                if (tween.IsActive)
                {
                    tween.DropForDestroyedTarget();
                }
                else if (!tween.IsDestroyed && tween.IdleNode == null)
                {
                    // WHY a never-played tween joins the idle list: its target is gone, so Play
                    // can only raise; bounding it with the finished ones keeps a script that
                    // creates a tween per short-lived part from growing the registry forever.
                    AddIdle(tween);
                }
            }
        }

        private void Track(RbxTween tween)
        {
            _tracked.Add(tween);
            InstanceId targetId = tween.Target.Id;
            if (!_byTarget.TryGetValue(targetId, out List<RbxTween> targeting))
            {
                targeting = new List<RbxTween>();
                _byTarget.Add(targetId, targeting);
            }

            targeting.Add(tween);
        }

        private void Release(RbxTween tween)
        {
            _active.Remove(tween);
            RemoveIdle(tween);
            if (!_tracked.Remove(tween) || tween.Target == null)
            {
                return;
            }

            InstanceId targetId = tween.Target.Id;
            if (_byTarget.TryGetValue(targetId, out List<RbxTween> targeting))
            {
                targeting.Remove(tween);
                if (targeting.Count == 0)
                {
                    _byTarget.Remove(targetId);
                }
            }
        }

        private void AddIdle(RbxTween tween)
        {
            RemoveIdle(tween);
            string actorKey = ActorKey(tween.Caller.ActorId);
            if (!_idleByActor.TryGetValue(actorKey, out LinkedList<RbxTween> idle))
            {
                idle = new LinkedList<RbxTween>();
                _idleByActor.Add(actorKey, idle);
            }

            tween.IdleNode = idle.AddLast(tween);
            while (idle.Count > MaxIdleTweensPerActor)
            {
                RbxTween oldest = idle.First.Value;
                Release(oldest);
                DestroyQuietly(oldest, "its actor keeps more than "
                    + MaxIdleTweensPerActor + " finished tweens");
            }
        }

        private void RemoveIdle(RbxTween tween)
        {
            LinkedListNode<RbxTween> node = tween.IdleNode;
            if (node == null)
            {
                return;
            }

            tween.IdleNode = null;
            LinkedList<RbxTween> idle = node.List;
            if (idle == null)
            {
                return;
            }

            idle.Remove(node);
            if (idle.Count == 0)
            {
                _idleByActor.Remove(ActorKey(tween.Caller.ActorId));
            }
        }

        private void DestroyQuietly(RbxTween tween, string reason)
        {
            try
            {
                tween.Destroy();
            }
            catch (Exception ex)
            {
                _log?.Invoke("[CoreAI.RbxApi] TweenService could not destroy a Tween of "
                    + DescribeTarget(tween) + " after " + reason + ": " + ex.Message);
            }
        }

        private static string DescribeTarget(RbxTween tween)
        {
            RbxInstance target = tween.Target;
            return target == null
                ? "nothing"
                : target.ClassName + " '" + target.Name + "' (id " + target.Id.Value + ")";
        }

        private static string ActorKey(string actorId)
        {
            return string.IsNullOrWhiteSpace(actorId) ? string.Empty : actorId.Trim();
        }
    }
}
