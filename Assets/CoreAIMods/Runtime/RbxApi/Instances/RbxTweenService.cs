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
    /// </summary>
    public sealed class RbxTweenService : RbxInstance
    {
        private readonly HashSet<RbxTween> _active = new();
        private ModScheduler _scheduler;
        private ITweenPropertyHost _propertyHost;
        private Func<RbxTweenPlaybackState, RbxEnumItem> _stateItemResolver;
        private InstanceRegistry _subscribedRegistry;

        internal RbxTweenService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "TweenService";
        }

        /// <summary>Property IO behind per-frame writes; null until the host attaches.</summary>
        internal ITweenPropertyHost PropertyHost => _propertyHost;

        /// <summary>Currently driven tweens (Delayed, Playing, or Paused).</summary>
        internal int ActiveTweenCount => _active.Count;

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
        /// triggers the conflict rule.
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

            RbxTween tween = (RbxTween)registry.Create("Tween");
            tween.Owner = this;
            tween.Initialize(target, info, checkedGoals, caller, _stateItemResolver);
            tween.BindHost(_scheduler);
            return tween;
        }

        /// <summary>
        /// Attaches the Heartbeat driver and the property host; safe to call again (a snapshot
        /// restore replaces the service instance, and the next Create re-attaches through here).
        /// </summary>
        internal void AttachHost(ModScheduler scheduler, ITweenPropertyHost propertyHost,
            Func<RbxTweenPlaybackState, RbxEnumItem> stateItemResolver)
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
        }

        /// <summary>Attaches when the scheduler host is missing or replaced; otherwise a no-op.</summary>
        internal void EnsureHost(ModScheduler scheduler, ITweenPropertyHost propertyHost,
            Func<RbxTweenPlaybackState, RbxEnumItem> stateItemResolver)
        {
            if (_scheduler == null || !ReferenceEquals(_scheduler, scheduler)
                || _subscribedRegistry == null || _propertyHost == null)
            {
                AttachHost(scheduler, propertyHost, stateItemResolver);
                return;
            }

            _propertyHost = propertyHost;
            _stateItemResolver = stateItemResolver;
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

        /// <summary>Tracks a tween the driver must step; called by the tween on (re)start.</summary>
        internal void Activate(RbxTween tween)
        {
            _active.Add(tween);
        }

        /// <summary>Releases a tween from the driver; called by the tween on terminal states.</summary>
        internal void Deactivate(RbxTween tween)
        {
            _active.Remove(tween);
        }

        /// <summary>
        /// Re-checks the call-time write authorization when playback starts (ownership may have
        /// changed between Create and Play). Uses the bare Demand like Create — never the
        /// enveloped AuthorizeMutation — because Play runs outside the per-call mutation
        /// envelope by design. Per-frame writes are NOT re-checked (OURS): they converge to
        /// the goal values authorized here.
        /// </summary>
        internal void AuthorizePlay(RbxTween tween)
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "Tween:Play cannot start: the TweenService is not attached to a world",
                    "resolve it via game:GetService(\"TweenService\")");
            }

            TweenCaller caller = tween.Caller;
            WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted,
                caller.WorldId, tween.Target, WorldAclDecision.WriteProperty,
                "tween properties");
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
                    throw RbxError.BadArgument(
                        "TweenService:Create does not tween " + sample.TypeName
                        + " properties (MVP-later tweenable backlog: boolean, EnumItem, Rect,"
                        + " UDim, Vector2, Vector2int16)",
                        "tween a number, Vector3, CFrame, Color3, or UDim2 property instead");
                }

                string goalType = DescribeGoal(goal);
                if (goal == null || !RbxTween.SameBoxType(sample.Value, goal))
                {
                    throw RbxError.BadArgument(
                        "TweenService:Create goal for '" + propertyName + "' expects "
                        + sample.TypeName + ", got " + goalType,
                        "pass a " + sample.TypeName + " goal for '" + propertyName + "'");
                }

                checkedGoals.Add(new TweenGoal(propertyName, goal));
            }

            return checkedGoals;
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
                if (tween.Target == null || tween.Target.IsDestroyed)
                {
                    tween.DropForDestroyedTarget();
                    continue;
                }

                tween.Step(deltaSeconds, host);
                if (!tween.IsActive)
                {
                    _active.Remove(tween);
                }
            }
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (_active.Count == 0)
            {
                return;
            }

            RbxTween[] snapshot = new RbxTween[_active.Count];
            _active.CopyTo(snapshot);
            for (int index = 0; index < snapshot.Length; index++)
            {
                RbxTween tween = snapshot[index];
                if (tween.Target != null && tween.Target.Id == record.Id)
                {
                    tween.DropForDestroyedTarget();
                }
            }
        }
    }
}
