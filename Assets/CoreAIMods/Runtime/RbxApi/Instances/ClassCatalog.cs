using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances
{
    public enum RbxKnownUnimplementedMemberStatus
    {
        Planned,
        Backlog,
        Unsupported
    }

    [Flags]
    public enum RbxKnownUnimplementedMemberAccess
    {
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write
    }

    /// <summary>Catalog metadata for a real Rbx member whose runtime binding is not implemented.</summary>
    public sealed class RbxKnownUnimplementedMemberDescriptor
    {
        public string Name { get; }

        public RbxKnownUnimplementedMemberStatus Status { get; }

        public RbxKnownUnimplementedMemberAccess Access { get; }

        public string Phase { get; }

        public string Workaround { get; }

        public bool IsMethod { get; }

        private RbxKnownUnimplementedMemberDescriptor(string name,
            RbxKnownUnimplementedMemberStatus status, RbxKnownUnimplementedMemberAccess access,
            string phase, string workaround, bool isMethod)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (status == RbxKnownUnimplementedMemberStatus.Planned
                && string.IsNullOrWhiteSpace(phase))
            {
                throw new ArgumentException("Planned members require a roadmap phase", nameof(phase));
            }

            if (status != RbxKnownUnimplementedMemberStatus.Planned && phase != null)
            {
                throw new ArgumentException(
                    "Only planned members may carry a roadmap phase", nameof(phase));
            }

            Status = status;
            Access = access;
            Phase = phase;
            Workaround = workaround ?? throw new ArgumentNullException(nameof(workaround));
            IsMethod = isMethod;
        }

        public static RbxKnownUnimplementedMemberDescriptor PlannedProperty(
            string name, string phase, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Planned,
                RbxKnownUnimplementedMemberAccess.ReadWrite, phase, workaround, false);
        }

        public static RbxKnownUnimplementedMemberDescriptor PlannedMethod(
            string name, string phase, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Planned,
                RbxKnownUnimplementedMemberAccess.Read, phase, workaround, true);
        }

        public static RbxKnownUnimplementedMemberDescriptor BacklogProperty(
            string name, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Backlog,
                RbxKnownUnimplementedMemberAccess.ReadWrite, null, workaround, false);
        }

        public static RbxKnownUnimplementedMemberDescriptor BacklogMethod(
            string name, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Backlog,
                RbxKnownUnimplementedMemberAccess.Read, null, workaround, true);
        }

        public static RbxKnownUnimplementedMemberDescriptor UnsupportedProperty(
            string name, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Unsupported,
                RbxKnownUnimplementedMemberAccess.ReadWrite, null, workaround, false);
        }

        public static RbxKnownUnimplementedMemberDescriptor UnsupportedWriteProperty(
            string name, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Unsupported,
                RbxKnownUnimplementedMemberAccess.Write, null, workaround, false);
        }

        public static RbxKnownUnimplementedMemberDescriptor UnsupportedMethod(
            string name, string workaround)
        {
            return new RbxKnownUnimplementedMemberDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Unsupported,
                RbxKnownUnimplementedMemberAccess.Read, null, workaround, true);
        }
    }

    /// <summary>
    /// One class descriptor row: ancestry is data, not C# inheritance depth (roadmap §5.1.7 risk
    /// table) — adding a class is one row plus an optional behavior class via <see cref="Factory"/>.
    /// </summary>
    public sealed class ClassDescriptor
    {
        /// <summary>Roblox ClassName.</summary>
        public string Name { get; }

        /// <summary>Parent ClassName in the IsA hierarchy; null only for "Instance".</summary>
        public string BaseClassName { get; }

        /// <summary>Abstract classes exist only as IsA ancestors; they are never instantiated.</summary>
        public bool IsAbstract { get; }

        /// <summary>Creatable via the script-facing Instance.new path.</summary>
        public bool IsCreatable { get; }

        /// <summary>Resolvable through ServiceProvider.GetService.</summary>
        public bool IsService { get; }

        /// <summary>Optional behavior-class constructor; null uses the plain RbxInstance shape.</summary>
        public Func<ClassDescriptor, RbxInstance> Factory { get; }

        public ClassDescriptor(string name, string baseClassName, bool isAbstract,
            bool isCreatable, bool isService, Func<ClassDescriptor, RbxInstance> factory = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BaseClassName = baseClassName;
            IsAbstract = isAbstract;
            IsCreatable = isCreatable;
            IsService = isService;
            Factory = factory;
        }
    }

    /// <summary>
    /// Data-driven class registry powering IsA and instance creation for the MVP1 class set.
    /// The same catalog later feeds the API manifest generator (§MVP6).
    /// </summary>
    public sealed class ClassCatalog
    {
        private readonly struct FlattenedKnownUnimplementedMember
        {
            public FlattenedKnownUnimplementedMember(string declaringClassName,
                RbxKnownUnimplementedMemberDescriptor member)
            {
                DeclaringClassName = declaringClassName;
                Member = member;
            }

            public string DeclaringClassName { get; }

            public RbxKnownUnimplementedMemberDescriptor Member { get; }
        }

        private readonly Dictionary<string, ClassDescriptor> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, RbxKnownUnimplementedMemberDescriptor>>
            _knownUnimplementedMembers = new(StringComparer.Ordinal);
        private readonly Dictionary<
            (string ClassName, string MemberName, RbxKnownUnimplementedMemberAccess Access),
            FlattenedKnownUnimplementedMember> _flattenedKnownUnimplementedMembers = new();
        private bool _flattenedKnownUnimplementedMembersDirty = true;

        public void Register(ClassDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (_byName.ContainsKey(descriptor.Name))
            {
                throw new InvalidOperationException("Class already registered: " + descriptor.Name);
            }

            if (descriptor.BaseClassName != null && !_byName.ContainsKey(descriptor.BaseClassName))
            {
                throw new InvalidOperationException(
                    "Base class must be registered first: " + descriptor.BaseClassName);
            }

            _byName.Add(descriptor.Name, descriptor);
            _flattenedKnownUnimplementedMembersDirty = true;
        }

        public bool TryGet(string className, out ClassDescriptor descriptor)
        {
            return _byName.TryGetValue(className, out descriptor);
        }

        public IEnumerable<ClassDescriptor> All => _byName.Values;

        public void RegisterKnownUnimplementedMembers(string className,
            params RbxKnownUnimplementedMemberDescriptor[] members)
        {
            if (!_byName.ContainsKey(className))
            {
                throw new InvalidOperationException("Class must be registered first: " + className);
            }

            if (!_knownUnimplementedMembers.TryGetValue(className,
                    out Dictionary<string, RbxKnownUnimplementedMemberDescriptor> classMembers))
            {
                classMembers = new Dictionary<string, RbxKnownUnimplementedMemberDescriptor>(
                    StringComparer.Ordinal);
                _knownUnimplementedMembers.Add(className, classMembers);
            }

            foreach (RbxKnownUnimplementedMemberDescriptor member in members)
            {
                if (member == null)
                {
                    throw new ArgumentNullException(nameof(members));
                }

                if (!classMembers.TryAdd(member.Name, member))
                {
                    throw new InvalidOperationException(
                        "Known unimplemented member already registered: " + className + "." + member.Name);
                }
            }

            _flattenedKnownUnimplementedMembersDirty = true;
        }

        /// <summary>Resolves pre-flattened inherited metadata with one dictionary lookup.</summary>
        public bool TryGetKnownUnimplementedMember(string className, string memberName,
            RbxKnownUnimplementedMemberAccess access, out string declaringClassName,
            out RbxKnownUnimplementedMemberDescriptor member)
        {
            EnsureKnownUnimplementedMembersFlattened();
            if (_flattenedKnownUnimplementedMembers.TryGetValue(
                    (className, memberName, access),
                    out FlattenedKnownUnimplementedMember flattenedMember))
            {
                declaringClassName = flattenedMember.DeclaringClassName;
                member = flattenedMember.Member;
                return true;
            }

            declaringClassName = null;
            member = null;
            return false;
        }

        private void EnsureKnownUnimplementedMembersFlattened()
        {
            if (!_flattenedKnownUnimplementedMembersDirty)
            {
                return;
            }

            _flattenedKnownUnimplementedMembers.Clear();
            foreach (ClassDescriptor classDescriptor in _byName.Values)
            {
                string current = classDescriptor.Name;
                while (current != null)
                {
                    if (_knownUnimplementedMembers.TryGetValue(current,
                            out Dictionary<string, RbxKnownUnimplementedMemberDescriptor> classMembers))
                    {
                        foreach (KeyValuePair<string, RbxKnownUnimplementedMemberDescriptor> pair
                                 in classMembers)
                        {
                            FlattenedKnownUnimplementedMember flattenedMember =
                                new(current, pair.Value);
                            if ((pair.Value.Access & RbxKnownUnimplementedMemberAccess.Read) != 0)
                            {
                                _flattenedKnownUnimplementedMembers.TryAdd(
                                    (classDescriptor.Name, pair.Key,
                                        RbxKnownUnimplementedMemberAccess.Read),
                                    flattenedMember);
                            }

                            if ((pair.Value.Access & RbxKnownUnimplementedMemberAccess.Write) != 0)
                            {
                                _flattenedKnownUnimplementedMembers.TryAdd(
                                    (classDescriptor.Name, pair.Key,
                                        RbxKnownUnimplementedMemberAccess.Write),
                                    flattenedMember);
                            }
                        }
                    }

                    current = _byName.TryGetValue(current, out ClassDescriptor descriptor)
                        ? descriptor.BaseClassName
                        : null;
                }
            }

            _flattenedKnownUnimplementedMembersDirty = false;
        }

        /// <summary>Walks the ancestry chain: true when <paramref name="className"/> is
        /// <paramref name="ancestorClassName"/> or inherits from it.</summary>
        public bool IsA(string className, string ancestorClassName)
        {
            string current = className;
            while (current != null)
            {
                if (string.Equals(current, ancestorClassName, StringComparison.Ordinal))
                {
                    return true;
                }

                current = _byName.TryGetValue(current, out ClassDescriptor descriptor)
                    ? descriptor.BaseClassName
                    : null;
            }

            return false;
        }

        /// <summary>
        /// The MVP1 class set (roadmap §5.1.3): Instance, Folder, Model, Part (geometry-free
        /// placeholder — spatial properties arrive with the property/datatype slice), Workspace,
        /// DataModel, the container services so paths resolve, and Lighting as a structural
        /// service node (its ClockTime/Ambient properties stay absent — the loud stub answers).
        /// </summary>
        public static ClassCatalog CreateMvp1()
        {
            ClassCatalog catalog = new();
            catalog.Register(new ClassDescriptor("Instance", null, true, false, false));
            catalog.Register(new ClassDescriptor("BaseRemoteEvent", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("RemoteEvent", "BaseRemoteEvent", false, true, false,
                descriptor => new RbxRemoteEvent(descriptor)));
            catalog.Register(new ClassDescriptor("UnreliableRemoteEvent", "BaseRemoteEvent",
                false, true, false, descriptor => new RbxUnreliableRemoteEvent(descriptor)));
            catalog.Register(new ClassDescriptor("RemoteFunction", "Instance", false, true, false,
                descriptor => new RbxRemoteFunction(descriptor)));
            catalog.Register(new ClassDescriptor("Player", "Instance", false, false, false,
                descriptor => new RbxPlayer(descriptor)));
            catalog.Register(new ClassDescriptor("PVInstance", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("Folder", "Instance", false, true, false));
            catalog.Register(new ClassDescriptor("Model", "PVInstance", false, true, false));
            catalog.Register(new ClassDescriptor("WorldRoot", "Model", true, false, false));
            catalog.Register(new ClassDescriptor("Workspace", "WorldRoot", false, false, true));
            catalog.Register(new ClassDescriptor("BasePart", "PVInstance", true, false, false));
            catalog.Register(new ClassDescriptor("Part", "BasePart", false, true, false));
            // WHY: one canonical Camera per world (bootstrap creates it under Workspace and the
            // Lua layer routes its CFrame to the camera rig), so scripted creation stays off.
            // TODO: MVP-later — creatable Cameras with per-instance state once multiple
            // viewports/cameras are meaningful.
            catalog.Register(new ClassDescriptor("Camera", "Instance", false, false, false));
            catalog.Register(new ClassDescriptor("ServiceProvider", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("DataModel", "ServiceProvider", false, false, false,
                descriptor => new RbxDataModel(descriptor)));
            // TODO: MVP-later — Lighting sun/ambient property mapping (ClockTime, Ambient,
            // GeographicLatitude ...) lands with the lighting slice; today it is structure only.
            catalog.Register(new ClassDescriptor("Lighting", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ReplicatedStorage", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ServerStorage", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ServerScriptService", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("StarterPlayer", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("Players", "Instance", false, false, true,
                descriptor => new RbxPlayers(descriptor)));
            // WHY: pulled forward from MVP10 for MVP1 mini-game controls (TODO.md pending note);
            // behavior class carries the input signals + poll surface over the IInputSource seam.
            catalog.Register(new ClassDescriptor("UserInputService", "Instance", false, false, true,
                descriptor => new RbxUserInputService(descriptor)));
            // WHY: RunService pulled forward for the per-frame game loop (Heartbeat/Stepped/
            // RenderStepped); behavior class fires the signals from the host's per-frame Step pump.
            catalog.Register(new ClassDescriptor("RunService", "Instance", false, false, true,
                descriptor => new RbxRunService(descriptor)));
            // WHY: ClickDetector is a normal creatable Instance (superclass Instance, NOT a service) —
            // a mod does Instance.new("ClickDetector") and parents it under a Part; the behavior class
            // carries the MouseClick signal the host pick pump fires when that part is clicked.
            catalog.Register(new ClassDescriptor("ClickDetector", "Instance", false, true, false,
                descriptor => new RbxClickDetector(descriptor)));

            catalog.RegisterKnownUnimplementedMembers("PVInstance",
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "PivotTo", "MVP2 (Model pivot)",
                    "set part.CFrame directly until pivot aggregation lands"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "GetPivot", "MVP2 (Model pivot)",
                    "read part.CFrame directly until pivot aggregation lands"));
            catalog.RegisterKnownUnimplementedMembers("Model",
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty(
                    "PrimaryPart", "MVP2 (Model pivot)",
                    "use a child BasePart.CFrame directly until Model pivot support lands"),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty(
                    "WorldPivot", "MVP2 (Model pivot)",
                    "use a child BasePart.CFrame directly until Model pivot support lands"));
            catalog.RegisterKnownUnimplementedMembers("WorldRoot",
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "Raycast", "MVP8",
                    "use CoreAI world-query tools outside Lua until workspace:Raycast lands"));
            catalog.RegisterKnownUnimplementedMembers("Workspace",
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty(
                    "Gravity", "MVP8",
                    "keep parts Anchored or use host physics settings until per-body gravity lands"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "GetServerTimeNow", "MVP2",
                    "use os.time() for wall-clock timestamps until the synchronized clock lands"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedWriteProperty(
                    "SignalBehavior",
                    "signal mode is Deferred-only; use task.defer when explicit ordering is needed"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Terrain",
                    "build terrain from Parts; voxel Terrain is a roadmap non-goal"));

            string physicsWorkaround =
                "keep the part Anchored and animate CFrame, or use host physics until this bridge lands";
            string collisionWorkaround =
                "use CanCollide and host-side layers until the extended collision controls land";
            string surfaceWorkaround =
                "use Material, Color, and geometry; legacy surface joints are not currently scheduled";
            catalog.RegisterKnownUnimplementedMembers("BasePart",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Velocity", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AssemblyLinearVelocity", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AssemblyAngularVelocity", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Massless",
                    "keep parts Anchored or configure mass through host physics until this bridge lands"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CanQuery", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CanTouch", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CollisionGroup", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CustomPhysicalProperties",
                    "use host-side Rigidbody and collider settings until physical properties land"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "BackSurface", surfaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "BottomSurface", surfaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "FrontSurface", surfaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "LeftSurface", surfaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "RightSurface", surfaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "TopSurface", surfaceWorkaround));
            catalog.RegisterKnownUnimplementedMembers("Lighting",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ClockTime",
                    "configure the host scene lighting outside Lua until the lighting slice is scheduled"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Ambient",
                    "configure the host scene lighting outside Lua until the lighting slice is scheduled"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "GeographicLatitude",
                    "configure the host scene lighting outside Lua until the lighting slice is scheduled"));
            catalog.RegisterKnownUnimplementedMembers("RunService",
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "IsServer", "MVP2",
                    "target Solo behavior until runtime topology queries land"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "IsClient", "MVP2",
                    "target Solo behavior until runtime topology queries land"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "IsRunning", "MVP2",
                    "use the existing Heartbeat signal as the running-state boundary"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "IsStudio", "MVP2",
                    "do not branch on Studio; CoreAI mods must run in built players"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "BindToRenderStep", "MVP2",
                    "connect RunService.RenderStepped until named render-step binding lands"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "UnbindFromRenderStep", "MVP2",
                    "disconnect the RunService.RenderStepped connection explicitly"));
            catalog.EnsureKnownUnimplementedMembersFlattened();
            return catalog;
        }
    }
}
