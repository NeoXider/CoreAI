using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Replication;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Engine-free core of the Roblox Instance member set (roadmap §5.1.2): hierarchy,
    /// navigation, lifecycle, attributes, and tags. Geometry-free by design — spatial
    /// properties live in later slices behind the property system. Instances are created only
    /// through <see cref="InstanceRegistry"/>, which owns identity (§3.3).
    /// Destroyed-instance policy (DEV-7, registry-level interpretation): tombstone reads
    /// (Name, ClassName, Parent, IsDestroyed) stay available at the C# Domain level; every
    /// mutation and navigation raises INSTANCE_DESTROYED. The Lua boundary permits those reads
    /// only while a destruction-queued handler owns the scheduler tombstone scope.
    /// Every tree walk (navigation, Clone, Destroy, re-parenting) runs on an explicit stack, and
    /// a tree is never deeper than <see cref="InstanceTreeSerializer.MaximumSnapshotDepth"/>, so
    /// no hierarchy a script can build overflows the call stack or becomes unsaveable.
    /// </summary>
    public class RbxInstance
    {
        /// <summary>Mirror: "The name of an instance cannot exceed 100 characters in size."</summary>
        public const int MaxNameLength = 100;

        private const string ChangedSignalName = "Changed";

        private readonly struct WalkFrame
        {
            public WalkFrame(RbxInstance instance, int depth)
            {
                Instance = instance;
                Depth = depth;
            }

            public RbxInstance Instance { get; }

            public int Depth { get; }
        }

        private readonly struct CloneFrame
        {
            public CloneFrame(RbxInstance source, RbxInstance copy, int nextChild)
            {
                Source = source;
                Copy = copy;
                NextChild = nextChild;
            }

            public RbxInstance Source { get; }

            public RbxInstance Copy { get; }

            public int NextChild { get; }
        }

        private readonly struct DestroyFrame
        {
            public DestroyFrame(RbxInstance instance, RbxInstance[] children, int nextChild)
            {
                Instance = instance;
                Children = children;
                NextChild = nextChild;
            }

            public RbxInstance Instance { get; }

            public RbxInstance[] Children { get; }

            public int NextChild { get; }
        }

        // WHY: the walks that use this stack only read the tree, so one stack per thread serves
        // them all without a per-call allocation; renting clears the slot, so a walk that is
        // re-entered or cut short by an exception allocates a fresh stack instead of sharing one.
        [System.ThreadStatic] private static Stack<WalkFrame> _walkScratch;

        private readonly ClassDescriptor _descriptor;
        private readonly List<RbxInstance> _children = new();
        private readonly Dictionary<string, object> _attributes = new(System.StringComparer.Ordinal);
        private Dictionary<string, RbxScriptSignal> _signals;
        private Dictionary<string, RbxScriptSignal> _propertyChangedSignals;

        private string _name;
        private bool _archivable = true;
        private RbxInstance _parent;
        private bool _destroyed;
        private bool _destroying;

        protected internal RbxInstance(ClassDescriptor descriptor)
        {
            _descriptor = descriptor ?? throw new System.ArgumentNullException(nameof(descriptor));
            _name = descriptor.Name;
        }

        internal InstanceRegistry Registry { get; private set; }

        internal ClassDescriptor Descriptor => _descriptor;

        /// <summary>True for singleton services (UserInputService, Workspace, Lighting, …) — the Lua
        /// lifecycle bindings refuse to Destroy/Clone these so one mod cannot brick a shared service
        /// for the whole world. Internal teardown (world destroy) still tears them down directly.</summary>
        public bool IsService => _descriptor.IsService;

        internal void Attach(InstanceRegistry registry, InstanceId id)
        {
            Registry = registry;
            Id = id;
        }

        /// <summary>Stable identity; appears in every log line/error about this instance (§3.3).</summary>
        public InstanceId Id { get; private set; }

        public string ClassName => _descriptor.Name;

        /// <summary>Tombstone-readable after Destroy (DEV-7). An assignment longer than
        /// <see cref="MaxNameLength"/> keeps its first <see cref="MaxNameLength"/> characters, never
        /// splitting a surrogate pair (OURS — truncation rather than an error, so a world saved
        /// before the cap existed still restores).</summary>
        public string Name
        {
            get => _name;
            set
            {
                ThrowIfDestroyed("Name");
                string nextName = CapNameLength(value ?? throw RbxError.BadArgument("Name cannot be nil",
                    "pass a string, e.g. instance.Name = \"SpawnPad\""));
                if (string.Equals(_name, nextName, System.StringComparison.Ordinal))
                {
                    return;
                }

                _name = nextName;
                Registry?.OnNameChanged(this);
                Registry?.AdvanceRevision(Id, ReplicationMembers.Name);
                NotifyPropertyChanged(ReplicationMembers.Name);
            }
        }

        /// <summary>Honored by Clone (R6.5) and, later, world-file save (R6.6).</summary>
        public bool Archivable
        {
            get
            {
                ThrowIfDestroyed("Archivable");
                return _archivable;
            }
            set
            {
                ThrowIfDestroyed("Archivable");
                if (_archivable == value)
                {
                    return;
                }

                _archivable = value;
                Registry?.AdvanceRevision(Id, ReplicationMembers.Archivable);
                NotifyPropertyChanged(ReplicationMembers.Archivable);
            }
        }

        public bool IsDestroyed => _destroyed;

        /// <summary>Tombstone-readable after Destroy (always null then). Setter runs the full
        /// re-parent pipeline with hierarchy validation; throws PARENT_LOCKED after Destroy (D6),
        /// and BAD_ARGUMENT when the move would make the tree deeper than
        /// <see cref="InstanceTreeSerializer.MaximumSnapshotDepth"/> levels.</summary>
        public RbxInstance Parent
        {
            get => _parent;
            set => SetParent(value);
        }

        private void SetParent(RbxInstance newParent)
        {
            if (_destroyed)
            {
                throw RbxError.ParentLocked(_name);
            }

            if (ReferenceEquals(newParent, _parent))
            {
                return;
            }

            if (newParent != null)
            {
                if (ReferenceEquals(newParent, this) || newParent.IsDescendantOf(this))
                {
                    throw RbxError.BadArgument(
                        "Attempt to set parent of " + _name + " to " + newParent._name +
                        " would result in circular reference",
                        "parent the instance to a node outside its own subtree");
                }

                if (newParent._destroyed)
                {
                    throw RbxError.InstanceDestroyed("Parent assignment", newParent._name, newParent.Id);
                }
            }

            List<RbxInstance> movedSubtree = SnapshotSubtree(out int subtreeHeight);
            List<RbxInstance> newAncestors = SnapshotAncestors(newParent);
            int resultingDepth = newAncestors.Count + subtreeHeight;
            if (newParent != null && resultingDepth > InstanceTreeSerializer.MaximumSnapshotDepth)
            {
                // WHY: the world package refuses a tree deeper than this, so a live tree past it
                // could never be saved again; refusing the move keeps every live world saveable.
                throw RbxError.BadArgument(
                    "Attempt to set parent of " + _name + " to " + newParent._name
                    + " would make the instance hierarchy " + resultingDepth
                    + " levels deep; the depth limit is " + InstanceTreeSerializer.MaximumSnapshotDepth,
                    "keep instance trees at most " + InstanceTreeSerializer.MaximumSnapshotDepth
                    + " levels deep, e.g. parent the subtree under a shallower ancestor");
            }

            bool wasInScene = Registry != null && Registry.IsInScene(this);
            RbxInstance oldParent = _parent;
            List<RbxInstance> oldAncestors = SnapshotAncestors(oldParent);
            for (int ancestorIndex = 0; ancestorIndex < oldAncestors.Count; ancestorIndex++)
            {
                RbxInstance ancestor = oldAncestors[ancestorIndex];
                for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
                {
                    ancestor.FireIfConnected("DescendantRemoving", movedSubtree[movedIndex]);
                }
            }

            oldParent?._children.Remove(this);
            _parent = newParent;
            newParent?._children.Add(this);

            Registry?.AdvanceRevision(Id, ReplicationMembers.Parent);
            oldParent?.Registry?.AdvanceRevision(oldParent.Id, ReplicationMembers.Children);
            newParent?.Registry?.AdvanceRevision(newParent.Id, ReplicationMembers.Children);

            Registry?.OnParentChanged(this, wasInScene);
            oldParent?.FireIfConnected("ChildRemoved", this);
            newParent?.FireIfConnected("ChildAdded", this);
            NotifyPropertyChangedCore(ReplicationMembers.Parent, _destroying);
            FireAncestryChanged(movedSubtree);

            for (int ancestorIndex = 0; ancestorIndex < newAncestors.Count; ancestorIndex++)
            {
                RbxInstance ancestor = newAncestors[ancestorIndex];
                for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
                {
                    ancestor.FireIfConnected("DescendantAdded", movedSubtree[movedIndex]);
                }
            }
        }

        /// <summary>
        /// Mirror AncestryChanged(child, parent): every listener in the moved subtree receives the
        /// instance whose Parent changed (this one) and its new parent — not itself and its own
        /// parent. One argument array serves every listener, since the pair is the same for all.
        /// </summary>
        private void FireAncestryChanged(List<RbxInstance> movedSubtree)
        {
            object[] arguments = null;
            for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
            {
                RbxInstance moved = movedSubtree[movedIndex];
                if (!moved.TryGetConnectedSignal("AncestryChanged", out RbxScriptSignal signal))
                {
                    continue;
                }

                arguments ??= new object[] { this, _parent };
                if (_destroying)
                {
                    signal.FireForDestruction(moved, arguments);
                }
                else
                {
                    signal.Fire(arguments);
                }
            }
        }

        // ---- Navigation (R6.10) -------------------------------------------------------------

        public RbxInstance FindFirstChild(string name, bool recursive = false)
        {
            ThrowIfDestroyed("FindFirstChild");
            if (recursive)
            {
                return SearchDescendants(name, false);
            }

            for (int index = 0; index < _children.Count; index++)
            {
                RbxInstance child = _children[index];
                if (string.Equals(child._name, name, System.StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        public RbxInstance FindFirstChildOfClass(string className)
        {
            ThrowIfDestroyed("FindFirstChildOfClass");
            foreach (RbxInstance child in _children)
            {
                if (string.Equals(child.ClassName, className, System.StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        public RbxInstance FindFirstChildWhichIsA(string className, bool recursive = false)
        {
            ThrowIfDestroyed("FindFirstChildWhichIsA");
            if (recursive)
            {
                return SearchDescendants(className, true);
            }

            for (int index = 0; index < _children.Count; index++)
            {
                RbxInstance child = _children[index];
                if (child.IsA(className))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// First descendant in preorder whose name equals <paramref name="key"/>, or which IsA
        /// <paramref name="key"/> when <paramref name="matchClass"/> is set.
        /// </summary>
        /// <remarks>
        /// WHY preorder: Roblox's recursive search checks a child, then that child's whole subtree,
        /// then the next sibling, so a deep descendant of an earlier child beats a later child.
        /// </remarks>
        private RbxInstance SearchDescendants(string key, bool matchClass)
        {
            if (_children.Count == 0)
            {
                return null;
            }

            Stack<WalkFrame> pending = RentWalkStack();
            PushChildren(pending, this, 0);
            RbxInstance found = null;
            while (pending.Count > 0)
            {
                RbxInstance current = pending.Pop().Instance;
                bool matches = matchClass
                    ? current.IsA(key)
                    : string.Equals(current._name, key, System.StringComparison.Ordinal);
                if (matches)
                {
                    found = current;
                    break;
                }

                PushChildren(pending, current, 0);
            }

            ReturnWalkStack(pending);
            return found;
        }

        public RbxInstance FindFirstAncestor(string name)
        {
            ThrowIfDestroyed("FindFirstAncestor");
            for (RbxInstance ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (string.Equals(ancestor._name, name, System.StringComparison.Ordinal))
                {
                    return ancestor;
                }
            }

            return null;
        }

        public RbxInstance FindFirstAncestorOfClass(string className)
        {
            ThrowIfDestroyed("FindFirstAncestorOfClass");
            for (RbxInstance ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (string.Equals(ancestor.ClassName, className, System.StringComparison.Ordinal))
                {
                    return ancestor;
                }
            }

            return null;
        }

        public RbxInstance FindFirstAncestorWhichIsA(string className)
        {
            ThrowIfDestroyed("FindFirstAncestorWhichIsA");
            for (RbxInstance ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (ancestor.IsA(className))
                {
                    return ancestor;
                }
            }

            return null;
        }

        /// <summary>Insertion order (acceptance item 3).</summary>
        public IReadOnlyList<RbxInstance> GetChildren()
        {
            ThrowIfDestroyed("GetChildren");
            return _children.ToArray();
        }

        /// <summary>Preorder (acceptance item 3).</summary>
        public IReadOnlyList<RbxInstance> GetDescendants()
        {
            ThrowIfDestroyed("GetDescendants");
            List<RbxInstance> result = new();
            CollectDescendants(result);
            return result;
        }

        private void CollectDescendants(List<RbxInstance> result)
        {
            if (_children.Count == 0)
            {
                return;
            }

            Stack<WalkFrame> pending = RentWalkStack();
            PushChildren(pending, this, 0);
            while (pending.Count > 0)
            {
                RbxInstance current = pending.Pop().Instance;
                result.Add(current);
                PushChildren(pending, current, 0);
            }

            ReturnWalkStack(pending);
        }

        /// <summary>Walks the data-driven class ancestry including "Instance" (§5.1.7 risk table).</summary>
        public bool IsA(string className)
        {
            return Registry != null
                ? Registry.Catalog.IsA(ClassName, className)
                : string.Equals(ClassName, className, System.StringComparison.Ordinal);
        }

        /// <summary>Mirror: "cannot be used with a parameter of nil" — a nil ancestor raises
        /// BAD_ARGUMENT instead of quietly answering false.</summary>
        public bool IsDescendantOf(RbxInstance ancestor)
        {
            if (ancestor == null)
            {
                throw RbxError.BadArgument(
                    "IsDescendantOf expects an Instance, got nil",
                    "to test whether an instance was removed, check instance.Parent == nil instead");
            }

            for (RbxInstance current = _parent; current != null; current = current._parent)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsAncestorOf(RbxInstance descendant)
        {
            return descendant != null && descendant.IsDescendantOf(this);
        }

        /// <summary>Dot-joined names from the topmost ancestor, excluding the DataModel root
        /// (Roblox parity: Workspace.Part, not game.Workspace.Part).</summary>
        public virtual string GetFullName()
        {
            ThrowIfDestroyed("GetFullName");
            List<string> names = new();
            for (RbxInstance current = this; current != null; current = current._parent)
            {
                if (current is RbxDataModel)
                {
                    break;
                }

                names.Add(current._name);
            }

            names.Reverse();
            return string.Join(".", names);
        }

        // ---- Lifecycle (R6.2, R6.5, D6, D8) -------------------------------------------------

        /// <summary>Deep copy skipping Archivable == false subtrees; returns null when this
        /// instance itself is non-archivable (R6.5), the DataModel or a service — those are world
        /// singletons, and a DataModel or service below the cloned root is skipped the same way.
        /// Fresh ids; Parent is null; attributes and tags copy; identity is never cloned (D8). A
        /// reference property (Model.PrimaryPart, ObjectValue.Value) whose target was cloned too
        /// points at the target's copy; any other target is kept (mirror Instance:Clone).</summary>
        public RbxInstance Clone()
        {
            ThrowIfDestroyed("Clone");
            if (!_archivable || IsWorldSingleton)
            {
                return null;
            }

            return CloneSubtree(false, null, null);
        }

        /// <summary>Deep-copies this subtree under the supplied teardown owner and provenance;
        /// the same rules as <see cref="Clone()"/>.</summary>
        public RbxInstance Clone(string ownerModId, string originTag)
        {
            ThrowIfDestroyed("Clone");
            if (!_archivable || IsWorldSingleton)
            {
                return null;
            }

            return CloneSubtree(true, ownerModId, originTag);
        }

        private bool IsWorldSingleton => _descriptor.IsService || this is RbxDataModel;

        /// <summary>
        /// Builds the copy in the same order the recursive form did — each node is created and
        /// filled before its children, and each finished child copy is parented to its parent's
        /// copy after its own subtree is complete — then runs the reference fix-up over every copy.
        /// On failure every copy made so far is destroyed.
        /// </summary>
        private RbxInstance CloneSubtree(bool overrideOwnership, string ownerModId, string originTag)
        {
            RbxInstance rootCopy = CreateCloneCopy(overrideOwnership, ownerModId, originTag);
            if (NextClonedChildIndex(this, 0) < 0)
            {
                rootCopy.RemapClonedReferences(new CloneReferenceMap(this, rootCopy, null));
                return rootCopy;
            }

            Dictionary<RbxInstance, RbxInstance> descendantCopies = new();
            List<CloneFrame> pending = new() { new CloneFrame(this, rootCopy, 0) };
            try
            {
                while (pending.Count > 0)
                {
                    int topIndex = pending.Count - 1;
                    CloneFrame top = pending[topIndex];
                    int childIndex = NextClonedChildIndex(top.Source, top.NextChild);
                    if (childIndex >= 0)
                    {
                        RbxInstance child = top.Source._children[childIndex];
                        pending[topIndex] = new CloneFrame(top.Source, top.Copy, childIndex + 1);
                        RbxInstance childCopy = child.CreateCloneCopy(
                            overrideOwnership, ownerModId, originTag);
                        descendantCopies.Add(child, childCopy);
                        pending.Add(new CloneFrame(child, childCopy, 0));
                        continue;
                    }

                    if (topIndex > 0)
                    {
                        top.Copy.SetParent(pending[topIndex - 1].Copy);
                    }

                    pending.RemoveAt(topIndex);
                }
            }
            catch
            {
                // WHY: every copy still on the stack is an unparented subtree root holding the
                // children finished under it, so destroying each one frees everything built.
                for (int index = pending.Count - 1; index >= 0; index--)
                {
                    pending[index].Copy.Destroy();
                }

                throw;
            }

            CloneReferenceMap map = new(this, rootCopy, descendantCopies);
            rootCopy.RemapClonedReferences(map);
            foreach (RbxInstance copy in descendantCopies.Values)
            {
                copy.RemapClonedReferences(map);
            }

            return rootCopy;
        }

        private static int NextClonedChildIndex(RbxInstance source, int startIndex)
        {
            List<RbxInstance> children = source._children;
            for (int index = startIndex; index < children.Count; index++)
            {
                RbxInstance child = children[index];
                if (child._archivable && !child.IsWorldSingleton)
                {
                    return index;
                }
            }

            return -1;
        }

        // WHY: BasePart spatial/appearance state (Size, CFrame, Color, Anchored, ...) lives outside
        // this class in an IPartPropertySink implemented alongside the Unity backing binder — an
        // assembly that references Instances, not the reverse, so RbxInstance cannot see that type
        // without inverting the engine-free split. Registry.Binder.CopyBackingState is the seam
        // that lets this method trigger the copy anyway: it is declared on IInstanceBackingBinder,
        // which Instances already owns, and implemented on the Unity side where the sink lives.
        // CloneSubtree calls this once per node that makes it into the copy — the Archivable ==
        // false skip there already keeps source and copy aligned; no separate parallel walk is needed.
        private RbxInstance CreateCloneCopy(bool overrideOwnership, string ownerModId, string originTag)
        {
            InstanceRecord sourceRecord = Registry.GetRecord(Id);
            InstanceIdAuthority authority = Id.IsServerAssigned
                ? InstanceIdAuthority.Server
                : InstanceIdAuthority.Local;
            string resolvedOwnerModId = overrideOwnership ? ownerModId : sourceRecord.OwnerModId;
            string resolvedOriginTag = overrideOwnership ? originTag : sourceRecord.OriginTag;
            RbxInstance copy = Registry.Create(
                ClassName, resolvedOwnerModId, resolvedOriginTag, authority);
            try
            {
                copy._name = _name;
                copy._archivable = _archivable;
                CopyCustomStateTo(copy);
                Registry.Binder.CopyBackingState(Id, copy.Id);
                foreach (KeyValuePair<string, object> attribute in _attributes)
                {
                    copy._attributes[attribute.Key] = attribute.Value;
                }

                foreach (string tag in Registry.Tags.GetTags(Id))
                {
                    Registry.Tags.AddTag(copy.Id, tag);
                }

                return copy;
            }
            catch
            {
                copy.Destroy();
                throw;
            }
        }

        /// <summary>
        /// Atomic destroy per R6.2/D6 at registry level: (1) detach — Parent set to nil,
        /// (2) Parent locked, (3) signals disconnect while preserving pending invocations,
        /// (4) children destroy in turn, each running steps 1-6 before its own children,
        /// (5) tags cleared and the registry record unregistered, (6) the backing binder releases
        /// the backing object. Idempotent. Deep trees are walked on an explicit stack.
        /// </summary>
        public void Destroy()
        {
            if (!BeginDestroy())
            {
                return;
            }

            if (_children.Count == 0)
            {
                Registry?.OnInstanceDestroyed(this);
                return;
            }

            List<DestroyFrame> pending = new() { new DestroyFrame(this, _children.ToArray(), 0) };
            while (pending.Count > 0)
            {
                int topIndex = pending.Count - 1;
                DestroyFrame top = pending[topIndex];
                if (top.NextChild < top.Children.Length)
                {
                    RbxInstance child = top.Children[top.NextChild];
                    pending[topIndex] = new DestroyFrame(top.Instance, top.Children, top.NextChild + 1);
                    if (child.BeginDestroy())
                    {
                        pending.Add(new DestroyFrame(child, child.SnapshotChildrenForDestroy(), 0));
                    }

                    continue;
                }

                pending.RemoveAt(topIndex);
                top.Instance.Registry?.OnInstanceDestroyed(top.Instance);
            }
        }

        /// <summary>Steps 1-3 of <see cref="Destroy"/>; false when already destroyed.</summary>
        private bool BeginDestroy()
        {
            if (_destroyed)
            {
                return false;
            }

            _destroying = true;
            FireSignalForDestruction("Destroying");
            SetParent(null);
            _destroyed = true;
            DisconnectSignals();
            return true;
        }

        private RbxInstance[] SnapshotChildrenForDestroy()
        {
            return _children.Count == 0 ? System.Array.Empty<RbxInstance>() : _children.ToArray();
        }

        /// <summary>Destroys every child (Roblox ClearAllChildren).</summary>
        public void ClearAllChildren()
        {
            ThrowIfDestroyed("ClearAllChildren");
            RbxInstance[] childrenCopy = _children.ToArray();
            foreach (RbxInstance child in childrenCopy)
            {
                child.Destroy();
            }
        }

        // ---- Attributes (R6.7) --------------------------------------------------------------

        public object GetAttribute(string attribute)
        {
            ThrowIfDestroyed("GetAttribute");
            AttributeContract.ValidateName(attribute);
            return _attributes.TryGetValue(attribute, out object value) ? value : null;
        }

        /// <summary>Null value removes the attribute (R6.7). Values are validated and numbers
        /// normalized to double for stable serialization. Adding an attribute beyond
        /// <see cref="InstanceTreeSerializer.MaximumAttributesPerInstance"/> on one instance
        /// raises BAD_ARGUMENT — the most a saved world stores; replacing or removing one never
        /// does.</summary>
        public void SetAttribute(string attribute, object value)
        {
            ThrowIfDestroyed("SetAttribute");
            AttributeContract.ValidateName(attribute);
            bool hadValue = _attributes.TryGetValue(attribute, out object previousValue);
            if (value == null)
            {
                if (!hadValue)
                {
                    return;
                }

                _attributes.Remove(attribute);
            }
            else
            {
                object normalized = AttributeContract.NormalizeValue(value);
                if (hadValue && Equals(previousValue, normalized))
                {
                    return;
                }

                if (!hadValue && _attributes.Count >= InstanceTreeSerializer.MaximumAttributesPerInstance)
                {
                    throw RbxError.BadArgument(
                        "cannot add attribute \"" + attribute + "\": " + _name + " already holds "
                        + InstanceTreeSerializer.MaximumAttributesPerInstance
                        + " attributes, the most one instance can keep in a saved world",
                        "remove an unused attribute with SetAttribute(name, nil) before adding another");
                }

                _attributes[attribute] = normalized;
            }

            Registry?.AdvanceRevision(Id, ReplicationMembers.Attribute(attribute));
            FireSignal("AttributeChanged", attribute);
            FireSignal("GetAttributeChangedSignal(" + attribute + ")");
        }

        public IReadOnlyDictionary<string, object> GetAttributes()
        {
            ThrowIfDestroyed("GetAttributes");
            return new Dictionary<string, object>(_attributes, System.StringComparer.Ordinal);
        }

        // ---- Tags (R6.8, CollectionService substrate) ---------------------------------------

        /// <summary>Idempotent. Adding a tag beyond
        /// <see cref="InstanceTreeSerializer.MaximumTagsPerInstance"/> on one instance raises
        /// BAD_ARGUMENT — the most a saved world stores.</summary>
        public void AddTag(string tag)
        {
            ThrowIfDestroyed("AddTag");
            bool alreadyTagged = Registry.Tags.HasTag(Id, tag);
            // TODO: read a per-instance tag count from InstanceTagStore once it exposes one;
            // GetTags copies and sorts the set, and this runs only for a tag that is new here.
            if (!alreadyTagged
                && Registry.Tags.GetTags(Id).Count >= InstanceTreeSerializer.MaximumTagsPerInstance)
            {
                throw RbxError.BadArgument(
                    "cannot add tag \"" + tag + "\": " + _name + " already holds "
                    + InstanceTreeSerializer.MaximumTagsPerInstance
                    + " tags, the most one instance can keep in a saved world",
                    "remove an unused tag with RemoveTag before adding another");
            }

            bool tagInUse = Registry.Tags.IsTagInUse(tag);
            Registry.Tags.AddTag(Id, tag);
            if (!alreadyTagged)
            {
                Registry.AdvanceRevision(Id, ReplicationMembers.Tag(tag));
                Registry.OnTagAdded(this, tag, !tagInUse);
            }
        }

        public void RemoveTag(string tag)
        {
            ThrowIfDestroyed("RemoveTag");
            bool wasTagged = Registry.Tags.HasTag(Id, tag);
            Registry.Tags.RemoveTag(Id, tag);
            if (wasTagged)
            {
                Registry.AdvanceRevision(Id, ReplicationMembers.Tag(tag));
                Registry.OnTagRemoved(this, tag, !Registry.Tags.IsTagInUse(tag));
            }
        }

        public bool HasTag(string tag)
        {
            ThrowIfDestroyed("HasTag");
            return Registry.Tags.HasTag(Id, tag);
        }

        public IReadOnlyList<string> GetTags()
        {
            ThrowIfDestroyed("GetTags");
            return Registry.Tags.GetTags(Id);
        }

        public RbxScriptSignal ChildAdded => GetSignal("ChildAdded");
        public RbxScriptSignal ChildRemoved => GetSignal("ChildRemoved");
        public RbxScriptSignal DescendantAdded => GetSignal("DescendantAdded");
        public RbxScriptSignal DescendantRemoving => GetSignal("DescendantRemoving");
        public RbxScriptSignal Destroying => GetSignal("Destroying");
        public RbxScriptSignal AncestryChanged => GetSignal("AncestryChanged");
        public RbxScriptSignal AttributeChanged => GetSignal("AttributeChanged");

        /// <summary>
        /// Mirror Object.Changed: fires with the NAME of a property right after that property
        /// changes (R6.11). Value objects replace it — <see cref="RbxValueBase.Changed"/> fires with
        /// the new Value, and only when Value changes.
        /// </summary>
        public RbxScriptSignal Changed => GetSignal(ChangedSignalName);

        public RbxScriptSignal GetAttributeChangedSignal(string attribute)
        {
            ThrowIfDestroyed("GetAttributeChangedSignal");
            AttributeContract.ValidateName(attribute);
            return GetSignal("GetAttributeChangedSignal(" + attribute + ")");
        }

        /// <summary>Mirror Object:GetPropertyChangedSignal: fires with no arguments right after
        /// <paramref name="property"/> changes; the same signal instance is returned on every
        /// call for one property.</summary>
        public RbxScriptSignal GetPropertyChangedSignal(string property)
        {
            ThrowIfDestroyed("GetPropertyChangedSignal");
            if (string.IsNullOrEmpty(property))
            {
                throw RbxError.BadArgument(
                    "GetPropertyChangedSignal expects a property name",
                    "pass the property to watch, e.g. part:GetPropertyChangedSignal(\"Name\")");
            }

            if (_propertyChangedSignals != null
                && _propertyChangedSignals.TryGetValue(property, out RbxScriptSignal signal))
            {
                return signal;
            }

            signal = GetSignal("GetPropertyChangedSignal(" + property + ")");
            _propertyChangedSignals ??= new Dictionary<string, RbxScriptSignal>(
                System.StringComparer.Ordinal);
            _propertyChangedSignals.Add(property, signal);
            return signal;
        }

        /// <summary>
        /// Fires <see cref="Changed"/> with <paramref name="propertyName"/> and that property's
        /// <see cref="GetPropertyChangedSignal"/> signal. Every property setter calls it once, right
        /// after a real change (an equal assignment is not a change). A value object keeps its own
        /// Changed contract, so for it only the per-property signal fires here. Costs no allocation
        /// while nothing listens.
        /// </summary>
        protected internal void NotifyPropertyChanged(string propertyName)
        {
            NotifyPropertyChangedCore(propertyName, false);
        }

        private void NotifyPropertyChangedCore(string propertyName, bool forDestruction)
        {
            if (_signals == null || string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            // WHY: Object.yaml — for ValueBase objects Changed "only fires when the object's Value
            // property changes", and it carries the new value; RbxValueBase fires that itself.
            if (!(this is RbxValueBase)
                && TryGetConnectedSignal(ChangedSignalName, out RbxScriptSignal changed))
            {
                FireOnSignal(changed, forDestruction, new object[] { propertyName });
            }

            if (_propertyChangedSignals != null
                && _propertyChangedSignals.TryGetValue(propertyName, out RbxScriptSignal propertySignal)
                && propertySignal.HasConnections)
            {
                FireOnSignal(propertySignal, forDestruction, System.Array.Empty<object>());
            }
        }

        private void FireOnSignal(RbxScriptSignal signal, bool forDestruction, object[] arguments)
        {
            if (forDestruction)
            {
                signal.FireForDestruction(this, arguments);
            }
            else
            {
                signal.Fire(arguments);
            }
        }

        /// <summary>Preorder snapshot of this subtree (this instance first) and its height in
        /// levels, counting this instance as one.</summary>
        private List<RbxInstance> SnapshotSubtree(out int height)
        {
            List<RbxInstance> result = new() { this };
            height = 1;
            if (_children.Count == 0)
            {
                return result;
            }

            Stack<WalkFrame> pending = RentWalkStack();
            PushChildren(pending, this, 2);
            while (pending.Count > 0)
            {
                WalkFrame frame = pending.Pop();
                result.Add(frame.Instance);
                if (frame.Depth > height)
                {
                    height = frame.Depth;
                }

                PushChildren(pending, frame.Instance, frame.Depth + 1);
            }

            ReturnWalkStack(pending);
            return result;
        }

        /// <summary>Pushes the children last-first, so the stack pops them in insertion order
        /// and the walk stays in preorder.</summary>
        private static void PushChildren(Stack<WalkFrame> pending, RbxInstance parent, int depth)
        {
            List<RbxInstance> children = parent._children;
            for (int index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(new WalkFrame(children[index], depth));
            }
        }

        private static Stack<WalkFrame> RentWalkStack()
        {
            Stack<WalkFrame> stack = _walkScratch;
            if (stack == null)
            {
                return new Stack<WalkFrame>();
            }

            _walkScratch = null;
            return stack;
        }

        private static void ReturnWalkStack(Stack<WalkFrame> stack)
        {
            stack.Clear();
            _walkScratch = stack;
        }

        private static List<RbxInstance> SnapshotAncestors(RbxInstance start)
        {
            List<RbxInstance> result = new();
            RbxInstance current = start;
            while (current != null)
            {
                result.Add(current);
                current = current._parent;
            }

            return result;
        }

        protected void FireSignal(string signalName, params object[] arguments)
        {
            if (_signals != null
                && _signals.TryGetValue(signalName, out RbxScriptSignal signal))
            {
                signal.Fire(arguments);
            }
        }

        private void FireSignalForDestruction(string signalName, params object[] arguments)
        {
            if (_signals != null
                && _signals.TryGetValue(signalName, out RbxScriptSignal signal))
            {
                signal.FireForDestruction(this, arguments);
            }
        }

        /// <summary>Fires a one-argument signal, building the argument array only when a
        /// connection will receive it.</summary>
        private void FireIfConnected(string signalName, object argument)
        {
            if (TryGetConnectedSignal(signalName, out RbxScriptSignal signal))
            {
                signal.Fire(argument);
            }
        }

        private bool TryGetConnectedSignal(string signalName, out RbxScriptSignal signal)
        {
            if (_signals != null
                && _signals.TryGetValue(signalName, out signal)
                && signal.HasConnections)
            {
                return true;
            }

            signal = null;
            return false;
        }

        /// <summary>True when a script is listening to the named signal.</summary>
        /// <remarks>
        /// WHY: the highest-frequency events a world produces (contacts) must cost nothing when
        /// nobody subscribed, and asking the signal for its connections is the only honest way to
        /// know — GetOrCreateSignal would itself create the signal and answer "yes".
        /// </remarks>
        internal bool HasSignalConnections(string signalName)
        {
            return _signals != null
                   && _signals.TryGetValue(signalName, out RbxScriptSignal signal)
                   && signal.HasConnections;
        }

        private void DisconnectSignals()
        {
            if (_signals == null)
            {
                return;
            }

            foreach (RbxScriptSignal signal in _signals.Values)
            {
                signal.DisconnectAll();
            }
        }

        private RbxScriptSignal GetSignal(string signalName)
        {
            return GetOrCreateSignal(signalName);
        }

        /// <summary>Lazily resolves a named signal; subclasses expose theirs over this, and
        /// <see cref="Destroy"/> disconnects every signal made through it.</summary>
        protected RbxScriptSignal GetOrCreateSignal(string signalName)
        {
            _signals ??= new Dictionary<string, RbxScriptSignal>(System.StringComparer.Ordinal);
            if (!_signals.TryGetValue(signalName, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal(ClassName + "." + signalName);
                _signals.Add(signalName, signal);
            }

            return signal;
        }

        // ---- Guards -------------------------------------------------------------------------

        /// <summary>Copies subclass state into a <see cref="Clone()"/> copy; the base shape
        /// (name/archivable/attributes/tags/children) is copied by the caller. No-op here.</summary>
        protected internal virtual void CopyCustomStateTo(RbxInstance copy)
        {
        }

        /// <summary>
        /// Clone reference fix-up (R6.5), run on every copy once the whole cloned subtree exists:
        /// a reference property still pointing at a source instance is passed through
        /// <see cref="CloneReferenceMap.Resolve"/>, which answers that instance's copy when it was
        /// cloned in the same call and the instance itself otherwise. Silent — no signal, no
        /// revision, like <see cref="CopyCustomStateTo"/>. No-op here.
        /// </summary>
        private protected virtual void RemapClonedReferences(in CloneReferenceMap map)
        {
        }

        protected void ThrowIfDestroyed(string memberName)
        {
            if (_destroyed)
            {
                throw RbxError.InstanceDestroyed(memberName, _name, Id);
            }
        }

        private static string CapNameLength(string name)
        {
            if (name.Length <= MaxNameLength)
            {
                return name;
            }

            int length = char.IsHighSurrogate(name[MaxNameLength - 1])
                ? MaxNameLength - 1
                : MaxNameLength;
            return name.Substring(0, length);
        }

        public override string ToString()
        {
            return _name;
        }
    }

    /// <summary>The source-to-copy pairs of one <see cref="RbxInstance.Clone()"/> call.</summary>
    internal readonly struct CloneReferenceMap
    {
        private readonly RbxInstance _rootSource;
        private readonly RbxInstance _rootCopy;
        private readonly Dictionary<RbxInstance, RbxInstance> _descendantCopies;

        internal CloneReferenceMap(RbxInstance rootSource, RbxInstance rootCopy,
            Dictionary<RbxInstance, RbxInstance> descendantCopies)
        {
            _rootSource = rootSource;
            _rootCopy = rootCopy;
            _descendantCopies = descendantCopies;
        }

        /// <summary>The copy made of <paramref name="reference"/> by this Clone, or
        /// <paramref name="reference"/> itself when it was not cloned (mirror Instance:Clone:
        /// "the same value is maintained in the copy").</summary>
        internal RbxInstance Resolve(RbxInstance reference)
        {
            if (reference == null)
            {
                return null;
            }

            if (ReferenceEquals(reference, _rootSource))
            {
                return _rootCopy;
            }

            return _descendantCopies != null
                   && _descendantCopies.TryGetValue(reference, out RbxInstance copy)
                ? copy
                : reference;
        }
    }

    /// <summary>Runtime state owned by Model descendants for the PVInstance pivot surface.</summary>
    public sealed class RbxModel : RbxInstance
    {
        private RbxInstance _primaryPart;
        private RbxCFrame _storedWorldPivot = RbxCFrame.Identity;
        private bool _hasStoredWorldPivot;

        protected internal RbxModel(ClassDescriptor descriptor) : base(descriptor)
        {
        }

        public RbxInstance PrimaryPart => _primaryPart;

        public bool HasStoredWorldPivot => _hasStoredWorldPivot;

        public RbxCFrame StoredWorldPivot => _storedWorldPivot;

        public void SetPrimaryPart(RbxInstance primaryPart)
        {
            ThrowIfDestroyed("PrimaryPart");
            if (primaryPart != null && !primaryPart.IsA("BasePart"))
            {
                throw RbxError.BadArgument(
                    "Model.PrimaryPart expects a BasePart or nil",
                    "assign a BasePart descendant of the Model, or nil");
            }

            if (ReferenceEquals(_primaryPart, primaryPart))
            {
                return;
            }

            _primaryPart = primaryPart;
            Registry?.AdvanceRevision(Id, ReplicationMembers.PrimaryPart);
            NotifyPropertyChanged(ReplicationMembers.PrimaryPart);
        }

        public void SetWorldPivot(in RbxCFrame worldPivot)
        {
            ThrowIfDestroyed("WorldPivot");
            if (_hasStoredWorldPivot && _storedWorldPivot == worldPivot)
            {
                return;
            }

            _storedWorldPivot = worldPivot;
            _hasStoredWorldPivot = true;
            Registry?.AdvanceRevision(Id, ReplicationMembers.WorldPivot);
            NotifyPropertyChanged(ReplicationMembers.WorldPivot);
        }

        internal void ResetInvalidPrimaryPart()
        {
            if (_primaryPart != null
                && (_primaryPart.IsDestroyed || !_primaryPart.IsDescendantOf(this)))
            {
                SetPrimaryPart(null);
            }
        }

        /// <summary>Clone carries PrimaryPart and the stored WorldPivot (R6.5); PrimaryPart is
        /// redirected to the copied part by <see cref="RemapClonedReferences"/>.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            if (copy is RbxModel modelCopy)
            {
                modelCopy._primaryPart = _primaryPart;
                modelCopy._storedWorldPivot = _storedWorldPivot;
                modelCopy._hasStoredWorldPivot = _hasStoredWorldPivot;
            }
        }

        private protected override void RemapClonedReferences(in CloneReferenceMap map)
        {
            _primaryPart = map.Resolve(_primaryPart);
        }
    }
}
