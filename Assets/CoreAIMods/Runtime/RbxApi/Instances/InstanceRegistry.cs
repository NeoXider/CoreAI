using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Runtime bridge that can lazily wrap a host world object into this registry.</summary>
    public interface IWorldInstanceAdapter
    {
        bool TryWrap(InstanceRegistry registry, string worldName, out RbxInstance instance);
    }

    /// <summary>
    /// Single owner of instance identity (roadmap §3.3): allocates ids, holds one
    /// <see cref="InstanceRecord"/> per live instance, reconciles the three identity spaces
    /// (InstanceId / Mirror netId / CoreAI world name), and drives the backing-object binder
    /// per D5. Instances are created only through this registry so no instance ever exists
    /// without a record.
    /// WHY: single-threaded, main-thread-only by invariant — Lua executes on the main thread and
    /// the registry's dictionaries are unsynchronized; only InstanceIdAllocator locks for ids that
    /// may be minted off-thread.
    /// </summary>
    public sealed class InstanceRegistry
    {
        private readonly struct MutationOperationKey : IEquatable<MutationOperationKey>
        {
            public MutationOperationKey(string actorId, string operationId)
            {
                ActorId = actorId;
                OperationId = operationId;
            }

            private string ActorId { get; }

            private string OperationId { get; }

            public bool Equals(MutationOperationKey other)
            {
                return string.Equals(ActorId, other.ActorId, StringComparison.Ordinal)
                       && string.Equals(OperationId, other.OperationId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is MutationOperationKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int actorHash = StringComparer.Ordinal.GetHashCode(ActorId);
                    int operationHash = StringComparer.Ordinal.GetHashCode(OperationId);
                    return (actorHash * 397) ^ operationHash;
                }
            }
        }

        private sealed class MutationOperationRecord
        {
            public MutationOperationRecord(MutationEnvelope envelope, Type resultType, object result)
            {
                Envelope = envelope;
                ResultType = resultType;
                Result = result;
            }

            public MutationEnvelope Envelope { get; }

            public Type ResultType { get; }

            public object Result { get; }
        }

        public const int CurrentWorldAclVersion = 1;

        /// <summary>Maximum completed mutation results retained for each durable actor.</summary>
        public const int DefaultMutationReplayCapacityPerActor = 64;

        private readonly Dictionary<InstanceId, InstanceRecord> _byId = new();
        private readonly Dictionary<uint, InstanceRecord> _byNetId = new();
        private readonly Dictionary<string, InstanceRecord> _byWorldName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _actorByOwnerModId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _actorByOriginTag = new(StringComparer.Ordinal);
        private readonly Dictionary<MutationOperationKey, MutationOperationRecord>
            _mutationOperations = new();
        private readonly Dictionary<string, Queue<MutationOperationKey>>
            _mutationOperationOrderByActor = new(StringComparer.Ordinal);
        private readonly object _mutationGate = new();
        private readonly IInstanceBackingBinder _binder;
        private readonly IWorldInstanceAdapter _worldInstanceAdapter;
        private readonly int _mutationReplayCapacityPerActor;

        private RbxInstance _worldRoot;
        private RbxInstance _sceneRoot;
        private MutationEnvelopeScope _mutationEnvelopeScope;

        public InstanceRegistry(ClassCatalog catalog = null, IInstanceBackingBinder binder = null,
            InstanceIdAllocator allocator = null, int? worldAclVersion = null, string worldId = "",
            IWorldInstanceAdapter worldInstanceAdapter = null,
            int mutationReplayCapacityPerActor = DefaultMutationReplayCapacityPerActor)
        {
            if (worldAclVersion.HasValue && worldAclVersion.Value != CurrentWorldAclVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(worldAclVersion), worldAclVersion,
                    "Unsupported world ACL version.");
            }

            if (mutationReplayCapacityPerActor <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mutationReplayCapacityPerActor),
                    mutationReplayCapacityPerActor,
                    "Mutation replay capacity per actor must be positive.");
            }

            Catalog = catalog ?? ClassCatalog.CreateMvp1();
            Allocator = allocator ?? new InstanceIdAllocator();
            Tags = new InstanceTagStore();
            _binder = binder ?? NullInstanceBackingBinder.Instance;
            _worldInstanceAdapter = worldInstanceAdapter;
            _mutationReplayCapacityPerActor = mutationReplayCapacityPerActor;
            WorldAclVersion = worldAclVersion;
            WorldId = worldId?.Trim() ?? "";
        }

        /// <summary>
        /// Optional sink for composition faults this registry detects but must not throw on. Engine-free
        /// by design (a delegate, not a Unity logger); the Unity host wires it to the console.
        /// WHY: these failures are otherwise SILENT — an instance tree that diverges from the registry
        /// backing it yields no exception and no GameObject, which is indistinguishable from "the script
        /// did nothing".
        /// </summary>
        public Action<string> Diagnostics { get; set; }

        public ClassCatalog Catalog { get; }

        public InstanceIdAllocator Allocator { get; }

        /// <summary>Persisted world ACL schema; null means legacy compatibility mode.</summary>
        public int? WorldAclVersion { get; private set; }

        /// <summary>Whether actor-level strict authorization is active for this world.</summary>
        public bool IsWorldAclEnabled => WorldAclVersion.HasValue;

        /// <summary>
        /// Selects the ACL schema for a world created by composition. Null preserves legacy
        /// compatibility; a non-null value enables that schema.
        /// </summary>
        public void ConfigureWorldAclVersion(int? worldAclVersion)
        {
            if (worldAclVersion.HasValue
                && worldAclVersion.Value != CurrentWorldAclVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(worldAclVersion), worldAclVersion,
                    "Unsupported world ACL version.");
            }

            if (WorldAclVersion.HasValue && WorldAclVersion != worldAclVersion)
            {
                throw new InvalidOperationException(
                    "A world ACL version cannot be changed after it has been enabled.");
            }

            WorldAclVersion = worldAclVersion;
        }

        /// <summary>Stable world identity used to reject cross-world actor contexts when specified.</summary>
        public string WorldId { get; }

        /// <summary>CollectionService substrate; instances delegate their tag members here.</summary>
        public InstanceTagStore Tags { get; }

        /// <summary>The Workspace instance — the physical-world handle callers parent visible
        /// content to. Distinct from the materialization boundary (<see cref="_sceneRoot"/>):
        /// the whole DataModel tree materializes, but only Workspace content is the active world.</summary>
        public RbxInstance WorldRoot => _worldRoot;

        /// <summary>All live records, including host objects and runtime infrastructure.</summary>
        public int Count => _byId.Count;

        /// <summary>Live user/mod-authored records, excluding host objects and runtime infrastructure.</summary>
        public int AuthoredCount
        {
            get
            {
                int count = 0;
                foreach (InstanceRecord record in _byId.Values)
                {
                    if (record.IsAuthoredContent)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// True once the world this registry backs was torn down with its host. Scripts keep a direct
        /// reference to this object (the Lua bindings capture it at install time), so the registry
        /// outlives the host and every later creation would land in a world with no scene behind it.
        /// </summary>
        public bool IsDetached { get; private set; }

        /// <summary>
        /// Declares the backing world gone, so scripted creation fails with a named error instead of
        /// quietly building instances nothing will ever render.
        /// </summary>
        public void MarkDetached()
        {
            lock (_mutationGate)
            {
                IsDetached = true;
                _mutationOperations.Clear();
                _mutationOperationOrderByActor.Clear();
            }
        }

        /// <summary>Number of idempotency results retained across all actors in the live world.</summary>
        public int RetainedMutationOperationCount
        {
            get
            {
                lock (_mutationGate)
                {
                    return _mutationOperations.Count;
                }
            }
        }

        public event Action<InstanceRecord> Registered;

        public event Action<InstanceRecord> Unregistered;

        /// <summary>
        /// Tag transition on one instance: the instance, the tag, and whether this add made the
        /// tag used anywhere for the first time. CollectionService layers TagAdded and the
        /// per-tag added signals here; raised only when the tag was newly applied.
        /// </summary>
        public event Action<RbxInstance, string, bool> TagAdded;

        /// <summary>
        /// Tag transition on one instance: the instance, the tag, and whether this removal left
        /// the tag used nowhere. CollectionService layers TagRemoved and the per-tag removed
        /// signals here; raised only when the tag was actually held.
        /// </summary>
        public event Action<RbxInstance, string, bool> TagRemoved;

        /// <summary>
        /// A reparented subtree root crossed the scene boundary: the moved root and whether it
        /// is in the scene now. CollectionService fires per-tag added/removed signals for the
        /// tagged nodes of the moved subtree here.
        /// </summary>
        internal event Action<RbxInstance, bool> SceneMembershipChanged;

        /// <summary>Raises <see cref="TagAdded"/> for a newly applied tag.</summary>
        internal void OnTagAdded(RbxInstance instance, string tag, bool isFirstPlaceUse)
        {
            TagAdded?.Invoke(instance, tag, isFirstPlaceUse);
        }

        /// <summary>Raises <see cref="TagRemoved"/> for a tag that was actually held.</summary>
        internal void OnTagRemoved(RbxInstance instance, string tag, bool isLastPlaceUse)
        {
            TagRemoved?.Invoke(instance, tag, isLastPlaceUse);
        }

        /// <summary>
        /// Serializes one enveloped mutation, rejects stale observations, and returns the first
        /// completed result when the durable actor retries the same operation id while it remains
        /// in that actor's FIFO retention window. Once evicted, the result is unavailable and the
        /// request is evaluated against its original expected revision as a first-seen request. A
        /// state-changing replay is therefore rejected as stale; a true no-op whose target revision
        /// never advanced may execute again.
        /// </summary>
        public T ApplyMutation<T>(MutationEnvelope envelope, Func<T> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            lock (_mutationGate)
            {
                if (IsDetached)
                {
                    throw RbxError.WorldDetached(
                        "actor '" + envelope.ActorId + "' operation '"
                        + envelope.OperationId + "'");
                }

                MutationOperationKey key = new(envelope.ActorId, envelope.OperationId);
                if (_mutationOperations.TryGetValue(
                        key, out MutationOperationRecord completed))
                {
                    EnsureReplayMatches(envelope, completed);
                    if (completed.ResultType != typeof(T))
                    {
                        throw MutationDenied(envelope,
                            "the operation id was already completed with a different result type");
                    }

                    return completed.Result == null ? default : (T)completed.Result;
                }

                if (!_byId.TryGetValue(
                        envelope.TargetInstanceId, out InstanceRecord targetRecord))
                {
                    throw MutationDenied(envelope,
                        "the target has no live instance record");
                }

                if (targetRecord.Revision != envelope.ExpectedRevision)
                {
                    throw MutationDenied(envelope,
                        "stale expected revision " + envelope.ExpectedRevision
                        + "; current revision is " + targetRecord.Revision);
                }

                T result = operation();
                _mutationOperations.Add(
                    key, new MutationOperationRecord(envelope, typeof(T), result));
                RetainMutationOperation(envelope.ActorId, key);
                return result;
            }
        }

        /// <summary>Runs one production entry batch under a server-generated ambient envelope.</summary>
        public T ApplyServerGeneratedMutation<T>(string actorId, bool actorIsUnrestricted,
            string actorWorldId, string operation, Func<T> mutation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }

            string actor = string.IsNullOrWhiteSpace(actorId)
                ? throw RbxError.BadArgument(
                    "actor id is required for a mutation envelope",
                    "use the durable actor id from the trusted identity provider")
                : actorId.Trim();
            if (HasActiveMutationEnvelope(actor))
            {
                return mutation();
            }

            InstanceRecord anchor = ResolveMutationAnchor(actor, operation);
            MutationEnvelope envelope = new MutationEnvelope(
                actor,
                anchor.Id,
                Guid.NewGuid().ToString("N"),
                anchor.Revision);
            using (BeginMutationEnvelopeScope(
                       envelope, actorIsUnrestricted, actorWorldId))
            {
                return ApplyMutation(envelope, mutation);
            }
        }

        /// <summary>Pushes an instance-bound ambient envelope for one trusted production entry.</summary>
        public MutationEnvelopeScope BeginMutationEnvelopeScope(MutationEnvelope envelope,
            bool actorIsUnrestricted, string actorWorldId)
        {
            MutationEnvelopeScope scope = new MutationEnvelopeScope(
                this,
                envelope,
                actorIsUnrestricted,
                actorWorldId,
                _mutationEnvelopeScope);
            _mutationEnvelopeScope = scope;
            return scope;
        }

        /// <summary>Refuses mutation code reached without the matching actor's ambient envelope.</summary>
        public void DemandMutationEnvelope(string actorId, string operation)
        {
            if (!IsWorldAclEnabled)
            {
                return;
            }

            string actor = actorId?.Trim() ?? "";
            MutationEnvelopeScope scope = _mutationEnvelopeScope;
            if (scope != null
                && string.Equals(scope.Envelope.ActorId, actor, StringComparison.Ordinal))
            {
                return;
            }

            string reason = scope == null
                ? "no server-generated mutation envelope is active"
                : "the active mutation envelope belongs to actor '"
                  + scope.Envelope.ActorId + "'";
            throw RbxError.BadArgument(
                "actor '" + actor + "' cannot " + operation
                + " without a mutation envelope in an ACL-versioned world: " + reason,
                "execute the operation through a server-enveloped production entry point");
        }

        /// <summary>Reports whether this registry currently has an envelope for the actor.</summary>
        public bool HasActiveMutationEnvelope(string actorId)
        {
            MutationEnvelopeScope scope = _mutationEnvelopeScope;
            return scope != null
                   && string.Equals(scope.Envelope.ActorId, actorId?.Trim() ?? "",
                       StringComparison.Ordinal);
        }

        internal void EndMutationEnvelopeScope(MutationEnvelopeScope scope)
        {
            if (!ReferenceEquals(_mutationEnvelopeScope, scope))
            {
                throw new InvalidOperationException(
                    "Mutation envelope scopes must be disposed in LIFO order.");
            }

            _mutationEnvelopeScope = scope.Previous;
        }

        private InstanceRecord ResolveMutationAnchor(string actorId, string operation)
        {
            if (_worldRoot != null
                && !_worldRoot.IsDestroyed
                && _byId.TryGetValue(_worldRoot.Id, out InstanceRecord worldRecord))
            {
                return worldRecord;
            }

            foreach (InstanceRecord record in _byId.Values)
            {
                if (!record.Instance.IsDestroyed)
                {
                    return record;
                }
            }

            throw RbxError.BadArgument(
                "actor '" + actorId + "' cannot " + operation
                + ": the world has no live mutation anchor",
                "bootstrap the DataModel before running production Lua");
        }

        private void RetainMutationOperation(string actorId, MutationOperationKey key)
        {
            if (!_mutationOperationOrderByActor.TryGetValue(
                    actorId, out Queue<MutationOperationKey> actorOperations))
            {
                actorOperations = new Queue<MutationOperationKey>(
                    _mutationReplayCapacityPerActor);
                _mutationOperationOrderByActor.Add(actorId, actorOperations);
            }

            actorOperations.Enqueue(key);
            while (actorOperations.Count > _mutationReplayCapacityPerActor)
            {
                MutationOperationKey evicted = actorOperations.Dequeue();
                _mutationOperations.Remove(evicted);
            }
        }

        /// <summary>Advances and returns the revision for a successful instance mutation.</summary>
        public long AdvanceRevision(InstanceId id)
        {
            lock (_mutationGate)
            {
                InstanceRecord record = RequireRecord(id);
                record.Revision = checked(record.Revision + 1L);
                return record.Revision;
            }
        }

        /// <summary>Applies Model state cleanup whose documented boundary is the next simulation step.</summary>
        public void ProcessPreSimulation()
        {
            foreach (InstanceRecord record in _byId.Values)
            {
                if (record.Instance is RbxModel model)
                {
                    model.ResetInvalidPrimaryPart();
                }
            }
        }

        private static void EnsureReplayMatches(MutationEnvelope envelope,
            MutationOperationRecord completed)
        {
            MutationEnvelope first = completed.Envelope;
            if (first.TargetInstanceId != envelope.TargetInstanceId
                || first.ExpectedRevision != envelope.ExpectedRevision)
            {
                throw MutationDenied(envelope,
                    "the operation id was already used for target instance id "
                    + first.TargetInstanceId.Value + " at expected revision "
                    + first.ExpectedRevision);
            }
        }

        private static RbxError MutationDenied(MutationEnvelope envelope, string reason)
        {
            return RbxError.BadArgument(
                "actor '" + envelope.ActorId + "' cannot apply operation '"
                + envelope.OperationId + "' to instance id "
                + envelope.TargetInstanceId.Value + ": " + reason,
                "refresh the target revision and submit a new caller-generated operation id");
        }

        // ---- Creation -----------------------------------------------------------------------

        /// <summary>
        /// Host-level creation: any concrete class, including services. Script-facing
        /// Instance.new goes through <see cref="CreateScripted"/> which additionally enforces
        /// the creatable flag.
        /// </summary>
        public RbxInstance Create(string className, string ownerModId = null, string originTag = null,
            InstanceIdAuthority authority = InstanceIdAuthority.Server, string ownerActorId = null,
            InstanceAccessScope? accessScope = null, bool isRuntimeInfrastructure = false)
        {
            ClassDescriptor descriptor = ResolveConcrete(className);
            return RegisterNew(Instantiate(descriptor), Allocator.Next(authority), ownerModId, originTag,
                ownerActorId, accessScope, isRuntimeInfrastructure);
        }

        /// <summary>Roblox Instance.new semantics: unknown/abstract/non-creatable class names
        /// raise BAD_ARGUMENT with the Roblox message shape.</summary>
        public RbxInstance CreateScripted(string className, string ownerModId = null,
            string originTag = null, InstanceIdAuthority authority = InstanceIdAuthority.Server,
            string ownerActorId = null, InstanceAccessScope? accessScope = null)
        {
            // WHY: reported here rather than at the later Parent assignment. Parenting into the dead
            // world throws PARENT_LOCKED about Workspace, which reads as a script mistake; the real
            // event — the host died and took every existing part with it — is only visible from here.
            if (IsDetached)
            {
                throw RbxError.WorldDetached("Instance.new(\"" + className + "\")");
            }

            if (!Catalog.TryGet(className, out ClassDescriptor descriptor)
                || descriptor.IsAbstract
                || !descriptor.IsCreatable)
            {
                throw RbxError.BadArgument(
                    "Unable to create an Instance of type '" + className + "'",
                    "pass a creatable class name like \"Part\", \"Folder\", or \"Model\"");
            }

            return RegisterNew(Instantiate(descriptor), Allocator.Next(authority), ownerModId, originTag,
                ownerActorId, accessScope, false);
        }

        /// <summary>
        /// Snapshot restore: recreates an instance under its serialized id (stable-id contract,
        /// roadmap §2 world file) and advances the allocator past it.
        /// </summary>
        public RbxInstance RestoreInstance(string className, InstanceId id, string ownerModId = null,
            string originTag = null, string ownerActorId = null,
            InstanceAccessScope? accessScope = null)
        {
            if (!id.IsValid)
            {
                throw RbxError.BadArgument("cannot restore an instance with InstanceId.None",
                    "restore only snapshots captured from a registry");
            }

            if (_byId.ContainsKey(id))
            {
                throw RbxError.BadArgument("InstanceId " + id.Value + " is already registered",
                    "restore into an empty registry or destroy the conflicting instance first");
            }

            ClassDescriptor descriptor = ResolveConcrete(className);
            Allocator.EnsureNotBelow(id);
            return RegisterNew(Instantiate(descriptor), id, ownerModId, originTag, ownerActorId,
                accessScope, false);
        }

        private ClassDescriptor ResolveConcrete(string className)
        {
            if (!Catalog.TryGet(className, out ClassDescriptor descriptor))
            {
                throw RbxError.BadArgument("unknown class name '" + className + "'",
                    "use a class from the MVP1 catalog, e.g. \"Part\" or \"Folder\"");
            }

            if (descriptor.IsAbstract)
            {
                throw RbxError.BadArgument(
                    "class '" + className + "' is abstract and cannot be instantiated",
                    "instantiate a concrete descendant instead, e.g. \"Part\" for \"BasePart\"");
            }

            return descriptor;
        }

        private static RbxInstance Instantiate(ClassDescriptor descriptor)
        {
            return descriptor.Factory != null
                ? descriptor.Factory(descriptor)
                : new RbxInstance(descriptor);
        }

        private RbxInstance RegisterNew(RbxInstance instance, InstanceId id, string ownerModId,
            string originTag, string ownerActorId, InstanceAccessScope? accessScope,
            bool isRuntimeInfrastructure)
        {
            if (!OriginTag.IsValid(originTag))
            {
                throw RbxError.BadArgument(
                    "origin tag '" + originTag + "' is not a valid ledger tag",
                    "use OriginTag.FromMod/FromConsole/FromAi or null for host-owned");
            }

            instance.Attach(this, id);
            string resolvedOwnerActorId = isRuntimeInfrastructure
                ? (string.IsNullOrWhiteSpace(ownerActorId) ? null : ownerActorId.Trim())
                : ResolveOwnerActorId(ownerModId, originTag, ownerActorId);
            InstanceAccessScope resolvedAccessScope = accessScope
                ?? (isRuntimeInfrastructure
                    ? InstanceAccessScope.SharedWritable
                    : DefaultAccessScope(instance, resolvedOwnerActorId));
            InstanceRecord record = new(id, instance, ownerModId, originTag, resolvedOwnerActorId,
                resolvedAccessScope, isRuntimeInfrastructure);
            _byId.Add(id, record);
            Registered?.Invoke(record);
            return instance;
        }

        private string ResolveOwnerActorId(string ownerModId, string originTag, string ownerActorId)
        {
            if (!string.IsNullOrWhiteSpace(ownerActorId))
            {
                return ownerActorId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(ownerModId)
                && _actorByOwnerModId.TryGetValue(ownerModId, out string modActorId))
            {
                return modActorId;
            }

            if (!string.IsNullOrWhiteSpace(originTag)
                && _actorByOriginTag.TryGetValue(originTag, out string originActorId))
            {
                return originActorId;
            }

            return null;
        }

        private static InstanceAccessScope DefaultAccessScope(RbxInstance instance,
            string ownerActorId)
        {
            if (instance.IsService || instance.ClassName == "DataModel"
                                   || instance.ClassName == "Camera")
            {
                return InstanceAccessScope.HostProtected;
            }

            return ownerActorId != null
                ? InstanceAccessScope.Owned
                : InstanceAccessScope.SharedWritable;
        }

        /// <summary>
        /// Binds provenance keys to a restricted actor so the existing Instance.new call path can
        /// attribute records without treating a mod id or console tag as authority.
        /// </summary>
        public void BindActorAttribution(string ownerModId, string originTag, string ownerActorId)
        {
            if (string.IsNullOrWhiteSpace(ownerActorId))
            {
                throw new ArgumentException("Owner actor id is required.", nameof(ownerActorId));
            }

            string actorId = ownerActorId.Trim();
            bool bound = false;
            if (!string.IsNullOrWhiteSpace(ownerModId))
            {
                _actorByOwnerModId[ownerModId] = actorId;
                bound = true;
            }

            if (!string.IsNullOrWhiteSpace(originTag))
            {
                if (!OriginTag.IsValid(originTag))
                {
                    throw RbxError.BadArgument(
                        "origin tag '" + originTag + "' is not a valid ledger tag",
                        "use OriginTag.FromMod/FromConsole/FromAi");
                }

                _actorByOriginTag[originTag] = actorId;
                bound = true;
            }

            if (!bound)
            {
                throw new ArgumentException(
                    "An owner mod id or origin tag is required for actor attribution.");
            }
        }

        /// <summary>Resolves actor attribution without granting or issuing actor authority.</summary>
        public bool TryGetActorAttribution(string ownerModId, string originTag, out string ownerActorId)
        {
            if (!string.IsNullOrWhiteSpace(ownerModId)
                && _actorByOwnerModId.TryGetValue(ownerModId, out ownerActorId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(originTag)
                && _actorByOriginTag.TryGetValue(originTag, out ownerActorId))
            {
                return true;
            }

            ownerActorId = null;
            return false;
        }

        /// <summary>Removes provenance attribution without issuing or changing actor authority.</summary>
        public void ClearActorAttribution(string ownerModId, string originTag)
        {
            if (!string.IsNullOrWhiteSpace(ownerModId))
            {
                _actorByOwnerModId.Remove(ownerModId);
            }

            if (!string.IsNullOrWhiteSpace(originTag))
            {
                if (!OriginTag.IsValid(originTag))
                {
                    throw RbxError.BadArgument(
                        "origin tag '" + originTag + "' is not a valid ledger tag",
                        "use OriginTag.FromMod/FromConsole/FromAi");
                }

                _actorByOriginTag.Remove(originTag);
            }
        }

        /// <summary>Reattributes a completed clone or trusted import atomically at the record layer.</summary>
        public void SetAccessControl(RbxInstance root, string ownerActorId,
            InstanceAccessScope accessScope, bool recursive)
        {
            SetAccessControl(root, ownerActorId, accessScope, recursive, null, true, "");
        }

        /// <summary>Actor-checked reattribute; refuses an unauthorized caller by actor identity.</summary>
        public void SetAccessControl(RbxInstance root, string ownerActorId,
            InstanceAccessScope accessScope, bool recursive, string callerActorId,
            bool callerIsUnrestricted, string callerWorldId)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (!ReferenceEquals(root.Registry, this) || root.IsDestroyed)
            {
                throw RbxError.BadArgument(
                    "access control can only be set on a live instance in this registry",
                    "pass a live instance owned by this world");
            }

            if (IsWorldAclEnabled && callerActorId != null)
            {
                AuthorizeMutation(callerActorId, callerIsUnrestricted, callerWorldId,
                    root, WorldAclDecision.MutateMetadata, "set access control");
                if (recursive)
                {
                    foreach (RbxInstance descendant in root.GetDescendants())
                    {
                        AuthorizeMutation(callerActorId, callerIsUnrestricted,
                            callerWorldId, descendant,
                            WorldAclDecision.MutateMetadata, "set access control");
                    }
                }
            }
            else if (IsWorldAclEnabled && callerActorId == null)
            {
                // WHY: legacy call without actor is treated as host for backward compat; new code must use the actor overload.
            }

            string normalizedActorId = string.IsNullOrWhiteSpace(ownerActorId)
                ? null
                : ownerActorId.Trim();
            SetRecordAccessControl(root, normalizedActorId, accessScope);
            if (!recursive)
            {
                return;
            }

            foreach (RbxInstance descendant in root.GetDescendants())
            {
                SetRecordAccessControl(descendant, normalizedActorId, accessScope);
            }
        }

        /// <summary>Engine-free ACL check used by tests and the disconnect seam.</summary>
        public void AuthorizeMutation(string callerActorId, bool callerIsUnrestricted,
            string callerWorldId, RbxInstance target, WorldAclDecision decision,
            string operation)
        {
            WorldAclAuthorizer.Demand(this, callerActorId, callerIsUnrestricted,
                callerWorldId, target, decision, operation);
            DemandMutationEnvelope(callerActorId, operation);
        }

        /// <summary>Actor-checked destroy entry point.</summary>
        public void DestroyInstance(RbxInstance instance, string callerActorId,
            bool callerIsUnrestricted, string callerWorldId)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            AuthorizeMutation(callerActorId, callerIsUnrestricted, callerWorldId,
                instance, WorldAclDecision.Destroy, "destroy");
            foreach (RbxInstance descendant in instance.GetDescendants())
            {
                WorldAclAuthorizer.Demand(this, callerActorId, callerIsUnrestricted,
                    callerWorldId, descendant, WorldAclDecision.Destroy, "destroy");
            }

            instance.Destroy();
        }

        private void SetRecordAccessControl(RbxInstance instance, string ownerActorId,
            InstanceAccessScope accessScope)
        {
            InstanceRecord record = RequireRecord(instance.Id);
            record.OwnerActorId = ownerActorId;
            record.AccessScope = accessScope;
        }

        // ---- Lookup (§3.3: any key resolves to the same record) -----------------------------

        public bool TryGet(InstanceId id, out RbxInstance instance)
        {
            if (_byId.TryGetValue(id, out InstanceRecord record))
            {
                instance = record.Instance;
                return true;
            }

            instance = null;
            return false;
        }

        public bool TryGetRecord(InstanceId id, out InstanceRecord record)
        {
            return _byId.TryGetValue(id, out record);
        }

        internal InstanceRecord GetRecord(InstanceId id)
        {
            return _byId[id];
        }

        public bool TryGetByNetId(uint netId, out RbxInstance instance)
        {
            if (_byNetId.TryGetValue(netId, out InstanceRecord record))
            {
                instance = record.Instance;
                return true;
            }

            instance = null;
            return false;
        }

        public bool TryGetByWorldName(string worldName, out RbxInstance instance)
        {
            if (worldName != null && _byWorldName.TryGetValue(worldName, out InstanceRecord record))
            {
                instance = record.Instance;
                return true;
            }

            if (worldName != null && _worldInstanceAdapter != null
                                  && _worldInstanceAdapter.TryWrap(this, worldName, out instance))
            {
                BindWorldName(instance.Id, worldName);
                return true;
            }

            instance = null;
            return false;
        }

        // ---- Identity binding ---------------------------------------------------------------

        /// <summary>MVP12 seam: binds the Mirror netId. Only server-assigned ids replicate (§3.3).</summary>
        public void BindNetId(InstanceId id, uint netId)
        {
            InstanceIdWireContract.EnsureWireSafe(id);
            InstanceRecord record = RequireRecord(id);
            if (record.NetId != 0)
            {
                _byNetId.Remove(record.NetId);
            }

            record.NetId = netId;
            if (netId != 0)
            {
                _byNetId[netId] = record;
            }
        }

        /// <summary>Binds the CoreAI world-command name so world queries and Lua resolve to one record.</summary>
        public void BindWorldName(InstanceId id, string worldName)
        {
            InstanceRecord record = RequireRecord(id);
            if (record.WorldName != null)
            {
                _byWorldName.Remove(record.WorldName);
            }

            record.WorldName = worldName;
            if (worldName != null)
            {
                _byWorldName[worldName] = record;
            }
        }

        private InstanceRecord RequireRecord(InstanceId id)
        {
            if (!_byId.TryGetValue(id, out InstanceRecord record))
            {
                throw RbxError.BadArgument("no registered instance with id " + id.Value,
                    "bind identities only for live registered instances");
            }

            return record;
        }

        /// <summary>Returns mod-authored content, excluding host-created runtime infrastructure.</summary>
        public IReadOnlyList<RbxInstance> GetOwnedBy(string modId)
        {
            List<RbxInstance> result = new();
            foreach (InstanceRecord record in _byId.Values)
            {
                if (record.IsAuthoredContent
                    && string.Equals(record.OwnerModId, modId, StringComparison.Ordinal))
                {
                    result.Add(record.Instance);
                }
            }

            return result;
        }

        /// <summary>Hot-reload teardown sweep including per-mod runtime infrastructure.</summary>
        public IReadOnlyList<RbxInstance> GetTeardownOwnedBy(string modId)
        {
            List<RbxInstance> result = new();
            foreach (InstanceRecord record in _byId.Values)
            {
                if (string.Equals(record.OwnerModId, modId, StringComparison.Ordinal))
                {
                    result.Add(record.Instance);
                }
            }

            return result;
        }

        /// <summary>Returns a stable snapshot of every live instance in this registry.</summary>
        public IReadOnlyList<RbxInstance> GetLiveInstances()
        {
            List<RbxInstance> result = new(_byId.Count);
            foreach (InstanceRecord record in _byId.Values)
            {
                result.Add(record.Instance);
            }

            return result;
        }

        // ---- Scene-root / binder plumbing (D5) ----------------------------------------------

        /// <summary>Sets the Workspace handle (physical-world root). Pointer only — the backing
        /// hierarchy is driven by the scene root (<see cref="SetSceneRoot"/>), which mirrors the
        /// whole DataModel tree, not just Workspace.</summary>
        public void SetWorldRoot(RbxInstance worldRoot)
        {
            _worldRoot = worldRoot;
        }

        /// <summary>Declares the DataModel root whose whole subtree materializes into backing
        /// objects (the Unity hierarchy mirrors the Roblox explorer); sweeps the existing subtree
        /// so late binding is consistent. Storage-service subtrees materialize too — the binder
        /// renders them inactive.</summary>
        public void SetSceneRoot(RbxInstance sceneRoot)
        {
            _sceneRoot = sceneRoot;
            if (sceneRoot == null)
            {
                return;
            }

            ApplySceneMembership(sceneRoot, true);
        }

        internal bool IsInScene(RbxInstance instance)
        {
            if (_sceneRoot == null || instance == null)
            {
                return false;
            }

            return ReferenceEquals(instance, _sceneRoot) || instance.IsDescendantOf(_sceneRoot);
        }

        internal void OnParentChanged(RbxInstance instance, bool wasInScene)
        {
            bool isInScene = IsInScene(instance);
            if (wasInScene == isInScene)
            {
                // WHY: a move fully inside the scene tree changes no membership but must
                // still mirror into the backing hierarchy (transform re-parent in Unity) — this
                // is how a Part dragged Workspace->ReplicatedStorage slides under the inactive
                // service GO and disappears from the physical world without leaving the tree.
                if (isInScene && _byId.TryGetValue(instance.Id, out InstanceRecord record)
                              && record.IsMaterialized)
                {
                    _binder.OnReparented(record);
                }

                return;
            }

            ApplySceneMembership(instance, isInScene);
            SceneMembershipChanged?.Invoke(instance, isInScene);
        }

        internal void OnNameChanged(RbxInstance instance)
        {
            if (_byId.TryGetValue(instance.Id, out InstanceRecord record) && record.IsMaterialized)
            {
                _binder.OnNameChanged(record);
            }
        }

        private void ApplySceneMembership(RbxInstance root, bool entered)
        {
            NotifyMembership(root, entered);
            foreach (RbxInstance descendant in root.GetDescendants())
            {
                NotifyMembership(descendant, entered);
            }
        }

        private void NotifyMembership(RbxInstance instance, bool entered)
        {
            if (!_byId.TryGetValue(instance.Id, out InstanceRecord record))
            {
                // WHY: the instance reached this registry's tree but was never registered HERE, so it can
                // never materialize — the world the script writes to and the registry backing the binder
                // are two different objects (a composition fault). Reporting it here is the only way it
                // becomes visible: the script still completes and the only symptom is a missing object.
                Diagnostics?.Invoke(
                    $"[CoreAI.RbxApi] '{instance.Name}' (class={instance.ClassName}) entered a tree this " +
                    "registry does not own, so it will never materialize. The Rbx world used by scripts " +
                    "and the registry wired to the binder are different instances — check that " +
                    "RbxWorldHost.Registry/Game are the ones passed to LuaCsRbxApiBindings.");
                return;
            }

            if (record.IsMaterialized == entered)
            {
                return;
            }

            record.IsMaterialized = entered;
            if (entered)
            {
                _binder.OnEnteredWorld(record);
            }
            else
            {
                _binder.OnLeftWorld(record);
            }
        }

        // ---- Destroy plumbing (D6 steps 5–6) ------------------------------------------------

        internal void OnInstanceDestroyed(RbxInstance instance)
        {
            if (!_byId.TryGetValue(instance.Id, out InstanceRecord record))
            {
                return;
            }

            IReadOnlyList<string> clearedTags = Tags.GetTags(instance.Id);
            Tags.ClearInstance(instance.Id);
            _byId.Remove(instance.Id);
            if (record.NetId != 0)
            {
                _byNetId.Remove(record.NetId);
            }

            if (record.WorldName != null)
            {
                _byWorldName.Remove(record.WorldName);
            }

            record.IsMaterialized = false;
            _binder.OnDestroyed(record);
            Unregistered?.Invoke(record);

            // WHY: the destroy sweep drops tags silently at the store, but the per-tag removed
            // signals already fired on the detach (SceneMembershipChanged) and the TagRemoved
            // global still needs its last-use transition, so each cleared tag notifies here.
            for (int index = 0; index < clearedTags.Count; index++)
            {
                OnTagRemoved(instance, clearedTags[index],
                    !Tags.IsTagInUse(clearedTags[index]));
            }
        }
    }
}
