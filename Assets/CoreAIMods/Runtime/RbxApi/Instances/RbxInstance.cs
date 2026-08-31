using System.Collections.Generic;

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
    /// </summary>
    public class RbxInstance
    {
        private readonly ClassDescriptor _descriptor;
        private readonly List<RbxInstance> _children = new();
        private readonly Dictionary<string, object> _attributes = new(System.StringComparer.Ordinal);
        private Dictionary<string, RbxScriptSignal> _signals;

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

        /// <summary>Tombstone-readable after Destroy (DEV-7).</summary>
        public string Name
        {
            get => _name;
            set
            {
                ThrowIfDestroyed("Name");
                string nextName = value ?? throw RbxError.BadArgument("Name cannot be nil",
                    "pass a string, e.g. instance.Name = \"SpawnPad\"");
                if (string.Equals(_name, nextName, System.StringComparison.Ordinal))
                {
                    return;
                }

                _name = nextName;
                Registry?.OnNameChanged(this);
                Registry?.AdvanceRevision(Id);
                FireSignal("GetPropertyChangedSignal(Name)");
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
                Registry?.AdvanceRevision(Id);
                FireSignal("GetPropertyChangedSignal(Archivable)");
            }
        }

        public bool IsDestroyed => _destroyed;

        /// <summary>Tombstone-readable after Destroy (always null then). Setter runs the full
        /// re-parent pipeline with hierarchy validation; throws PARENT_LOCKED after Destroy (D6).</summary>
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

            bool wasInScene = Registry != null && Registry.IsInScene(this);
            RbxInstance oldParent = _parent;
            List<RbxInstance> movedSubtree = SnapshotSubtree();
            List<RbxInstance> oldAncestors = SnapshotAncestors(oldParent);
            List<RbxInstance> newAncestors = SnapshotAncestors(newParent);
            for (int ancestorIndex = 0; ancestorIndex < oldAncestors.Count; ancestorIndex++)
            {
                RbxInstance ancestor = oldAncestors[ancestorIndex];
                for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
                {
                    ancestor.FireSignal("DescendantRemoving", movedSubtree[movedIndex]);
                }
            }

            oldParent?._children.Remove(this);
            _parent = newParent;
            newParent?._children.Add(this);

            Registry?.AdvanceRevision(Id);
            oldParent?.Registry?.AdvanceRevision(oldParent.Id);
            newParent?.Registry?.AdvanceRevision(newParent.Id);

            Registry?.OnParentChanged(this, wasInScene);
            oldParent?.FireSignal("ChildRemoved", this);
            newParent?.FireSignal("ChildAdded", this);
            FireSignal("GetPropertyChangedSignal(Parent)");
            for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
            {
                RbxInstance moved = movedSubtree[movedIndex];
                if (_destroying)
                {
                    moved.FireSignalForDestruction("AncestryChanged", moved, moved._parent);
                }
                else
                {
                    moved.FireSignal("AncestryChanged", moved, moved._parent);
                }
            }

            for (int ancestorIndex = 0; ancestorIndex < newAncestors.Count; ancestorIndex++)
            {
                RbxInstance ancestor = newAncestors[ancestorIndex];
                for (int movedIndex = 0; movedIndex < movedSubtree.Count; movedIndex++)
                {
                    ancestor.FireSignal("DescendantAdded", movedSubtree[movedIndex]);
                }
            }
        }

        // ---- Navigation (R6.10) -------------------------------------------------------------

        public RbxInstance FindFirstChild(string name, bool recursive = false)
        {
            ThrowIfDestroyed("FindFirstChild");
            // WHY: depth-first (check child, then its whole subtree, then next sibling) matches
            // Roblox's recursive search order.
            foreach (RbxInstance child in _children)
            {
                if (string.Equals(child._name, name, System.StringComparison.Ordinal))
                {
                    return child;
                }

                if (recursive)
                {
                    RbxInstance found = child.FindFirstChild(name, true);
                    if (found != null)
                    {
                        return found;
                    }
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
            // WHY: depth-first matches Roblox's recursive search order.
            foreach (RbxInstance child in _children)
            {
                if (child.IsA(className))
                {
                    return child;
                }

                if (recursive)
                {
                    RbxInstance found = child.FindFirstChildWhichIsA(className, true);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            return null;
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
            foreach (RbxInstance child in _children)
            {
                result.Add(child);
                child.CollectDescendants(result);
            }
        }

        /// <summary>Walks the data-driven class ancestry including "Instance" (§5.1.7 risk table).</summary>
        public bool IsA(string className)
        {
            return Registry != null
                ? Registry.Catalog.IsA(ClassName, className)
                : string.Equals(ClassName, className, System.StringComparison.Ordinal);
        }

        public bool IsDescendantOf(RbxInstance ancestor)
        {
            if (ancestor == null)
            {
                return false;
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
        /// instance itself is non-archivable (R6.5). Fresh ids; Parent is null; attributes and
        /// tags copy; identity is never cloned (D8).</summary>
        public RbxInstance Clone()
        {
            ThrowIfDestroyed("Clone");
            if (!_archivable)
            {
                return null;
            }

            return CloneSubtree(false, null, null);
        }

        /// <summary>Deep-copies this subtree under the supplied teardown owner and provenance.</summary>
        public RbxInstance Clone(string ownerModId, string originTag)
        {
            ThrowIfDestroyed("Clone");
            if (!_archivable)
            {
                return null;
            }

            return CloneSubtree(true, ownerModId, originTag);
        }

        private RbxInstance CloneSubtree(bool overrideOwnership, string ownerModId, string originTag)
        {
            InstanceRecord sourceRecord = Registry.GetRecord(Id);
            InstanceIdAuthority authority = Id.IsServerAssigned
                ? InstanceIdAuthority.Server
                : InstanceIdAuthority.Local;
            string resolvedOwnerModId = overrideOwnership ? ownerModId : sourceRecord.OwnerModId;
            string resolvedOriginTag = overrideOwnership ? originTag : sourceRecord.OriginTag;
            RbxInstance copy = Registry.Create(
                ClassName, resolvedOwnerModId, resolvedOriginTag, authority);
            copy._name = _name;
            copy._archivable = _archivable;
            foreach (KeyValuePair<string, object> attribute in _attributes)
            {
                copy._attributes[attribute.Key] = attribute.Value;
            }

            foreach (string tag in Registry.Tags.GetTags(Id))
            {
                Registry.Tags.AddTag(copy.Id, tag);
            }

            foreach (RbxInstance child in _children)
            {
                if (!child._archivable)
                {
                    continue;
                }

                RbxInstance childCopy = child.CloneSubtree(
                    overrideOwnership, ownerModId, originTag);
                childCopy.SetParent(copy);
            }

            return copy;
        }

        /// <summary>
        /// Atomic destroy per R6.2/D6 at registry level: (1) detach — Parent set to nil,
        /// (2) Parent locked, (3) signals disconnect while preserving pending invocations,
        /// (4) children destroy recursively, (5) tags cleared and the registry record
        /// unregistered, (6) the backing binder releases the backing object. Idempotent.
        /// </summary>
        public void Destroy()
        {
            if (_destroyed)
            {
                return;
            }

            _destroying = true;
            FireSignalForDestruction("Destroying");
            SetParent(null);
            _destroyed = true;
            DisconnectSignals();

            RbxInstance[] childrenCopy = _children.ToArray();
            foreach (RbxInstance child in childrenCopy)
            {
                child.Destroy();
            }

            Registry?.OnInstanceDestroyed(this);
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
        /// normalized to double for stable serialization.</summary>
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

                _attributes[attribute] = normalized;
            }

            Registry?.AdvanceRevision(Id);
            FireSignal("AttributeChanged", attribute);
            FireSignal("GetAttributeChangedSignal(" + attribute + ")");
        }

        public IReadOnlyDictionary<string, object> GetAttributes()
        {
            ThrowIfDestroyed("GetAttributes");
            return new Dictionary<string, object>(_attributes, System.StringComparer.Ordinal);
        }

        // ---- Tags (R6.8, CollectionService substrate) ---------------------------------------

        public void AddTag(string tag)
        {
            ThrowIfDestroyed("AddTag");
            bool alreadyTagged = Registry.Tags.HasTag(Id, tag);
            Registry.Tags.AddTag(Id, tag);
            if (!alreadyTagged)
            {
                Registry.AdvanceRevision(Id);
            }
        }

        public void RemoveTag(string tag)
        {
            ThrowIfDestroyed("RemoveTag");
            bool wasTagged = Registry.Tags.HasTag(Id, tag);
            Registry.Tags.RemoveTag(Id, tag);
            if (wasTagged)
            {
                Registry.AdvanceRevision(Id);
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

        public RbxScriptSignal GetAttributeChangedSignal(string attribute)
        {
            ThrowIfDestroyed("GetAttributeChangedSignal");
            AttributeContract.ValidateName(attribute);
            return GetSignal("GetAttributeChangedSignal(" + attribute + ")");
        }

        public RbxScriptSignal GetPropertyChangedSignal(string property)
        {
            ThrowIfDestroyed("GetPropertyChangedSignal");
            return GetSignal("GetPropertyChangedSignal(" + property + ")");
        }

        private List<RbxInstance> SnapshotSubtree()
        {
            List<RbxInstance> result = new();
            AddSubtree(this, result);
            return result;
        }

        private static void AddSubtree(RbxInstance instance, List<RbxInstance> result)
        {
            result.Add(instance);
            for (int index = 0; index < instance._children.Count; index++)
            {
                AddSubtree(instance._children[index], result);
            }
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

        private void FireSignal(string signalName, params object[] arguments)
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
            _signals ??= new Dictionary<string, RbxScriptSignal>(System.StringComparer.Ordinal);
            if (!_signals.TryGetValue(signalName, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal(ClassName + "." + signalName);
                _signals.Add(signalName, signal);
            }

            return signal;
        }

        // ---- Guards -------------------------------------------------------------------------

        protected void ThrowIfDestroyed(string memberName)
        {
            if (_destroyed)
            {
                throw RbxError.InstanceDestroyed(memberName, _name, Id);
            }
        }

        public override string ToString()
        {
            return _name;
        }
    }
}
