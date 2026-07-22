using System.Collections.Generic;
using CoreAI.Mods.Roblox.Datatypes;
using CoreAI.Mods.Roblox.Spatial;
using CoreAI.Mods.Roblox.Instances;
using UnityEngine;

namespace CoreAI.Mods.Roblox.Binding
{
    /// <summary>
    /// Unity adapter of the backing-object seam (ROBLOX_API_ROADMAP.md §5.1.1 task 7):
    /// materializes registry instances as GameObjects under a world root. Semantics per D5:
    /// materialize on entering the workspace subtree, DEACTIVATE (not destroy) on detach so
    /// re-parenting stays cheap, destroy on Destroy. Parts become unit-cube primitives scaled
    /// by Size * RobloxSpace.MetersPerStud (asset rule, §2 — assets are never rescaled, only
    /// numbers convert); Folder/Model/other containers become empty transforms. Every spatial
    /// conversion goes through RobloxSpace (D2) — this class holds the binder's single call
    /// sites allowed by the lint.
    /// TODO: MVP1 follow-up — primitives catalog for Ball and the stud-authored
    /// Wedge/CornerWedge/oriented-Cylinder meshes (currently Block only).
    /// TODO: MVP8 — per-body gravity force (DEV-6) and reverse physics sync.
    /// </summary>
    public sealed class InstanceGameObjectBinder : IInstanceBackingBinder, IPartPropertySink
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");

        private readonly Transform _worldParent;
        private readonly Dictionary<InstanceId, BindingEntry> _bindings =
            new Dictionary<InstanceId, BindingEntry>();
        private readonly Dictionary<InstanceId, PartProperties> _partProperties =
            new Dictionary<InstanceId, PartProperties>();

        private sealed class BindingEntry
        {
            public GameObject GameObject;
            public bool IsPart;
        }

        /// <summary>Backing objects parent under <paramref name="worldParent"/>
        /// (null = scene root).</summary>
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

        // ---- IInstanceBackingBinder (D5/D6) -------------------------------------------------

        public void OnEnteredWorld(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                // WHY: re-entry reactivates the parked object — D5 makes re-parenting cheap.
                entry.GameObject.transform.SetParent(ResolveParentTransform(record.Instance), true);
                entry.GameObject.name = record.Instance.Name;
                entry.GameObject.SetActive(true);
                return;
            }

            entry = CreateEntry(record.Instance);
            _bindings.Add(record.Id, entry);
            if (entry.IsPart)
            {
                Apply(entry, GetPartPropertiesOrDefault(record.Id));
            }
        }

        public void OnLeftWorld(InstanceRecord record)
        {
            if (!_bindings.TryGetValue(record.Id, out BindingEntry entry))
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
            SafeDestroy(entry.GameObject);
        }

        public void OnReparented(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                // WHY: worldPositionStays — CFrames are world-space, so a hierarchy move
                // must not shift the rendered pose.
                entry.GameObject.transform.SetParent(ResolveParentTransform(record.Instance), true);
            }
        }

        public void OnNameChanged(InstanceRecord record)
        {
            if (_bindings.TryGetValue(record.Id, out BindingEntry entry))
            {
                entry.GameObject.name = record.Instance.Name;
            }
        }

        // ---- IPartPropertySink (one-way push) -----------------------------------------------

        public void SetCFrame(InstanceId id, in RbxCFrame cframe)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CFrame = cframe;
            Store(id, properties);
        }

        public void SetPosition(InstanceId id, RbxVector3 position)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Position = position;
            Store(id, properties);
        }

        public void SetSize(InstanceId id, RbxVector3 size)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Size = size;
            Store(id, properties);
        }

        public void SetColor(InstanceId id, RbxColor3 color)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Color = color;
            Store(id, properties);
        }

        public void SetAnchored(InstanceId id, bool anchored)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Anchored = anchored;
            Store(id, properties);
        }

        public void SetTransparency(InstanceId id, float transparency)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Transparency = Mathf.Clamp01(transparency);
            Store(id, properties);
        }

        public void SetCanCollide(InstanceId id, bool canCollide)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CanCollide = canCollide;
            Store(id, properties);
        }

        public void SetPartProperties(InstanceId id, in PartProperties properties)
        {
            Store(id, properties);
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

        private void Store(InstanceId id, in PartProperties properties)
        {
            _partProperties[id] = properties;
            if (_bindings.TryGetValue(id, out BindingEntry entry) && entry.IsPart)
            {
                Apply(entry, properties);
            }
        }

        // ---- Materialization ----------------------------------------------------------------

        private BindingEntry CreateEntry(RbxInstance instance)
        {
            bool isPart = instance.IsA("BasePart");
            // WHY: CreatePrimitive's built-in cube is authored 1 unit = 1 stud for us, so the
            // asset rule holds: geometry is never rescaled, only localScale carries the numbers.
            GameObject gameObject = isPart
                ? GameObject.CreatePrimitive(PrimitiveType.Cube)
                : new GameObject();
            gameObject.name = instance.Name;
            gameObject.transform.SetParent(ResolveParentTransform(instance), false);
            return new BindingEntry { GameObject = gameObject, IsPart = isPart };
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
            Transform transform = entry.GameObject.transform;
            (Vector3 position, Quaternion rotation) = RobloxSpace.ToUnityPose(properties.CFrame);
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = RobloxSpace.SizeToUnity(properties.Size);

            ApplyAppearance(entry.GameObject, properties);
            ApplyAnchored(entry.GameObject, properties.Anchored);
            ApplyCanCollide(entry.GameObject, properties.CanCollide);
        }

        private static void ApplyAppearance(GameObject gameObject, in PartProperties properties)
        {
            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            // WHY: MaterialPropertyBlock avoids per-part material instantiation (edit-mode
            // safe, no leaks); both _Color and _BaseColor are set so BiRP and URP shaders read
            // the same value.
            float alpha = 1f - Mathf.Clamp01(properties.Transparency);
            var color = new Color(properties.Color.R, properties.Color.G, properties.Color.B, alpha);
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor(ColorPropertyId, color);
            block.SetColor(BaseColorPropertyId, color);
            renderer.SetPropertyBlock(block);

            // TODO: MVP1 follow-up — Material catalog with a transparent-blend variant so
            // 0 < Transparency < 1 actually alpha-blends; today partial transparency only
            // carries the alpha value, full transparency hides the renderer.
            renderer.enabled = properties.Transparency < 1f;
        }

        private static void ApplyAnchored(GameObject gameObject, bool anchored)
        {
            var body = gameObject.GetComponent<Rigidbody>();
            if (anchored)
            {
                if (body != null)
                {
                    SafeDestroy(body);
                }

                return;
            }

            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
                // WHY: DEV-6 — Roblox gravity is applied per-body as a custom force (MVP8);
                // Unity's global Physics.gravity must never move Roblox parts.
                body.useGravity = false;
            }
        }

        private static void ApplyCanCollide(GameObject gameObject, bool canCollide)
        {
            var collider = gameObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = canCollide;
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
    }
}
