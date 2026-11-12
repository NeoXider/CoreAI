using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Building;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity adapter of <see cref="IClickPickSource"/> over the rendering camera and the GameObject
    /// binder, resolved once at composition (RbxWorldHost) — no scene searches in the pick path.
    /// Mirrors <see cref="UnityCameraRig"/>: converts the Roblox top-left screen point to the
    /// camera's bottom-left screen space, casts a ray, and maps the nearest collider back to a world
    /// instance through <see cref="InstanceGameObjectBinder.TryResolvePartInstanceId"/> (which walks
    /// up the hit transform's ancestry so a Cylinder's Shape child or a nested visual still resolves
    /// to its part). Distance is reported in studs through <see cref="RbxSpace"/> (D2, this file is
    /// in the lint-allowed Binding folder).
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
            // WHY Collide, stated rather than left to the project setting: a CanCollide=false part is
            // a trigger on the Unity side, and Roblox still lets a player click it.
            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide))
            {
                return false;
            }

            // WHY the resolver: the collider may sit on the part root (Block/Ball/Wedge) or a
            // binder-owned Shape child (Cylinder), and a mod may nest visuals; the nearest bound
            // ancestor decides, and only a part counts as clicked.
            if (hit.collider == null
                || !_binder.TryResolvePartInstanceId(hit.collider.gameObject, out InstanceId id))
            {
                return false;
            }

            hitId = id;
            distanceStuds = RbxSpace.LengthFromUnity(hit.distance);
            return true;
        }

        /// <summary>Resolves Terrain chunk hits to the Terrain instance; null picks parts only.</summary>
        public Terrain.IRbxTerrainHitResolver TerrainHits { get; set; }

        /// <inheritdoc />
        public bool TryPickWorld(RbxVector2 screenPositionTopLeft, Func<InstanceId, bool> isEligible,
            double maxDistanceStuds, out RbxWorldPick pick)
        {
            pick = RbxWorldPick.None;
            if (_camera == null || maxDistanceStuds <= 0d)
            {
                return false;
            }

            Ray ray = ScreenRay(screenPositionTopLeft);
            float reach = double.IsInfinity(maxDistanceStuds) || maxDistanceStuds > 1e6d
                ? Mathf.Infinity
                : RbxSpace.LengthToUnity((float)maxDistanceStuds);
            if (TerrainHits is Terrain.IRbxTerrainColliderReadiness terrainColliders)
            {
                // WHY: a chunk whose collider is still queued behind the frame budget would let the pick pass through
                // the ground; the walk is capped as for workspace:Raycast.
                terrainColliders.EnsureCollidersAlong(ray.origin, ray.direction,
                    float.IsInfinity(reach) ? RbxSpace.LengthToUnity(2048f) : reach,
                    UnityRbxPhysicsPort.MaxColliderBuildsPerRay);
            }

            int count = Physics.RaycastNonAlloc(ray, _hits, reach, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            if (count == _hits.Length)
            {
                // WHY grown and cast again: a full buffer may have dropped hits nearer than those it kept.
                _hits = new RaycastHit[_hits.Length * 2];
                count = Physics.RaycastNonAlloc(ray, _hits, reach, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide);
            }
            // WHY sorted: RaycastNonAlloc returns hits in no order, and the nearest eligible one is the pick.
            System.Array.Sort(_hits, 0, count, HitDistanceComparer.Instance);
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = _hits[index];
                if (hit.collider == null)
                {
                    continue;
                }

                bool terrain = false;
                InstanceId id;
                RbxVector3 normal;
                if (_binder.TryResolvePartInstanceId(hit.collider.gameObject, out id))
                {
                    normal = RbxSpace.DirectionFromUnity(hit.normal);
                }
                else if (TerrainHits != null && TerrainHits.TryResolveHit(hit.collider, hit.point, hit.normal, out id,
                             out _, out normal))
                {
                    terrain = true;
                }
                else
                {
                    continue;
                }

                if (isEligible != null && !isEligible(id))
                {
                    continue;
                }

                pick = new RbxWorldPick(id, RbxSpace.FromUnity(hit.point), normal,
                    RbxSpace.LengthFromUnity(hit.distance), terrain, true);
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryGetRay(RbxVector2 screenPositionTopLeft, out RbxVector3 origin, out RbxVector3 direction)
        {
            if (_camera == null)
            {
                origin = default;
                direction = default;
                return false;
            }

            Ray ray = ScreenRay(screenPositionTopLeft);
            origin = RbxSpace.FromUnity(ray.origin);
            direction = RbxSpace.DirectionFromUnity(ray.direction);
            return true;
        }

        private Ray ScreenRay(RbxVector2 screenPositionTopLeft)
        {
            return _camera.ScreenPointToRay(new Vector3(screenPositionTopLeft.X, Screen.height - screenPositionTopLeft.Y,
                0f));
        }

        private RaycastHit[] _hits = new RaycastHit[32];

        private sealed class HitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly HitDistanceComparer Instance = new();

            public int Compare(RaycastHit x, RaycastHit y)
            {
                return x.distance.CompareTo(y.distance);
            }
        }
    }
}
