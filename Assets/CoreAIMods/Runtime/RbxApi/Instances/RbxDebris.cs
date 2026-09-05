using System;
using System.Collections.Generic;
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
    public sealed class RbxDebris : RbxInstance
    {
        /// <summary>Mirror default: AddItem lifetime when the script omits it.</summary>
        public const double DefaultLifetimeSeconds = 10d;

        /// <summary>Mirror hard cap: the oldest entry is destroyed instantly past this many.</summary>
        public const int MaxItems = 1000;

        private sealed class DebrisEntry
        {
            public DebrisEntry(InstanceId id, long generation, long insertionOrder,
                double deadline, DebrisCaller caller)
            {
                Id = id;
                Generation = generation;
                InsertionOrder = insertionOrder;
                Deadline = deadline;
                Caller = caller;
            }

            public InstanceId Id { get; }

            public long Generation { get; }

            public long InsertionOrder { get; }

            public double Deadline { get; }

            public DebrisCaller Caller { get; }
        }

        private sealed class DeadlineHeap
        {
            private readonly List<DebrisEntry> _items = new();

            public int Count => _items.Count;

            public void Add(DebrisEntry entry)
            {
                _items.Add(entry);
                int index = _items.Count - 1;
                while (index > 0)
                {
                    int parentIndex = (index - 1) / 2;
                    if (Compare(_items[index], _items[parentIndex]) >= 0)
                    {
                        return;
                    }

                    DebrisEntry entryToMove = _items[index];
                    _items[index] = _items[parentIndex];
                    _items[parentIndex] = entryToMove;
                    index = parentIndex;
                }
            }

            public DebrisEntry Peek()
            {
                return _items[0];
            }

            public DebrisEntry Pop()
            {
                DebrisEntry root = _items[0];
                int lastIndex = _items.Count - 1;
                DebrisEntry last = _items[lastIndex];
                _items.RemoveAt(lastIndex);
                if (_items.Count > 0)
                {
                    _items[0] = last;
                    int index = 0;
                    while (true)
                    {
                        int leftIndex = (index * 2) + 1;
                        if (leftIndex >= _items.Count)
                        {
                            return root;
                        }

                        int rightIndex = leftIndex + 1;
                        int smallestIndex = rightIndex < _items.Count
                            && Compare(_items[rightIndex], _items[leftIndex]) < 0
                            ? rightIndex
                            : leftIndex;
                        if (Compare(_items[smallestIndex], _items[index]) >= 0)
                        {
                            return root;
                        }

                        DebrisEntry entryToMove = _items[index];
                        _items[index] = _items[smallestIndex];
                        _items[smallestIndex] = entryToMove;
                        index = smallestIndex;
                    }
                }

                return root;
            }

            private static int Compare(DebrisEntry left, DebrisEntry right)
            {
                int deadline = left.Deadline.CompareTo(right.Deadline);
                return deadline != 0
                    ? deadline
                    : left.InsertionOrder.CompareTo(right.InsertionOrder);
            }
        }

        private readonly Dictionary<InstanceId, DebrisEntry> _byId = new();
        private readonly DeadlineHeap _deadlineHeap = new();
        private readonly Queue<DebrisEntry> _insertionOrder = new();
        private ModScheduler _scheduler;
        private Action<string> _log;
        private InstanceRegistry _subscribedRegistry;
        private long _insertionSequence;

        internal RbxDebris(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Debris";
        }

        /// <summary>Live scheduled destroys; the 1,000-item cap is enforced against this count.</summary>
        internal int PendingCount => _byId.Count;

        /// <summary>
        /// Schedules destruction of <paramref name="item"/> after <paramref name="lifetimeSeconds"/>
        /// scaled seconds. A NaN or infinite lifetime is refused; a negative lifetime is clamped to
        /// 0 (OURS — the mirror does not specify it) so the item is destroyed on the next frame.
        /// Re-adding an id replaces its deadline and caller but keeps its original insertion order
        /// for cap eviction (OURS). When the queue is full, the oldest entry by insertion order is
        /// destroyed immediately to make room. Authorization runs at call time; the stored caller is
        /// re-checked under a server-generated mutation envelope when the timer fires, and a refusal
        /// there (ownership changed since scheduling) drops the entry with one log line, leaving
        /// canonical state unchanged.
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

            double deadline = _scheduler.CurrentTime + lifetime;
            if (_byId.TryGetValue(item.Id, out DebrisEntry existing))
            {
                // WHY: a replacement node (rather than mutating in place) keeps the deadline heap
                // ordered in both directions; the old heap/fifo nodes go stale and are skipped by
                // reference check, and the old host callback drops on its generation mismatch.
                _insertionSequence++;
                DebrisEntry replacement = new DebrisEntry(item.Id, existing.Generation + 1,
                    existing.InsertionOrder, deadline, caller);
                _byId[item.Id] = replacement;
                _deadlineHeap.Add(replacement);
                _insertionOrder.Enqueue(replacement);
                ScheduleFire(replacement, lifetime);
                return;
            }

            if (_byId.Count >= MaxItems)
            {
                EvictOldest();
            }

            _insertionSequence++;
            DebrisEntry entry = new DebrisEntry(item.Id, 0, _insertionSequence, deadline, caller);
            _byId.Add(item.Id, entry);
            _deadlineHeap.Add(entry);
            _insertionOrder.Enqueue(entry);
            ScheduleFire(entry, lifetime);
        }

        /// <summary>
        /// Attaches the ownerless host timer and the mod log sink; safe to call again (a snapshot
        /// restore replaces the service instance, and the next AddItem re-attaches through here).
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

            _scheduler = scheduler;
            _log = log;
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

        private void ScheduleFire(DebrisEntry entry, double lifetime)
        {
            InstanceId id = entry.Id;
            long generation = entry.Generation;
            _scheduler.ScheduleHostCallback(lifetime, () => Fire(id, generation));
        }

        private void Fire(InstanceId id, long generation)
        {
            if (!_byId.TryGetValue(id, out DebrisEntry entry)
                || entry.Generation != generation)
            {
                return;
            }

            RemoveEntry(entry);
            DestroyEntryTarget(entry);
        }

        private void EvictOldest()
        {
            PruneHeap();
            while (_insertionOrder.Count > 0)
            {
                DebrisEntry candidate = _insertionOrder.Dequeue();
                if (_byId.TryGetValue(candidate.Id, out DebrisEntry live)
                    && ReferenceEquals(live, candidate))
                {
                    RemoveEntry(candidate);
                    DestroyEntryTarget(candidate);
                    return;
                }
            }

            throw new InvalidOperationException(
                "Debris eviction found no live entry for a full queue.");
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
                        registry.AuthorizeMutation(caller.ActorId, caller.IsUnrestricted,
                            caller.WorldId, target, WorldAclDecision.Destroy, "Debris destroy");
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
            PruneHeap();
        }

        private void PruneHeap()
        {
            while (_deadlineHeap.Count > 0)
            {
                DebrisEntry top = _deadlineHeap.Peek();
                if (_byId.TryGetValue(top.Id, out DebrisEntry live)
                    && ReferenceEquals(live, top))
                {
                    return;
                }

                _deadlineHeap.Pop();
            }
        }
    }
}
