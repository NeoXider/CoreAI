using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// Decides which instances a given client is allowed to know about at all.
    /// </summary>
    /// <remarks>
    /// WHY replication is filtered rather than broadcast: a client that receives the whole tree can
    /// read <c>ServerStorage</c>, every other player's private state and the answer to any puzzle in
    /// the world — with no exploit, just by looking at what it was sent. Filtering at the source is
    /// the only place that cannot be bypassed by a modified client.
    /// </remarks>
    public interface IReplicationFilter
    {
        /// <summary>Whether <paramref name="recipientActorId"/> may be told about this instance.</summary>
        bool IsVisibleTo(string recipientActorId, RbxInstance instance);
    }

    /// <summary>
    /// The default filter: the containers Roblox itself replicates, and nothing else.
    /// </summary>
    /// <remarks>
    /// Workspace, ReplicatedStorage and Lighting go out; ServerStorage and ServerScriptService never
    /// do — that is what the word "Server" in their names means, and a client that received them
    /// would hold the server's private content on its own disk.
    /// </remarks>
    public sealed class DefaultReplicationFilter : IReplicationFilter
    {
        /// <summary>Shared instance; the type holds no state.</summary>
        public static readonly DefaultReplicationFilter Instance = new();

        private static readonly string[] ReplicatedRoots =
        {
            "Workspace", "ReplicatedStorage", "Lighting"
        };

        private static readonly string[] NeverReplicatedRoots =
        {
            "ServerStorage", "ServerScriptService"
        };

        /// <inheritdoc />
        public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
        {
            if (instance == null || instance.IsDestroyed)
            {
                return false;
            }

            RbxInstance node = instance;
            while (node != null)
            {
                for (int index = 0; index < NeverReplicatedRoots.Length; index++)
                {
                    if (string.Equals(node.ClassName, NeverReplicatedRoots[index],
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                for (int index = 0; index < ReplicatedRoots.Length; index++)
                {
                    if (string.Equals(node.ClassName, ReplicatedRoots[index],
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                node = node.Parent;
            }

            // WHY the default is "no": an instance under a container nobody listed is one nobody
            // decided to share, and the safe reading of an undecided case is silence.
            return false;
        }
    }

    /// <summary>One instance's change, stamped with the revision it produced.</summary>
    public readonly struct ReplicationDelta
    {
        /// <summary>Records one change.</summary>
        public ReplicationDelta(InstanceId instanceId, long revision, bool removed)
        {
            InstanceId = instanceId;
            Revision = revision;
            Removed = removed;
        }

        /// <summary>The instance that changed.</summary>
        public InstanceId InstanceId { get; }

        /// <summary>The revision after the change; a client applies deltas in this order.</summary>
        public long Revision { get; }

        /// <summary>True when the instance left the world rather than changing.</summary>
        public bool Removed { get; }
    }

    /// <summary>
    /// Collects what changed this step and hands each client only its own visible share.
    /// </summary>
    /// <remarks>
    /// WHY a set rather than a per-change send: a script that writes five properties in one frame
    /// produces one delta per instance, not five packets, and a part touched twice is sent once.
    /// Batching per step is also what makes the revision meaningful — it is the state after the
    /// step, not a point midway through one.
    /// </remarks>
    public sealed class ReplicationDirtySet
    {
        private readonly Dictionary<ulong, ReplicationDelta> _dirty = new();
        private readonly InstanceRegistry _registry;
        private readonly IReplicationFilter _filter;

        /// <summary>Creates a dirty set over one world.</summary>
        public ReplicationDirtySet(InstanceRegistry registry, IReplicationFilter filter = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _filter = filter ?? DefaultReplicationFilter.Instance;
        }

        /// <summary>How many instances are waiting to be published.</summary>
        public int PendingCount => _dirty.Count;

        /// <summary>Records that an instance changed, keeping the newest revision for it.</summary>
        public void MarkDirty(InstanceId id, long revision)
        {
            if (_dirty.TryGetValue(id.Value, out ReplicationDelta existing)
                && (existing.Removed || existing.Revision >= revision))
            {
                // WHY a removal is never downgraded to a change: destruction fires signals that
                // themselves touch the instance, so a later MarkDirty for the same id is ordinary —
                // and letting it win would tell the client the thing it must delete merely changed.
                return;
            }

            _dirty[id.Value] = new ReplicationDelta(id, revision, removed: false);
        }

        /// <summary>Records that an instance left the world.</summary>
        /// <remarks>
        /// A removal always wins over a property change in the same step: a client told "it changed"
        /// and then never told "it is gone" would keep drawing something that no longer exists.
        /// </remarks>
        public void MarkRemoved(InstanceId id, long revision)
        {
            _dirty[id.Value] = new ReplicationDelta(id, revision, removed: true);
        }

        /// <summary>
        /// Takes this step's deltas for one recipient and, on the last recipient, clears the set.
        /// </summary>
        /// <remarks>
        /// WHY visibility is decided per recipient at publish time: two clients in the same world do
        /// not see the same things, and computing one shared batch would mean either leaking to the
        /// narrower client or starving the wider one.
        /// </remarks>
        public IReadOnlyList<ReplicationDelta> DeltasFor(string recipientActorId)
        {
            List<ReplicationDelta> visible = new();
            foreach (KeyValuePair<ulong, ReplicationDelta> pair in _dirty)
            {
                ReplicationDelta delta = pair.Value;
                if (delta.Removed)
                {
                    // A removal is sent to everyone who could previously see it; the instance is
                    // already gone, so the filter has nothing left to inspect. Sending it to a
                    // client that never saw the instance is harmless — it removes nothing.
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

        /// <summary>Clears the set after every recipient has been served.</summary>
        public void Clear()
        {
            _dirty.Clear();
        }
    }
}
