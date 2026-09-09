using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>What a replica must do with one instance.</summary>
    public enum ReplicationOperationKind
    {
        /// <summary>Create the instance at the server's id; the recipient did not have it.</summary>
        Spawn,

        /// <summary>Apply the named members to an instance the recipient already has.</summary>
        Patch,

        /// <summary>Destroy the instance; the recipient may already have lost it.</summary>
        Remove
    }

    /// <summary>One planned operation: an id, a kind and member names — never values.</summary>
    public sealed class ReplicationOperation
    {
        private static readonly string[] NoMembers = Array.Empty<string>();

        /// <summary>Creates one operation.</summary>
        public ReplicationOperation(ReplicationOperationKind kind, InstanceId instanceId,
            long revision, IReadOnlyList<string> members = null)
        {
            Kind = kind;
            InstanceId = instanceId;
            Revision = revision;
            Members = members ?? NoMembers;
        }

        public ReplicationOperationKind Kind { get; }

        public InstanceId InstanceId { get; }

        /// <summary>The server's revision of the instance after this step; zero on a removal.</summary>
        public long Revision { get; }

        /// <summary>
        /// For a spawn, the members the reader should serialize besides class and parent; for a
        /// patch, the members to apply — none when only the revision moved. Empty on a removal.
        /// </summary>
        public IReadOnlyList<string> Members { get; }

        public override string ToString()
        {
            return Kind + " " + InstanceId.Value + "@" + Revision
                   + (Members.Count == 0 ? "" : " [" + string.Join(",", Members) + "]");
        }
    }

    /// <summary>One recipient's batch for one step, in the order a replica must apply it.</summary>
    public sealed class ReplicationBatchPlan
    {
        /// <summary>Creates a plan.</summary>
        public ReplicationBatchPlan(string recipientActorId, long sequence,
            IReadOnlyList<ReplicationOperation> operations)
        {
            RecipientActorId = recipientActorId;
            Sequence = sequence;
            Operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        public string RecipientActorId { get; }

        /// <summary>Monotonic per recipient, starting at 1; the replica uses it to detect gaps.</summary>
        public long Sequence { get; }

        /// <summary>Spawns parent-first, then patches parent-first, then removals.</summary>
        public IReadOnlyList<ReplicationOperation> Operations { get; }
    }

    /// <summary>
    /// One recipient's view of the world as the server knows it: which ids it holds, the last batch
    /// sequence it was sent, and the planning that turns the dirty set into spawn, patch and remove
    /// operations for it.
    /// </summary>
    /// <remarks>
    /// WHY per recipient: an instance moving between another player's Backpack and Workspace is a
    /// spawn for one client and a patch for the other; only a per-recipient record of "what does this
    /// client already have" can tell them apart, and that record is the whole state a stream owns.
    /// <para>
    /// WHY the order spawn, patch, remove: a patch may re-parent under a node spawned in the same
    /// step, and a patch may move a child out of a subtree removed in the same step; either other
    /// order would ask the replica to reference something it does not have yet or has just destroyed.
    /// </para>
    /// </remarks>
    public sealed class ReplicationStream
    {
        /// <summary>An item pinned to its instance's depth in the tree the batch moves toward.</summary>
        private readonly struct TreeOrdered<T>
        {
            public TreeOrdered(T item, int depth, int order)
            {
                Item = item;
                Depth = depth;
                Order = order;
            }

            public T Item { get; }

            public int Depth { get; }

            public int Order { get; }

            public static int Compare(TreeOrdered<T> left, TreeOrdered<T> right)
            {
                return left.Depth != right.Depth
                    ? left.Depth.CompareTo(right.Depth)
                    : left.Order.CompareTo(right.Order);
            }
        }

        private readonly ReplicationDirtySet _dirty;
        private readonly InstanceRegistry _registry;
        private readonly IReplicationFilter _filter;
        private readonly HashSet<ulong> _known = new();
        private long _lastSequence;
        private bool _seeded;

        /// <summary>Creates the stream for one recipient over one dirty set.</summary>
        public ReplicationStream(ReplicationDirtySet dirty, string recipientActorId)
        {
            _dirty = dirty ?? throw new ArgumentNullException(nameof(dirty));
            if (string.IsNullOrWhiteSpace(recipientActorId))
            {
                throw new ArgumentException("Recipient actor id is required.", nameof(recipientActorId));
            }

            _registry = dirty.Registry;
            _filter = dirty.Filter;
            RecipientActorId = recipientActorId.Trim();
        }

        public string RecipientActorId { get; }

        /// <summary>The sequence of the last batch planned; zero before the first.</summary>
        public long LastSequence => _lastSequence;

        /// <summary>How many instances the recipient is known to hold.</summary>
        public int KnownCount => _known.Count;

        /// <summary>Whether the recipient is known to hold this instance.</summary>
        public bool Knows(InstanceId id)
        {
            return _known.Contains(id.Value);
        }

        /// <summary>
        /// Forgets everything the recipient held without telling it: the next <see cref="Plan"/>
        /// spawns whatever is visible and dirty, and <see cref="PlanWorld"/> spawns everything.
        /// </summary>
        public void ForgetAll()
        {
            _known.Clear();
        }

        /// <summary>
        /// Declares that the recipient holds an instance it received outside this stream; whoever
        /// sent it has taken over the seeding <see cref="PlanWorld"/> would otherwise do.
        /// </summary>
        public void MarkKnown(InstanceId id)
        {
            _known.Add(id.Value);
            _seeded = true;
        }

        /// <summary>
        /// Plans the recipient's whole visible world as one batch of spawns, read from the registry
        /// rather than the dirty set: the seed for a stream built over a world that already existed,
        /// and the answer to a replica that asked for the world again. Whatever the recipient was
        /// known to hold is forgotten first, so the batch stands on its own; the replica that receives
        /// it starts from empty. Returns null when the recipient may see nothing.
        /// </summary>
        public ReplicationBatchPlan PlanWorld()
        {
            _seeded = true;
            _known.Clear();
            List<TreeOrdered<RbxInstance>> spawns = new();
            HashSet<ulong> spawnIds = new();
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (IsSpawnable(live[index]))
                {
                    AddSpawnCandidate(live[index], spawns, spawnIds);
                }
            }

            return spawns.Count == 0
                ? null
                : Commit(spawns, new List<TreeOrdered<ReplicationOperation>>(), new List<InstanceId>());
        }

        /// <summary>
        /// Plans this step's batch from the dirty set, or returns null when nothing the recipient may
        /// see changed. Planning commits: spawned ids are known from now on, removed ids are
        /// forgotten, and the sequence advances. The dirty set is left for the other recipients.
        /// </summary>
        /// <remarks>
        /// WHY the first plan refuses when the world predates the dirty set: the set never saw those
        /// instances, so every plan from it would send the recipient an empty world with no error —
        /// the natural composition order (bootstrap, then replication) hits exactly this. The check
        /// is here and not in the constructor because <see cref="PlanWorld"/>, the cure, needs a
        /// constructed stream. It throws rather than reporting through the registry's diagnostics
        /// because that sink is optional, and a guard that is silent when nobody wired the sink is the
        /// defect it guards against; the condition is a composition error no later step can repair.
        /// </remarks>
        public ReplicationBatchPlan Plan()
        {
            if (!_seeded && _dirty.UnobservedInstanceCount > 0)
            {
                throw new InvalidOperationException(
                    "Replication stream for '" + RecipientActorId + "' cannot plan from its dirty set: the "
                    + "registry already held " + _dirty.UnobservedInstanceCount + " instance(s) when the set "
                    + "began observing, and nothing that built them reached it. Build the dirty set before "
                    + "the world, or seed the recipient with PlanWorld() first.");
            }

            IReadOnlyList<ReplicationDelta> pending = _dirty.Pending;
            if (pending.Count == 0)
            {
                return null;
            }

            List<TreeOrdered<RbxInstance>> spawns = new();
            HashSet<ulong> spawnIds = new();
            List<TreeOrdered<ReplicationOperation>> patches = new();
            List<InstanceId> removes = new();
            HashSet<ulong> removeIds = new();

            for (int index = 0; index < pending.Count; index++)
            {
                ReplicationDelta delta = pending[index];
                if (delta.Removed
                    || !_registry.TryGet(delta.InstanceId, out RbxInstance instance)
                    || instance.IsDestroyed)
                {
                    Forget(delta.InstanceId, removes, removeIds);
                    continue;
                }

                if (!_filter.IsVisibleTo(RecipientActorId, instance))
                {
                    if (_known.Contains(instance.Id.Value))
                    {
                        ForgetSubtree(instance, removes, removeIds);
                    }

                    continue;
                }

                if (_known.Contains(instance.Id.Value))
                {
                    // WHY a patch may carry no members: a child came or went, so only the revision
                    // moved; the child's own operation carries the change, and the recipient still
                    // needs the parent's revision for the intents it will submit against it.
                    patches.Add(new TreeOrdered<ReplicationOperation>(
                        new ReplicationOperation(ReplicationOperationKind.Patch, instance.Id,
                            RevisionOf(instance, delta.Revision), VisibleMembers(instance, delta)),
                        DepthOf(instance), patches.Count));
                    continue;
                }

                CollectSpawns(instance, spawns, spawnIds);
            }

            if (spawns.Count == 0 && patches.Count == 0 && removes.Count == 0)
            {
                return null;
            }

            return Commit(spawns, patches, removes);
        }

        private ReplicationBatchPlan Commit(List<TreeOrdered<RbxInstance>> spawns,
            List<TreeOrdered<ReplicationOperation>> patches, List<InstanceId> removes)
        {
            // WHY sorted by depth rather than trusting dictionary order: two separately dirty nodes
            // in one subtree can arrive child-first, and a spawn whose parent the replica does not
            // hold yet is exactly the case the planner exists to rule out.
            spawns.Sort(TreeOrdered<RbxInstance>.Compare);

            // WHY patches are sorted by the depth the batch moves each instance TO, parent-first,
            // rather than left in dirty order: dirty order is insertion order, so an unrelated
            // earlier write on a parent puts its patch ahead of its child's. Parent is the only
            // member that can make a patch illegal, and it is illegal exactly when the replica still
            // holds the new parent below the instance being moved. Once everything shallower in the
            // target tree is in place — moved earlier in this list, spawned above, or never moved —
            // the new parent's ancestor chain on the replica is already the target chain, which
            // cannot pass through a node below it; so the move can neither close a cycle nor name a
            // parent the replica lacks. Ties keep dirty order, and depth is read from the server's
            // tree now, which is the tree the batch moves toward.
            patches.Sort(TreeOrdered<ReplicationOperation>.Compare);

            List<ReplicationOperation> operations = new(spawns.Count + patches.Count + removes.Count);
            for (int index = 0; index < spawns.Count; index++)
            {
                RbxInstance instance = spawns[index].Item;
                _known.Add(instance.Id.Value);
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Spawn, instance.Id,
                    RevisionOf(instance, 0L), VisibleMembers(instance, includeParent: false)));
            }

            for (int index = 0; index < patches.Count; index++)
            {
                operations.Add(patches[index].Item);
            }

            for (int index = 0; index < removes.Count; index++)
            {
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Remove,
                    removes[index], 0L));
            }

            _lastSequence = checked(_lastSequence + 1L);
            return new ReplicationBatchPlan(RecipientActorId, _lastSequence, operations);
        }

        private void Forget(InstanceId id, List<InstanceId> removes, HashSet<ulong> removeIds)
        {
            if (_known.Remove(id.Value) && removeIds.Add(id.Value))
            {
                removes.Add(id);
            }
        }

        /// <summary>
        /// Forgets a root that left visibility and every known descendant it has NOW, each with its
        /// own removal.
        /// </summary>
        /// <remarks>
        /// WHY each descendant gets a removal rather than being dropped from the known set: this walk
        /// is over the server's tree after the step, the replica holds the tree before it, and the two
        /// disagree exactly when something moved. A part re-parented under this root in the same step
        /// still sits elsewhere on the replica; forgetting it silently would leave it there for good
        /// and make its return a spawn for an id the replica already holds. A removal for a descendant
        /// the replica destroyed with the root is ignored by the applier, so the extra operation is
        /// safe where it is redundant and decisive where it is not.
        /// </remarks>
        private void ForgetSubtree(RbxInstance root, List<InstanceId> removes, HashSet<ulong> removeIds)
        {
            Forget(root.Id, removes, removeIds);
            IReadOnlyList<RbxInstance> descendants = root.GetDescendants();
            for (int index = 0; index < descendants.Count; index++)
            {
                Forget(descendants[index].Id, removes, removeIds);
            }
        }

        private void CollectSpawns(RbxInstance instance, List<TreeOrdered<RbxInstance>> spawns,
            HashSet<ulong> spawnIds)
        {
            if (!IsSpawnable(instance))
            {
                return;
            }

            AddSpawnCandidate(instance, spawns, spawnIds);
            IReadOnlyList<RbxInstance> children = instance.GetChildren();
            for (int index = 0; index < children.Count; index++)
            {
                CollectSpawns(children[index], spawns, spawnIds);
            }
        }

        private void AddSpawnCandidate(RbxInstance instance, List<TreeOrdered<RbxInstance>> spawns,
            HashSet<ulong> spawnIds)
        {
            if (!_known.Contains(instance.Id.Value) && spawnIds.Add(instance.Id.Value))
            {
                spawns.Add(new TreeOrdered<RbxInstance>(instance, DepthOf(instance), spawns.Count));
            }
        }

        private bool IsSpawnable(RbxInstance instance)
        {
            return !instance.IsDestroyed && _filter.IsVisibleTo(RecipientActorId, instance);
        }

        private List<string> VisibleMembers(RbxInstance instance, ReplicationDelta delta)
        {
            // WHY the union and not one or the other: the whole-node expansion names what the
            // instance has now, the named marks name what changed — and only the latter can name
            // an attribute set to nil or a tag removed, which the replica must still be told about.
            // TODO: MVP-later — a whole-node mark (LuaCsRbxInstanceBindings.RecordMutation on every
            // part-property write, LuaCsTweenPropertyHost on every tick) loses WHICH member changed,
            // so this expansion can only name what the engine-free core enumerates; a Unity-layer
            // applier for BasePart geometry cannot hang off it until those call sites name their member.
            List<string> members = delta.IsWholeNode
                ? VisibleMembers(instance, includeParent: true)
                : new List<string>(delta.Members.Count);
            for (int index = 0; index < delta.Members.Count; index++)
            {
                string member = delta.Members[index];
                if (!ReplicationMembers.IsStructural(member)
                    && !members.Contains(member)
                    && _filter.IsMemberVisibleTo(RecipientActorId, instance, member))
                {
                    members.Add(member);
                }
            }

            return members;
        }

        private List<string> VisibleMembers(RbxInstance instance, bool includeParent)
        {
            List<string> candidates = ReplicationMembers.EnumerateReplicable(instance, includeParent);
            List<string> members = new(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                if (_filter.IsMemberVisibleTo(RecipientActorId, instance, candidates[index]))
                {
                    members.Add(candidates[index]);
                }
            }

            return members;
        }

        private long RevisionOf(RbxInstance instance, long fallback)
        {
            return _registry.TryGetRecord(instance.Id, out InstanceRecord record)
                ? record.Revision
                : fallback;
        }

        private static int DepthOf(RbxInstance instance)
        {
            int depth = 0;
            for (RbxInstance node = instance.Parent; node != null; node = node.Parent)
            {
                depth++;
            }

            return depth;
        }
    }
}
