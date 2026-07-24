using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity adapter of <see cref="IClickPickSource"/> over the rendering camera and the GameObject
    /// binder, resolved once at composition (RbxWorldHost) — no scene searches in the pick path.
    /// Mirrors <see cref="UnityCameraRig"/>: converts the Roblox top-left screen point to the
    /// camera's bottom-left screen space, casts a ray, and maps the nearest collider back to a world
    /// instance through <see cref="InstanceGameObjectBinder.TryGetInstanceId"/> (walking up the hit
    /// transform's ancestry so a Cylinder's Shape child or a nested visual still resolves to its
    /// part). Distance is reported in studs through <see cref="RbxSpace"/> (D2, this file is in
    /// the lint-allowed Binding folder).
    /// </summary>
    public sealed class UnityClickPickSource : IClickPickSource
    {
        private readonly Camera _camera;
        private readonly InstanceGameObjectBinder _binder;

        /// <summary><paramref name="camera"/> is the rendering camera used for ScreenPointToRay;
        /// <paramref name="binder"/> maps hit GameObjects back to world instances. Both are required.</summary>
        public UnityClickPickSource(Camera camera, InstanceGameObjectBinder binder)
        {
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        }

        public bool TryPick(RbxVector2 screenPositionTopLeft, out InstanceId hitId,
            out double distanceStuds)
        {
            hitId = InstanceId.None;
            distanceStuds = 0d;

            // WHY: the camera can be destroyed at runtime (scene teardown); guard so a click never
            // touches a dead Unity object.
            if (_camera == null)
            {
                return false;
            }

            // WHY: GetMouseLocation is Roblox convention (pixels, top-left origin); Unity screen space is
            // bottom-left origin, so flip Y back. Flip against Screen.height (absolute screen space, what
            // ScreenPointToRay expects) — the SAME height the input source used to build the top-left point
            // (UnityNewInputSource: Screen.height - y). Using _camera.pixelHeight here instead would only
            // match for a full-screen camera and mis-pick under a viewport rect / render-texture target.
            float screenX = screenPositionTopLeft.X;
            float screenY = Screen.height - screenPositionTopLeft.Y;
            Ray ray = _camera.ScreenPointToRay(new Vector3(screenX, screenY, 0f));
            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
            {
                return false;
            }

            // WHY: walk up from the hit collider — the collider may sit on the part root
            // (Block/Ball/Wedge) or a binder-owned Shape child (Cylinder), and a mod may nest
            // visuals; the first ancestor that is a bound backing object is the clicked part.
            for (Transform t = hit.collider != null ? hit.collider.transform : null;
                 t != null;
                 t = t.parent)
            {
                if (_binder.TryGetInstanceId(t.gameObject, out InstanceId id))
                {
                    hitId = id;
                    distanceStuds = RbxSpace.LengthFromUnity(hit.distance);
                    return true;
                }
            }

            return false;
        }
    }
}
