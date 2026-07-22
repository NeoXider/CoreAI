using System.Collections.Generic;

namespace CoreAI.RobloxApi.Instances
{
    /// <summary>
    /// Engine-free core of the Roblox Instance member set (roadmap §5.1.2): hierarchy,
    /// navigation, lifecycle, attributes, and tags. Geometry-free by design — spatial
    /// properties live in later slices behind the property system. Instances are created only
    /// through <see cref="InstanceRegistry"/>, which owns identity (§3.3).
    /// Destroyed-instance policy (DEV-7, registry-level interpretation): tombstone reads
    /// (Name, ClassName, Parent, IsDestroyed) stay available at the C# Domain level; every
    /// mutation and navigation raises INSTANCE_DESTROYED. The stricter Lua-context rule
    /// (tombstone only inside destruction-queued handlers) is enforced by the marshalling
    /// layer when it lands.
    /// </summary>
    public class RbxInstance
    {
        private readonly ClassDescriptor _descriptor;
        private readonly List<RbxInstance> _children = new List<RbxInstance>();
        private readonly Dictionary<string, object> _attributes =
            new Dictionary<string, object>(System.StringComparer.Ordinal);
        private Dictionary<string, RbxScriptSignal> _signals;

        private string _name;
        private bool _archivable = true;
        private RbxInstance _parent;
        private bool _destroyed;

        protected internal RbxInstance(ClassDescriptor descriptor)
        {
            _descriptor = descriptor ?? throw new System.ArgumentNullException(nameof(descriptor));
            _name = descriptor.Name;
        }

        internal InstanceRegistry Registry { get; private set; }

        internal ClassDescriptor Descriptor => _descriptor;

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
                _name = value ?? throw RbxError.BadArgument("Name cannot be nil",
                    "pass a string, e.g. instance.Name = \"SpawnPad\"");
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
                _archivable = value;
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

            bool wasInWorld = Registry != null && Registry.IsInWorld(this);

            RbxInstance oldParent = _parent;
            oldParent?._children.Remove(this);
            _parent = newParent;
            newParent?._children.Add(this);

            Registry?.OnParentChanged(this, wasInWorld);

            // TODO: MVP2 — fire ChildAdded/ChildRemoved/AncestryChanged/DescendantAdded on the
            // deferred signal queue; the hook points exist (signal properties) but stay inert.
        }

        // ---- Navigation (R6.10) -------------------------------------------------------------

        public RbxInstance FindFirstChild(string name, bool recursive = false)
        {
            ThrowIfDestroyed("FindFirstChild");
            foreach (RbxInstance child in _children)
            {
                if (string.Equals(child._name, name, System.StringComparison.Ordinal))
                {
                    return child;
                }
            }

            if (recursive)
            {
                foreach (RbxInstance child in _children)
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
            foreach (RbxInstance child in _children)
            {
                if (child.IsA(className))
                {
                    return child;
                }
            }

            if (recursive)
            {
                foreach (RbxInstance child in _children)
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
            var result = new List<RbxInstance>();
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
            var names = new List<string>();
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

            return CloneSubtree();
        }

        private RbxInstance CloneSubtree()
        {
            // WHY: the clone inherits owner/origin from the source record so the ownership
            // ledger keeps attributing clone-created content to whoever created the original.
            InstanceRecord sourceRecord = Registry.GetRecord(Id);
            InstanceIdAuthority authority = Id.IsServerAssigned
                ? InstanceIdAuthority.Server
                : InstanceIdAuthority.Local;
            RbxInstance copy = Registry.Create(ClassName, sourceRecord.OwnerModId,
                sourceRecord.OriginTag, authority);
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

                RbxInstance childCopy = child.CloneSubtree();
                childCopy.SetParent(copy);
            }

            return copy;
        }

        /// <summary>
        /// Atomic destroy per R6.2/D6 at registry level: (1) detach — Parent set to nil,
        /// (2) Parent locked, (3) connections would disconnect here (inert until MVP2),
        /// (4) children destroy recursively, (5) tags cleared and the registry record
        /// unregistered, (6) the backing binder releases the backing object. Idempotent.
        /// </summary>
        public void Destroy()
        {
            if (_destroyed)
            {
                return;
            }

            SetParent(null);
            _destroyed = true;

            // TODO: MVP2 — enqueue Destroying/AncestryChanged on the deferred queue and
            // disconnect all connections (R5.7/R5.12); signals are inert in MVP1.

            var childrenCopy = _children.ToArray();
            _children.Clear();
            foreach (RbxInstance child in childrenCopy)
            {
                child._parent = null;
                child.Destroy();
            }

            Registry?.OnInstanceDestroyed(this);
        }

        /// <summary>Destroys every child (Roblox ClearAllChildren).</summary>
        public void ClearAllChildren()
        {
            ThrowIfDestroyed("ClearAllChildren");
            var childrenCopy = _children.ToArray();
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
            if (value == null)
            {
                _attributes.Remove(attribute);
            }
            else
            {
                _attributes[attribute] = AttributeContract.NormalizeValue(value);
            }

            // TODO: MVP2 — fire AttributeChanged / GetAttributeChangedSignal on the deferred queue.
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
            Registry.Tags.AddTag(Id, tag);
        }

        public void RemoveTag(string tag)
        {
            ThrowIfDestroyed("RemoveTag");
            Registry.Tags.RemoveTag(Id, tag);
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

        // ---- Signal hook points (inert until MVP2, roadmap §5.1.6) --------------------------

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

        public override string ToString() => _name;
    }
}
