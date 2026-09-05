using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Mirror <c>Enum.RaycastFilterType</c>, valued 1:1 (Exclude 0, Include 1).
    /// </summary>
    /// <remarks>
    /// WHY only two items: the build plan expected deprecated <c>Blacklist</c>/<c>Whitelist</c>
    /// aliases too, but <c>RaycastFilterType.yaml</c> lists exactly these — the pre-2022 names were
    /// retired, not kept as aliases. Shipping a spelling the mirror no longer documents would teach
    /// scripts a name a Roblox round-trip cannot carry back.
    /// </remarks>
    public enum RbxRaycastFilterType
    {
        /// <summary>Every part is considered except descendants of the filter list.</summary>
        Exclude = 0,

        /// <summary>Only descendants of the filter list are considered.</summary>
        Include = 1
    }

    /// <summary>
    /// Mirror <c>RaycastParams</c>: the eligibility rules one raycast runs under.
    /// </summary>
    /// <remarks>
    /// Mutable by design — <c>RaycastParams.new()</c> takes no arguments and the caller assigns
    /// properties afterwards, so an immutable value would not match the documented idiom.
    /// <para>
    /// Two members are accepted and inert, and say so rather than pretending otherwise:
    /// <c>IgnoreWater</c> only means something against <c>Terrain</c>, which CoreAI deliberately does
    /// not have, and <c>BruteForceAllSlow</c> picks a broadphase strategy that has no equivalent
    /// here. Neither can change which part a ray hits, so refusing them would break otherwise
    /// portable scripts over a setting with no consequence. <c>CollisionGroup</c> is the opposite
    /// case: a real group WOULD change which parts are eligible, so accepting one and ignoring it
    /// would return a confidently wrong hit — it raises instead.
    /// </para>
    /// This type lives beside the instances rather than in the datatypes assembly because its filter
    /// holds instances, and the datatypes assembly deliberately references nothing.
    /// </remarks>
    public sealed class RbxRaycastParams
    {
        /// <summary>The only collision group CoreAI models; the mirror's default name.</summary>
        public const string DefaultCollisionGroup = "Default";

        private readonly List<RbxInstance> _filterDescendantsInstances = new();
        private string _collisionGroup = DefaultCollisionGroup;

        /// <summary>Mirror default: an empty filter list, i.e. every part is eligible.</summary>
        public IReadOnlyList<RbxInstance> FilterDescendantsInstances => _filterDescendantsInstances;

        /// <summary>Mirror default: <see cref="RbxRaycastFilterType.Exclude"/>.</summary>
        public RbxRaycastFilterType FilterType { get; set; } = RbxRaycastFilterType.Exclude;

        /// <summary>Mirror default false. Accepted and inert: CoreAI has no Terrain water.</summary>
        public bool IgnoreWater { get; set; }

        /// <summary>Mirror default false. Accepted and inert: CoreAI has one broadphase.</summary>
        public bool BruteForceAllSlow { get; set; }

        /// <summary>
        /// Mirror default false: the query respects <c>CanQuery</c>. True makes it respect
        /// <c>CanCollide</c> instead.
        /// </summary>
        public bool RespectCanCollide { get; set; }

        /// <summary>The collision group this query runs in. Only <c>Default</c> exists in CoreAI.</summary>
        public string CollisionGroup
        {
            get => _collisionGroup;
            set
            {
                string requested = string.IsNullOrEmpty(value) ? DefaultCollisionGroup : value;
                if (!string.Equals(requested, DefaultCollisionGroup, StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "RaycastParams.CollisionGroup '" + requested + "' does not exist; CoreAI models "
                        + "one collision group (Default)",
                        "leave CollisionGroup unset and filter with FilterDescendantsInstances");
                }

                _collisionGroup = requested;
            }
        }

        /// <summary>Replaces the filter list wholesale, as assigning the property does.</summary>
        public void SetFilterDescendantsInstances(IEnumerable<RbxInstance> instances)
        {
            _filterDescendantsInstances.Clear();
            AddToFilter(instances);
        }

        /// <summary>Mirror <c>RaycastParams:AddToFilter</c>: appends without replacing the list.</summary>
        public void AddToFilter(IEnumerable<RbxInstance> instances)
        {
            if (instances == null)
            {
                return;
            }

            foreach (RbxInstance instance in instances)
            {
                if (instance != null && !_filterDescendantsInstances.Contains(instance))
                {
                    _filterDescendantsInstances.Add(instance);
                }
            }
        }

        /// <summary>True when <paramref name="candidate"/> passes this filter.</summary>
        /// <remarks>
        /// WHY descendants and not membership: the mirror names the property
        /// FilterDescendants<i>Instances</i> — filtering a Model must filter every part inside it,
        /// which is the whole reason scripts pass the character rather than listing its limbs.
        /// </remarks>
        public bool Accepts(RbxInstance candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            bool listed = false;
            for (int index = 0; index < _filterDescendantsInstances.Count; index++)
            {
                RbxInstance filter = _filterDescendantsInstances[index];
                if (ReferenceEquals(filter, candidate) || candidate.IsDescendantOf(filter))
                {
                    listed = true;
                    break;
                }
            }

            return FilterType == RbxRaycastFilterType.Include ? listed : !listed;
        }
    }

    /// <summary>
    /// Mirror <c>RaycastResult</c>: what a ray hit. Only ever produced by a successful cast — a miss
    /// is <c>nil</c> in Lua, never a result with an empty instance.
    /// </summary>
    public sealed class RbxRaycastResult
    {
        /// <summary>Creates a hit result. Every field is measured in Roblox units.</summary>
        public RbxRaycastResult(RbxInstance instance, RbxVector3 position, RbxVector3 normal,
            RbxMaterialId material, double distance)
        {
            Instance = instance ?? throw new ArgumentNullException(nameof(instance),
                "a RaycastResult without an instance is a miss, and a miss is nil");
            Position = position;
            Normal = normal;
            Material = material;
            Distance = distance;
        }

        /// <summary>The BasePart the ray intersected.</summary>
        public RbxInstance Instance { get; }

        /// <summary>The world-space intersection point, in studs.</summary>
        public RbxVector3 Position { get; }

        /// <summary>The normal of the intersected face.</summary>
        public RbxVector3 Normal { get; }

        /// <summary>The intersected part's <c>Enum.Material</c>.</summary>
        public RbxMaterialId Material { get; }

        /// <summary>Distance from the ray origin to the intersection, in studs.</summary>
        public double Distance { get; }
    }

    /// <summary>One hit as the physics port reports it, before instance resolution.</summary>
    /// <remarks>
    /// WHY an id and not an instance: the port lives on the engine side and must not hold or resolve
    /// tree references. The engine-free caller turns the id back into an instance, which is also the
    /// point where a hit on something outside the world tree is dropped instead of surfacing.
    /// </remarks>
    public readonly struct RbxPhysicsRaycastHit
    {
        /// <summary>Creates a raw hit in Roblox units.</summary>
        public RbxPhysicsRaycastHit(InstanceId instance, RbxVector3 position, RbxVector3 normal,
            RbxMaterialId material, double distance)
        {
            Instance = instance;
            Position = position;
            Normal = normal;
            Material = material;
            Distance = distance;
        }

        /// <summary>The part that was hit.</summary>
        public InstanceId Instance { get; }

        /// <summary>Intersection point in studs.</summary>
        public RbxVector3 Position { get; }

        /// <summary>Face normal.</summary>
        public RbxVector3 Normal { get; }

        /// <summary>
        /// The hit part's <c>Enum.Material</c>, read by the adapter that owns the part state.
        /// </summary>
        /// <remarks>
        /// WHY the port reports it instead of the caller looking it up: part appearance lives in the
        /// binding assembly's property sink, which the engine-free side deliberately cannot see. A
        /// second lookup path here would be a second source of truth for what a part is made of.
        /// </remarks>
        public RbxMaterialId Material { get; }

        /// <summary>Distance from the origin in studs.</summary>
        public double Distance { get; }
    }

    /// <summary>
    /// The engine seam for world queries, per-body gravity, and contact events.
    /// </summary>
    /// <remarks>
    /// WHY a port at all: <c>CoreAI.RbxApi.Instances</c> is engine-free by fitness test, so the
    /// classes that answer <c>workspace:Raycast</c>, <c>Workspace.Gravity</c> and
    /// <c>BasePart.Touched</c> cannot reach the engine's own physics API. The adapter in the binding
    /// assembly implements this; headless hosts and EditMode tests get
    /// <see cref="NullRbxPhysicsPort"/> or a fake, which is what makes the eligibility rules
    /// testable without a scene.
    /// <para>
    /// Contacts are reported as raw id pairs. Deciding which signal fires, and on which of the two
    /// parts, stays on the engine-free side so the mirror rule "Touched fires on BOTH parts" is
    /// written once and tested without a physics engine.
    /// </para>
    /// </remarks>
    public interface IRbxPhysicsPort
    {
        /// <summary>
        /// Casts a ray in Roblox space. <paramref name="isEligible"/> is the already-resolved filter;
        /// the port must not re-derive eligibility from its own scene state.
        /// </summary>
        bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
            Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit);

        /// <summary>
        /// Applies world gravity, in studs/s², as a per-body force. Implementations must never write
        /// the host scene's global gravity (DEV-6).
        /// </summary>
        void SetGravity(double studsPerSecondSquared);

        /// <summary>Two parts began touching through physical movement.</summary>
        event Action<InstanceId, InstanceId> ContactBegan;

        /// <summary>Two parts stopped touching.</summary>
        event Action<InstanceId, InstanceId> ContactEnded;
    }

    /// <summary>
    /// Null object for hosts with no physics engine: every ray misses and nothing ever touches.
    /// </summary>
    /// <remarks>
    /// WHY a miss rather than a throw: a headless world (tests, a storage-only tree, the world-package
    /// tools) runs the same mod code as a live one, and a mod that casts a ray to look around should
    /// find nothing there, not crash. Gravity is accepted and dropped for the same reason — there are
    /// no bodies to accelerate.
    /// </remarks>
    public sealed class NullRbxPhysicsPort : IRbxPhysicsPort
    {
        /// <summary>Shared instance; the type holds no state.</summary>
        public static readonly NullRbxPhysicsPort Instance = new();

        /// <inheritdoc />
        public event Action<InstanceId, InstanceId> ContactBegan
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public event Action<InstanceId, InstanceId> ContactEnded
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
            bool respectCanCollide, Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
        {
            hit = default;
            return false;
        }

        /// <inheritdoc />
        public void SetGravity(double studsPerSecondSquared)
        {
        }
    }
}
