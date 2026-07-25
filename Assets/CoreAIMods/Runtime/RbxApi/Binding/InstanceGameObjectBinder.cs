using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity adapter of the backing-object seam (ROBLOX_API_ROADMAP.md §5.1.1 task 7): mirrors
    /// the whole Roblox explorer into the Unity hierarchy. The DataModel (game) IS the host
    /// GameObject; services parent under it; Folder/Model/Part nest under their instance parents.
    /// Semantics per D5: materialize on entering the scene (DataModel) subtree, DEACTIVATE (not
    /// destroy) on detach so re-parenting stays cheap, destroy on Destroy. Parts become unit-cube
    /// primitives scaled by Size * RbxSpace.MetersPerStud (asset rule, §2 — assets are never
    /// rescaled, only numbers convert); services/Folder/Model become empty transforms. Storage
    /// services (ReplicatedStorage etc.) materialize INACTIVE so their subtrees never render or
    /// collide, mirroring Roblox where only Workspace content is the physical world; a Part
    /// re-parented out of Workspace slides under the inactive service GO and disappears
    /// automatically via Unity's activeInHierarchy. Every spatial conversion goes through
    /// RbxSpace (D2) — this class holds the binder's single call sites allowed by the lint.
    /// Shapes: Block maps to the unit cube, Ball to the unit sphere (both directly on the part
    /// GameObject, localScale = Size * MetersPerStud); Cylinder needs an axis correction, so its
    /// mesh lives on a rotated child (see <see cref="BuildCylinderVisual"/>); Wedge uses a custom
    /// normalized ramp mesh on the root (see <see cref="BuildWedgeVisual"/>).
    /// TODO: MVP1 follow-up — custom normalized CornerWedge mesh (still falls back to Cube today).
    /// TODO: MVP1 follow-up — a Part parented under another Part inherits the parent's Size-driven
    /// localScale (compound world scale); Roblox Size is absolute regardless of ancestry, so parts
    /// should materialize under an unscaled container (generalize the Cylinder Shape-child pattern).
    /// TODO: MVP8 — colliders approximate the visual (non-uniform Ball → SphereCollider on the max
    /// axis; Cylinder → CapsuleCollider with rounded ends); swap to exact colliders with physics.
    /// TODO: MVP8 — per-body gravity force (DEV-6) and reverse physics sync.
    /// </summary>
    public sealed class InstanceGameObjectBinder : IInstanceBackingBinder, IPartPropertySink
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");

        // WHY: Unity has no wedge primitive, so we author one normalized to 1 unit = 1 stud (the
        // only stud-authored asset the scale rule allows, §2) and share the single mesh across all
        // wedge parts — the root's Size-driven localScale carries the dimensions like Block/Ball.
        private static Mesh _wedgeMesh;

        // WHY: the built-in cube/sphere meshes and the pipeline default material are cached once
        // (they are shared assets that survive their template's destroy) so a swarm spawning many
        // parts never pays a CreatePrimitive-plus-destroy GameObject per part.
        private static Mesh _cubeMesh;
        private static Mesh _sphereMesh;
        private static Material _defaultMaterial;

        // WHY: storage services and their subtrees are not part of the physical world, so their
        // GameObject materializes inactive (children inherit inactive-in-hierarchy). Workspace and
        // Lighting stay active. Roblox's service set is fixed, so an explicit list is honest here.
        private static readonly HashSet<string> InactiveServiceClasses =
            new HashSet<string>(System.StringComparer.Ordinal)
            {
                "ReplicatedStorage", "ServerStorage", "ServerScriptService", "StarterPlayer"
            };

        private readonly Transform _worldParent;
        private readonly Dictionary<InstanceId, BindingEntry> _bindings =
            new Dictionary<InstanceId, BindingEntry>();
        private readonly Dictionary<InstanceId, PartProperties> _partProperties =
            new Dictionary<InstanceId, PartProperties>();

        private sealed class BindingEntry
        {
            public GameObject GameObject;
            public bool IsPart;

            /// <summary>False only for the DataModel entry, which reuses the host GameObject —
            /// teardown must never destroy, rename, or re-parent it.</summary>
            public bool OwnsGameObject;

            /// <summary>Shape whose visual is currently built; null until the first Apply.</summary>
            public RbxPartShape? MaterializedShape;

            // WHY: cached on the visual build (ApplyShape) so per-frame property writes skip the
            // GetComponent scans, and the appearance/collide setters target THIS part's own visual
            // (root for Block/Ball/Wedge, the Shape child for Cylinder) rather than descending into
            // nested child parts via GetComponentInChildren. Rebuilt on every shape switch.
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
        }

        /// <summary>Backing objects parent under <paramref name="worldParent"/> (the host that
        /// represents game/DataModel; null = scene root).</summary>
        public InstanceGameObjectBinder(Transform worldParent = null)
        {
            _worldParent = worldParent;
        }

        /// <summary>Count of live backing GameObjects (materialized or parked-deactivated).</summary>
        public int BoundCount => _bindings.Count;

        /// <summary>The backing GameObject, when one exists (world adapter / test seam).</summary>
        public bool TryGetBoundObject(InstanceId id, out GameObject gameObject)
        {
            if (_bindings.TryGetValue(id, out BindingEntry entry))
            {
                gameObject = entry.GameObject;
                return true;
            }

            gameObject = null;
            return false;
        }

        /// <summary>
        /// Reverse of <see cref="TryGetBoundObject"/>: the world instance whose backing GameObject is
        /// <paramref name="gameObject"/> (used by the click-pick source to map a raycast hit back to
        /// an <see cref="InstanceId"/>). A part's own visual and collider sit on this GameObject
        /// (Block/Ball/Wedge) or a binder-owned Shape child (Cylinder), so the pick source walks up
        /// the hit transform's ancestry calling this until a bound object matches.
        /// </summary>
        // WHY: linear scan — a click is a per-click (not per-frame) event and the bound set is the
        // live part count, so a reverse dictionary is not worth the extra bookkeeping on every
        // create/destroy/reparent.
        public bool TryGetInstanceId(GameObject gameObject, out InstanceId id)
        {
            if (gameObject != null)
            {
                foreach (KeyValuePair<InstanceId, BindingEntry> pair in _bindings)
                {
                    if (ReferenceEquals(pair.Value.GameObject, gameObject))
                    {
                        id = pair.Key;
                        return true;
                    }
                }
            }

            id = InstanceId.None;
            return false;
        }

        // ---- IInstanceBackingBinder (D5/D6) -------------------------------------------------

        public void OnEnteredWorld(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                if (entry.OwnsGameObject)
                {
                    // WHY: re-entry reactivates the parked object — D5 makes re-parenting cheap.
                    entry.GameObject.transform.SetParent(ResolveParentTransform(record.Instance), true);
                    entry.GameObject.name = record.Instance.Name;
                    entry.GameObject.SetActive(DesiredActiveSelf(record.Instance));
                }

                return;
            }

            try
            {
                entry = CreateEntry(record.Instance);
                _bindings.Add(record.Id, entry);
                if (entry.IsPart)
                {
                    Apply(entry, GetPartPropertiesOrDefault(record.Id));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    $"[CoreAI.RbxApi] Failed to materialize '{record.Instance.Name}' " +
                    $"(class={record.Instance.ClassName}, id={record.Id}): {ex.Message}");
            }
        }

        public void OnLeftWorld(InstanceRecord record)
        {
            // WHY: the DataModel host GameObject never leaves its own tree; guard so nothing
            // deactivates or re-parents the host.
            if (!_bindings.TryGetValue(record.Id, out BindingEntry entry) || !entry.OwnsGameObject)
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
            if (!_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                _partProperties.Remove(record.Id);
                return;
            }

            _bindings.Remove(record.Id);
            _partProperties.Remove(record.Id);
            // WHY: the DataModel's backing object is the host GameObject — teardown releases the
            // materialized children but never the host itself (RbxWorldHost owns its lifecycle).
            if (entry.OwnsGameObject)
            {
                SafeDestroy(entry.GameObject);
            }
        }

        public void OnReparented(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry) && entry.OwnsGameObject)
            {
                // WHY: worldPositionStays — CFrames are world-space, so a hierarchy move
                // must not shift the rendered pose.
                entry.GameObject.transform.SetParent(ResolveParentTransform(record.Instance), true);
            }
        }

        public void OnNameChanged(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry) && entry.OwnsGameObject)
            {
                entry.GameObject.name = record.Instance.Name;
            }
        }

        // ---- IPartPropertySink (one-way push) -----------------------------------------------

        public void SetCFrame(InstanceId id, in RbxCFrame cframe)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CFrame = cframe;
            Store(id, properties, PartAspect.Transform);
        }

        public void SetPosition(InstanceId id, RbxVector3 position)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Position = position;
            Store(id, properties, PartAspect.Transform);
        }

        public void SetSize(InstanceId id, RbxVector3 size)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Size = size;
            Store(id, properties, PartAspect.Transform);
        }

        public void SetColor(InstanceId id, RbxColor3 color)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Color = color;
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

        public void SetPartProperties(InstanceId id, in PartProperties properties)
        {
            Store(id, properties, PartAspect.Full);
        }

        public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
        {
            return _partProperties.TryGetValue(id, out properties);
        }

        public PartProperties GetPartPropertiesOrDefault(InstanceId id)
        {
            return _partProperties.TryGetValue(id, out PartProperties properties)
                ? properties
                : PartProperties.CreateDefault();
        }

        // WHY: a script that moves/recolors a part each frame is the hottest API path, so each
        // setter re-applies ONLY the aspect it touched instead of re-running the whole
        // materialization (shape scan + component lookups + a MaterialPropertyBlock alloc) on
        // every transform write. Full is used at materialization and on shape switches.
        private enum PartAspect { Full, Transform, Appearance, Anchored, CanCollide }

        private void Store(InstanceId id, in PartProperties properties, PartAspect aspect)
        {
            _partProperties[id] = properties;
            if (!_bindings.TryGetValue(id, out BindingEntry entry) || !entry.IsPart)
            {
                return;
            }

            switch (aspect)
            {
                case PartAspect.Transform:
                    ApplyTransform(entry, properties);
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

        // ---- Materialization ----------------------------------------------------------------

        private BindingEntry CreateEntry(RbxInstance instance)
        {
            if (instance.IsA("DataModel"))
            {
                // WHY: game IS the host GameObject — no new GO, and the entry is flagged so
                // teardown never destroys/renames/re-parents the host. Falls back to a fresh root
                // only when the binder was created without a host transform.
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
            GameObject gameObject = new GameObject();
            gameObject.name = instance.Name;
            gameObject.transform.SetParent(ResolveParentTransform(instance), false);
            gameObject.SetActive(DesiredActiveSelf(instance));
            return new BindingEntry { GameObject = gameObject, IsPart = isPart, OwnsGameObject = true };
        }

        /// <summary>Storage-service GameObjects materialize inactive so their subtrees stay out
        /// of the physical world; everything else (Workspace, Lighting, Folder, Model, Part) is
        /// active and inherits its parent's hierarchy state.</summary>
        private static bool DesiredActiveSelf(RbxInstance instance)
        {
            return !InactiveServiceClasses.Contains(instance.ClassName);
        }

        private Transform ResolveParentTransform(RbxInstance instance)
        {
            RbxInstance parent = instance.Parent;
            if (parent != null && _bindings.TryGetValue(parent.Id, out BindingEntry parentEntry))
            {
                return parentEntry.GameObject.transform;
            }

            return _worldParent;
        }

        // ---- Property application (the D2-allowed conversion call sites) --------------------

        private static void Apply(BindingEntry entry, in PartProperties properties)
        {
            ApplyShape(entry, properties.Shape);
            ApplyTransform(entry, properties);
            ApplyAppearance(entry, properties);
            ApplyAnchored(entry, properties.Anchored);
            ApplyCanCollide(entry, properties.CanCollide);
        }

        private static void ApplyTransform(BindingEntry entry, in PartProperties properties)
        {
            Transform transform = entry.GameObject.transform;
            (Vector3 position, Quaternion rotation) = RbxSpace.ToUnityPose(properties.CFrame);
            transform.SetPositionAndRotation(position, rotation);
            // WHY: for every shape the part root carries Size * MetersPerStud (D3); shape
            // primitives are authored so 1 local unit = 1 stud (Cylinder's child corrects
            // Unity's 2-unit-tall mesh, see BuildCylinderVisual).
            transform.localScale = RbxSpace.SizeToUnity(properties.Size);
        }

        // ---- Shape materialization ----------------------------------------------------------

        private const string ShapeChildName = "Shape";

        private static void ApplyShape(BindingEntry entry, RbxPartShape shape)
        {
            RbxPartShape normalized = NormalizeShape(shape);
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
                    break;
                case RbxPartShape.Wedge:
                    BuildWedgeVisual(entry.GameObject);
                    break;
                default:
                    BuildRootPrimitiveVisual(entry.GameObject, PrimitiveType.Cube);
                    break;
            }

            entry.MaterializedShape = normalized;
            CacheVisualComponents(entry);
        }

        // WHY: resolve the renderer/collider from THIS part's own visual — the root for
        // Block/Ball/Wedge, or the binder-owned ShapeChild for Cylinder (held by reference, never
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

        // TODO: MVP1 follow-up — custom normalized CornerWedge mesh; it still falls back to Cube.
        private static RbxPartShape NormalizeShape(RbxPartShape shape)
        {
            return shape == RbxPartShape.CornerWedge
                ? RbxPartShape.Block
                : shape;
        }

        /// <summary>Removes the previous shape's mesh/collider (root components and the binder-owned
        /// ShapeChild), keeping the GameObject identity and its Rigidbody untouched.</summary>
        private static void StripShapeVisual(BindingEntry entry)
        {
            // WHY: destroy synchronously even in Play Mode. ApplyShape rebuilds the visual on the
            // very next lines, and a deferred Object.Destroy would leave the old MeshRenderer alive
            // when AddComponent<MeshRenderer> runs (Renderer is single-per-GameObject → the add
            // fails and returns null) and would let CacheVisualComponents cache soon-fake-null
            // components. These are binder-owned, so DestroyImmediate is legal here.
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
            var filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = type == PrimitiveType.Sphere ? _sphereMesh : _cubeMesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
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
        }

        /// <summary>Roblox Cylinder: the circular axis is the part's local X and the length is
        /// Size.X studs, while Unity's Cylinder mesh is 2 units tall along local Y. The mesh
        /// lives on a child rotated Z+90 (mesh Y onto part X) with the height halved, so the
        /// root's Size-driven localScale yields correct proportions on every axis.</summary>
        private static GameObject BuildCylinderVisual(GameObject gameObject)
        {
            GameObject child = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            child.name = ShapeChildName;
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
            var filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = DefaultLitMaterial();
            var collider = gameObject.AddComponent<MeshCollider>();
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
                b, f, d,
            };
            Vector3[] normals =
            {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                slopeNormal, slopeNormal, slopeNormal, slopeNormal,
                Vector3.left, Vector3.left, Vector3.left,
                Vector3.right, Vector3.right, Vector3.right,
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 3,
                4, 5, 6, 4, 6, 7,
                8, 10, 9, 8, 11, 10,
                12, 13, 14,
                15, 16, 17,
            };

            var mesh = new Mesh { name = "CoreAiWedge" };
            mesh.SetVertices(new List<Vector3>(vertices));
            mesh.SetNormals(new List<Vector3>(normals));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            _wedgeMesh = mesh;
            return _wedgeMesh;
        }

        // WHY: the wedge reuses the same cached pipeline default material as Block/Ball so it
        // renders identically before the appearance pass overrides color — no extra template.
        private static Material DefaultLitMaterial()
        {
            EnsurePrimitiveCache();
            return _defaultMaterial;
        }

        private static void ApplyAppearance(BindingEntry entry, in PartProperties properties)
        {
            Renderer renderer = entry.Renderer;
            if (renderer == null)
            {
                return;
            }

            // WHY: MaterialPropertyBlock avoids per-part material instantiation (edit-mode
            // safe, no leaks); both _Color and _BaseColor are set so BiRP and URP shaders read
            // the same value. The block is reused off the entry so no alloc per write.
            float alpha = 1f - Mathf.Clamp01(properties.Transparency);
            var color = new Color(properties.Color.R, properties.Color.G, properties.Color.B, alpha);
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

        private static void ApplyCanCollide(BindingEntry entry, bool canCollide)
        {
            // WHY: entry.Collider is the part's own visual collider (root for Block/Ball/Wedge, the
            // Shape child for Cylinder), cached at build — never a nested child part's collider.
            if (entry.Collider != null)
            {
                entry.Collider.enabled = canCollide;
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
