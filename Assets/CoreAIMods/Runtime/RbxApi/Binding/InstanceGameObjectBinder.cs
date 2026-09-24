using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity adapter of the backing-object seam (ROBLOX_API_ROADMAP.md §5.1.1 task 7): mirrors
    /// the whole Roblox explorer into the Unity hierarchy. The DataModel (game) IS the host
    /// GameObject; services parent under it; Folder/Model/Part nest under their instance parents.
    /// Semantics per D5: materialize on entering the scene (DataModel) subtree, DEACTIVATE (not
    /// destroy) on detach so re-parenting stays cheap, destroy on Destroy. Parts become unit-cube
    /// primitives scaled by Size * RbxSpace.MetersPerStud (asset rule, §2 — assets are never
    /// rescaled, only numbers convert); services/Folder/Model become empty transforms. Only
    /// Workspace and its descendants are the physical world (Workspace.yaml): every other child of
    /// the DataModel (Lighting, Players, MaterialService, the storage services, a Part parented
    /// straight to game) materializes INACTIVE, so its subtree never renders, collides, fires
    /// Touched or answers a raycast. A Part re-parented out of Workspace slides under an inactive
    /// GameObject (or has its own flag recomputed) and leaves the world via Unity's
    /// activeInHierarchy. Every spatial conversion goes through RbxSpace (D2) — this class holds
    /// the binder's single call sites allowed by the lint.
    /// CanCollide=false keeps the part's collider as a trigger: bodies pass through it, yet it
    /// still reports contacts (Touched) and is hit by raycasts unless RespectCanCollide is set.
    /// Host objects adopted through <see cref="AdoptWorldObject"/> follow pose writes only; their
    /// scale, mesh, collider, material and Rigidbody stay exactly as the host authored them.
    /// Shapes: Block maps to the unit cube, Ball to the unit sphere (both directly on the part
    /// GameObject, localScale = Size * MetersPerStud); Cylinder needs an axis correction, so its
    /// mesh lives on a rotated child (see <see cref="BuildCylinderVisual"/>); Wedge and CornerWedge
    /// use custom normalized meshes on the root.
    /// A part's instance children never sit under the part's own GameObject: they materialize in
    /// the part's child container, an unscaled, identity-pose sibling named after the part plus
    /// <see cref="ChildContainerSuffix"/> that follows the part through re-parenting and world
    /// membership. Roblox Size is absolute and a part under a part is independent unless welded,
    /// so a nested part neither inherits its parent's Size-driven scale nor moves with it, and its
    /// collider never joins the parent's Rigidbody as a compound collider.
    /// A simulated (unanchored) part reads back the pose its body actually has, and every write
    /// starts from that pose, so gravity or a character motor is never undone by a later Size,
    /// Shape or Anchored write. A destroyed part keeps its last-known state, bounded by
    /// <see cref="InMemoryPartPropertySink.DestroyedPartRetention"/>, for destruction handlers.
    /// Writes are held to Roblox's bounds at this boundary: Size is clamped per axis by
    /// <see cref="PartPropertyBounds"/>, and a non-finite Position, CFrame or Size is refused with
    /// BAD_ARGUMENT, because the engine cannot hold such a pose and the stored value would stop
    /// matching what renders.
    /// TODO: MVP8 — colliders approximate the visual (non-uniform Ball → SphereCollider on the max
    /// axis; Cylinder → CapsuleCollider with rounded ends); swap to exact colliders with physics.
    /// TODO: MVP8 — velocity read-back (AssemblyLinearVelocity) for simulated parts.
    /// </summary>
    public sealed class InstanceGameObjectBinder : IInstanceBackingBinder, IPartPropertySink
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int NeutralDefaultPartColorPropertyId =
            Shader.PropertyToID("_NeutralDefaultPartColor");

        // WHY: Unity has no wedge primitive, so we author one normalized to 1 unit = 1 stud (the
        // only stud-authored asset the scale rule allows, §2) and share the single mesh across all
        // wedge parts — the root's Size-driven localScale carries the dimensions like Block/Ball.
        private static Mesh _wedgeMesh;
        private static Mesh _cornerWedgeMesh;

        private static Mesh _cubeMesh;
        private static Mesh _sphereMesh;
        private static Material _defaultMaterial;
        private static bool _loggedFirstPart;

        private readonly Transform _worldParent;
        private readonly IRbxMaterialProvider<Material> _materialProvider;
        private readonly Dictionary<InstanceId, BindingEntry> _bindings = new();

        // WHY: every contact event and every ray hit maps a collider back to its instance, and a scan
        // of _bindings made that cost grow with the bound set on the hottest physics paths. Keyed by
        // reference, like the scan it replaces, so Unity's overloaded equality never runs here and an
        // externally destroyed GameObject can still be removed.
        private readonly Dictionary<GameObject, InstanceId> _idsByGameObject =
            new(GameObjectReferenceComparer.Instance);

        private readonly Dictionary<InstanceId, PartProperties> _partProperties = new();

        // WHY an index by variant name (A3-08): editing a MaterialVariant repaints the parts wearing
        // it, and finding them used to walk every binding in the world — a whole-world pass for each
        // write to one variant. The index is kept in step with _partProperties, whose every write
        // goes through PutPartProperties and every removal through RemovePartProperties.
        private readonly Dictionary<string, HashSet<InstanceId>> _partsByVariant =
            new(StringComparer.Ordinal);

        private readonly DestroyedPartStateStore _destroyedParts =
            new(InMemoryPartPropertySink.DestroyedPartRetention);

        // WHY keyed by part id, apart from the binding: a part GameObject destroyed behind the
        // binder's back drops its binding, while its container and the children bound inside it are
        // still alive; the re-materialized part must find that container again, and the part's
        // Destroy must still release it.
        private readonly Dictionary<InstanceId, GameObject> _childContainers = new();

        private readonly Dictionary<GameObject, InstanceId> _childContainerOwners =
            new(GameObjectReferenceComparer.Instance);

        private ILog _log;
        private bool _hostTeardownStarted;

        // WHY: the binder is constructed by RbxWorldHost, not by the container, so the host hands it
        // the composition-scoped logger (see SetLog); the process-wide CoreAI logger backs direct
        // constructions (tests, harnesses) so materialization diagnostics are never silently lost.
        private ILog Logger => _log ??= Log.Instance;

        private sealed class BindingEntry
        {
            public GameObject GameObject;
            public bool IsPart;

            /// <summary>False for the DataModel host and lazily adopted world objects; teardown
            /// must never destroy, rename, or re-parent GameObjects owned outside the binder.</summary>
            public bool OwnsGameObject;

            /// <summary>Shape whose visual is currently built; null until the first Apply.</summary>
            public RbxPartShape? MaterializedShape;

            // WHY: cached on the visual build (ApplyShape) so per-frame property writes skip
            // GetComponent scans; rebuilt on every shape switch. See CacheVisualComponents for
            // how the target visual is resolved.
            public Renderer Renderer;
            public Collider Collider;

            /// <summary>The part's Rigidbody when unanchored; null when anchored. Lives on the root
            /// regardless of shape, so it survives shape switches.</summary>
            public Rigidbody Rigidbody;

            /// <summary>Reused across appearance writes so no MaterialPropertyBlock is allocated per
            /// property change (hot path: a script that recolors/moves a part each frame).</summary>
            public MaterialPropertyBlock PropertyBlock;

            /// <summary>The binder-owned mesh child a shape may create (Cylinder), or null when the
            /// visual lives on the root (Block/Ball/Wedge). Held by reference, NOT looked up by name,
            /// so a user-created child that happens to be named "Shape" can never be mistaken for it.</summary>
            public GameObject ShapeChild;

            /// <summary>Set once a non-pose write reached a host-owned object and was reported, so
            /// a script that recolors an adopted prop every frame logs one warning, not one per frame.</summary>
            public bool ReportedHostOwnedWrite;
        }

        private sealed class GameObjectReferenceComparer : IEqualityComparer<GameObject>
        {
            public static readonly GameObjectReferenceComparer Instance = new();

            public bool Equals(GameObject x, GameObject y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(GameObject obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        /// <summary>Backing objects parent under <paramref name="worldParent"/> (the host that
        /// represents game/DataModel; null = scene root). <paramref name="log"/> receives the
        /// materialization diagnostics; null falls back to the process-wide CoreAI logger.
        /// <paramref name="materialProvider"/> is swappable; null selects the hybrid
        /// texture/procedural runtime catalog.</summary>
        public InstanceGameObjectBinder(Transform worldParent = null, ILog log = null,
            IRbxMaterialProvider<Material> materialProvider = null)
        {
            _worldParent = worldParent;
            _log = log;
            _materialProvider = materialProvider ?? new RbxTextureMaterialProvider();
        }

        /// <summary>Re-points diagnostics at the composition-scoped logger after construction, so a
        /// scene whose host awakes before the container is built still logs through the authored
        /// game-log settings rather than the process-wide fallback.</summary>
        public void SetLog(ILog log)
        {
            if (log != null)
            {
                _log = log;
            }
        }

        /// <summary>Stops new materialization and transform re-parenting once the owning host has
        /// entered Unity destruction. Registry destruction may continue to release binding records
        /// and backing objects without moving them into the dying hierarchy.</summary>
        public void BeginHostTeardown()
        {
            _hostTeardownStarted = true;
        }

        /// <summary>Count of live backing GameObjects (materialized or parked-deactivated).</summary>
        public int BoundCount => _bindings.Count;

        /// <summary>Diagnostic: last-known bundles held for destroyed parts; never above
        /// <see cref="InMemoryPartPropertySink.DestroyedPartRetention"/>.</summary>
        public int RetainedDestroyedPartCount => _destroyedParts.Count;

        /// <summary>
        /// Diagnostic: how many GameObjects the GameObject → instance lookups have examined in total.
        /// A lookup examines the hit object and at most its ancestors, never the bound set, so this
        /// grows by a small constant per contact or ray hit however many objects are bound.
        /// </summary>
        public long ReverseLookupProbeCount { get; private set; }

        /// <summary>
        /// A bound part started or stopped touching another bound part, reported as instance ids.
        /// </summary>
        /// <remarks>
        /// WHY the binder is the one to raise it: it owns the GameObjects and is the only place that
        /// can turn a Unity collider back into an instance id. Everything about which Roblox signal
        /// this becomes is decided on the engine-free side.
        /// </remarks>
        public event System.Action<InstanceId, InstanceId, bool> ContactObserved;

        /// <summary>
        /// Fills <paramref name="bodies"/> with the Rigidbody of every part the binder built (adopted
        /// host objects excluded), for the caller that applies world gravity each fixed step.
        /// </summary>
        public void CollectSimulatedBodies(System.Collections.Generic.List<Rigidbody> bodies)
        {
            if (bodies == null)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<InstanceId, BindingEntry> pair in _bindings)
            {
                // WHY host-owned bodies are left out: the caller turns Unity gravity off on every body
                // it gets and applies the world's own gravity instead. An adopted prop is still the
                // host's object, and DEV-6 forbids a world's gravity from reaching the host's scene.
                if (pair.Value.IsPart && pair.Value.OwnsGameObject && pair.Value.Rigidbody != null)
                {
                    bodies.Add(pair.Value.Rigidbody);
                }
            }
        }

        private void OnRelayContact(GameObject self, GameObject other, bool began)
        {
            if (ContactObserved == null
                || !TryResolvePartInstanceId(self, out InstanceId selfId)
                || !TryResolvePartInstanceId(other, out InstanceId otherId)
                || selfId.Value == otherId.Value)
            {
                return;
            }

            // WHY only one direction: both parts carry a relay, so Unity reports the same contact
            // twice. Raising it once per relay and letting the engine-free side fan out to both
            // parts keeps "Touched fires on both" written in exactly one place.
            ContactObserved(selfId, otherId, began);
        }

        private void AttachContactRelay(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            RbxContactRelay relay = gameObject.GetComponent<RbxContactRelay>();
            if (relay == null)
            {
                relay = gameObject.AddComponent<RbxContactRelay>();
            }

            relay.Attach(OnRelayContact);
        }

        /// <summary>The backing GameObject, when a live one exists (world adapter / test seam). A
        /// GameObject destroyed outside the binder counts as unbound.</summary>
        public bool TryGetBoundObject(InstanceId id, out GameObject gameObject)
        {
            if (TryGetLiveEntry(id, out BindingEntry entry))
            {
                gameObject = entry.GameObject;
                return true;
            }

            gameObject = null;
            return false;
        }

        /// <summary>Adopts an existing meter-authored host object as a Part backing without
        /// duplicating or taking ownership of it. Initial Part state is read through the inverse
        /// RbxSpace boundary so subsequent Lua reads and writes use the normal property sink.</summary>
        public void AdoptWorldObject(InstanceId id, GameObject gameObject)
        {
            if (!id.IsValid)
            {
                throw new System.ArgumentException("A valid instance id is required.", nameof(id));
            }

            if (gameObject == null)
            {
                throw new System.ArgumentNullException(nameof(gameObject));
            }

            if (TryGetLiveEntry(id, out _))
            {
                throw new System.InvalidOperationException("The instance already has a backing GameObject.");
            }

            if (TryGetInstanceId(gameObject, out InstanceId existingId))
            {
                throw new System.InvalidOperationException(
                    "The host GameObject is already bound to instance " + existingId.Value + ".");
            }

            if (IsInsideBinderOwnedBacking(gameObject))
            {
                throw new System.InvalidOperationException(
                    "'" + gameObject.name + "' belongs to a bound instance's backing hierarchy, so it " +
                    "cannot be adopted as a separate part.");
            }

            Transform transform = gameObject.transform;
            Collider collider = gameObject.GetComponent<Collider>();
            Rigidbody rigidbody = gameObject.GetComponent<Rigidbody>();
            PartProperties properties = PartProperties.CreateDefault();
            properties.CFrame = RbxSpace.FromUnity(transform.position, transform.rotation);
            // WHY: world scale, not local — under a Size-scaled ancestor localScale omits the
            // parent factor, so the adopted Size must reflect the part the user actually sees.
            properties.Size = RbxSpace.SizeFromUnity(transform.lossyScale);
            properties.Anchored = rigidbody == null;
            // WHY isTrigger counts: a host trigger volume lets bodies through, which is exactly what
            // CanCollide=false means on this side of the binder.
            properties.CanCollide = collider == null || (collider.enabled && !collider.isTrigger);

            BindingEntry entry = new()
            {
                GameObject = gameObject,
                IsPart = true,
                OwnsGameObject = false,
                MaterializedShape = properties.Shape,
                Rigidbody = rigidbody
            };
            CacheVisualComponents(entry);
            PutPartProperties(id, in properties,
                _partProperties.TryGetValue(id, out PartProperties adoptedOver)
                    ? adoptedOver.MaterialVariant
                    : null);
            AddBinding(id, entry);
            AttachContactRelay(gameObject);
        }

        /// <summary>
        /// Reverse of <see cref="TryGetBoundObject"/>: the instance whose backing GameObject is
        /// exactly <paramref name="gameObject"/>, in constant time. A collider that sits below a
        /// backing object (a Cylinder's Shape child, a child of an adopted prop) is not matched here;
        /// <see cref="TryResolvePartInstanceId"/> maps those.
        /// </summary>
        public bool TryGetInstanceId(GameObject gameObject, out InstanceId id)
        {
            if (gameObject != null)
            {
                ReverseLookupProbeCount++;
                if (_idsByGameObject.TryGetValue(gameObject, out id))
                {
                    return true;
                }
            }

            id = InstanceId.None;
            return false;
        }

        /// <summary>
        /// Maps a collider's GameObject (a raycast hit, the other side of a contact, a click) back to
        /// the part it belongs to: the nearest bound GameObject at or above it, when that binding is a
        /// part. A Cylinder keeps its collider on a binder-owned Shape child and an adopted host prop
        /// may keep colliders on its children, so an exact match alone left both invisible to
        /// Raycast, Touched and clicks. A collider whose nearest bound ancestor is a container
        /// (Workspace, a Model, the DataModel host) is not a part and resolves to nothing. Cost is
        /// one dictionary probe per ancestor climbed, independent of how many objects are bound.
        /// </summary>
        public bool TryResolvePartInstanceId(GameObject gameObject, out InstanceId id)
        {
            for (Transform current = gameObject != null ? gameObject.transform : null;
                 current != null;
                 current = current.parent)
            {
                ReverseLookupProbeCount++;
                if (!_idsByGameObject.TryGetValue(current.gameObject, out InstanceId candidate))
                {
                    continue;
                }

                if (_bindings.TryGetValue(candidate, out BindingEntry entry) && entry.IsPart)
                {
                    id = candidate;
                    return true;
                }

                break;
            }

            id = InstanceId.None;
            return false;
        }

        /// <summary>
        /// True when <paramref name="gameObject"/> is not itself bound but sits inside a GameObject
        /// the binder created (a Cylinder's Shape child, anything under a materialized part or
        /// container). Such an object is part of an instance's backing, never a separate world
        /// object: adopting it would let a script move one piece of another part's visual.
        /// </summary>
        public bool IsInsideBinderOwnedBacking(GameObject gameObject)
        {
            if (gameObject == null || _idsByGameObject.ContainsKey(gameObject))
            {
                return false;
            }

            for (Transform current = gameObject.transform; current != null; current = current.parent)
            {
                // WHY checked before the bound-ancestor rule: a part's child container is binder-owned
                // even where its nearest bound ancestor is the host-owned DataModel GameObject.
                if (_childContainerOwners.ContainsKey(current.gameObject))
                {
                    return true;
                }

                if (_idsByGameObject.TryGetValue(current.gameObject, out InstanceId candidate))
                {
                    return _bindings.TryGetValue(candidate, out BindingEntry entry) && entry.OwnsGameObject;
                }
            }

            return false;
        }

        private void AddBinding(InstanceId id, BindingEntry entry)
        {
            _bindings.Add(id, entry);
            _idsByGameObject[entry.GameObject] = id;
        }

        /// <summary>Forgets a binding in both directions. The reverse entry is removed only while it
        /// still names <paramref name="id"/>, so a GameObject re-bound to a newer instance keeps its
        /// newer mapping.</summary>
        private void DropBinding(InstanceId id, BindingEntry entry)
        {
            _bindings.Remove(id);
            if (!ReferenceEquals(entry.GameObject, null)
                && _idsByGameObject.TryGetValue(entry.GameObject, out InstanceId mapped)
                && mapped.Value == id.Value)
            {
                _idsByGameObject.Remove(entry.GameObject);
            }
        }

        /// <summary>
        /// The binding for <paramref name="id"/> when its GameObject is still alive. A GameObject
        /// destroyed outside the binder (a host kill-plane script, a scene unload in progress, an
        /// editor deletion) is dropped here and reported as unbound, so no callback ever touches a
        /// dead object and throws halfway through a registry update.
        /// </summary>
        private bool TryGetLiveEntry(InstanceId id, out BindingEntry entry)
        {
            if (!_bindings.TryGetValue(id, out entry))
            {
                return false;
            }

            if (entry.GameObject != null)
            {
                return true;
            }

            DropBinding(id, entry);
            entry = null;
            return false;
        }

        // ---- IInstanceBackingBinder (D5/D6) -------------------------------------------------

        public void OnEnteredWorld(InstanceRecord record)
        {
            if (_hostTeardownStarted)
            {
                return;
            }

            // WHY: a destroyed instance never re-enters the world, so an id arriving here with
            // retained state is a new instance under a restored id and starts from its own state.
            _destroyedParts.Forget(record.Id);
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                if (entry.GameObject == null)
                {
                    // WHY: destroyed outside the binder. An owned backing re-materializes below, since
                    // the instance is entering the world and must have one; a host-owned object stays
                    // released, because the host that owns it decided it is gone.
                    DropBinding(record.Id, entry);
                    if (!entry.OwnsGameObject)
                    {
                        return;
                    }
                }
                else
                {
                    if (entry.OwnsGameObject)
                    {
                        // WHY: re-entry reactivates the parked object — D5 makes re-parenting cheap.
                        Transform parent = ResolveParentTransform(record.Instance, out bool parentIsBound);
                        entry.GameObject.transform.SetParent(parent, true);
                        entry.GameObject.name = record.Instance.Name;
                        entry.GameObject.SetActive(DesiredActiveSelf(record.Instance, parentIsBound));
                    }

                    PlaceChildContainer(record.Instance);
                    return;
                }
            }

            try
            {
                entry = CreateEntry(record.Instance);
                AddBinding(record.Id, entry);
                PlaceChildContainer(record.Instance);
                if (entry.IsPart)
                {
                    Apply(entry, GetPartPropertiesOrDefault(record.Id));
                    AttachContactRelay(entry.GameObject);
                    // WHY: log only the FIRST part — a materialized-but-invisible part looks
                    // identical to one never created, and a player has no inspector to tell them
                    // apart; logging every part would flood mods that spawn in bulk.
                    if (!_loggedFirstPart)
                    {
                        _loggedFirstPart = true;
                        Logger.Info(
                            $"[CoreAI.RbxApi] first part materialized: '{record.Instance.Name}' " +
                            $"active={entry.GameObject.activeInHierarchy} " +
                            $"renderer={(entry.Renderer != null ? "yes" : "NONE")} " +
                            $"shader={(entry.Renderer != null && entry.Renderer.sharedMaterial != null ? entry.Renderer.sharedMaterial.shader.name : "NO MATERIAL")}",
                            LogTag.World);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error(
                    $"[CoreAI.RbxApi] Failed to materialize '{record.Instance.Name}' " +
                    $"(class={record.Instance.ClassName}, id={record.Id}): {ex.Message}",
                    LogTag.World);
            }
        }

        public void OnLeftWorld(InstanceRecord record)
        {
            if (_hostTeardownStarted)
            {
                return;
            }

            // WHY: a MaterialVariant owns no GameObject, so the binding guard below returns before
            // the repaint would ever run.
            RepaintIfMaterialVariant(record);
            ParkChildContainer(record.Id);

            // WHY: the DataModel host GameObject never leaves its own tree; guard so nothing
            // deactivates or re-parents the host.
            if (!TryGetLiveEntry(record.Id, out BindingEntry entry) || !entry.OwnsGameObject)
            {
                return;
            }

            entry.GameObject.SetActive(false);
            // WHY: parked under the world parent so a later destroy of the old parent's
            // GameObject cannot take the detached object with it.
            entry.GameObject.transform.SetParent(_worldParent, true);
        }

        public void OnDestroyed(InstanceRecord record)
        {
            RepaintIfMaterialVariant(record);
            // WHY first: the last-known pose of a simulated part is read from its GameObject, which
            // the lines below release.
            OnPartDestroyed(record.Id);
            DestroyChildContainer(record.Id);
            if (!_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                return;
            }

            DropBinding(record.Id, entry);
            // WHY: the DataModel's backing object is the host GameObject — teardown releases the
            // materialized children but never the host itself (RbxWorldHost owns its lifecycle).
            if (entry.OwnsGameObject)
            {
                SafeDestroy(entry.GameObject);
            }
            else if (entry.IsPart)
            {
                ReleaseHostObject(entry);
            }
        }

        public void OnReparented(InstanceRecord record)
        {
            if (_hostTeardownStarted)
            {
                return;
            }

            RepaintIfMaterialVariant(record);
            if (TryGetLiveEntry(record.Id, out BindingEntry entry) && entry.OwnsGameObject)
            {
                // WHY: worldPositionStays — CFrames are world-space, so a hierarchy move
                // must not shift the rendered pose.
                Transform parent = ResolveParentTransform(record.Instance, out bool parentIsBound);
                entry.GameObject.transform.SetParent(parent, true);
                // WHY: a move can cross the Workspace boundary without leaving the DataModel
                // (Workspace -> game, game -> Workspace), so the object's own flag is recomputed with
                // its parent instead of relying on the new parent's activeInHierarchy alone.
                entry.GameObject.SetActive(DesiredActiveSelf(record.Instance, parentIsBound));
            }

            PlaceChildContainer(record.Instance);
        }

        public void OnNameChanged(InstanceRecord record)
        {
            RepaintIfMaterialVariant(record);
            if (TryGetLiveEntry(record.Id, out BindingEntry entry) && entry.OwnsGameObject)
            {
                entry.GameObject.name = record.Instance.Name;
            }

            if (TryGetChildContainer(record.Id, out GameObject container))
            {
                container.name = record.Instance.Name + ChildContainerSuffix;
            }
        }

        /// <summary>
        /// Moves the last-known state of a destroyed part out of the live store into the bounded
        /// retained store (see <see cref="IPartPropertySink.OnPartDestroyed"/>). The registry calls
        /// <see cref="OnDestroyed"/>, which runs this before the backing object is released, so a
        /// simulated part is remembered where its body actually was.
        /// </summary>
        public void OnPartDestroyed(InstanceId id)
        {
            bool boundPart = _bindings.TryGetValue(id, out BindingEntry entry) && entry.IsPart;
            if (!boundPart && !_partProperties.ContainsKey(id))
            {
                return;
            }

            PartProperties last = GetPartPropertiesOrDefault(id);
            RemovePartProperties(id);
            _destroyedParts.Remember(id, in last);
        }

        // ---- Child containers ----------------------------------------------------------------

        /// <summary>Suffix of a part's child-container name, so the Unity hierarchy reads
        /// "Handle", "Handle (children)/Blade" and a name lookup never mistakes one for the other.</summary>
        public const string ChildContainerSuffix = " (children)";

        private bool TryGetChildContainer(InstanceId partId, out GameObject container)
        {
            if (!_childContainers.TryGetValue(partId, out container))
            {
                return false;
            }

            if (container != null)
            {
                return true;
            }

            // WHY: destroyed outside the binder; the next child to need it builds a fresh one.
            ForgetChildContainer(partId, container);
            container = null;
            return false;
        }

        /// <summary>
        /// The transform a child of <paramref name="part"/> parents under: the part's own child
        /// container, created on first use next to the part's GameObject with an identity local
        /// pose and unit scale, carrying the part's active verdict.
        /// </summary>
        /// <remarks>
        /// WHY not the part's GameObject: its localScale is the part's Size and its pose is the
        /// part's CFrame, so a child parented there rendered at the product of both sizes, was
        /// dragged along by every move of the parent while its own Position still read the old
        /// value, and its collider became part of the parent's Rigidbody (welded in all but name).
        /// </remarks>
        private Transform ChildContainerOf(RbxInstance part)
        {
            if (TryGetChildContainer(part.Id, out GameObject container))
            {
                return container.transform;
            }

            Transform parent = ResolveParentTransform(part, out bool parentIsBound);
            container = new GameObject(part.Name + ChildContainerSuffix);
            container.transform.SetParent(parent, false);
            container.SetActive(DesiredActiveSelf(part, parentIsBound));
            _childContainers[part.Id] = container;
            _childContainerOwners[container] = part.Id;
            return container.transform;
        }

        /// <summary>Puts an existing child container where its part now is in the instance tree,
        /// with the part's active verdict. No container, nothing to do.</summary>
        private void PlaceChildContainer(RbxInstance part)
        {
            if (part == null || !TryGetChildContainer(part.Id, out GameObject container))
            {
                return;
            }

            Transform parent = ResolveParentTransform(part, out bool parentIsBound);
            // WHY worldPositionStays=false: every ancestor a container can sit under is itself an
            // unscaled identity transform, so keeping the local identity is what keeps it one too.
            container.transform.SetParent(parent, false);
            container.name = part.Name + ChildContainerSuffix;
            container.SetActive(DesiredActiveSelf(part, parentIsBound));
        }

        /// <summary>Deactivates and parks a leaving part's child container beside its parked part.</summary>
        private void ParkChildContainer(InstanceId partId)
        {
            if (!TryGetChildContainer(partId, out GameObject container))
            {
                return;
            }

            container.SetActive(false);
            container.transform.SetParent(_worldParent, false);
        }

        private void DestroyChildContainer(InstanceId partId)
        {
            if (!_childContainers.TryGetValue(partId, out GameObject container))
            {
                return;
            }

            ForgetChildContainer(partId, container);
            SafeDestroy(container);
        }

        private void ForgetChildContainer(InstanceId partId, GameObject container)
        {
            _childContainers.Remove(partId);
            if (!ReferenceEquals(container, null))
            {
                _childContainerOwners.Remove(container);
            }
        }

        /// <summary>
        /// Hands an adopted host object back when its instance is destroyed: the contact relay is the
        /// only thing the binder added to it, so the relay goes and the object itself stays, with the
        /// host's own components untouched.
        /// </summary>
        private static void ReleaseHostObject(BindingEntry entry)
        {
            if (entry.GameObject == null)
            {
                return;
            }

            RbxContactRelay relay = entry.GameObject.GetComponent<RbxContactRelay>();
            if (relay != null)
            {
                relay.Detach();
                SafeDestroy(relay);
            }
        }

        /// <summary>
        /// Implements the clone-completion seam (IInstanceBackingBinder.CopyBackingState) with the
        /// state this class also holds as IPartPropertySink: copies the source part's stored
        /// PartProperties onto the destination id, then re-applies them if the destination is
        /// already a materialized part. A no-op source (no part state stored, e.g. non-BasePart
        /// instances) copies nothing.
        /// </summary>
        public void CopyBackingState(InstanceId sourceId, InstanceId destinationId)
        {
            if (!TryGetPartProperties(sourceId, out PartProperties properties))
            {
                return;
            }

            SetPartProperties(destinationId, in properties);
        }

        /// <summary>
        /// Repaints every variant-wearing part when the instance that moved, was renamed, was
        /// destroyed or entered the world is a MaterialVariant.
        /// WHY: parts hold a variant by NAME and the provider only re-reads a variant when something
        /// asks it to. Renaming, destroying or reparenting a variant therefore left every part
        /// wearing it on a material that no longer corresponds to anything, with nothing in the log.
        /// Both names are affected by a rename, so this repaints all of them rather than one.
        /// </summary>
        private void RepaintIfMaterialVariant(InstanceRecord record)
        {
            if (_hostTeardownStarted || record?.Instance == null)
            {
                return;
            }

            if (record.Instance.IsA("MaterialVariant"))
            {
                RepaintVariantParts();
            }
        }

        // ---- IPartPropertySink (one-way push) -----------------------------------------------

        /// <summary>Sets the pose; a non-finite CFrame is refused with BAD_ARGUMENT and changes nothing.</summary>
        public void SetCFrame(InstanceId id, in RbxCFrame cframe)
        {
            RequireFinitePose(id, in cframe, "CFrame");
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CFrame = cframe;
            Store(id, properties, PartAspect.Transform);
        }

        /// <summary>Moves the part keeping its current orientation (the body's own, for a simulated
        /// part); a non-finite position is refused with BAD_ARGUMENT and changes nothing.</summary>
        public void SetPosition(InstanceId id, RbxVector3 position)
        {
            RequireFiniteVector(id, position, "Position");
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Position = position;
            Store(id, properties, PartAspect.Transform);
        }

        /// <summary>Sets Size clamped per axis into <see cref="PartPropertyBounds"/>; a non-finite
        /// size is refused with BAD_ARGUMENT and changes nothing.</summary>
        public void SetSize(InstanceId id, RbxVector3 size)
        {
            RequireFiniteVector(id, size, "Size");
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Size = PartPropertyBounds.ClampSize(size);
            Store(id, properties, PartAspect.Size);
        }

        public void SetColor(InstanceId id, RbxColor3 color)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Color = color;
            properties.ColorWasExplicitlySet = true;
            Store(id, properties, PartAspect.Appearance);
        }

        public void SetAnchored(InstanceId id, bool anchored)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Anchored = anchored;
            Store(id, properties, PartAspect.Anchored);
        }

        public void SetTransparency(InstanceId id, float transparency)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Transparency = Mathf.Clamp01(transparency);
            Store(id, properties, PartAspect.Appearance);
        }

        public void SetCanCollide(InstanceId id, bool canCollide)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CanCollide = canCollide;
            Store(id, properties, PartAspect.CanCollide);
        }

        public void SetShape(InstanceId id, RbxPartShape shape)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Shape = shape;
            Store(id, properties, PartAspect.Full);
        }

        public void SetMaterial(InstanceId id, in RbxMaterialId material)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Material = material;
            Store(id, properties, PartAspect.Appearance);
        }

        public void SetMaterialVariant(InstanceId id, string variantName)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.MaterialVariant = string.IsNullOrEmpty(variantName) ? null : variantName;
            Store(id, properties, PartAspect.Appearance);
        }

        /// <summary>
        /// Re-resolves the surface of every part wearing this variant.
        /// WHY: the provider only re-reads a variant when something asks it to, and editing the
        /// variant's own properties touches no part. Without this a script that changed a live
        /// variant's ColorMap left every part wearing it on the old texture forever.
        /// </summary>
        public void RefreshMaterialVariant(string variantName)
        {
            if (_hostTeardownStarted || string.IsNullOrEmpty(variantName)
                || !_partsByVariant.TryGetValue(variantName, out HashSet<InstanceId> wearers))
            {
                return;
            }

            foreach (InstanceId id in wearers)
            {
                if (!_bindings.TryGetValue(id, out BindingEntry entry) || !entry.IsPart
                    || !entry.OwnsGameObject
                    || !_partProperties.TryGetValue(id, out PartProperties properties))
                {
                    continue;
                }

                ApplyAppearance(entry, properties);
            }
        }

        /// <summary>Parts whose stored bundle names the variant (A3-08 regression counter).</summary>
        internal int CountPartsWearingVariant(string variantName)
        {
            return variantName != null
                   && _partsByVariant.TryGetValue(variantName, out HashSet<InstanceId> wearers)
                ? wearers.Count
                : 0;
        }

        /// <summary>Stores a part's bundle and moves it in the variant index when the variant it
        /// names differs from <paramref name="previousVariant"/>, the one its stored bundle named
        /// (null when none was stored).</summary>
        private void PutPartProperties(InstanceId id, in PartProperties properties,
            string previousVariant)
        {
            _partProperties[id] = properties;
            if (string.Equals(previousVariant, properties.MaterialVariant, StringComparison.Ordinal))
            {
                return;
            }

            UnindexVariantWearer(previousVariant, id);
            if (properties.MaterialVariant == null)
            {
                return;
            }

            if (!_partsByVariant.TryGetValue(properties.MaterialVariant,
                    out HashSet<InstanceId> wearers))
            {
                wearers = new HashSet<InstanceId>();
                _partsByVariant.Add(properties.MaterialVariant, wearers);
            }

            wearers.Add(id);
        }

        /// <summary>Forgets a part's bundle and its place in the variant index.</summary>
        private void RemovePartProperties(InstanceId id)
        {
            if (_partProperties.TryGetValue(id, out PartProperties previous))
            {
                UnindexVariantWearer(previous.MaterialVariant, id);
                _partProperties.Remove(id);
            }
        }

        private void UnindexVariantWearer(string variantName, InstanceId id)
        {
            if (variantName == null
                || !_partsByVariant.TryGetValue(variantName, out HashSet<InstanceId> wearers))
            {
                return;
            }

            wearers.Remove(id);
            if (wearers.Count == 0)
            {
                _partsByVariant.Remove(variantName);
            }
        }

        /// <summary>Variant lookup port the material provider consumes to resolve
        /// PartProperties.MaterialVariant without importing the Rbx instance tree. The host
        /// points it at the world's MaterialService; null renders every part plain.</summary>
        public IRbxMaterialVariantSource MaterialVariantSource
        {
            get => _materialVariantSource;
            set
            {
                _materialVariantSource = value;
                if (_materialProvider is IRbxMaterialVariantConsumer consumer)
                {
                    consumer.VariantSource = value;
                }

                RepaintVariantParts();
            }
        }

        /// <summary>
        /// Re-resolves the shared material of every bound part that names a MaterialVariant.
        /// WHY: a restored world is staged binder-first — every part materializes through
        /// RestoreFresh BEFORE the host can point the binder at the new MaterialService, so each
        /// one resolved to its plain material and nothing ever asked again. Every variant in a
        /// loaded world rendered plain, silently. Repainting here makes the wiring order stop
        /// mattering.
        /// </summary>
        private void RepaintVariantParts()
        {
            foreach (KeyValuePair<InstanceId, BindingEntry> pair in _bindings)
            {
                if (!pair.Value.IsPart || !pair.Value.OwnsGameObject ||
                    !_partProperties.TryGetValue(pair.Key, out PartProperties properties) ||
                    properties.MaterialVariant == null)
                {
                    continue;
                }

                ApplyAppearance(pair.Value, properties);
            }
        }

        private IRbxMaterialVariantSource _materialVariantSource;

        /// <summary>Whole-bundle push; the pose is applied verbatim (a restore or clone teleports).
        /// Size is clamped like <see cref="SetSize"/>, and a non-finite CFrame or Size refuses the
        /// whole bundle with BAD_ARGUMENT.</summary>
        public void SetPartProperties(InstanceId id, in PartProperties properties)
        {
            RequireFinitePose(id, in properties.CFrame, "CFrame");
            RequireFiniteVector(id, properties.Size, "Size");
            PartProperties bounded = properties;
            bounded.Size = PartPropertyBounds.ClampSize(properties.Size);
            _destroyedParts.Forget(id);
            Store(id, bounded, PartAspect.Full);
        }

        /// <summary>The stored bundle — with the body's live pose for a simulated part — or the
        /// retained last-known bundle of a recently destroyed part.</summary>
        public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
        {
            if (_partProperties.TryGetValue(id, out properties))
            {
                properties = WithSimulatedPose(id, properties);
                return true;
            }

            return _destroyedParts.TryGet(id, out properties);
        }

        /// <summary>
        /// What a script reads: <see cref="TryGetPartProperties"/>, or Roblox defaults (still
        /// carrying the live pose of a materialized simulated part) when nothing was stored.
        /// </summary>
        public PartProperties GetPartPropertiesOrDefault(InstanceId id)
        {
            return TryGetPartProperties(id, out PartProperties properties)
                ? properties
                : WithSimulatedPose(id, PartProperties.CreateDefault());
        }

        // WHY these tolerances: a pose written through RbxSpace and read straight back from the
        // Transform differs only by float rounding (none at all under the identity parents the
        // binder builds), and a simulated part at rest must read back exactly what the script wrote.
        // Movement below them is too small to matter, so the scripted value is kept.
        private const float SimulatedPoseAbsoluteToleranceMetres = 1e-4f;
        private const float SimulatedPoseRelativeTolerance = 1e-6f;
        private const float SimulatedRotationDotTolerance = 0.999999f;

        /// <summary>
        /// Replaces the pose in <paramref name="properties"/> with the pose the part's body actually
        /// has, when the part is simulated (it has a Rigidbody) and physics has moved it away from
        /// the last scripted pose. Anchored and unmaterialized parts keep the stored pose.
        /// </summary>
        /// <remarks>
        /// WHY: gravity and the character motor move the Rigidbody directly, so the stored pose froze
        /// at spawn: a fallen part read its spawn height, a kill plane never fired, PivotTo computed
        /// offsets from spawn poses, and every later Size, Shape or Anchored write re-applied the
        /// stored pose and teleported the part back. Every setter starts from this refreshed bundle,
        /// so a partial write keeps the pose the part really has.
        /// </remarks>
        private PartProperties WithSimulatedPose(InstanceId id, PartProperties properties)
        {
            if (!_bindings.TryGetValue(id, out BindingEntry entry) || !entry.IsPart
                || entry.Rigidbody == null || entry.GameObject == null)
            {
                return properties;
            }

            Transform transform = entry.GameObject.transform;
            Vector3 livePosition = transform.position;
            Quaternion liveRotation = transform.rotation;
            (Vector3 scriptedPosition, Quaternion scriptedRotation) = RbxSpace.ToUnityPose(properties.CFrame);
            float tolerance = SimulatedPoseAbsoluteToleranceMetres
                              + SimulatedPoseRelativeTolerance * scriptedPosition.magnitude;
            if ((livePosition - scriptedPosition).sqrMagnitude <= tolerance * tolerance
                && Mathf.Abs(Quaternion.Dot(scriptedRotation, liveRotation)) >= SimulatedRotationDotTolerance)
            {
                return properties;
            }

            properties.CFrame = RbxSpace.FromUnity(livePosition, liveRotation);
            return properties;
        }

        private static void RequireFiniteVector(InstanceId id, RbxVector3 value, string member)
        {
            if (!PartPropertyBounds.IsFinite(value))
            {
                throw NonFiniteWrite(id, member);
            }
        }

        private static void RequireFinitePose(InstanceId id, in RbxCFrame value, string member)
        {
            if (!PartPropertyBounds.IsFinite(in value))
            {
                throw NonFiniteWrite(id, member);
            }
        }

        /// <summary>
        /// The refusal a host-side writer gets for a NaN or infinite spatial value — the same rule
        /// and code a Lua assignment gets, raised here because tweens, restores, character seeding
        /// and the world adapter reach the sink without passing the Lua checks.
        /// </summary>
        private static RbxError NonFiniteWrite(InstanceId id, string member)
        {
            return RbxError.BadArgument(
                "Part." + member + " of instance " + id.Value
                + " must have finite components, got NaN or infinity; the part keeps its previous "
                + member,
                "check the arithmetic that produced it (0/0, math.huge, an overflow) before writing it");
        }

        /// <summary>
        /// Reads the materialized part's own transform (converted through RbxSpace, D2) instead of
        /// the stored PartProperties — WHY: a character motor or gravity moves the Rigidbody
        /// directly and never writes back into <see cref="_partProperties"/>, so the stored value
        /// would answer with wherever the part last was when a script (or the initial spawn seed)
        /// set it, however long ago. A part with no bound GameObject yet — never materialized, or
        /// not a part at all — falls back to the stored/default value; it has no live transform.
        /// </summary>
        public RbxVector3 GetLivePositionStuds(InstanceId id)
        {
            if (_bindings.TryGetValue(id, out BindingEntry entry) && entry.IsPart
                && entry.GameObject != null)
            {
                return RbxSpace.FromUnity(entry.GameObject.transform.position);
            }

            return GetPartPropertiesOrDefault(id).Position;
        }

        // WHY: a script that moves/recolors a part each frame is the hottest API path, so each
        // setter re-applies ONLY the aspect it touched instead of re-running the whole
        // materialization (shape scan + component lookups + a MaterialPropertyBlock alloc) on
        // every transform write. Full is used at materialization and on shape switches.
        private enum PartAspect
        {
            Full,
            Transform,
            Size,
            Appearance,
            Anchored,
            CanCollide
        }

        private void Store(InstanceId id, in PartProperties properties, PartAspect aspect)
        {
            // WHY: a per-property write to a destroyed part lands in its retained copy, never back
            // in the live store, so a late host write to a dead id cannot re-grow what destruction
            // released. A whole-bundle push revives the id before it gets here.
            bool stored = _partProperties.TryGetValue(id, out PartProperties previous);
            if (!stored && _destroyedParts.TryReplace(id, in properties))
            {
                return;
            }

            PutPartProperties(id, in properties, stored ? previous.MaterialVariant : null);
            if (!TryGetLiveEntry(id, out BindingEntry entry) || !entry.IsPart)
            {
                return;
            }

            if (!entry.OwnsGameObject)
            {
                ApplyToHostOwnedObject(entry, properties, aspect);
                return;
            }

            switch (aspect)
            {
                case PartAspect.Transform:
                    ApplyPose(entry, properties);
                    break;
                case PartAspect.Size:
                    // WHY scale only: the pose is unchanged by a Size write, and re-applying it
                    // would teleport a simulated part to whatever pose the bundle last held.
                    ApplyScale(entry, properties);
                    break;
                case PartAspect.Appearance:
                    ApplyAppearance(entry, properties);
                    break;
                case PartAspect.Anchored:
                    ApplyAnchored(entry, properties.Anchored);
                    break;
                case PartAspect.CanCollide:
                    ApplyCanCollide(entry, properties.CanCollide);
                    break;
                default:
                    Apply(entry, properties);
                    break;
            }
        }

        /// <summary>
        /// A host object adopted as a Part follows pose writes (position and orientation) and nothing
        /// else. The value is still stored, so the instance reads back what the script wrote, but
        /// the host's scale, mesh, collider, material, renderer and Rigidbody are never touched:
        /// writing Size back as localScale doubled a prop under a scaled parent, a Shape write
        /// stripped the host's mesh and collider, and Anchored destroyed the host's own Rigidbody.
        /// A whole-bundle write (shape switch, restore, clone copy) applies nothing at all, because
        /// re-applying the stored pose would snap a prop the host has since moved back to where it
        /// was adopted.
        /// </summary>
        private void ApplyToHostOwnedObject(BindingEntry entry, in PartProperties properties,
            PartAspect aspect)
        {
            if (aspect == PartAspect.Transform)
            {
                ApplyPose(entry, properties);
                return;
            }

            if (entry.ReportedHostOwnedWrite)
            {
                return;
            }

            entry.ReportedHostOwnedWrite = true;
            Logger.Warn(
                $"[CoreAI.RbxApi] '{entry.GameObject.name}' is a host-owned object adopted as a Part: " +
                "only its position and orientation follow script writes. Size, Shape, Color, " +
                "Material, Transparency, Anchored and CanCollide are kept on the instance but are " +
                "not applied, so the host's own scale, mesh, collider, material and Rigidbody stay " +
                "as the host authored them.",
                LogTag.World);
        }

        // ---- Materialization ----------------------------------------------------------------

        private BindingEntry CreateEntry(RbxInstance instance)
        {
            if (instance.IsA("DataModel"))
            {
                GameObject host = _worldParent != null ? _worldParent.gameObject : new GameObject(instance.Name);
                return new BindingEntry
                {
                    GameObject = host,
                    IsPart = false,
                    OwnsGameObject = _worldParent == null
                };
            }

            bool isPart = instance.IsA("BasePart");
            // WHY: parts start as an empty GameObject; OnEnteredWorld runs Apply right after,
            // and ApplyShape builds the primitive visual for the stored Shape there — one code
            // path for materialization and later Shape switches.
            GameObject gameObject = new();
            gameObject.name = instance.Name;
            Transform parent = ResolveParentTransform(instance, out bool parentIsBound);
            gameObject.transform.SetParent(parent, false);
            gameObject.SetActive(DesiredActiveSelf(instance, parentIsBound));
            return new BindingEntry { GameObject = gameObject, IsPart = isPart, OwnsGameObject = true };
        }

        /// <summary>
        /// Only Workspace and its descendants are the physical world (Workspace.yaml: "While such
        /// objects are descendant of Workspace, they will be active"). A direct child of the DataModel
        /// other than Workspace (Lighting, Players, MaterialService, the storage services, a Part
        /// parented straight to game) is inactive itself, so its whole subtree neither renders,
        /// collides, fires Touched nor answers a raycast; everything below that level keeps its own
        /// flag on and inherits the verdict through Unity's activeInHierarchy. When the parent has no
        /// live backing the object sits directly under the host, so the verdict is read from the
        /// instance tree instead.
        /// </summary>
        private static bool DesiredActiveSelf(RbxInstance instance, bool parentIsBound)
        {
            RbxInstance parent = instance.Parent;
            if (parent == null)
            {
                return true;
            }

            if (!parentIsBound)
            {
                return IsWorkspaceOrDescendant(instance);
            }

            return !parent.IsA("DataModel") || instance.IsA("Workspace");
        }

        private static bool IsWorkspaceOrDescendant(RbxInstance instance)
        {
            for (RbxInstance current = instance; current != null; current = current.Parent)
            {
                if (current.IsA("Workspace"))
                {
                    return true;
                }
            }

            return false;
        }

        private Transform ResolveParentTransform(RbxInstance instance, out bool parentIsBound)
        {
            RbxInstance parent = instance.Parent;
            if (parent != null && TryGetLiveEntry(parent.Id, out BindingEntry parentEntry))
            {
                parentIsBound = true;
                return parentEntry.IsPart ? ChildContainerOf(parent) : parentEntry.GameObject.transform;
            }

            parentIsBound = false;
            return _worldParent;
        }

        // ---- Property application (the D2-allowed conversion call sites) --------------------

        private void Apply(BindingEntry entry, in PartProperties properties)
        {
            ApplyShape(entry, properties.Shape);
            ApplyTransform(entry, properties);
            ApplyAppearance(entry, properties);
            ApplyAnchored(entry, properties.Anchored);
            ApplyCanCollide(entry, properties.CanCollide);
        }

        private static void ApplyTransform(BindingEntry entry, in PartProperties properties)
        {
            ApplyPose(entry, properties);
            ApplyScale(entry, properties);
        }

        private static void ApplyScale(BindingEntry entry, in PartProperties properties)
        {
            // WHY: for every shape the part root carries Size * MetersPerStud (D3); shape
            // primitives are authored so 1 local unit = 1 stud (Cylinder's child corrects
            // Unity's 2-unit-tall mesh, see BuildCylinderVisual). No other part's scale multiplies
            // into it, because a part is never parented under another part (see ChildContainerOf).
            entry.GameObject.transform.localScale = RbxSpace.SizeToUnity(properties.Size);
        }

        private static void ApplyPose(BindingEntry entry, in PartProperties properties)
        {
            (Vector3 position, Quaternion rotation) = RbxSpace.ToUnityPose(properties.CFrame);
            entry.GameObject.transform.SetPositionAndRotation(position, rotation);
        }

        // ---- Shape materialization ----------------------------------------------------------

        private const string ShapeChildName = "Shape";

        private void ApplyShape(BindingEntry entry, RbxPartShape shape)
        {
            RbxPartShape normalized = shape;
            if (entry.MaterializedShape == normalized)
            {
                return;
            }

            StripShapeVisual(entry);
            switch (normalized)
            {
                case RbxPartShape.Ball:
                    BuildRootPrimitiveVisual(entry.GameObject, PrimitiveType.Sphere);
                    break;
                case RbxPartShape.Cylinder:
                    entry.ShapeChild = BuildCylinderVisual(entry.GameObject);
                    // WHY: an anchored Cylinder has no Rigidbody, so Unity delivers its contact
                    // messages to the collider's own GameObject — this child, not the root relay.
                    AttachContactRelay(entry.ShapeChild);
                    break;
                case RbxPartShape.Wedge:
                    BuildWedgeVisual(entry.GameObject);
                    break;
                case RbxPartShape.CornerWedge:
                    BuildCornerWedgeVisual(entry.GameObject);
                    break;
                default:
                    BuildRootPrimitiveVisual(entry.GameObject, PrimitiveType.Cube);
                    break;
            }

            entry.MaterializedShape = normalized;
            CacheVisualComponents(entry);
        }

        // WHY: resolve the renderer/collider from THIS part's own visual — the root for
        // Block/Ball/Wedge/CornerWedge, or the binder-owned ShapeChild for Cylinder (held by reference, never
        // found by name) — so neither a nested child part nor a user child named "Shape" can be
        // mistaken for the visual. Cached so appearance/collide setters skip the scan on every write.
        private static void CacheVisualComponents(BindingEntry entry)
        {
            Transform visual = entry.ShapeChild != null
                ? entry.ShapeChild.transform
                : entry.GameObject.transform;
            entry.Renderer = visual.GetComponent<Renderer>();
            entry.Collider = visual.GetComponent<Collider>();
        }

        /// <summary>Removes the previous shape's mesh/collider (root components and the binder-owned
        /// ShapeChild), keeping the GameObject identity and its Rigidbody untouched.</summary>
        private static void StripShapeVisual(BindingEntry entry)
        {
            // WHY: destroy synchronously even in Play Mode — ApplyShape rebuilds the visual right
            // after, and a deferred Object.Destroy would leave the old MeshRenderer alive for the
            // next AddComponent<MeshRenderer> (single-per-GameObject, so the add would fail).
            // Binder-owned, so DestroyImmediate is legal here.
            GameObject gameObject = entry.GameObject;
            DestroyNow(gameObject.GetComponent<Collider>());
            DestroyNow(gameObject.GetComponent<MeshRenderer>());
            DestroyNow(gameObject.GetComponent<MeshFilter>());
            // WHY: destroy the shape child by the OWNED reference, never transform.Find("Shape") — a
            // mod can legally name one of its own child instances "Shape", and a name lookup would
            // then destroy the user's object and cache its components as this part's visual.
            if (entry.ShapeChild != null)
            {
                DestroyNow(entry.ShapeChild);
                entry.ShapeChild = null;
            }
        }

        /// <summary>Block/Ball: Unity's built-in cube and sphere are 1 unit = 1 stud for us
        /// (asset rule, §2 — geometry is never rescaled, only localScale carries numbers), so
        /// their mesh and collider live directly on the part GameObject.</summary>
        private static void BuildRootPrimitiveVisual(GameObject gameObject, PrimitiveType type)
        {
            EnsurePrimitiveCache();
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = type == PrimitiveType.Sphere ? _sphereMesh : _cubeMesh;
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _defaultMaterial;
            if (type == PrimitiveType.Sphere)
            {
                gameObject.AddComponent<SphereCollider>();
            }
            else
            {
                gameObject.AddComponent<BoxCollider>();
            }
        }

        // WHY: build the cube/sphere primitives once to capture their shared meshes and the
        // pipeline default material, then discard the templates — the captured assets stay valid
        // and every later part reuses them with no per-part GameObject churn.
        private static void EnsurePrimitiveCache()
        {
            if (_defaultMaterial != null)
            {
                return;
            }

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            _defaultMaterial = cube.GetComponent<MeshRenderer>().sharedMaterial;
            SafeDestroy(cube);

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = sphere.GetComponent<MeshFilter>().sharedMesh;
            SafeDestroy(sphere);

            // WHY: never trust the primitive's own material while a Scriptable Render Pipeline is active.
            // URP's UniversalRenderPipelineAsset.defaultMaterial is compiled under #if UNITY_EDITOR, so in a
            // PLAYER it is null and CreatePrimitive silently substitutes the BUILT-IN Default-Material
            // (shader "Standard"). That material is non-null — so a null check does not catch it — yet URP
            // cannot render a built-in shader, so every part came out invisible: present, active, correctly
            // sized and collidable, drawing nothing. The Editor never showed it because there the URP asset
            // does return a real Lit material. Build the material from the pipeline's own shader instead.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null)
            {
                Shader shader = pipeline.defaultShader;
                shader = shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                {
                    _defaultMaterial = new Material(shader) { name = "CoreAiRbxPartDefault" };
                }
                else
                {
                    // WHY: the primitive cache is static (shared by every binder instance), so there is
                    // no instance logger to reach here — the process-wide CoreAI logger is the only
                    // seam available, and it still routes through the game-log category filter.
                    Log.Instance.Error(
                        "[CoreAI.RbxApi] A render pipeline is active but its default shader could not be " +
                        "resolved; spawned parts will be invisible. Add the pipeline's Lit shader " +
                        "(e.g. 'Universal Render Pipeline/Lit') to Always Included Shaders.",
                        LogTag.World);
                }
            }
        }

        /// <summary>Roblox Cylinder: the circular axis is the part's local X and the length is
        /// Size.X studs, while Unity's Cylinder mesh is 2 units tall along local Y. The mesh
        /// lives on a child rotated Z+90 (mesh Y onto part X) with the height halved, so the
        /// root's Size-driven localScale yields correct proportions on every axis.</summary>
        private static GameObject BuildCylinderVisual(GameObject gameObject)
        {
            GameObject child = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            child.name = ShapeChildName;
            // WHY: the primitive keeps the pipeline's own material, which is null in a player — see
            // EnsurePrimitiveCache. Cylinders would stay invisible even once every other shape is fixed.
            EnsurePrimitiveCache();
            child.GetComponent<MeshRenderer>().sharedMaterial = _defaultMaterial;
            child.transform.SetParent(gameObject.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            child.transform.localScale = new Vector3(1f, 0.5f, 1f);
            return child;
        }

        /// <summary>Roblox Wedge: a right triangular prism (ramp) — full height at the back
        /// (local -Z) sloping to zero at the front (+Z), width along X. The mesh and its convex
        /// collider live on the root (authored 1 unit = 1 stud), so the Size-driven localScale
        /// carries proportions like Block/Ball.</summary>
        private static void BuildWedgeVisual(GameObject gameObject)
        {
            Mesh mesh = GetWedgeMesh();
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = DefaultLitMaterial();
            MeshCollider collider = gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
        }

        // WHY: flat-facet wedge — each face gets its own vertices with an explicit outward normal
        // (shared vertices would smooth-shade the edges). Winding is CCW-outward so Unity renders
        // each face from the outside. Extents are +-0.5 on every axis (1 unit = 1 stud).
        private static Mesh GetWedgeMesh()
        {
            if (_wedgeMesh != null)
            {
                return _wedgeMesh;
            }

            // WHY: A-F are the six wedge corners — A/B bottom-back, C/D bottom-front, E/F top-back
            // (the slope runs from edge E-F down to edge C-D). The vertex/normal/triangle arrays
            // are grouped per face in the order: bottom (verts 0-3), back (4-7), slope (8-11),
            // left triangle (12-14), right triangle (15-17).
            Vector3 a = new(-0.5f, -0.5f, -0.5f);
            Vector3 b = new(0.5f, -0.5f, -0.5f);
            Vector3 c = new(-0.5f, -0.5f, 0.5f);
            Vector3 d = new(0.5f, -0.5f, 0.5f);
            Vector3 e = new(-0.5f, 0.5f, -0.5f);
            Vector3 f = new(0.5f, 0.5f, -0.5f);
            Vector3 slopeNormal = new Vector3(0f, 1f, 1f).normalized;

            Vector3[] vertices =
            {
                a, b, d, c,
                a, e, f, b,
                e, f, d, c,
                a, c, e,
                b, f, d
            };
            Vector3[] normals =
            {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                slopeNormal, slopeNormal, slopeNormal, slopeNormal,
                Vector3.left, Vector3.left, Vector3.left,
                Vector3.right, Vector3.right, Vector3.right
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 3,
                4, 5, 6, 4, 6, 7,
                8, 10, 9, 8, 11, 10,
                12, 13, 14,
                15, 16, 17
            };

            Mesh mesh = new() { name = "CoreAiWedge" };
            mesh.SetVertices(new List<Vector3>(vertices));
            mesh.SetNormals(new List<Vector3>(normals));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            _wedgeMesh = mesh;
            return _wedgeMesh;
        }

        /// <summary>Roblox CornerWedge: a convex solid with a square base, one raised corner,
        /// two vertical triangular sides, and two sloped triangular sides. Extents stay normalized
        /// to one unit so the part root remains the only scale boundary.</summary>
        private static void BuildCornerWedgeVisual(GameObject gameObject)
        {
            Mesh mesh = GetCornerWedgeMesh();
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = DefaultLitMaterial();
            MeshCollider collider = gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
        }

        // WHY: flat facets need face-local vertices and normals. The raised local -X/-Z corner
        // slopes toward +X and +Z; the two slope faces share the raised-to-opposite diagonal.
        private static Mesh GetCornerWedgeMesh()
        {
            if (_cornerWedgeMesh != null)
            {
                return _cornerWedgeMesh;
            }

            Vector3 a = new(-0.5f, -0.5f, -0.5f);
            Vector3 b = new(0.5f, -0.5f, -0.5f);
            Vector3 c = new(-0.5f, -0.5f, 0.5f);
            Vector3 d = new(0.5f, -0.5f, 0.5f);
            Vector3 e = new(-0.5f, 0.5f, -0.5f);
            Vector3 frontSlopeNormal = new Vector3(0f, 1f, 1f).normalized;
            Vector3 rightSlopeNormal = new Vector3(1f, 1f, 0f).normalized;

            Vector3[] vertices =
            {
                a, b, d, c,
                a, e, b,
                a, c, e,
                e, c, d,
                e, d, b
            };
            Vector3[] normals =
            {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                Vector3.back, Vector3.back, Vector3.back,
                Vector3.left, Vector3.left, Vector3.left,
                frontSlopeNormal, frontSlopeNormal, frontSlopeNormal,
                rightSlopeNormal, rightSlopeNormal, rightSlopeNormal
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 3,
                4, 5, 6,
                7, 8, 9,
                10, 11, 12,
                13, 14, 15
            };

            Mesh mesh = new() { name = "CoreAiCornerWedge" };
            mesh.SetVertices(new List<Vector3>(vertices));
            mesh.SetNormals(new List<Vector3>(normals));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            _cornerWedgeMesh = mesh;
            return _cornerWedgeMesh;
        }

        private static Material DefaultLitMaterial()
        {
            EnsurePrimitiveCache();
            return _defaultMaterial;
        }

        private void ApplyAppearance(BindingEntry entry, in PartProperties properties)
        {
            Renderer renderer = entry.Renderer;
            if (renderer == null)
            {
                return;
            }

            RbxMaterialId materialId = properties.Material;
            if (properties.MaterialVariant != null)
            {
                materialId = new RbxMaterialId(properties.Material.Name,
                    properties.Material.Value, properties.MaterialVariant);
            }

            _materialProvider.TryGetMaterial(in materialId, out Material sharedMaterial);
            renderer.sharedMaterial = sharedMaterial;

            // WHY: MaterialPropertyBlock avoids per-part material instantiation (edit-mode
            // safe, no leaks); both _Color and _BaseColor are set so BiRP and URP shaders read
            // the same value. The block is reused off the entry so no alloc per write.
            float alpha = 1f - Mathf.Clamp01(properties.Transparency);
            bool materialUsesNeutralDefault = sharedMaterial != null &&
                                              sharedMaterial.HasProperty(
                                                  NeutralDefaultPartColorPropertyId) &&
                                              sharedMaterial.GetFloat(
                                                  NeutralDefaultPartColorPropertyId) > 0.5f;
            RbxColor3 tint = properties.ResolveRenderTint(materialUsesNeutralDefault);
            Color color = new(tint.R, tint.G, tint.B, alpha);
            MaterialPropertyBlock block = entry.PropertyBlock ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor(ColorPropertyId, color);
            block.SetColor(BaseColorPropertyId, color);
            renderer.SetPropertyBlock(block);

            // TODO: MVP1 follow-up — Material catalog with a transparent-blend variant so
            // 0 < Transparency < 1 actually alpha-blends; today partial transparency only
            // carries the alpha value, full transparency hides the renderer.
            renderer.enabled = properties.Transparency < 1f;
        }

        private static void ApplyAnchored(BindingEntry entry, bool anchored)
        {
            if (anchored)
            {
                if (entry.Rigidbody != null)
                {
                    // WHY: immediate — toggling Anchored twice in one frame would otherwise
                    // AddComponent a second Rigidbody while the deferred-destroyed one still lives.
                    DestroyNow(entry.Rigidbody);
                    entry.Rigidbody = null;
                }

                return;
            }

            if (entry.Rigidbody == null)
            {
                entry.Rigidbody = entry.GameObject.AddComponent<Rigidbody>();
                // WHY: DEV-6 — Roblox gravity is applied per-body as a custom force (MVP8);
                // Unity's global Physics.gravity must never move Roblox parts.
                entry.Rigidbody.useGravity = false;
            }
        }

        /// <summary>
        /// CanCollide=false turns the part's collider into a trigger rather than disabling it.
        /// WHY: in Roblox a non-colliding part still fires Touched and is still hit by raycasts
        /// (CanTouch and CanQuery default true); a disabled Unity collider does neither, so the
        /// pickup and kill-zone idiom never fired and every ray passed through decorations. A
        /// trigger lets bodies through while the contact relay still hears OnTrigger events, and
        /// RespectCanCollide reads isTrigger back in the physics port.
        /// </summary>
        private static void ApplyCanCollide(BindingEntry entry, bool canCollide)
        {
            if (entry.Collider != null)
            {
                entry.Collider.isTrigger = !canCollide;
            }
        }

        private static void SafeDestroy(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }

        // WHY: synchronous destroy for binder-owned components/objects that are rebuilt in the same
        // call (shape swap, Anchored toggle). Deferred Object.Destroy in Play Mode would leave the
        // old single-per-GameObject component alive for the immediate AddComponent — main-thread
        // only, which the binder already is.
        private static void DestroyNow(Object target)
        {
            if (target != null)
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
