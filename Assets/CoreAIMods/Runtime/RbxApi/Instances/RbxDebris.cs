using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Trusted caller identity for one <see cref="RbxDebris.AddItem"/> call: the durable actor id,
    /// the unrestricted flag, and the world id, copied from the trusted
    /// <c>LuaCsRbxModContext.ActorContext</c> at the Lua boundary — never from a Lua argument,
    /// so a script cannot schedule destruction as another actor. This struct is the security
    /// boundary: the stored copy is re-checked when the timer fires, and an ownership change in
    /// between drops the destroy instead of applying it.
    /// </summary>
    public readonly struct DebrisCaller
    {
        public DebrisCaller(string actorId, bool isUnrestricted, string worldId)
        {
            ActorId = actorId;
            IsUnrestricted = isUnrestricted;
            WorldId = worldId;
        }

        /// <summary>Durable actor id the destroy is attributed to at fire time.</summary>
        public string ActorId { get; }

        /// <summary>Whether the scheduling actor holds the composition-issued host grant.</summary>
        public bool IsUnrestricted { get; }

        /// <summary>World the scheduling actor belongs to.</summary>
        public string WorldId { get; }
    }

    /// <summary>
    /// Roblox Debris service: schedules guaranteed destruction of an instance without yielding,
    /// running outside the scheduling script's lifetime. Mirror-pinned semantics: the lifetime
    /// argument is optional and defaults to 10 seconds, and the service holds a hardcoded maximum
    /// of 1,000 items — when full, the oldest debris is destroyed instantly to make room, so the
    /// lifetime is a maximum, not an exact lifetime. Pending entries are ephemeral scheduler state
    /// (WORLD_PACKAGE.md): nothing about them is serialised into the world package; a restored
    /// world starts with an empty queue.
    /// </summary>
    /// <remarks>
    /// Memory is bounded by the cap, not by how often a script calls AddItem: every pending item
    /// owns exactly one deadline-heap slot and one insertion-order node, a re-add updates them in
    /// place, and an item destroyed early removes both at once. The service drives all of its
    /// items from at most <see cref="MaxArmedTimers"/> + 1 scheduler host callbacks.
    /// </remarks>
    public sealed class RbxDebris : RbxInstance
    {
        /// <summary>Mirror default: AddItem lifetime when the script omits it.</summary>
        public const double DefaultLifetimeSeconds = 10d;

        /// <summary>Mirror hard cap: the oldest entry is destroyed instantly past this many.</summary>
        public const int MaxItems = 1000;

        /// <summary>
        /// Most precisely-timed scheduler callbacks the service keeps outstanding at once. Beyond
        /// it, one extra next-frame callback polls until an older callback drains.
        /// </summary>
        /// <remarks>
        /// WHY a small bound instead of exactly one callback: <see cref="ModScheduler"/> offers no
        /// way to cancel a host callback, so a new item that is due EARLIER than every armed
        /// callback needs one more. Unbounded, a script re-adding one item with ever-shorter
        /// lifetimes grew the scheduler heap by one entry per call; bounded, the worst case is
        /// this many far-future callbacks plus a per-frame poll.
        /// </remarks>
        internal const int MaxArmedTimers = 8;

        private sealed class DebrisEntry
        {
            public DebrisEntry(InstanceId id, long insertionOrder, double deadline,
                DebrisCaller caller)
            {
                Id = id;
                InsertionOrder = insertionOrder;
                Deadline = deadline;
                Caller = caller;
            }

            public InstanceId Id { get; }

            public long InsertionOrder { get; }

            public double Deadline { get; set; }

            public DebrisCaller Caller { get; set; }

            /// <summary>Slot in the deadline heap; -1 once the entry left it.</summary>
            public int HeapIndex { get; set; } = -1;

            /// <summary>Node in the insertion-order list; null once the entry left it.</summary>
            public LinkedListNode<DebrisEntry> InsertionNode { get; set; }
        }

        /// <summary>Binary min-heap on (deadline, insertion order) that tracks each entry's slot,
        /// so a re-add re-sifts in place and an early destroy removes the entry directly.</summary>
        private sealed class DeadlineHeap
        {
            private readonly List<DebrisEntry> _items = new();

            public int Count => _items.Count;

            public DebrisEntry Peek()
            {
                return _items[0];
            }

            public void Add(DebrisEntry entry)
            {
                _items.Add(entry);
                entry.HeapIndex = _items.Count - 1;
                SiftUp(entry.HeapIndex);
            }

            public void Remove(DebrisEntry entry)
            {
                int index = entry.HeapIndex;
                if (index < 0 || index >= _items.Count || !ReferenceEquals(_items[index], entry))
                {
                    return;
                }

                int lastIndex = _items.Count - 1;
                DebrisEntry last = _items[lastIndex];
                _items.RemoveAt(lastIndex);
                entry.HeapIndex = -1;
                if (index == lastIndex)
                {
                    return;
                }

                _items[index] = last;
                last.HeapIndex = index;
                Resift(index);
            }

            /// <summary>Restores heap order after the entry's deadline changed.</summary>
            public void Update(DebrisEntry entry)
            {
                int index = entry.HeapIndex;
                if (index >= 0 && index < _items.Count && ReferenceEquals(_items[index], entry))
                {
                    Resift(index);
                }
            }

            private void Resift(int index)
            {
                if (index > 0 && Compare(_items[index], _items[(index - 1) / 2]) < 0)
                {
                    SiftUp(index);
                    return;
                }

                SiftDown(index);
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parentIndex = (index - 1) / 2;
                    if (Compare(_items[index], _items[parentIndex]) >= 0)
                    {
                        return;
                    }

                    Swap(index, parentIndex);
                    index = parentIndex;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    int leftIndex = (index * 2) + 1;
                    if (leftIndex >= _items.Count)
                    {
                        return;
                    }

                    int rightIndex = leftIndex + 1;
                    int smallestIndex = rightIndex < _items.Count
                        && Compare(_items[rightIndex], _items[leftIndex]) < 0
                        ? rightIndex
                        : leftIndex;
                    if (Compare(_items[smallestIndex], _items[index]) >= 0)
                    {
                        return;
                    }

                    Swap(index, smallestIndex);
                    index = smallestIndex;
                }
            }

            private void Swap(int first, int second)
            {
                DebrisEntry entry = _items[first];
                _items[first] = _items[second];
                _items[second] = entry;
                _items[first].HeapIndex = first;
                _items[second].HeapIndex = second;
            }

            private static int Compare(DebrisEntry left, DebrisEntry right)
            {
                int deadline = left.Deadline.CompareTo(right.Deadline);
                return deadline != 0
                    ? deadline
                    : left.InsertionOrder.CompareTo(right.InsertionOrder);
            }
        }

        /// <summary>One scheduler host callback this service has armed and not yet seen fire.</summary>
        private sealed class ArmedTimer
        {
            public ArmedTimer(ModScheduler scheduler, double deadline, long epoch)
            {
                Scheduler = scheduler;
                Deadline = deadline;
                Epoch = epoch;
            }

            public ModScheduler Scheduler { get; }

            public double Deadline { get; }

            public long Epoch { get; }
        }

        private readonly Dictionary<InstanceId, DebrisEntry> _byId = new();
        private readonly DeadlineHeap _deadlineHeap = new();
        private readonly LinkedList<DebrisEntry> _insertionOrder = new();
        private readonly List<ArmedTimer> _armedTimers = new();
        private ModScheduler _scheduler;
        private Action<string> _log;
        private InstanceRegistry _subscribedRegistry;
        private long _insertionSequence;
        private long _timerEpoch;

        internal RbxDebris(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Debris";
        }

        /// <summary>Live scheduled destroys; the 1,000-item cap is enforced against this count.</summary>
        internal int PendingCount => _byId.Count;

        /// <summary>Entries held by the deadline heap; equals <see cref="PendingCount"/>.</summary>
        internal int DeadlineHeapCount => _deadlineHeap.Count;

        /// <summary>Entries held by the insertion-order list; equals <see cref="PendingCount"/>.</summary>
        internal int InsertionQueueCount => _insertionOrder.Count;

        /// <summary>Scheduler host callbacks armed by this service that have not fired yet.</summary>
        internal int ArmedTimerCount => _armedTimers.Count;

        /// <summary>
        /// Schedules destruction of <paramref name="item"/> after <paramref name="lifetimeSeconds"/>
        /// scaled seconds. A NaN or infinite lifetime is refused; a negative lifetime is clamped to
        /// 0 (OURS — the mirror does not specify it) so the item is destroyed on the next frame.
        /// Re-adding an id replaces its deadline and caller in place but keeps its original
        /// insertion order for cap eviction (OURS). When the queue is full, the oldest entry by
        /// insertion order is destroyed immediately to make room.
        /// <para>
        /// Authorization runs at call time over the WHOLE subtree, exactly like
        /// <c>Instance:Destroy()</c> (the mirror: the item "is destroyed in the same manner"):
        /// every descendant must be destroyable by the caller. Services, cameras and the DataModel
        /// are refused in every world, ACL-versioned or not, and a Player is refused with a pointer
        /// to <c>Player:Kick()</c>, which runs the leave teardown. The stored caller is re-checked
        /// over the subtree under a server-generated mutation envelope when the timer fires; a
        /// refusal there (ownership changed since scheduling) drops the entry with one log line,
        /// leaving canonical state unchanged. An item that is already destroyed schedules nothing.
        /// </para>
        /// </summary>
        public void AddItem(RbxInstance item, double lifetimeSeconds, DebrisCaller caller)
        {
            if (item == null)
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem expects an Instance at argument 1",
                    "pass an Instance, e.g. Debris:AddItem(part, 10)");
            }

            if (double.IsNaN(lifetimeSeconds) || double.IsInfinity(lifetimeSeconds))
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem expects a finite lifetime at argument 2",
                    "pass a number of seconds, e.g. Debris:AddItem(part, 10)");
            }

            // WHY: the mirror pins neither bound; clamping keeps a programming slip (a subtraction
            // gone negative) a next-frame destroy instead of a scheduler-rejected or time-travelling
            // entry.
            double lifetime = lifetimeSeconds < 0d ? 0d : lifetimeSeconds;
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem cannot schedule: the Debris service is not attached to a world",
                    "resolve it via game:GetService(\"Debris\")");
            }

            if (_scheduler == null)
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem cannot schedule: the Debris service has no scheduler host",
                    "front the world with the scripted API composition so AddItem timers can fire");
            }

            WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted,
                caller.WorldId, item, WorldAclDecision.Destroy, "schedule Debris destruction");
            if (item.IsDestroyed)
            {
                return;
            }

            RefuseProtectedItem(item, true);
            foreach (RbxInstance descendant in item.GetDescendants())
            {
                RefuseProtectedItem(descendant, false);
                WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted,
                    caller.WorldId, descendant, WorldAclDecision.Destroy,
                    "schedule Debris destruction");
            }

            double deadline = _scheduler.CurrentTime + lifetime;
            if (_byId.TryGetValue(item.Id, out DebrisEntry existing))
            {
                existing.Deadline = deadline;
                existing.Caller = caller;
                _deadlineHeap.Update(existing);
                ArmForEarliestDeadline();
                return;
            }

            if (_byId.Count >= MaxItems)
            {
                EvictOldest();
                if (item.IsDestroyed)
                {
                    return;
                }
            }

            _insertionSequence++;
            DebrisEntry entry = new DebrisEntry(item.Id, _insertionSequence, deadline, caller);
            _byId.Add(item.Id, entry);
            _deadlineHeap.Add(entry);
            entry.InsertionNode = _insertionOrder.AddLast(entry);
            ArmForEarliestDeadline();
        }

        /// <summary>
        /// Attaches the ownerless host timer and the mod log sink; safe to call again (a snapshot
        /// restore replaces the service instance, and the next AddItem re-attaches through here).
        /// A different scheduler re-arms every pending item on the new one.
        /// </summary>
        internal void AttachHost(ModScheduler scheduler, Action<string> log)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException(nameof(scheduler));
            }

            InstanceRegistry registry = Registry;
            if (registry != null && !ReferenceEquals(_subscribedRegistry, registry))
            {
                DetachHost();
                registry.Unregistered += OnInstanceUnregistered;
                _subscribedRegistry = registry;
            }

            if (!ReferenceEquals(_scheduler, scheduler))
            {
                // WHY a new epoch rather than trusting the old callbacks: they live in a scheduler
                // this service no longer drives, and may never fire.
                _timerEpoch++;
                _armedTimers.Clear();
            }

            _scheduler = scheduler;
            _log = log;
            ArmForEarliestDeadline();
        }

        /// <summary>Attaches when the scheduler host is missing or replaced; otherwise a no-op.</summary>
        internal void EnsureHost(ModScheduler scheduler, Action<string> log)
        {
            if (_scheduler == null || !ReferenceEquals(_scheduler, scheduler)
                || _subscribedRegistry == null)
            {
                AttachHost(scheduler, log);
            }
        }

        /// <summary>Releases the registry subscription; pending timers keep their single attempt.</summary>
        internal void DetachHost()
        {
            if (_subscribedRegistry != null)
            {
                _subscribedRegistry.Unregistered -= OnInstanceUnregistered;
                _subscribedRegistry = null;
            }

            _scheduler = null;
        }

        /// <summary>
        /// Refuses the world-lifetime singletons and Players whatever the world's ACL mode, so a
        /// legacy (ACL-off) world cannot lose a shared service through the timer path either.
        /// </summary>
        /// <remarks>
        /// WHY a Camera only as the root: that is the rule <c>Instance:Destroy()</c> applies, and a
        /// script's own Camera inside a model it destroys is ordinary content; the world's camera
        /// cannot end up inside such a model because its Parent is locked.
        /// </remarks>
        private static void RefuseProtectedItem(RbxInstance item, bool isRoot)
        {
            if (item.IsService || item is RbxDataModel
                || (isRoot && string.Equals(item.ClassName, "Camera", StringComparison.Ordinal)))
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem cannot schedule the destruction of " + item.ClassName
                    + " \"" + item.GetFullName() + "\": it is a world-lifetime singleton",
                    "services, the DataModel and workspace.CurrentCamera live for the world's "
                    + "lifetime; schedule the parts or models you created instead");
            }

            if (item is RbxPlayer)
            {
                throw RbxError.BadArgument(
                    "Debris:AddItem cannot schedule the destruction of Player \""
                    + item.GetFullName() + "\": a Player leaves only through Player:Kick()",
                    "call player:Kick() instead; PlayerRemoving fires and the character is "
                    + "cleaned up");
            }
        }

        /// <summary>
        /// Guarantees that some armed callback fires no later than the earliest pending deadline,
        /// adding one only when every armed callback is due later than that.
        /// </summary>
        private void ArmForEarliestDeadline()
        {
            ModScheduler scheduler = _scheduler;
            if (scheduler == null || _deadlineHeap.Count == 0)
            {
                return;
            }

            double deadline = _deadlineHeap.Peek().Deadline;
            if (double.IsPositiveInfinity(deadline))
            {
                return;
            }

            double now = scheduler.CurrentTime;
            double target = deadline > now ? deadline : now;
            for (int index = 0; index < _armedTimers.Count; index++)
            {
                if (_armedTimers[index].Deadline <= target)
                {
                    return;
                }
            }

            double seconds = _armedTimers.Count >= MaxArmedTimers
                ? 0d
                : SecondsUntil(now, deadline);
            ArmedTimer timer = new ArmedTimer(scheduler, now + seconds, _timerEpoch);
            _armedTimers.Add(timer);
            scheduler.ScheduleHostCallback(seconds, () => OnTimer(timer));
        }

        /// <summary>
        /// Delay from <paramref name="now"/> whose scheduler deadline (<c>now + delay</c>, as the
        /// scheduler computes it) never lands after <paramref name="deadline"/>.
        /// </summary>
        /// <remarks>
        /// WHY the correction: <c>now + (deadline - now)</c> can round one ulp above the deadline,
        /// and a callback due one ulp late misses a frame whose clock lands exactly on it — the
        /// item would be destroyed a frame after its lifetime.
        /// </remarks>
        private static double SecondsUntil(double now, double deadline)
        {
            if (deadline <= now)
            {
                return 0d;
            }

            double seconds = deadline - now;
            for (int attempt = 0; attempt < 4 && now + seconds > deadline; attempt++)
            {
                seconds -= (now + seconds) - deadline;
            }

            return seconds > 0d && now + seconds <= deadline ? seconds : 0d;
        }

        private void OnTimer(ArmedTimer timer)
        {
            if (timer.Epoch != _timerEpoch)
            {
                return;
            }

            _armedTimers.Remove(timer);
            double now = timer.Scheduler.CurrentTime;
            Exception firstFault = null;
            while (_deadlineHeap.Count > 0)
            {
                DebrisEntry due = _deadlineHeap.Peek();
                if (due.Deadline > now)
                {
                    break;
                }

                RemoveEntry(due);
                try
                {
                    DestroyEntryTarget(due);
                }
                catch (Exception exception)
                {
                    // WHY contained per entry: one timer now drives every due item, so a fault on
                    // one must not strand the rest or skip the re-arm below.
                    if (firstFault == null)
                    {
                        firstFault = exception;
                    }
                    else
                    {
                        _log?.Invoke("[CoreAI.RbxApi] Debris destroy faulted: " + exception.Message);
                    }
                }
            }

            ArmForEarliestDeadline();
            if (firstFault != null)
            {
                // WHY rethrown: the scheduler reports a throwing host callback through HostFaulted,
                // which is where a host looks for it.
                ExceptionDispatchInfo.Capture(firstFault).Throw();
            }
        }

        private void EvictOldest()
        {
            LinkedListNode<DebrisEntry> oldest = _insertionOrder.First;
            if (oldest == null)
            {
                throw new InvalidOperationException(
                    "Debris eviction found no live entry for a full queue.");
            }

            DebrisEntry candidate = oldest.Value;
            RemoveEntry(candidate);
            DestroyEntryTarget(candidate);
        }

        private void DestroyEntryTarget(DebrisEntry entry)
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                return;
            }

            if (!registry.TryGet(entry.Id, out RbxInstance target) || target.IsDestroyed)
            {
                return;
            }

            DebrisCaller caller = entry.Caller;
            try
            {
                registry.ApplyServerGeneratedMutation(caller.ActorId, caller.IsUnrestricted,
                    caller.WorldId, "Debris destroy", () =>
                    {
                        RefuseProtectedItem(target, true);
                        registry.AuthorizeMutation(caller.ActorId, caller.IsUnrestricted,
                            caller.WorldId, target, WorldAclDecision.Destroy, "Debris destroy");
                        foreach (RbxInstance descendant in target.GetDescendants())
                        {
                            RefuseProtectedItem(descendant, false);
                            WorldAclAuthorizer.Demand(registry, caller.ActorId,
                                caller.IsUnrestricted, caller.WorldId, descendant,
                                WorldAclDecision.Destroy, "Debris destroy");
                        }

                        target.Destroy();
                        return 0;
                    });
            }
            catch (RbxError error)
            {
                // WHY: exactly one line per dropped destroy — the ownership change that caused it is
                // already named inside the refusal, so the canonical state stays untouched and the
                // queue simply moves on. A null sink (no host attached) drops silently.
                Action<string> log = _log;
                if (log != null)
                {
                    log("[CoreAI.RbxApi] Debris dropped the scheduled destroy of "
                        + target.ClassName + " '" + target.GetFullName() + "' for actor '"
                        + caller.ActorId + "': " + error.RawMessage);
                }
            }
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (record == null)
            {
                return;
            }

            if (_byId.TryGetValue(record.Id, out DebrisEntry entry))
            {
                RemoveEntry(entry);
            }
        }

        private void RemoveEntry(DebrisEntry entry)
        {
            _byId.Remove(entry.Id);
            _deadlineHeap.Remove(entry);
            if (entry.InsertionNode != null)
            {
                _insertionOrder.Remove(entry.InsertionNode);
                entry.InsertionNode = null;
            }
        }
    }
}
