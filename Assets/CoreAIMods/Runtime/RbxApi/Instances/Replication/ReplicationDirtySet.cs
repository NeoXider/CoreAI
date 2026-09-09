using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>One instance's change, stamped with the revision it produced.</summary>
    public readonly struct ReplicationDelta
    {
        private static readonly string[] NoMembers = Array.Empty<string>();

        /// <summary>Records one change to the whole node, or a removal.</summary>
        public ReplicationDelta(InstanceId instanceId, long revision, bool removed)
            : this(instanceId, revision, removed, wholeNode: true, members: null)
        {
        }

        /// <summary>
        /// Records one change: the whole node, the named members, or both when one step produced
        /// marks of each kind.
        /// </summary>
        public ReplicationDelta(InstanceId instanceId, long revision, bool removed, bool wholeNode,
            IReadOnlyList<string> members)
        {
            InstanceId = instanceId;
            Revision = revision;
            Removed = removed;
            Members = removed || members == null ? NoMembers : members;
            IsWholeNode = !removed && (wholeNode || Members.Count == 0);
        }

        /// <summary>The instance that changed.</summary>
        public InstanceId InstanceId { get; }

        /// <summary>The revision after the change; a client applies deltas in this order.</summary>
        public long Revision { get; }

        /// <summary>True when the instance left the world rather than changing.</summary>
        public bool Removed { get; }

        /// <summary>
        /// The members named this step; empty on a removal. Next to <see cref="IsWholeNode"/> they
        /// still matter: an attribute set to nil or a tag removed is named here and nowhere else,
        /// because the instance no longer has it for a whole-node expansion to find.
        /// </summary>
        public IReadOnlyList<string> Members { get; }

        /// <summary>True when a change was recorded against the node as a whole, whatever else was named.</summary>
        public bool IsWholeNode { get; }
    }

    /// <summary>
    /// Collects what changed this step and hands each client only its own visible share. Fills
    /// itself from the registry: every <see cref="InstanceRegistry.RevisionAdvanced"/> is a change,
    /// every <see cref="InstanceRegistry.Unregistered"/> a removal. Safe to publish into from any
    /// thread; a step reads it from one.
    /// </summary>
    /// <remarks>
    /// WHY a set rather than a per-change send: a script that writes five properties in one frame
    /// produces one delta per instance, not five packets, and a part touched twice is sent once.
    /// Batching per step is also what makes the revision meaningful — it is the state after the
    /// step, not a point midway through one.
    /// <para>
    /// WHY it subscribes itself rather than waiting to be told: the only caller that ever told it
    /// was the intent gateway, and the gateway is not constructed in production — so nothing a
    /// server script wrote ever reached a client. The registry is the one place every write passes.
    /// </para>
    /// <para>
    /// WHY one leaf lock around the dictionary and nothing else: <see cref="InstanceRegistry.RevisionAdvanced"/>
    /// is raised after the mutation gate is released, so two mutations committed on two threads
    /// publish into this set at the same time, while <see cref="InstanceRegistry.Unregistered"/>
    /// reaches it with the gate still held by the destroying thread. The lock is therefore taken
    /// both with and without the gate, and it cannot deadlock against the gate because nothing that
    /// runs under it can take the gate: a mark touches the dictionary and one entry, a view copies
    /// the entries into an array, and <see cref="DeltasFor"/> asks the registry and the filter only
    /// after the lock is released. Lock order is always gate then set, never set then gate. An
    /// uncontended monitor is what every setter can afford; a lock-free structure would buy nothing
    /// on that path and cost the per-entry member list its simplicity.
    /// </para>
    /// <para>
    /// WHY <see cref="Clear"/> drops only what the step's first view captured: recipients are
    /// planned one after another while other threads keep publishing, so a mark can land after one
    /// recipient read the set and before the next did. Dropping everything at Clear would lose that
    /// mark for the first recipient with no error; keeping whatever is newer than the first view
    /// re-sends its instance once to a recipient that already has it, which the applier takes as a
    /// no-op. When nothing read the set this step, nothing was handed out and Clear drops it all.
    /// </para>
    /// </remarks>
    public sealed class ReplicationDirtySet : IDisposable
    {
        private sealed class Entry
        {
            public long Revision;
            public bool Removed;
            public bool WholeNode;
            public List<string> Members;

            /// <summary>The set's version when this entry last changed; <see cref="Clear"/> keeps what is newer than the step's first view.</summary>
            public long Stamp;
        }

        private const long Unread = -1L;

        private readonly object _sync = new();
        private readonly Dictionary<ulong, Entry> _dirty = new();
        private readonly InstanceRegistry _registry;
        private readonly GuardedReplicationFilter _filter;
        private ReplicationDelta[] _pendingView;
        private long _version;
        private long _viewVersion = Unread;
        private long _servedVersion = Unread;
        private bool _disposed;

        /// <summary>Creates a dirty set over one world; the filter is guarded whatever it is.</summary>
        public ReplicationDirtySet(InstanceRegistry registry, IReplicationFilter filter = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _filter = GuardedReplicationFilter.Wrap(filter, registry);
            UnobservedInstanceCount = registry.Count;
            _registry.RevisionAdvanced += OnRevisionAdvanced;
            _registry.Unregistered += OnUnregistered;
        }

        /// <summary>The world this set observes.</summary>
        public InstanceRegistry Registry => _registry;

        /// <summary>The guarded filter every recipient is served through.</summary>
        public IReplicationFilter Filter => _filter;

        /// <summary>
        /// How many instances the registry already held when this set began observing it. The
        /// registry parks nothing for a subscriber that is not there yet, so nothing that built those
        /// instances ever reaches this set; a recipient learns of them only through
        /// <see cref="ReplicationStream.PlanWorld"/>.
        /// </summary>
        public int UnobservedInstanceCount { get; }

        /// <summary>How many instances are waiting to be published.</summary>
        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _dirty.Count;
                }
            }
        }

        /// <summary>
        /// Every pending change, unfiltered; the per-recipient stream decides what each may see. The
        /// first read after a <see cref="Clear"/> fixes what that step was served.
        /// </summary>
        public IReadOnlyList<ReplicationDelta> Pending
        {
            get
            {
                lock (_sync)
                {
                    if (_servedVersion == Unread)
                    {
                        _servedVersion = _version;
                    }

                    if (_pendingView == null || _viewVersion != _version)
                    {
                        _pendingView = new ReplicationDelta[_dirty.Count];
                        int index = 0;
                        foreach (KeyValuePair<ulong, Entry> pair in _dirty)
                        {
                            Entry entry = pair.Value;
                            _pendingView[index++] = new ReplicationDelta(new InstanceId(pair.Key),
                                entry.Revision, entry.Removed, entry.WholeNode, entry.Members?.ToArray());
                        }

                        _viewVersion = _version;
                    }

                    return _pendingView;
                }
            }
        }

        /// <summary>Records that the whole instance changed, keeping the newest revision for it.</summary>
        public void MarkDirty(InstanceId id, long revision)
        {
            MarkDirty(id, revision, null);
        }

        /// <summary>
        /// Records that one member of an instance changed; null means the whole node, recorded
        /// alongside — never instead of — the members named before or after it in the same step.
        /// </summary>
        /// <remarks>
        /// WHY a whole-node mark keeps the named members: the whole node is expanded at plan time
        /// into the members the instance has THEN, and an attribute set to nil or a tag removed is
        /// not among them. Dropping the name here would leave the replica holding the old value with
        /// nothing ever sent to clear it — and every part-property write is a whole-node mark.
        /// </remarks>
        public void MarkDirty(InstanceId id, long revision, string member)
        {
            lock (_sync)
            {
                if (_dirty.TryGetValue(id.Value, out Entry entry))
                {
                    if (entry.Removed)
                    {
                        // WHY a removal is never downgraded to a change: destruction fires signals that
                        // themselves touch the instance, so a later change for the same id is ordinary —
                        // and letting it win would tell the client the thing it must delete merely changed.
                        return;
                    }

                    // WHY the newest revision wins regardless of arrival order: a subscriber that writes
                    // during a flush nests another flush, so the later revision can reach the set first.
                    if (revision > entry.Revision)
                    {
                        entry.Revision = revision;
                    }

                    if (member == null)
                    {
                        entry.WholeNode = true;
                    }
                    else
                    {
                        entry.Members ??= new List<string>();
                        if (!entry.Members.Contains(member))
                        {
                            entry.Members.Add(member);
                        }
                    }

                    entry.Stamp = ++_version;
                    return;
                }

                _dirty[id.Value] = new Entry
                {
                    Revision = revision,
                    WholeNode = member == null,
                    Members = member == null ? null : new List<string> { member },
                    Stamp = ++_version
                };
            }
        }

        /// <summary>Records that an instance left the world.</summary>
        /// <remarks>
        /// A removal always wins over a property change in the same step: a client told "it changed"
        /// and then never told "it is gone" would keep drawing something that no longer exists.
        /// </remarks>
        public void MarkRemoved(InstanceId id, long revision)
        {
            lock (_sync)
            {
                _dirty[id.Value] = new Entry { Revision = revision, Removed = true, Stamp = ++_version };
            }
        }

        /// <summary>
        /// Takes this step's deltas for one recipient without consuming them; the caller clears
        /// the set once every recipient has been served.
        /// </summary>
        /// <remarks>
        /// WHY visibility is decided per recipient at publish time: two clients in the same world do
        /// not see the same things, and computing one shared batch would mean either leaking to the
        /// narrower client or starving the wider one.
        /// </remarks>
        public IReadOnlyList<ReplicationDelta> DeltasFor(string recipientActorId)
        {
            List<ReplicationDelta> visible = new();
            IReadOnlyList<ReplicationDelta> pending = Pending;
            for (int index = 0; index < pending.Count; index++)
            {
                ReplicationDelta delta = pending[index];
                if (delta.Removed)
                {
                    // WHY a removal goes to everyone: the instance is already gone, so the filter has
                    // nothing left to inspect, and sending it to a client that never saw the instance
                    // is harmless — it removes nothing.
                    visible.Add(delta);
                    continue;
                }

                if (_registry.TryGet(delta.InstanceId, out RbxInstance instance)
                    && _filter.IsVisibleTo(recipientActorId, instance))
                {
                    visible.Add(delta);
                }
            }

            return visible;
        }

        /// <summary>
        /// Ends the step once every recipient has been served: drops what the step's first view
        /// captured and keeps every mark that landed after it for the next step.
        /// </summary>
        public void Clear()
        {
            lock (_sync)
            {
                long served = _servedVersion == Unread ? _version : _servedVersion;
                if (served == _version)
                {
                    _dirty.Clear();
                }
                else
                {
                    List<ulong> published = new();
                    foreach (KeyValuePair<ulong, Entry> pair in _dirty)
                    {
                        if (pair.Value.Stamp <= served)
                        {
                            published.Add(pair.Key);
                        }
                    }

                    for (int index = 0; index < published.Count; index++)
                    {
                        _dirty.Remove(published[index]);
                    }
                }

                _servedVersion = Unread;
                _viewVersion = Unread;
                _pendingView = null;
            }
        }

        /// <summary>Stops observing the registry.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.RevisionAdvanced -= OnRevisionAdvanced;
            _registry.Unregistered -= OnUnregistered;
        }

        private void OnRevisionAdvanced(InstanceId id, long revision, string member)
        {
            MarkDirty(id, revision, member);
        }

        private void OnUnregistered(InstanceRecord record)
        {
            MarkRemoved(record.Id, record.Revision);
        }
    }
}
