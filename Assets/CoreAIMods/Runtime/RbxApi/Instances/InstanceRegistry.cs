using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
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
        private readonly Dictionary<InstanceId, InstanceRecord> _byId =
            new Dictionary<InstanceId, InstanceRecord>();
        private readonly Dictionary<uint, InstanceRecord> _byNetId =
            new Dictionary<uint, InstanceRecord>();
        private readonly Dictionary<string, InstanceRecord> _byWorldName =
            new Dictionary<string, InstanceRecord>(StringComparer.Ordinal);
        private readonly IInstanceBackingBinder _binder;

        private RbxInstance _worldRoot;

        public InstanceRegistry(ClassCatalog catalog = null, IInstanceBackingBinder binder = null,
            InstanceIdAllocator allocator = null)
        {
            Catalog = catalog ?? ClassCatalog.CreateMvp1();
            Allocator = allocator ?? new InstanceIdAllocator();
            Tags = new InstanceTagStore();
            _binder = binder ?? NullInstanceBackingBinder.Instance;
        }

        public ClassCatalog Catalog { get; }

        public InstanceIdAllocator Allocator { get; }

        /// <summary>CollectionService substrate; instances delegate their tag members here.</summary>
        public InstanceTagStore Tags { get; }

        /// <summary>The workspace instance whose subtree materializes backing objects (D5).</summary>
        public RbxInstance WorldRoot => _worldRoot;

        public int Count => _byId.Count;

        public event Action<InstanceRecord> Registered;

        public event Action<InstanceRecord> Unregistered;

        // ---- Creation -----------------------------------------------------------------------

        /// <summary>
        /// Host-level creation: any concrete class, including services. Script-facing
        /// Instance.new goes through <see cref="CreateScripted"/> which additionally enforces
        /// the creatable flag.
        /// </summary>
        public RbxInstance Create(string className, string ownerModId = null, string originTag = null,
            InstanceIdAuthority authority = InstanceIdAuthority.Server)
        {
            ClassDescriptor descriptor = ResolveConcrete(className);
            return RegisterNew(Instantiate(descriptor), Allocator.Next(authority), ownerModId, originTag);
        }

        /// <summary>Roblox Instance.new semantics: unknown/abstract/non-creatable class names
        /// raise BAD_ARGUMENT with the Roblox message shape.</summary>
        public RbxInstance CreateScripted(string className, string ownerModId = null,
            string originTag = null, InstanceIdAuthority authority = InstanceIdAuthority.Server)
        {
            if (!Catalog.TryGet(className, out ClassDescriptor descriptor)
                || descriptor.IsAbstract
                || !descriptor.IsCreatable)
            {
                throw RbxError.BadArgument(
                    "Unable to create an Instance of type '" + className + "'",
                    "pass a creatable class name like \"Part\", \"Folder\", or \"Model\"");
            }

            return RegisterNew(Instantiate(descriptor), Allocator.Next(authority), ownerModId, originTag);
        }

        /// <summary>
        /// Snapshot restore: recreates an instance under its serialized id (stable-id contract,
        /// roadmap §2 world file) and advances the allocator past it.
        /// </summary>
        public RbxInstance RestoreInstance(string className, InstanceId id, string ownerModId = null,
            string originTag = null)
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
            return RegisterNew(Instantiate(descriptor), id, ownerModId, originTag);
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
            string originTag)
        {
            if (!OriginTag.IsValid(originTag))
            {
                throw RbxError.BadArgument(
                    "origin tag '" + originTag + "' is not a valid ledger tag",
                    "use OriginTag.FromMod/FromConsole/FromAi or null for host-owned");
            }

            instance.Attach(this, id);
            var record = new InstanceRecord(id, instance, ownerModId, originTag);
            _byId.Add(id, record);
            Registered?.Invoke(record);
            return instance;
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

        /// <summary>Hot-reload teardown sweep (roadmap §3.3 / MVP5).</summary>
        public IReadOnlyList<RbxInstance> GetOwnedBy(string modId)
        {
            var result = new List<RbxInstance>();
            foreach (InstanceRecord record in _byId.Values)
            {
                if (string.Equals(record.OwnerModId, modId, StringComparison.Ordinal))
                {
                    result.Add(record.Instance);
                }
            }

            return result;
        }

        // ---- World-root / binder plumbing (D5) ----------------------------------------------

        /// <summary>Declares the workspace root whose subtree materializes; sweeps the existing
        /// subtree so late binding is consistent.</summary>
        public void SetWorldRoot(RbxInstance worldRoot)
        {
            _worldRoot = worldRoot;
            if (worldRoot == null)
            {
                return;
            }

            ApplyWorldMembership(worldRoot, true);
        }

        internal bool IsInWorld(RbxInstance instance)
        {
            if (_worldRoot == null || instance == null)
            {
                return false;
            }

            return ReferenceEquals(instance, _worldRoot) || instance.IsDescendantOf(_worldRoot);
        }

        internal void OnParentChanged(RbxInstance instance, bool wasInWorld)
        {
            bool isInWorld = IsInWorld(instance);
            if (wasInWorld == isInWorld)
            {
                // WHY: a move fully inside the world subtree changes no membership but must
                // still mirror into the backing hierarchy (transform re-parent in Unity).
                if (isInWorld && _byId.TryGetValue(instance.Id, out InstanceRecord record)
                    && record.IsMaterialized)
                {
                    _binder.OnReparented(record);
                }

                return;
            }

            ApplyWorldMembership(instance, isInWorld);
        }

        internal void OnNameChanged(RbxInstance instance)
        {
            if (_byId.TryGetValue(instance.Id, out InstanceRecord record) && record.IsMaterialized)
            {
                _binder.OnNameChanged(record);
            }
        }

        private void ApplyWorldMembership(RbxInstance root, bool entered)
        {
            NotifyMembership(root, entered);
            foreach (RbxInstance descendant in root.GetDescendants())
            {
                NotifyMembership(descendant, entered);
            }
        }

        private void NotifyMembership(RbxInstance instance, bool entered)
        {
            if (!_byId.TryGetValue(instance.Id, out InstanceRecord record)
                || record.IsMaterialized == entered)
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
        }
    }
}
