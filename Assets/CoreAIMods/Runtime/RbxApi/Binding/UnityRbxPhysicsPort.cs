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
        private readonly List<Collider> _colliderScratch = new();

        /// <summary>Queued Terrain chunks one body may have built in one fixed step, nearest it first.</summary>
        public const int MaxColliderBuildsPerBody = 4;

        /// <summary>Queued Terrain chunks one ray may have built, in the order it crosses them.</summary>
        public const int MaxColliderBuildsPerRay = 8;

        /// <summary>Fixed steps a character was held because its next step's chunk was still queued (diagnostics).</summary>
        public long HeldCharacterSteps { get; private set; }

        /// <summary>The longest a character is held for a queued chunk, in seconds; past it the chunk is built anyway.</summary>
        public const float MaxCharacterHoldSeconds = 0.25f;

        /// <summary>Times a character's next-step chunks were built past the cap after a full hold (diagnostics).</summary>
        public long ForcedCharacterBuilds { get; private set; }

        private readonly Dictionary<Rigidbody, float> _heldSeconds = new();
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

        /// <summary>
        /// Resolves rays that hit a Terrain chunk collider to the Terrain cell; null leaves Terrain unhit. When it
        /// also implements <see cref="CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainColliderReadiness"/>, rays and
        /// simulated bodies have the queued Terrain colliders in their way built first.
        /// </summary>
        public CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainHitResolver TerrainHits { get; set; }

        /// <summary>The gravity vector applied to bound bodies, in metres per second squared.</summary>
        public Vector3 GravityMetresPerSecondSquared => _gravityMetres;

        /// <inheritdoc />
        public void SetGravity(double studsPerSecondSquared)
        {
            _gravityMetres = new Vector3(
                0f, -RbxSpace.AccelerationToUnity((float)studsPerSecondSquared), 0f);
        }

        /// <summary>
        /// Applies world gravity to every bound unanchored body. Call once per fixed step. When
        /// <see cref="TerrainHits"/> also builds colliders on demand
        /// (<see cref="CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainColliderReadiness"/>), the Terrain chunks
        /// around each body and its next step are built first, so no body falls through a chunk whose
        /// collider is still queued behind the frame budget.
        /// </summary>
        public void ApplyGravity()
        {
            _bodyScratch.Clear();
            _binder.CollectSimulatedBodies(_bodyScratch);
            CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainColliderReadiness terrain =
                TerrainHits as CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainColliderReadiness;
            for (int index = 0; index < _bodyScratch.Count; index++)
            {
                Rigidbody body = _bodyScratch[index];
                if (body == null || body.isKinematic)
                {
                    continue;
                }

                if (terrain != null)
                {
                    terrain.EnsureColliders(SweptBounds(body), MaxColliderBuildsPerBody);
                    // WHY only a chunk with no collider at all: a chunk that has one and only waits for a rebuild
                    // (the land streaming in, a block placed or broken) keeps its old collider for one more step;
                    // holding for it stopped every jump near a busy chunk. A missing collider past the cap would let
                    // the body into a block whose collider appears a step later, so only the horizontal motion into it
                    // waits; the vertical motion, a jump or a fall, is never touched.
                    if (UnityRbxCharacterMotor.TryGetFeet(body, out float _)
                        && terrain.IsColliderMissingIn(NextStepBounds(body), out Vector3 missingCentre))
                    {
                        _heldSeconds.TryGetValue(body, out float held);
                        if (held < MaxCharacterHoldSeconds)
                        {
                            _heldSeconds[body] = held + Time.fixedDeltaTime;
                            Vector3 velocity = body.linearVelocity;
                            Vector3 toward = missingCentre - body.position;
                            toward.y = 0f;
                            float length = Mathf.Sqrt(toward.sqrMagnitude);
                            if (length > 1e-4f)
                            {
                                toward /= length;
                                float into = velocity.x * toward.x + velocity.z * toward.z;
                                if (into > 0f)
                                {
                                    velocity.x -= toward.x * into;
                                    velocity.z -= toward.z * into;
                                    body.linearVelocity = velocity;
                                }
                            }

                            HeldCharacterSteps++;
                        }
                        else
                        {
                            // WHY build past the cap after the hold: a queue starved by a burst must never freeze a
                            // player; after a quarter second its next step's chunks are built now, whatever they cost.
                            terrain.EnsureColliders(NextStepBounds(body));
                            ForcedCharacterBuilds++;
                            _heldSeconds.Remove(body);
                        }
                    }
                    else
                    {
                        _heldSeconds.Remove(body);
                    }
                }

                if (TerrainHits is CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainSolidCells cells)
                {
                    LiftOutOfTerrain(body, cells);
                }

                if (TerrainHits is CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainWaterCells water)
                {
                    SenseWater(body, water);
                }

                // WHY Acceleration rather than Force: it is mass-independent, so a part's weight
                // cannot change how fast it falls — matching Roblox, where Gravity is an
                // acceleration and two parts of different size land together.
                body.useGravity = false;
                body.AddForce(_gravityMetres, ForceMode.Acceleration);
            }
        }

        /// <summary>
        /// The body's enabled colliders together, stretched along two steps of its velocity and grown by two Terrain
        /// cells.
        /// </summary>
        /// <remarks>
        /// WHY every enabled collider and not the first: a character's root keeps its switched-off box as its first
        /// collider and collides with the body capsule its motor added; the box's empty bounds left the cells the
        /// body was about to enter without colliders.
        /// </remarks>
        private Bounds SweptBounds(Rigidbody body)
        {
            _colliderScratch.Clear();
            body.GetComponents(_colliderScratch);
            Bounds bounds = new(body.position, Vector3.zero);
            bool found = false;
            foreach (Collider collider in _colliderScratch)
            {
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                if (!found)
                {
                    bounds = collider.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            Vector3 travel = body.linearVelocity * (Time.fixedDeltaTime * 2f);
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            bounds.Encapsulate(min + travel);
            bounds.Encapsulate(max + travel);
            bounds.Expand(RbxSpace.LengthToUnity((float)Instances.Terrain.TerrainBoxRasterizer.CellStuds) * 2f);
            return bounds;
        }

        /// <summary>
        /// Writes what water a character's body is in onto its <see cref="RbxCharacterWater"/>: whether the cell at its
        /// middle (half way from the feet to the top of the head) and the cell at the top of its head hold Water, and
        /// the height of the water's surface over its middle.
        /// </summary>
        private static void SenseWater(Rigidbody body, CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainWaterCells water)
        {
            if (!UnityRbxCharacterMotor.TryGetFeet(body, out float feetY)
                || !body.TryGetComponent(out CapsuleCollider shape))
            {
                return;
            }

            float topY = shape.bounds.max.y;
            float cell = (float)Instances.Terrain.TerrainBoxRasterizer.CellStuds;
            RbxVector3 feet = RbxSpace.FromUnity(new Vector3(body.position.x, feetY, body.position.z));
            RbxVector3 top = RbxSpace.FromUnity(new Vector3(body.position.x, topY, body.position.z));
            int x = Mathf.FloorToInt(feet.X / cell);
            int z = Mathf.FloorToInt(feet.Z / cell);
            float middle = (feet.Y + top.Y) * 0.5f;
            int middleY = Mathf.FloorToInt(middle / cell);
            int headY = Mathf.FloorToInt((top.Y - 0.25f) / cell);
            bool middleWet = water.IsWaterCell(x, middleY, z);
            bool headWet = water.IsWaterCell(x, headY, z);
            double surface = double.NegativeInfinity;
            if (middleWet || headWet)
            {
                int y = headWet ? headY : middleY;
                int limit = y + 64;
                while (y < limit && water.IsWaterCell(x, y + 1, z))
                {
                    y++;
                }

                surface = (y + 1) * cell;
            }

            if (!body.TryGetComponent(out RbxCharacterWater sense))
            {
                if (!middleWet && !headWet)
                {
                    return;
                }

                sense = body.gameObject.AddComponent<RbxCharacterWater>();
            }

            sense.Contact = new Instances.RbxWaterContact(middleWet, headWet, surface);
        }

        /// <summary>The body's enabled colliders stretched along one step of its velocity, with no margin.</summary>
        private Bounds NextStepBounds(Rigidbody body)
        {
            Bounds bounds = SweptBounds(body);
            float margin = RbxSpace.LengthToUnity((float)Instances.Terrain.TerrainBoxRasterizer.CellStuds) * 2f;
            bounds.Expand(-margin);
            return bounds;
        }

        /// <summary>
        /// Lifts a character whose feet are inside a solid Terrain cell to the top of that column's solid run (up
        /// to four cells), and stops it falling further into it.
        /// </summary>
        /// <remarks>
        /// WHY only characters and only up: a chunk collider is a surface, so a body that got inside a block (a
        /// block placed on the player, a collider built a step late) feels nothing there and stays stuck; a
        /// player expects to stand on the block, as in a block game. A plain part keeps the physics it has.
        /// </remarks>
        private static void LiftOutOfTerrain(Rigidbody body,
            CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainSolidCells cells)
        {
            if (!UnityRbxCharacterMotor.TryGetFeet(body, out float feetY))
            {
                return;
            }

            float cell = (float)Instances.Terrain.TerrainBoxRasterizer.CellStuds;
            RbxVector3 feet = RbxSpace.FromUnity(new Vector3(body.position.x, feetY, body.position.z));
            int x = Mathf.FloorToInt(feet.X / cell);
            int z = Mathf.FloorToInt(feet.Z / cell);
            // WHY a tenth of a cell above the feet: a body resting on a block touches its top face, and that
            // contact must not count as being inside it.
            int y = Mathf.FloorToInt((feet.Y + cell * 0.1f) / cell);
            if (!cells.IsSolidCell(x, y, z))
            {
                return;
            }

            int free = y + 1;
            while (free <= y + 4 && cells.IsSolidCell(x, free, z))
            {
                free++;
            }

            if (free > y + 4)
            {
                return;
            }

            float lift = RbxSpace.LengthToUnity(free * cell - feet.Y) + 0.01f;
            body.position += new Vector3(0f, lift, 0f);
            Vector3 velocity = body.linearVelocity;
            if (velocity.y < 0f)
            {
                velocity.y = 0f;
                body.linearVelocity = velocity;
            }
        }

        /// <inheritdoc />
        public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
            bool respectCanCollide, Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
        {
            hit = default;
            Vector3 origin = RbxSpace.ToUnity(originStuds);
            Vector3 direction = RbxSpace.ToUnity(directionStuds);
            float distance = direction.magnitude;
            if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
            {
                return false;
            }

            if (TerrainHits is CoreAI.Mods.Rbx.Binding.Terrain.IRbxTerrainColliderReadiness terrain)
            {
                // WHY a walk along the ray and a cap: the ray's box covered every queued chunk in a diagonal
                // slab, and a long mouse ray built tens of chunks in one call (the 43 ms frames).
                terrain.EnsureCollidersAlong(origin, direction / distance, distance, MaxColliderBuildsPerRay);
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
            //
            // WHY QueryTriggerInteraction.Collide, stated rather than left to the project setting: a
            // CanCollide=false part is a trigger on the Unity side, and a Roblox ray still hits it
            // unless RespectCanCollide asks otherwise (CanQuery defaults to true). Host trigger
            // volumes come back too and are dropped below, because they resolve to no part.
            int count = Physics.RaycastNonAlloc(
                origin, direction / distance, _hitScratch, distance,
                ~0, QueryTriggerInteraction.Collide);
            while (count == _hitScratch.Length && _hitScratch.Length < MaxHitScratchLength)
            {
                _hitScratch = new RaycastHit[Math.Min(_hitScratch.Length * 2, MaxHitScratchLength)];
                count = Physics.RaycastNonAlloc(
                    origin, direction / distance, _hitScratch, distance,
                    ~0, QueryTriggerInteraction.Collide);
            }

            bool found = false;
            float bestDistance = float.MaxValue;
            for (int index = 0; index < count; index++)
            {
                RaycastHit candidate = _hitScratch[index];
                if (candidate.distance >= bestDistance || candidate.collider == null)
                {
                    continue;
                }

                bool isPart = _binder.TryResolvePartInstanceId(candidate.collider.gameObject, out InstanceId id);
                RbxMaterialId terrainMaterial = default;
                RbxVector3 terrainNormal = default;
                if (!isPart && (TerrainHits == null || !TerrainHits.TryResolveHit(candidate.collider,
                        candidate.point, candidate.normal, out id, out terrainMaterial, out terrainNormal)))
                {
                    continue;
                }

                if (respectCanCollide && !CollidesWithQueries(candidate.collider, isPart ? id : InstanceId.None))
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
                    isPart ? RbxSpace.DirectionFromUnity(candidate.normal) : terrainNormal,
                    isPart ? MaterialOf(id) : terrainMaterial,
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

        /// <summary>
        /// RespectCanCollide asks the query to use CanCollide instead of CanQuery. The binder keeps a
        /// CanCollide=false part's collider as a trigger, so the collider's own flag is the answer.
        /// </summary>
        /// <remarks>
        /// WHY a part the character rig holds answers from its property: the binder keeps a held part a trigger
        /// whatever its CanCollide, so the trigger flag would make a held R6 Torso (CanCollide true) invisible
        /// to a RespectCanCollide ray.
        /// </remarks>
        private bool CollidesWithQueries(Collider collider, InstanceId part)
        {
            if (part.IsValid && _binder.IsPoseDriven(part))
            {
                return _binder.TryGetPartProperties(part, out PartProperties properties) && properties.CanCollide;
            }

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
