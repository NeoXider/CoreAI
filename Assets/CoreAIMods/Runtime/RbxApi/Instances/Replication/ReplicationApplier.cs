using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// Where a replica reads the state a plan refers to: the node description that travelled with
    /// the batch, keyed by id. Plans carry names; this carries values.
    /// </summary>
    /// <remarks>
    /// WHY a separate seam: the applier is engine-free and format-free. The wire decoder of a later
    /// phase implements this over bytes; the in-process harness implements it over captured
    /// snapshots; the applier cannot tell which, and that is what makes it testable without either.
    /// </remarks>
    public interface IReplicationStateSource
    {
        /// <summary>The node state that travelled with the batch for this id, or null when none did.</summary>
        InstanceSnapshot Describe(InstanceId id);
    }

    /// <summary>What the applier did with a batch.</summary>
    public enum ReplicationApplyStatus
    {
        /// <summary>The batch was the expected one and every operation applied.</summary>
        Applied,

        /// <summary>The batch was older than expected; counted and dropped.</summary>
        Duplicate,

        /// <summary>The batch was newer than expected; a resync was requested.</summary>
        GapDetected,

        /// <summary>The batch referred to state the replica cannot honour; a resync was requested.</summary>
        ProtocolViolation,

        /// <summary>A resync is pending; the batch was dropped.</summary>
        AwaitingResync
    }

    /// <summary>The outcome of applying one batch.</summary>
    public sealed class ReplicationApplyResult
    {
        internal ReplicationApplyResult(ReplicationApplyStatus status, long sequence, string detail,
            int spawned, int patched, int removed, int ignoredRemovals)
        {
            Status = status;
            Sequence = sequence;
            Detail = detail ?? "";
            Spawned = spawned;
            Patched = patched;
            Removed = removed;
            IgnoredRemovals = ignoredRemovals;
        }

        public ReplicationApplyStatus Status { get; }

        public long Sequence { get; }

        /// <summary>Why the batch was not applied; empty when it was.</summary>
        public string Detail { get; }

        public int Spawned { get; }

        public int Patched { get; }

        public int Removed { get; }

        /// <summary>Removals for ids the replica never had; harmless, but counted.</summary>
        public int IgnoredRemovals { get; }

        public bool IsApplied => Status == ReplicationApplyStatus.Applied;

        public override string ToString()
        {
            return Status + " #" + Sequence + " (spawn " + Spawned + ", patch " + Patched + ", remove "
                   + Removed + ", ignored " + IgnoredRemovals + ")" + (Detail.Length == 0 ? "" : ": " + Detail);
        }
    }

    /// <summary>
    /// Consumes batch plans against a replica registry: spawns restore the instance at the server's
    /// id, patches go through the ordinary setters so <c>Changed</c> and
    /// <c>GetPropertyChangedSignal</c> fire the way a Roblox client sees them, removals destroy.
    /// </summary>
    /// <remarks>
    /// WHY it never guesses: the sequence tells it whether a batch is the next one, an old one or a
    /// future one. An old one is a duplicate and changes nothing; a future one means something was
    /// lost, and applying it would build a tree the server never had. A patch for an id the replica
    /// does not hold is the same fault seen from the other side — the only honest answer to either
    /// is to ask for the world again. A batch that throws while applying — a domain refusal or a
    /// value the restore path cannot parse — is that same answer: the replica marks itself in need
    /// of a resync and drops everything until it gets one, rather than letting the exception escape
    /// and leaving a half-applied batch behind a replica that still believes it is in sync.
    /// <para>
    /// WHY it keeps every replicated reference on a ledger: a reference names a server id, and the
    /// instance behind that id may be absent now (invisible to this recipient, or spawned in a
    /// later batch), leave later, or come back. Only remembering what the server said each
    /// reference wants — resolved or not — lets the replica read nil while the target is absent
    /// and the right instance the moment it is present, with no gap and no resync in between.
    /// </para>
    /// <para>
    /// WHY a replicated Player goes through the replica's <c>Players</c> service: a client script
    /// finds players through <c>Players:GetPlayers()</c> and <c>Players.LocalPlayer</c>, never by
    /// walking the tree, so a Player node restored without its identity and without the service
    /// knowing it is a player nobody can find — and its removal must leave the service as well.
    /// </para>
    /// </remarks>
    public sealed class ReplicationApplier : IDisposable
    {
        private readonly struct DeferredReference
        {
            public DeferredReference(RbxInstance instance, string member, ulong targetId)
            {
                Instance = instance;
                Member = member;
                TargetId = targetId;
            }

            public RbxInstance Instance { get; }

            public string Member { get; }

            public ulong TargetId { get; }
        }

        /// <summary>
        /// One reference-bearing member the applier keeps in step with the server: who holds it,
        /// which member, and the server id it wants — whether or not that id is present now.
        /// </summary>
        private sealed class ReferenceSlot
        {
            public ReferenceSlot(RbxInstance holder, string member, ulong targetId)
            {
                Holder = holder;
                Member = member;
                TargetId = targetId;
            }

            public RbxInstance Holder { get; }

            public string Member { get; }

            public ulong TargetId { get; }
        }

        /// <summary>The sequence a fresh replica expects first.</summary>
        public const long FirstSequence = 1L;

        // WHY the ledger cannot grow without bound: there is at most one slot per reference-bearing
        // member (ObjectValue.Value, Model.PrimaryPart, Player.Character) of a live replica
        // instance, so it is bounded by the replica's own size — never by how many batches arrived
        // or how many targets the recipient will never see. A slot is dropped when its holder is
        // unregistered (a Remove operation or a local destroy, both reaching OnUnregistered), and
        // overwritten when the server re-sends that member with nil or a different id. A target the
        // recipient never sees therefore costs exactly one slot for as long as its holder lives.
        private static readonly string[] ReferenceMembers =
        {
            ReplicationMembers.Value, ReplicationMembers.PrimaryPart, RbxPlayer.CharacterMember
        };

        private readonly InstanceRegistry _registry;
        private readonly Dictionary<(ulong HolderId, string Member), ReferenceSlot> _slots = new();
        private readonly Dictionary<ulong, List<ReferenceSlot>> _slotsByTarget = new();
        private readonly Dictionary<ulong, RbxPlayers> _admittedPlayers = new();
        private long _expectedSequence = FirstSequence;
        private bool _disposed;

        /// <summary>Creates the applier over one replica registry.</summary>
        public ReplicationApplier(InstanceRegistry replica)
        {
            _registry = replica ?? throw new ArgumentNullException(nameof(replica));
            if (replica.Authority != RegistryAuthority.Replica)
            {
                throw new ArgumentException(
                    "The applier writes what the server sent; it needs a replica registry, not an authoritative one.",
                    nameof(replica));
            }

            _registry.Unregistered += OnUnregistered;
        }

        /// <summary>The sequence the next batch must carry.</summary>
        public long ExpectedSequence => _expectedSequence;

        /// <summary>
        /// How many replicated references (ObjectValue.Value, Model.PrimaryPart, Player.Character)
        /// the applier is keeping in step with the server, resolved or not.
        /// </summary>
        public int TrackedReferenceCount => _slots.Count;

        /// <summary>Stops observing the replica; the applier holds nothing else.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Unregistered -= OnUnregistered;
        }

        /// <summary>How many older-than-expected batches were dropped.</summary>
        public int DuplicateCount { get; private set; }

        /// <summary>How many batches were dropped while a resync was pending.</summary>
        public int DroppedAwaitingResyncCount { get; private set; }

        /// <summary>True from the first gap or violation until <see cref="CompleteResync"/>.</summary>
        public bool NeedsResync { get; private set; }

        /// <summary>Why a resync is pending; empty otherwise.</summary>
        public string ResyncReason { get; private set; } = "";

        /// <summary>Raised once per fault that needs the server to send the world again.</summary>
        public event Action<string> ResyncRequested;

        /// <summary>Applies one batch, reading the values it refers to from <paramref name="state"/>.</summary>
        public ReplicationApplyResult Apply(ReplicationBatchPlan batch, IReplicationStateSource state)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (NeedsResync)
            {
                DroppedAwaitingResyncCount++;
                return new ReplicationApplyResult(ReplicationApplyStatus.AwaitingResync, batch.Sequence,
                    ResyncReason, 0, 0, 0, 0);
            }

            if (batch.Sequence < _expectedSequence)
            {
                DuplicateCount++;
                return new ReplicationApplyResult(ReplicationApplyStatus.Duplicate, batch.Sequence,
                    "batch " + batch.Sequence + " is older than the expected " + _expectedSequence,
                    0, 0, 0, 0);
            }

            if (batch.Sequence > _expectedSequence)
            {
                string gap = "batch " + batch.Sequence + " arrived while " + _expectedSequence
                             + " was expected; " + (batch.Sequence - _expectedSequence)
                             + " batch(es) are missing";
                RequestResync(gap);
                return new ReplicationApplyResult(ReplicationApplyStatus.GapDetected, batch.Sequence,
                    gap, 0, 0, 0, 0);
            }

            int spawned = 0;
            int patched = 0;
            int removed = 0;
            int ignored = 0;
            string violation = null;
            List<DeferredReference> deferred = new();
            List<InstanceId> arrivals = new();
            using (_registry.BeginReplicationApply())
            {
                try
                {
                    for (int index = 0; index < batch.Operations.Count && violation == null; index++)
                    {
                        ReplicationOperation operation = batch.Operations[index];
                        switch (operation.Kind)
                        {
                            case ReplicationOperationKind.Spawn:
                                violation = ApplySpawn(operation, state, deferred);
                                if (violation == null)
                                {
                                    spawned++;
                                    arrivals.Add(operation.InstanceId);
                                }

                                break;
                            case ReplicationOperationKind.Patch:
                                violation = ApplyPatch(operation, state, deferred);
                                if (violation == null)
                                {
                                    patched++;
                                }

                                break;
                            case ReplicationOperationKind.Remove:
                                if (ApplyRemove(operation))
                                {
                                    removed++;
                                }
                                else
                                {
                                    ignored++;
                                }

                                break;
                            default:
                                violation = "unknown operation kind " + operation.Kind;
                                break;
                        }
                    }

                    if (violation == null)
                    {
                        ResolveDeferred(deferred);
                        SettleArrivals(arrivals);
                    }
                }
                catch (RbxError error)
                {
                    // WHY a domain error is a protocol violation and not a crash: the value came from
                    // the server; if the replica cannot apply it, the two disagree about what is legal,
                    // and only a fresh snapshot restores agreement.
                    violation = "could not apply: " + error.Message;
                }
                catch (Exception exception)
                {
                    // WHY every other exception is the same violation: the restore path parses numbers
                    // out of snapshot strings, so a malformed or missing value surfaces as a
                    // FormatException, an OverflowException or an ArgumentNullException, and the
                    // applier cannot tell those apart from a bug in its own restore code by type —
                    // either way the replica now holds a state the server never had, and a resync is
                    // the only recovery for both. The full exception goes to Diagnostics so a
                    // programmer error stays loud in the log without taking the client down. What is
                    // deliberately left to escape: Apply's own argument checks and the apply scope's
                    // LIFO check, which run outside this block because they are the applier's contract
                    // with its caller, not the server's data.
                    violation = "could not apply (" + exception.GetType().Name + "): " + exception.Message;
                    _registry.Diagnostics?.Invoke("[CoreAI.RbxApi] replica batch " + batch.Sequence
                                                  + " threw while applying; treated as a protocol violation: "
                                                  + exception);
                }
            }

            if (violation != null)
            {
                string reason = "batch " + batch.Sequence + ": " + violation;
                RequestResync(reason);
                return new ReplicationApplyResult(ReplicationApplyStatus.ProtocolViolation,
                    batch.Sequence, reason, spawned, patched, removed, ignored);
            }

            _expectedSequence = checked(_expectedSequence + 1L);
            return new ReplicationApplyResult(ReplicationApplyStatus.Applied, batch.Sequence, "",
                spawned, patched, removed, ignored);
        }

        /// <summary>
        /// Declares the replica whole again after the resync path (a later phase) has rebuilt it;
        /// the next batch expected is the one after the snapshot's.
        /// </summary>
        public void CompleteResync(long nextExpectedSequence)
        {
            if (nextExpectedSequence < FirstSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(nextExpectedSequence), nextExpectedSequence,
                    "The next expected sequence must be at least " + FirstSequence + ".");
            }

            NeedsResync = false;
            ResyncReason = "";
            _expectedSequence = nextExpectedSequence;
        }

        private void RequestResync(string reason)
        {
            NeedsResync = true;
            ResyncReason = reason;
            _registry.Diagnostics?.Invoke("[CoreAI.RbxApi] replica requests resync: " + reason);
            ResyncRequested?.Invoke(reason);
        }

        private string ApplySpawn(ReplicationOperation operation, IReplicationStateSource state,
            List<DeferredReference> deferred)
        {
            InstanceId id = operation.InstanceId;
            if (_registry.TryGet(id, out _))
            {
                return "spawn for id " + id.Value + " which the replica already holds";
            }

            InstanceSnapshot node = state.Describe(id);
            string missing = CheckState(node, id, "spawn");
            if (missing != null)
            {
                return missing;
            }

            RbxInstance parent = null;
            if (node.ParentId != 0UL && !_registry.TryGet(new InstanceId(node.ParentId), out parent))
            {
                return "spawn for id " + id.Value + " names parent " + node.ParentId
                       + " which the replica does not hold";
            }

            RbxInstance instance = _registry.RestoreInstance(node.ClassName, id, node.OwnerModId,
                node.OriginTag, node.OwnerActorId, node.AccessScope);
            if (instance is RbxDataModel game)
            {
                // WHY: a replica has no bootstrap of its own; its roots are the server's, by id.
                _registry.SetSceneRoot(game);
            }
            else if (string.Equals(instance.ClassName, "Workspace", StringComparison.Ordinal))
            {
                _registry.SetWorldRoot(instance);
            }

            if (instance is RbxPlayer player)
            {
                string hollow = HydratePlayer(player, node, deferred);
                if (hollow != null)
                {
                    return hollow;
                }
            }

            string violation = ApplyMembers(instance, node, operation.Members, deferred, isSpawn: true);
            if (violation != null)
            {
                return violation;
            }

            ApplySpecializedState(instance, node);

            // WHY parent last: a ChildAdded handler on the replica must see a finished instance, not
            // one whose Name and attributes land after the handler already ran.
            instance.Parent = parent;
            if (instance is RbxPlayer admitted)
            {
                string refused = AdmitPlayer(admitted);
                if (refused != null)
                {
                    return refused;
                }
            }

            _registry.SetReplicatedRevision(id, operation.Revision);
            return null;
        }

        /// <summary>
        /// Gives a spawned Player the identity the server sent, before its members land, so its
        /// username, UserId and DisplayName are what the server's are; the character is a
        /// reference and takes the deferred path.
        /// </summary>
        private static string HydratePlayer(RbxPlayer player, InstanceSnapshot node,
            List<DeferredReference> deferred)
        {
            PlayerSnapshot identity = node.Player;
            if (identity == null)
            {
                return "spawn for Player id " + node.Id + " carried no Player identity";
            }

            if (string.IsNullOrWhiteSpace(identity.ActorId) || identity.UserId <= 0L)
            {
                return "spawn for Player id " + node.Id + " carried an unusable Player identity (actor '"
                       + identity.ActorId + "', UserId " + identity.UserId + ")";
            }

            if (node.Name == null)
            {
                return "spawn for Player id " + node.Id + " carried no Name to serve as the username";
            }

            player.Initialize(identity.ActorId, identity.UserId, node.Name, identity.DisplayName);
            deferred.Add(new DeferredReference(player, RbxPlayer.CharacterMember, identity.CharacterId));
            return null;
        }

        /// <summary>
        /// Registers a spawned Player with the <c>Players</c> service it sits under, so the
        /// service's lookups and <c>PlayerAdded</c> see it; after the parent is set, as the
        /// mirror's join order is the Player first, then its signal.
        /// </summary>
        private string AdmitPlayer(RbxPlayer player)
        {
            if (!(player.Parent is RbxPlayers service))
            {
                // WHY reported rather than refused: the mirror keeps every Player under Players, but
                // a tree in which the server put one elsewhere is the server's to explain; the replica
                // mirrors it faithfully, and the service simply does not list it.
                _registry.Diagnostics?.Invoke("[CoreAI.RbxApi] replicated Player '" + player.Name
                                              + "' (id " + player.Id.Value + ") is not under a Players "
                                              + "service, so Players:GetPlayers() will not list it");
                return null;
            }

            if (service.TryGetByActorId(player.NetworkActorId, out RbxPlayer existing)
                && !ReferenceEquals(existing, player))
            {
                return "spawn for Player id " + player.Id.Value + " names actor '" + player.NetworkActorId
                       + "' which the replica's Players already serves with id " + existing.Id.Value;
            }

            service.AdoptReplicated(player);
            _admittedPlayers[player.Id.Value] = service;
            return null;
        }

        private string ApplyPatch(ReplicationOperation operation, IReplicationStateSource state,
            List<DeferredReference> deferred)
        {
            InstanceId id = operation.InstanceId;
            if (!_registry.TryGet(id, out RbxInstance instance) || instance.IsDestroyed)
            {
                return "patch for id " + id.Value + " which the replica does not hold";
            }

            InstanceSnapshot node = state.Describe(id);
            string missing = CheckState(node, id, "patch");
            if (missing != null)
            {
                return missing;
            }

            string violation = ApplyMembers(instance, node, operation.Members, deferred, isSpawn: false);
            if (violation != null)
            {
                return violation;
            }

            _registry.SetReplicatedRevision(id, operation.Revision);
            return null;
        }

        private bool ApplyRemove(ReplicationOperation operation)
        {
            if (!_registry.TryGet(operation.InstanceId, out RbxInstance instance) || instance.IsDestroyed)
            {
                return false;
            }

            ReleasePlayers(instance);
            instance.Destroy();
            return true;
        }

        /// <summary>
        /// Takes every admitted Player in a subtree about to be destroyed out of its service first.
        /// </summary>
        /// <remarks>
        /// WHY before Destroy and not only from <see cref="OnUnregistered"/>: the mirror fires
        /// <c>PlayerRemoving</c> "right before a Player leaves", and a handler that reads the
        /// player's Parent or a child must still find them. The Unregistered path stays as the
        /// fallback for a destroy the applier did not perform.
        /// </remarks>
        private void ReleasePlayers(RbxInstance root)
        {
            if (_admittedPlayers.Count == 0)
            {
                return;
            }

            ReleasePlayer(root);
            if (root is RbxPlayer)
            {
                return;
            }

            IReadOnlyList<RbxInstance> descendants = root.GetDescendants();
            for (int index = 0; index < descendants.Count; index++)
            {
                ReleasePlayer(descendants[index]);
            }
        }

        private void ReleasePlayer(RbxInstance instance)
        {
            if (instance is RbxPlayer player
                && _admittedPlayers.Remove(player.Id.Value, out RbxPlayers service))
            {
                service.ReleaseReplicated(player);
            }
        }

        private static string CheckState(InstanceSnapshot node, InstanceId id, string kind)
        {
            if (node == null)
            {
                return kind + " for id " + id.Value + " carried no state";
            }

            if (node.Id != id.Value)
            {
                return kind + " for id " + id.Value + " carried state for id " + node.Id;
            }

            return null;
        }

        private string ApplyMembers(RbxInstance instance, InstanceSnapshot node,
            IReadOnlyList<string> members, List<DeferredReference> deferred, bool isSpawn)
        {
            for (int index = 0; index < members.Count; index++)
            {
                string member = members[index];
                if (string.Equals(member, ReplicationMembers.Name, StringComparison.Ordinal))
                {
                    if (node.Name == null)
                    {
                        return "state for id " + node.Id + " names member Name but carries none";
                    }

                    instance.Name = node.Name;
                }
                else if (string.Equals(member, ReplicationMembers.Archivable, StringComparison.Ordinal))
                {
                    instance.Archivable = node.Archivable;
                }
                else if (string.Equals(member, ReplicationMembers.Parent, StringComparison.Ordinal))
                {
                    if (isSpawn)
                    {
                        continue;
                    }

                    RbxInstance parent = null;
                    if (node.ParentId != 0UL
                        && !_registry.TryGet(new InstanceId(node.ParentId), out parent))
                    {
                        return "patch for id " + node.Id + " names parent " + node.ParentId
                               + " which the replica does not hold";
                    }

                    instance.Parent = parent;
                }
                else if (ReplicationMembers.TryGetAttribute(member, out string attribute))
                {
                    AttributeSnapshot carried = FindAttribute(node, attribute);
                    instance.SetAttribute(attribute,
                        carried == null ? null : InstanceTreeSerializer.FromAttributeSnapshot(carried));
                }
                else if (ReplicationMembers.TryGetTag(member, out string tag))
                {
                    if (node.Tags != null && node.Tags.Contains(tag))
                    {
                        instance.AddTag(tag);
                    }
                    else
                    {
                        instance.RemoveTag(tag);
                    }
                }
                else if (string.Equals(member, ReplicationMembers.Value, StringComparison.Ordinal))
                {
                    if (instance is RbxValueBase valueBase && node.Value != null)
                    {
                        ApplyValue(valueBase, node.Value, deferred);
                    }
                }
                else if (string.Equals(member, ReplicationMembers.PrimaryPart, StringComparison.Ordinal))
                {
                    if (instance is RbxModel model && node.Model != null)
                    {
                        deferred.Add(new DeferredReference(model, member, node.Model.PrimaryPartId));
                    }
                }
                else if (string.Equals(member, RbxPlayer.CharacterMember, StringComparison.Ordinal))
                {
                    if (instance is RbxPlayer player && node.Player != null)
                    {
                        deferred.Add(new DeferredReference(player, member, node.Player.CharacterId));
                    }
                }
                else if (string.Equals(member, RbxPlayer.DisplayNameMember, StringComparison.Ordinal))
                {
                    if (instance is RbxPlayer player && node.Player != null)
                    {
                        player.DisplayName = string.IsNullOrEmpty(node.Player.DisplayName)
                            ? player.Name
                            : node.Player.DisplayName;
                    }
                }
                else if (string.Equals(member, ReplicationMembers.WorldPivot, StringComparison.Ordinal))
                {
                    if (instance is RbxModel model && node.Model != null && node.Model.HasStoredWorldPivot)
                    {
                        model.SetWorldPivot(InstanceTreeSerializer.ParseCFrame(node.Model.StoredWorldPivot));
                    }
                }
                else if (!ReplicationMembers.IsStructural(member))
                {
                    // WHY reported and skipped rather than refused: the engine-free core applies the
                    // members it owns; a member it does not know belongs to a layer above (BasePart
                    // geometry lives with the Unity binder) and silence here would hide that layer's
                    // absence.
                    _registry.Diagnostics?.Invoke("[CoreAI.RbxApi] replication member '" + member
                                                  + "' on " + instance.ClassName + " (id " + node.Id
                                                  + ") is not applied by the engine-free core; skipped");
                }
            }

            return null;
        }

        private static AttributeSnapshot FindAttribute(InstanceSnapshot node, string attribute)
        {
            if (node.Attributes == null)
            {
                return null;
            }

            for (int index = 0; index < node.Attributes.Count; index++)
            {
                AttributeSnapshot candidate = node.Attributes[index];
                if (candidate != null && string.Equals(candidate.Name, attribute, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void ApplyValue(RbxValueBase valueBase, ValueSnapshot value,
            List<DeferredReference> deferred)
        {
            switch (valueBase)
            {
                case RbxIntValue intValue:
                    intValue.Value = long.Parse(value.StringValue, CultureInfo.InvariantCulture);
                    break;
                case RbxNumberValue numberValue:
                    numberValue.Value = double.Parse(value.StringValue, CultureInfo.InvariantCulture);
                    break;
                case RbxStringValue stringValue:
                    stringValue.Value = value.StringValue ?? string.Empty;
                    break;
                case RbxBoolValue boolValue:
                    boolValue.Value = string.Equals(value.StringValue, "true", StringComparison.Ordinal);
                    break;
                case RbxObjectValue objectValue:
                    deferred.Add(new DeferredReference(objectValue, ReplicationMembers.Value,
                        value.ObjectTargetId));
                    break;
                case RbxVector3Value vector3Value:
                {
                    float[] parts = InstanceTreeSerializer.Parse(value.StringValue, 3);
                    vector3Value.Value = new RbxVector3(parts[0], parts[1], parts[2]);
                    break;
                }
                case RbxCFrameValue cframeValue:
                    cframeValue.Value = InstanceTreeSerializer.ParseCFrame(value.StringValue);
                    break;
                case RbxColor3Value color3Value:
                {
                    float[] parts = InstanceTreeSerializer.Parse(value.StringValue, 3);
                    color3Value.Value = new RbxColor3(parts[0], parts[1], parts[2]);
                    break;
                }
                default:
                    throw RbxError.BadArgument(
                        "cannot apply a replicated value to class '" + valueBase.ClassName + "'",
                        "replicate only the eight value classes the serializer knows");
            }
        }

        private void ResolveDeferred(List<DeferredReference> deferred)
        {
            // WHY references resolve after the whole batch, and to nil when the target is absent:
            // the target may be spawned later in the same batch, or may be invisible to this
            // recipient — and Roblox replicates a reference to a non-replicated instance as nil.
            // The wanted id stays on the ledger either way, so an absent target is repaired the
            // moment it arrives (SettleArrivals) rather than left nil for good.
            for (int index = 0; index < deferred.Count; index++)
            {
                DeferredReference reference = deferred[index];
                if (reference.Instance.IsDestroyed)
                {
                    continue;
                }

                Remember(reference.Instance, reference.Member, reference.TargetId);
                WriteReference(reference.Instance, reference.Member, FindLive(reference.TargetId));
            }
        }

        /// <summary>
        /// Points every reference waiting for one of this batch's spawns at the instance that
        /// just arrived, after the batch's own references have been resolved so a reference the
        /// same batch rewrote is not repaired to the target it no longer wants.
        /// </summary>
        private void SettleArrivals(List<InstanceId> arrivals)
        {
            for (int index = 0; index < arrivals.Count; index++)
            {
                ulong id = arrivals[index].Value;
                if (!_slotsByTarget.TryGetValue(id, out List<ReferenceSlot> waiting))
                {
                    continue;
                }

                RbxInstance target = FindLive(id);
                if (target == null)
                {
                    continue;
                }

                // WHY a copy: a write below fires nothing synchronously today, but the list must
                // not be walked while a handler that wrote a reference could edit it.
                ReferenceSlot[] slots = waiting.ToArray();
                for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                {
                    ReferenceSlot slot = slots[slotIndex];
                    if (!slot.Holder.IsDestroyed
                        && !ReferenceEquals(ReadReference(slot.Holder, slot.Member), target))
                    {
                        WriteReference(slot.Holder, slot.Member, target);
                    }
                }
            }
        }

        /// <summary>
        /// Records what the server said this member wants; nil drops the slot, and a new id
        /// replaces whatever the member wanted before.
        /// </summary>
        private void Remember(RbxInstance holder, string member, ulong targetId)
        {
            (ulong, string) key = (holder.Id.Value, member);
            if (_slots.Remove(key, out ReferenceSlot previous))
            {
                Unindex(previous);
            }

            if (targetId == 0UL)
            {
                return;
            }

            ReferenceSlot slot = new(holder, member, targetId);
            _slots.Add(key, slot);
            if (!_slotsByTarget.TryGetValue(targetId, out List<ReferenceSlot> waiting))
            {
                waiting = new List<ReferenceSlot>();
                _slotsByTarget.Add(targetId, waiting);
            }

            waiting.Add(slot);
        }

        private void Unindex(ReferenceSlot slot)
        {
            if (_slotsByTarget.TryGetValue(slot.TargetId, out List<ReferenceSlot> waiting))
            {
                waiting.Remove(slot);
                if (waiting.Count == 0)
                {
                    _slotsByTarget.Remove(slot.TargetId);
                }
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            ulong id = record.Id.Value;
            for (int index = 0; index < ReferenceMembers.Length; index++)
            {
                if (_slots.Remove((id, ReferenceMembers[index]), out ReferenceSlot slot))
                {
                    Unindex(slot);
                }
            }

            if (_slotsByTarget.TryGetValue(id, out List<ReferenceSlot> waiting))
            {
                // WHY nil rather than a tombstone: on the replica a target that is gone has left
                // this recipient's sight, whatever became of it on the server, and ObjectValue.yaml
                // reads nil for a referenced object that is not streamed in. The slot stays, so the
                // reference follows the target back in if it returns under the same id.
                ReferenceSlot[] slots = waiting.ToArray();
                for (int index = 0; index < slots.Length; index++)
                {
                    ReferenceSlot slot = slots[index];
                    if (!slot.Holder.IsDestroyed && ReadReference(slot.Holder, slot.Member) != null)
                    {
                        WriteReference(slot.Holder, slot.Member, null);
                    }
                }
            }

            if (record.Instance is RbxPlayer player
                && _admittedPlayers.Remove(id, out RbxPlayers service))
            {
                service.ReleaseReplicated(player);
            }
        }

        private RbxInstance FindLive(ulong targetId)
        {
            return targetId != 0UL
                   && _registry.TryGet(new InstanceId(targetId), out RbxInstance target)
                   && !target.IsDestroyed
                ? target
                : null;
        }

        private static RbxInstance ReadReference(RbxInstance holder, string member)
        {
            switch (holder)
            {
                case RbxModel model when string.Equals(member, ReplicationMembers.PrimaryPart, StringComparison.Ordinal):
                    return model.PrimaryPart;
                case RbxObjectValue objectValue when string.Equals(member, ReplicationMembers.Value, StringComparison.Ordinal):
                    return objectValue.Value;
                case RbxPlayer player when string.Equals(member, RbxPlayer.CharacterMember, StringComparison.Ordinal):
                    return player.Character;
                default:
                    return null;
            }
        }

        private static void WriteReference(RbxInstance holder, string member, RbxInstance target)
        {
            switch (holder)
            {
                case RbxModel model when string.Equals(member, ReplicationMembers.PrimaryPart, StringComparison.Ordinal):
                    model.SetPrimaryPart(target);
                    break;
                case RbxObjectValue objectValue when string.Equals(member, ReplicationMembers.Value, StringComparison.Ordinal):
                    objectValue.Value = target;
                    break;
                case RbxPlayer player when string.Equals(member, RbxPlayer.CharacterMember, StringComparison.Ordinal):
                    player.ApplyReplicatedCharacter(target);
                    break;
            }
        }

        // WHY these arrive only with a spawn: their setters do not advance revisions today, so no
        // member is ever reported for them and no patch can carry them.
        // TODO: MVP-later — have the ClickDetector, MaterialVariant and Humanoid setters name their
        // members so a change after the spawn replicates too.
        private static void ApplySpecializedState(RbxInstance instance, InstanceSnapshot node)
        {
            if (node.ClickDetector != null && instance is RbxClickDetector clickDetector)
            {
                InstanceTreeSerializer.RestoreClickDetector(clickDetector, node.ClickDetector);
            }

            if (node.MaterialVariant != null && instance is RbxMaterialVariant materialVariant)
            {
                InstanceTreeSerializer.RestoreMaterialVariant(materialVariant, node.MaterialVariant);
            }

            if (node.Humanoid != null && instance is RbxHumanoid humanoid)
            {
                InstanceTreeSerializer.RestoreHumanoid(humanoid, node.Humanoid);
            }
        }
    }
}
