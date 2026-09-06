using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The Unity half of MVP8's physics slice: real raycasts, per-body gravity, and contact events
    /// for <c>BasePart.Touched</c>.
    /// </summary>
    /// <remarks>
    /// WHY gravity is applied per body instead of set once on the scene: <c>Physics.gravity</c> is
    /// the HOST's setting. CoreAI is a package dropped into someone else's project, and a world whose
    /// script writes <c>workspace.Gravity = 5</c> must not change how the host's own objects fall
    /// (DEV-6). Each bound part therefore has <c>useGravity</c> off and is accelerated by the world's
    /// own gravity every fixed step — the same fall, without reaching outside the world.
    /// </remarks>
    public sealed class UnityRbxPhysicsPort : IRbxPhysicsPort, IDisposable
    {
        /// <summary>Upper bound on how far <see cref="TryRaycast"/> grows its scratch buffer.</summary>
        /// <remarks>
        /// WHY a cap at all: without one, a pathological scene (or a script deliberately spamming
        /// colliders along a ray) could grow the buffer without limit every raycast. 4096 colliders
        /// on one ray is already an unreasonable scene; beyond the cap the sweep uses whatever the
        /// buffer holds rather than growing forever.
        /// </remarks>
        private const int MaxHitScratchLength = 4096;

        private readonly InstanceGameObjectBinder _binder;
        private readonly List<Rigidbody> _bodyScratch = new();
        private RaycastHit[] _hitScratch = new RaycastHit[32];
        private Vector3 _gravityMetres =
            new(0f, -RbxSpace.AccelerationToUnity((float)RbxWorldPhysics.DefaultGravity), 0f);

        /// <summary>Creates the adapter over the binder that owns the bound GameObjects.</summary>
        public UnityRbxPhysicsPort(InstanceGameObjectBinder binder)
        {
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            _binder.ContactObserved += OnBinderContact;
        }

        /// <inheritdoc />
        public event Action<InstanceId, InstanceId> ContactBegan;

        /// <inheritdoc />
        public event Action<InstanceId, InstanceId> ContactEnded;

        /// <summary>The gravity vector applied to bound bodies, in metres per second squared.</summary>
        public Vector3 GravityMetresPerSecondSquared => _gravityMetres;

        /// <inheritdoc />
        public void SetGravity(double studsPerSecondSquared)
        {
            _gravityMetres = new Vector3(
                0f, -RbxSpace.AccelerationToUnity((float)studsPerSecondSquared), 0f);
        }

        /// <summary>
        /// Applies world gravity to every bound unanchored body. Call once per fixed step.
        /// </summary>
        public void ApplyGravity()
        {
            _bodyScratch.Clear();
            _binder.CollectSimulatedBodies(_bodyScratch);
            for (int index = 0; index < _bodyScratch.Count; index++)
            {
                Rigidbody body = _bodyScratch[index];
                if (body == null || body.isKinematic)
                {
                    continue;
                }

                // WHY Acceleration rather than Force: it is mass-independent, so a part's weight
                // cannot change how fast it falls — matching Roblox, where Gravity is an
                // acceleration and two parts of different size land together.
                body.useGravity = false;
                body.AddForce(_gravityMetres, ForceMode.Acceleration);
            }
        }

        /// <inheritdoc />
        public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
            bool respectCanCollide, Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
        {
            hit = default;
            Vector3 origin = RbxSpace.ToUnity(originStuds);
            Vector3 direction = RbxSpace.DirectionToUnity(directionStuds)
                * RbxSpace.LengthToUnity(directionStuds.Magnitude);
            float distance = direction.magnitude;
            if (distance <= 0f)
            {
                return false;
            }

            // WHY every hit and not Physics.Raycast: the nearest collider may belong to a part the
            // filter excludes, and stopping at it would report a miss through a wall the script
            // deliberately ignored. The scratch buffer keeps the common case allocation-free.
            //
            // WHY grow-and-retry rather than Physics.RaycastAll: RaycastNonAlloc returning a count
            // equal to the buffer's length means the buffer was full and Unity may have dropped hits
            // arbitrarily, including ones nearer than what made it in — the leftover 32-slot buffer
            // silently mis-reported misses in any host scene with more than 32 colliders along a
            // ray. RaycastAll allocates a new array on every call regardless of hit count; growing
            // _hitScratch instead pays that allocation once per session, the first time a ray meets
            // an unusually crowded line, and every raycast after that stays allocation-free again.
            int count = Physics.RaycastNonAlloc(
                origin, direction / distance, _hitScratch, distance,
                ~0, QueryTriggerInteraction.Ignore);
            while (count == _hitScratch.Length && _hitScratch.Length < MaxHitScratchLength)
            {
                _hitScratch = new RaycastHit[Math.Min(_hitScratch.Length * 2, MaxHitScratchLength)];
                count = Physics.RaycastNonAlloc(
                    origin, direction / distance, _hitScratch, distance,
                    ~0, QueryTriggerInteraction.Ignore);
            }

            bool found = false;
            float bestDistance = float.MaxValue;
            for (int index = 0; index < count; index++)
            {
                RaycastHit candidate = _hitScratch[index];
                if (candidate.distance >= bestDistance
                    || candidate.collider == null
                    || !_binder.TryGetInstanceId(candidate.collider.gameObject, out InstanceId id))
                {
                    continue;
                }

                if (respectCanCollide && !CollidesWithQueries(candidate.collider))
                {
                    continue;
                }

                if (isEligible != null && !isEligible(id))
                {
                    continue;
                }

                bestDistance = candidate.distance;
                hit = new RbxPhysicsRaycastHit(
                    id,
                    RbxSpace.FromUnity(candidate.point),
                    RbxSpace.DirectionFromUnity(candidate.normal),
                    MaterialOf(id),
                    RbxSpace.LengthFromUnity(candidate.distance));
                found = true;
            }

            return found;
        }

        /// <summary>Stops observing the binder.</summary>
        public void Dispose()
        {
            _binder.ContactObserved -= OnBinderContact;
            ContactBegan = null;
            ContactEnded = null;
        }

        private RbxMaterialId MaterialOf(InstanceId id)
        {
            return _binder.TryGetPartProperties(id, out PartProperties properties)
                ? properties.Material
                : RbxMaterialId.Plastic;
        }

        private static bool CollidesWithQueries(Collider collider)
        {
            // RespectCanCollide asks the query to use CanCollide instead of CanQuery; a non-colliding
            // part is a trigger on the Unity side, so the collider's own flag is the answer.
            return !collider.isTrigger;
        }

        private void OnBinderContact(InstanceId first, InstanceId second, bool began)
        {
            if (began)
            {
                ContactBegan?.Invoke(first, second);
            }
            else
            {
                ContactEnded?.Invoke(first, second);
            }
        }
    }
}
