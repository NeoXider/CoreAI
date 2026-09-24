using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Engine-free <see cref="IPartPropertySink"/>: stores BasePart spatial/appearance state in
    /// pure Roblox space (studs, right-handed) keyed by <see cref="InstanceId"/>, with no Unity
    /// materialization. Analog of <see cref="CoreAI.Mods.Rbx.Instances.InMemoryInstanceBackingBinder"/>
    /// for the part-property seam: the headless/solo default and the test double the Lua bindings
    /// use when no live <see cref="InstanceGameObjectBinder"/> is wired, so scripts read and write
    /// Part properties through the same <see cref="PartProperties"/> path the Unity binder uses.
    /// A destroyed part's state leaves the live store and is retained, bounded, for destruction
    /// handlers (<see cref="IPartPropertySink.OnPartDestroyed"/>); built with a registry, the sink
    /// hears every destruction itself.
    /// </summary>
    public sealed class InMemoryPartPropertySink : IPartPropertySink
    {
        /// <summary>
        /// How many destroyed parts keep their last-known state for reads made by destruction
        /// handlers, in both sinks. The oldest destroyed part is forgotten first.
        /// </summary>
        /// <remarks>
        /// WHY 2048: it equals the default per-actor instance quota
        /// (LuaCsModRuntime.DefaultMaxRegisteredInstancesPerActor), so a mod can destroy everything it
        /// built in one call (a Model holding all of it) and every Destroying handler of that burst
        /// still reads real values, while a world that destroys parts forever holds at most this many
        /// bundles (a few hundred KB).
        /// </remarks>
        public const int DestroyedPartRetention = 2048;

        private readonly Dictionary<InstanceId, PartProperties> _properties = new();
        private readonly DestroyedPartStateStore _destroyed = new(DestroyedPartRetention);

        /// <summary>A sink told about destruction through <see cref="OnPartDestroyed"/>.</summary>
        public InMemoryPartPropertySink()
        {
        }

        /// <summary>
        /// A sink that releases a part's state by itself when <paramref name="registry"/> destroys
        /// the instance, the way the Unity binder hears it through the backing-binder seam.
        /// </summary>
        public InMemoryPartPropertySink(InstanceRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            registry.Unregistered += OnInstanceUnregistered;
        }

        /// <summary>Diagnostic: bundles held for parts that are still alive.</summary>
        public int LivePartCount => _properties.Count;

        /// <summary>Diagnostic: last-known bundles held for destroyed parts; never above
        /// <see cref="DestroyedPartRetention"/>.</summary>
        public int RetainedDestroyedPartCount => _destroyed.Count;

        public void SetCFrame(InstanceId id, in RbxCFrame cframe)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CFrame = cframe;
            Put(id, properties);
        }

        public void SetPosition(InstanceId id, RbxVector3 position)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Position = position;
            Put(id, properties);
        }

        /// <summary>
        /// Finite axes are clamped exactly as the Unity binder clamps them; a non-finite axis is kept.
        /// </summary>
        /// <remarks>
        /// WHY non-finite values are stored here while the Unity binder refuses them: the binder
        /// refuses because the engine cannot hold such a pose and the stored value would stop
        /// matching what renders. This sink renders nothing, so there is nothing to diverge from,
        /// and the durable boundary for non-finite state is the world-package capture projection,
        /// which reports each such member with a diagnostic
        /// (pinned by Mvp3WorldPackageEditModeTests.
        /// Capture_EveryScriptReachableNonFiniteMember_PackagesItsDefaultWithOneDiagnosticEach).
        /// </remarks>
        public void SetSize(InstanceId id, RbxVector3 size)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Size = PartPropertyBounds.ClampSize(size);
            Put(id, properties);
        }

        public void SetColor(InstanceId id, RbxColor3 color)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Color = color;
            properties.ColorWasExplicitlySet = true;
            Put(id, properties);
        }

        public void SetAnchored(InstanceId id, bool anchored)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Anchored = anchored;
            Put(id, properties);
        }

        public void SetTransparency(InstanceId id, float transparency)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            // WHY: clamp to Roblox's [0, 1] like the Unity binder so both sinks store identically.
            properties.Transparency = transparency < 0f ? 0f : transparency > 1f ? 1f : transparency;
            Put(id, properties);
        }

        public void SetCanCollide(InstanceId id, bool canCollide)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CanCollide = canCollide;
            Put(id, properties);
        }

        public void SetShape(InstanceId id, RbxPartShape shape)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Shape = shape;
            Put(id, properties);
        }

        public void SetMaterial(InstanceId id, in RbxMaterialId material)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Material = material;
            Put(id, properties);
        }

        public void SetMaterialVariant(InstanceId id, string variantName)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.MaterialVariant = string.IsNullOrEmpty(variantName) ? null : variantName;
            Put(id, properties);
        }

        /// <summary>No-op: this sink stores properties and renders nothing.</summary>
        public void RefreshMaterialVariant(string variantName)
        {
        }

        public void SetPartProperties(InstanceId id, in PartProperties properties)
        {
            PartProperties bounded = properties;
            bounded.Size = PartPropertyBounds.ClampSize(properties.Size);
            _destroyed.Forget(id);
            _properties[id] = bounded;
        }

        public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
        {
            return _properties.TryGetValue(id, out properties) || _destroyed.TryGet(id, out properties);
        }

        public PartProperties GetPartPropertiesOrDefault(InstanceId id)
        {
            return TryGetPartProperties(id, out PartProperties properties)
                ? properties
                : PartProperties.CreateDefault();
        }

        /// <inheritdoc />
        public void OnPartDestroyed(InstanceId id)
        {
            if (_properties.TryGetValue(id, out PartProperties last))
            {
                _properties.Remove(id);
                _destroyed.Remember(id, in last);
            }
        }

        /// <summary>
        /// A per-property write lands in the retained copy of a destroyed part, never back in the
        /// live store: a late host write to a dead id must not re-grow the store destruction just
        /// released.
        /// </summary>
        private void Put(InstanceId id, in PartProperties properties)
        {
            if (!_properties.ContainsKey(id) && _destroyed.TryReplace(id, in properties))
            {
                return;
            }

            _properties[id] = properties;
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (record != null)
            {
                OnPartDestroyed(record.Id);
            }
        }
    }

    /// <summary>
    /// Last-known state of destroyed parts, holding at most a fixed number of entries and forgetting
    /// the oldest destroyed part first. Shared by both sinks so a destruction handler reads the same
    /// answer whether or not the world renders.
    /// </summary>
    internal sealed class DestroyedPartStateStore
    {
        private readonly int _capacity;
        private readonly LinkedList<KeyValuePair<InstanceId, PartProperties>> _order = new();

        private readonly Dictionary<InstanceId, LinkedListNode<KeyValuePair<InstanceId, PartProperties>>>
            _nodes = new();

        public DestroyedPartStateStore(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 1;
        }

        public int Count => _nodes.Count;

        /// <summary>Retains <paramref name="properties"/> for a part that was just destroyed,
        /// evicting the oldest retained parts beyond the capacity.</summary>
        public void Remember(InstanceId id, in PartProperties properties)
        {
            if (TryReplace(id, in properties))
            {
                return;
            }

            _nodes[id] = _order.AddLast(new KeyValuePair<InstanceId, PartProperties>(id, properties));
            while (_nodes.Count > _capacity)
            {
                LinkedListNode<KeyValuePair<InstanceId, PartProperties>> oldest = _order.First;
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value.Key);
            }
        }

        /// <summary>Overwrites a retained entry in place, keeping its eviction position; false when
        /// the id is not retained.</summary>
        public bool TryReplace(InstanceId id, in PartProperties properties)
        {
            if (!_nodes.TryGetValue(id, out LinkedListNode<KeyValuePair<InstanceId, PartProperties>> node))
            {
                return false;
            }

            node.Value = new KeyValuePair<InstanceId, PartProperties>(id, properties);
            return true;
        }

        public bool TryGet(InstanceId id, out PartProperties properties)
        {
            if (_nodes.TryGetValue(id, out LinkedListNode<KeyValuePair<InstanceId, PartProperties>> node))
            {
                properties = node.Value.Value;
                return true;
            }

            properties = default;
            return false;
        }

        /// <summary>Drops the retained entry of an id that is a live part again.</summary>
        public void Forget(InstanceId id)
        {
            if (_nodes.TryGetValue(id, out LinkedListNode<KeyValuePair<InstanceId, PartProperties>> node))
            {
                _nodes.Remove(id);
                _order.Remove(node);
            }
        }
    }
}
