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

        /// <summary>
        /// The loud NOT_IMPLEMENTED error for this member as declared on
        /// <paramref name="declaringClassName"/>, in the same wording the Lua member dispatch uses.
        /// </summary>
        public RbxError CreateError(string declaringClassName)
        {
            string separator = IsMethod ? ":" : ".";
            return RbxKnownUnimplementedErrors.ForMember(
                declaringClassName + separator + Name, Status, Phase, Workaround);
        }

        internal static void ValidateStatusPhase(RbxKnownUnimplementedMemberStatus status,
            string phase)
        {
            if (status == RbxKnownUnimplementedMemberStatus.Planned
                && string.IsNullOrWhiteSpace(phase))
            {
                throw new ArgumentException("Planned entries require a roadmap phase", nameof(phase));
            }

            if (status != RbxKnownUnimplementedMemberStatus.Planned && phase != null)
            {
                throw new ArgumentException(
                    "Only planned entries may carry a roadmap phase", nameof(phase));
            }
        }
    }

    /// <summary>
    /// The loud-stub wording for known-but-unimplemented Rbx surface, one template per status, so a
    /// backlog or unsupported entry never reads "is planned for no planned MVP" (M1-25).
    /// </summary>
    internal static class RbxKnownUnimplementedErrors
    {
        /// <summary>A member or service member: <c>Class.Member</c> / <c>Class:Method</c>.</summary>
        internal static RbxError ForMember(string feature,
            RbxKnownUnimplementedMemberStatus status, string phase, string workaround)
        {
            switch (status)
            {
                case RbxKnownUnimplementedMemberStatus.Planned:
                    return RbxError.NotImplemented(feature, phase, workaround);
                case RbxKnownUnimplementedMemberStatus.Backlog:
                    return new RbxError(RbxErrorCode.NotImplemented,
                        feature + " is a known Rbx member, but no roadmap rung is assigned.",
                        workaround);
                case RbxKnownUnimplementedMemberStatus.Unsupported:
                    return new RbxError(RbxErrorCode.NotImplemented,
                        feature + " is a known Rbx member deliberately unsupported by CoreAI.",
                        workaround);
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, null);
            }
        }

        /// <summary>A class that <c>Instance.new</c> was asked to create.</summary>
        internal static RbxError ForClass(string className,
            RbxKnownUnimplementedMemberStatus status, string phase, string workaround)
        {
            string call = "Instance.new(\"" + className + "\")";
            switch (status)
            {
                case RbxKnownUnimplementedMemberStatus.Planned:
                    return RbxError.NotImplemented(call, phase, workaround);
                case RbxKnownUnimplementedMemberStatus.Backlog:
                    return new RbxError(RbxErrorCode.NotImplemented,
                        call + ": " + className
                        + " is a known Rbx class, but no roadmap rung is assigned.",
                        workaround);
                case RbxKnownUnimplementedMemberStatus.Unsupported:
                    return new RbxError(RbxErrorCode.NotImplemented,
                        call + ": " + className
                        + " is a known Rbx class deliberately unsupported by CoreAI.",
                        workaround);
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, null);
            }
        }
    }

    /// <summary>
    /// Catalog metadata for a real, script-creatable Rbx class that CoreAI does not implement yet:
    /// <c>Instance.new</c> answers it with a loud NOT_IMPLEMENTED stub instead of "unable to create".
    /// </summary>
    public sealed class RbxKnownUnimplementedClassDescriptor
    {
        private RbxKnownUnimplementedClassDescriptor(string name,
            RbxKnownUnimplementedMemberStatus status, string phase, string workaround)
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("A class name is required", nameof(name))
                : name;
            RbxKnownUnimplementedMemberDescriptor.ValidateStatusPhase(status, phase);
            Status = status;
            Phase = phase;
            Workaround = workaround ?? throw new ArgumentNullException(nameof(workaround));
        }

        public string Name { get; }

        public RbxKnownUnimplementedMemberStatus Status { get; }

        /// <summary>Roadmap rung for a planned class; null otherwise.</summary>
        public string Phase { get; }

        public string Workaround { get; }

        public static RbxKnownUnimplementedClassDescriptor Planned(string name, string phase,
            string workaround)
        {
            return new RbxKnownUnimplementedClassDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Planned, phase, workaround);
        }

        public static RbxKnownUnimplementedClassDescriptor Backlog(string name, string workaround)
        {
            return new RbxKnownUnimplementedClassDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Backlog, null, workaround);
        }

        public static RbxKnownUnimplementedClassDescriptor Unsupported(string name,
            string workaround)
        {
            return new RbxKnownUnimplementedClassDescriptor(
                name, RbxKnownUnimplementedMemberStatus.Unsupported, null, workaround);
        }

        /// <summary>The loud NOT_IMPLEMENTED error <c>Instance.new</c> raises for this class.</summary>
        public RbxError CreateInstanceNewError()
        {
            return RbxKnownUnimplementedErrors.ForClass(Name, Status, Phase, Workaround);
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

        /// <summary>Parent ClassName in the IsA hierarchy; null only for the root, "Object".</summary>
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
        private readonly Dictionary<string, RbxKnownUnimplementedClassDescriptor>
            _knownUnimplementedClasses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, RbxKnownUnimplementedMemberDescriptor>>
            _knownUnimplementedMembers = new(StringComparer.Ordinal);
        private readonly Dictionary<
            (string ClassName, string MemberName, RbxKnownUnimplementedMemberAccess Access),
            FlattenedKnownUnimplementedMember> _flattenedKnownUnimplementedMembers = new();
        private readonly HashSet<string> _flattenedClasses = new(StringComparer.Ordinal);
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
            // WHY: the day a class ships it is registered for real, and a stub left behind for the
            // same name would be dead data that still describes it as unimplemented.
            _knownUnimplementedClasses.Remove(descriptor.Name);
            _flattenedKnownUnimplementedMembersDirty = true;
        }

        public bool TryGet(string className, out ClassDescriptor descriptor)
        {
            return _byName.TryGetValue(className, out descriptor);
        }

        public IEnumerable<ClassDescriptor> All => _byName.Values;

        /// <summary>Every known Rbx class <c>Instance.new</c> answers with a loud stub.</summary>
        public IEnumerable<RbxKnownUnimplementedClassDescriptor> KnownUnimplementedClasses =>
            _knownUnimplementedClasses.Values;

        /// <summary>
        /// Records real, script-creatable Rbx classes that are not implemented, so
        /// <c>Instance.new</c> raises their loud stub instead of claiming they do not exist.
        /// </summary>
        public void RegisterKnownUnimplementedClasses(
            params RbxKnownUnimplementedClassDescriptor[] classes)
        {
            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            foreach (RbxKnownUnimplementedClassDescriptor knownClass in classes)
            {
                if (knownClass == null)
                {
                    throw new ArgumentNullException(nameof(classes));
                }

                if (_byName.ContainsKey(knownClass.Name))
                {
                    throw new InvalidOperationException(
                        "A registered class cannot also be a known unimplemented class: "
                        + knownClass.Name);
                }

                if (!_knownUnimplementedClasses.TryAdd(knownClass.Name, knownClass))
                {
                    throw new InvalidOperationException(
                        "Known unimplemented class already registered: " + knownClass.Name);
                }
            }
        }

        public bool TryGetKnownUnimplementedClass(string className,
            out RbxKnownUnimplementedClassDescriptor descriptor)
        {
            if (className == null)
            {
                descriptor = null;
                return false;
            }

            return _knownUnimplementedClasses.TryGetValue(className, out descriptor);
        }

        /// <summary>
        /// The known-unimplemented members declared directly on <paramref name="className"/>
        /// (inherited ones excluded); empty when it declares none.
        /// </summary>
        public IReadOnlyCollection<RbxKnownUnimplementedMemberDescriptor>
            GetDeclaredKnownUnimplementedMembers(string className)
        {
            if (className != null
                && _knownUnimplementedMembers.TryGetValue(className,
                    out Dictionary<string, RbxKnownUnimplementedMemberDescriptor> classMembers))
            {
                return classMembers.Values;
            }

            return Array.Empty<RbxKnownUnimplementedMemberDescriptor>();
        }

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

        /// <summary>
        /// Resolves inherited metadata with one dictionary lookup; a class's inherited entries are
        /// flattened the first time that class is asked about.
        /// </summary>
        public bool TryGetKnownUnimplementedMember(string className, string memberName,
            RbxKnownUnimplementedMemberAccess access, out string declaringClassName,
            out RbxKnownUnimplementedMemberDescriptor member)
        {
            EnsureKnownUnimplementedMembersFlattened();
            EnsureClassFlattened(className);
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

        /// <summary>Drops every flattened entry once a registration has changed what they derive from.</summary>
        private void EnsureKnownUnimplementedMembersFlattened()
        {
            if (!_flattenedKnownUnimplementedMembersDirty)
            {
                return;
            }

            _flattenedKnownUnimplementedMembers.Clear();
            _flattenedClasses.Clear();
            _flattenedKnownUnimplementedMembersDirty = false;
        }

        /// <summary>
        /// Flattens one registered class's own and inherited entries, nearest declaration first.
        /// </summary>
        /// <remarks>
        /// WHY per class and on demand: Instance declares members every class inherits, so
        /// flattening all classes up front copied those entries into every class of every registry —
        /// a cost each world and each test paid at construction, while the lookup only ever runs on
        /// the rare path where a Lua member access has already missed every binding.
        /// </remarks>
        private void EnsureClassFlattened(string className)
        {
            if (className == null || !_byName.ContainsKey(className)
                                  || !_flattenedClasses.Add(className))
            {
                return;
            }

            string current = className;
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
                                (className, pair.Key, RbxKnownUnimplementedMemberAccess.Read),
                                flattenedMember);
                        }

                        if ((pair.Value.Access & RbxKnownUnimplementedMemberAccess.Write) != 0)
                        {
                            _flattenedKnownUnimplementedMembers.TryAdd(
                                (className, pair.Key, RbxKnownUnimplementedMemberAccess.Write),
                                flattenedMember);
                        }
                    }
                }

                current = _byName.TryGetValue(current, out ClassDescriptor descriptor)
                    ? descriptor.BaseClassName
                    : null;
            }
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
            // WHY Object above Instance: the mirror roots the hierarchy there (Object.yaml: IsA("Object")
            // "will always return true"), and Changed/IsA/GetPropertyChangedSignal are declared on it.
            catalog.Register(new ClassDescriptor("Object", null, true, false, false));
            catalog.Register(new ClassDescriptor("Instance", "Object", true, false, false));
            catalog.Register(new ClassDescriptor(
                "LuaSourceContainer", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor(
                "BaseScript", "LuaSourceContainer", true, false, false));
            catalog.Register(new ClassDescriptor(
                "Script", "BaseScript", false, false, false));
            catalog.Register(new ClassDescriptor(
                "LocalScript", "Script", false, false, false));
            catalog.Register(new ClassDescriptor("BaseRemoteEvent", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("RemoteEvent", "BaseRemoteEvent", false, true, false,
                descriptor => new RbxRemoteEvent(descriptor)));
            catalog.Register(new ClassDescriptor("UnreliableRemoteEvent", "BaseRemoteEvent",
                false, true, false, descriptor => new RbxUnreliableRemoteEvent(descriptor)));
            catalog.Register(new ClassDescriptor("RemoteFunction", "Instance", false, true, false,
                descriptor => new RbxRemoteFunction(descriptor)));
            catalog.Register(new ClassDescriptor("Player", "Instance", false, false, false,
                descriptor => new RbxPlayer(descriptor)));
            // WHY: MVP8 slice 8.3 — the empty per-player containers Roblox creates on join. The
            // mirror tags Backpack with no NotCreatable (script-creatable) while PlayerGui and
            // PlayerScripts are NotCreatable engine children, so only Backpack is creatable here.
            catalog.Register(new ClassDescriptor("Backpack", "Instance", false, true, false));
            catalog.Register(new ClassDescriptor("PlayerGui", "Instance", false, false, false));
            catalog.Register(new ClassDescriptor("PlayerScripts", "Instance", false, false, false));
            catalog.Register(new ClassDescriptor("PVInstance", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("Folder", "Instance", false, true, false));
            catalog.Register(new ClassDescriptor("Model", "PVInstance", false, true, false,
                descriptor => new RbxModel(descriptor)));
            catalog.Register(new ClassDescriptor("WorldRoot", "Model", true, false, false));
            catalog.Register(new ClassDescriptor("Workspace", "WorldRoot", false, false, true,
                descriptor => new RbxModel(descriptor)));
            catalog.Register(new ClassDescriptor("BasePart", "PVInstance", true, false, false));
            // WHY MVP8 slice 8.6: Humanoid carries health, the movement parameters and the state
            // machine; the character controller behind it is a swappable motor, not a class here.
            catalog.Register(new ClassDescriptor("Humanoid", "Instance", false, true, false,
                descriptor => new RbxHumanoid(descriptor)));
            // WHY FormFactorPart between BasePart and Part: Part.yaml inherits it, so
            // part:IsA("FormFactorPart") is true in Roblox; it is abstract (NotCreatable) there too.
            catalog.Register(new ClassDescriptor("FormFactorPart", "BasePart", true, false, false));
            // WHY Part has a behaviour class from MVP8 slice 8.5: Touched/TouchEnded belong to the
            // part the mirror fires them on, not to a side table keyed by id that every reader of
            // the tree would have to know about.
            catalog.Register(new ClassDescriptor("Part", "FormFactorPart", false, true, false,
                descriptor => new RbxBasePart(descriptor)));
            // WHY: one canonical Camera per world (bootstrap creates it under Workspace and the
            // Lua layer routes its CFrame to the camera rig), so scripted creation stays off.
            // Camera.yaml inherits PVInstance, so camera:IsA("PVInstance") holds and GetPivot/
            // PivotTo answer with the camera's CFrame.
            // TODO: MVP-later — creatable Cameras with per-instance state once multiple
            // viewports/cameras are meaningful.
            catalog.Register(new ClassDescriptor("Camera", "PVInstance", false, false, false));
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
            // WHY: MVP2 exposes local JSON/GUID/URL helpers and a fail-closed outbound policy seam;
            // the production transport still refuses loudly until the host installs a safe one.
            catalog.Register(new ClassDescriptor("HttpService", "Instance", false, false, true));
            // WHY no behavior subclass, mirroring HttpService: ScriptContext:SetTimeout has no
            // instance-tree state of its own — it reads/writes the composition's LuaCsCoroutineBudgetSettings,
            // which lives at the Lua-CSharp binding layer (CoreAI.Ai.LuaCs), not here in the engine-free
            // instance layer. properties: [] per the mirror — no Timeout is ever readable back.
            catalog.Register(new ClassDescriptor("ScriptContext", "Instance", false, false, true));
            // WHY: ClickDetector is a normal creatable Instance (superclass Instance, NOT a service) —
            // a mod does Instance.new("ClickDetector") and parents it under a Part; the behavior class
            // carries the MouseClick signal the host pick pump fires when that part is clicked.
            catalog.Register(new ClassDescriptor("ClickDetector", "Instance", false, true, false,
                descriptor => new RbxClickDetector(descriptor)));
            catalog.Register(new ClassDescriptor("MaterialService", "Instance", false, false, true,
                descriptor => new RbxMaterialService(descriptor)));
            // WHY: MVP8 slice 8.0 — Debris is engine-free (deadline queue over the scheduler host
            // timer); the behavior class is constructed here like every other service behavior.
            catalog.Register(new ClassDescriptor("Debris", "Instance", false, false, true,
                descriptor => new RbxDebris(descriptor)));
            // WHY: MVP8 slice 8.2 — CollectionService is engine-free (tag queries and signals
            // over the registry tag store); the behavior class is constructed here.
            catalog.Register(new ClassDescriptor("CollectionService", "Instance", false, false, true,
                descriptor => new RbxCollectionService(descriptor)));
            // WHY: MVP8 slice 8.4 — TweenService and its tweens are engine-free (Heartbeat
            // driver on the scheduler scaled clock); TweenBase is the mirror's abstract
            // ancestor (NotCreatable), Tween instances are service-created and never pass
            // through Instance.new (creatable false), and TweenService is a service behavior.
            catalog.Register(new ClassDescriptor("TweenBase", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("Tween", "TweenBase", false, false, false,
                descriptor => new RbxTween(descriptor)));
            catalog.Register(new ClassDescriptor("TweenService", "Instance", false, false, true,
                descriptor => new RbxTweenService(descriptor)));
            // WHY: MVP8 slice 8.1 — ValueBase is the mirror's abstract ancestor of all value
            // instances (NotCreatable); the eight concrete values are creatable and carry
            // Value + Changed (lowercase `changed` stays an unknown member: deprecated).
            catalog.Register(new ClassDescriptor("ValueBase", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("IntValue", "ValueBase", false, true, false,
                descriptor => new RbxIntValue(descriptor)));
            catalog.Register(new ClassDescriptor("NumberValue", "ValueBase", false, true, false,
                descriptor => new RbxNumberValue(descriptor)));
            catalog.Register(new ClassDescriptor("StringValue", "ValueBase", false, true, false,
                descriptor => new RbxStringValue(descriptor)));
            catalog.Register(new ClassDescriptor("BoolValue", "ValueBase", false, true, false,
                descriptor => new RbxBoolValue(descriptor)));
            catalog.Register(new ClassDescriptor("ObjectValue", "ValueBase", false, true, false,
                descriptor => new RbxObjectValue(descriptor)));
            catalog.Register(new ClassDescriptor("Vector3Value", "ValueBase", false, true, false,
                descriptor => new RbxVector3Value(descriptor)));
            catalog.Register(new ClassDescriptor("CFrameValue", "ValueBase", false, true, false,
                descriptor => new RbxCFrameValue(descriptor)));
            catalog.Register(new ClassDescriptor("Color3Value", "ValueBase", false, true, false,
                descriptor => new RbxColor3Value(descriptor)));
            catalog.Register(new ClassDescriptor("MaterialVariant", "Instance", false, true, false,
                descriptor => new RbxMaterialVariant(descriptor)));

            // WHY Raycast is absent from the WorldRoot table below: it landed in MVP8 slice 8.5, and
            // a stub for a shipped member is worse than none — it fails a call that works.
            catalog.RegisterKnownUnimplementedMembers("Workspace",
                RbxKnownUnimplementedMemberDescriptor.UnsupportedWriteProperty(
                    "SignalBehavior",
                    "signal mode is Deferred-only; use task.defer when explicit ordering is needed"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Terrain",
                    "build terrain from Parts; voxel Terrain is a roadmap non-goal"));

            string humanoidRigWorkaround =
                "CoreAI's character is a motor, not a full R15 rig; these members land with the "
                + "character-rig slice";
            string physicsWorkaround =
                "keep the part Anchored and animate CFrame, or use host physics until this bridge lands";
            // WHY this wording: CanCollide = false lets bodies pass through a part but still fires
            // Touched and is still hit by raycasts (Roblox semantics since BINDER-A), so it is not
            // a substitute for CanTouch, CanQuery or collision groups; the stub names what is.
            string collisionWorkaround =
                "set CanCollide = false where bodies should pass through (the part still fires "
                + "Touched and is hit by raycasts), filter raycasts with RaycastParams, and ignore "
                + "unwanted hits inside the Touched handler until collision groups land";
            const string queryWorkaround =
                "exclude the part from raycasts with RaycastParams.FilterDescendantsInstances "
                + "(FilterType Exclude) or RaycastParams:AddToFilter(part)";
            const string touchWorkaround =
                "ignore the part inside the Touched handler (check the hit part or an attribute), "
                + "or Disconnect the Touched connection while it should not report";
            string surfaceWorkaround =
                "use Material, Color, and geometry; legacy surface joints are not currently scheduled";
            // WHY network ownership is a loud stub rather than absent: the MVP2.5 plan defers it
            // (owner decision 5) and MVP12's replication gate asserts it raises the deferred stub,
            // so a script that calls it has to be told the rung, not told it made a typo.
            const string networkOwnershipWorkaround =
                "the server simulates every part; network ownership is deferred until the "
                + "replication rung assigns it";
            catalog.RegisterKnownUnimplementedMembers("BasePart",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "SetNetworkOwner", networkOwnershipWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetNetworkOwner", networkOwnershipWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "SetNetworkOwnershipAuto", networkOwnershipWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetNetworkOwnershipAuto", networkOwnershipWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "CanSetNetworkOwnership", networkOwnershipWorkaround),
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
                    "CanQuery", queryWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CanTouch", touchWorkaround),
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
                    "BindToRenderStep", "MVP2",
                    "connect RunService.RenderStepped until named render-step binding lands"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "UnbindFromRenderStep", "MVP2",
                    "disconnect the RunService.RenderStepped connection explicitly"));
            // WHY: MVP8 slice 8.3 — everything outside the slice (lookups, profile names, Kick,
            // Character read, empty Backpack/PlayerGui/PlayerScripts) stays a loud stub so an
            // accidental delivery or un-stubbing is caught by the gate tests. "Unsupported" marks
            // the plan's "not planned" backend/security members; "Backlog" marks members no rung
            // has claimed. The character pipeline shipped without the respawn and appearance
            // fields, so no stub may promise them to it any more (M8-24).
            catalog.RegisterKnownUnimplementedMembers("Players",
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "BanAsync",
                    "bans are a platform backend concern, not planned; track identity via Player.UserId"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "UnbanAsync",
                    "bans are a platform backend concern, not planned; track identity via Player.UserId"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetBanHistoryAsync",
                    "bans are a platform backend concern, not planned; track identity via Player.UserId"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "CreateHumanoidModelFromDescription",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "CreateHumanoidModelFromDescriptionAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "CreateHumanoidModelFromUserId",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "CreateHumanoidModelFromUserIdAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetCharacterAppearanceAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetCharacterAppearanceInfoAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetFriendsAsync",
                    "social graph fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetHumanoidDescriptionFromOutfitId",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetHumanoidDescriptionFromOutfitIdAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetHumanoidDescriptionFromUserId",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetHumanoidDescriptionFromUserIdAsync",
                    "avatar appearance fetch is a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetNameFromUserIdAsync",
                    "username lookup is a platform backend concern, not planned; use the profile port"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetUserIdFromNameAsync",
                    "username lookup is a platform backend concern, not planned; use the profile port"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetUserThumbnailAsync",
                    "avatar thumbnails are a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "Chat",
                    "PluginSecurity chat entry point; chat ships as TextChatService, a non-goal"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "TeamChat",
                    "PluginSecurity chat entry point; chat ships as TextChatService, a non-goal"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "SetChatStyle",
                    "PluginSecurity chat entry point; chat ships as TextChatService, a non-goal"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "PlayerMembershipChanged",
                    "premium membership callbacks are a platform backend concern, not planned"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "UserSubscriptionStatusChanged",
                    "subscription callbacks are a platform backend concern, not planned"));
            catalog.RegisterKnownUnimplementedMembers("Player",
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Team",
                    "no Teams service in this rung; team play arrives with it"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "TeamColor",
                    "no Teams service in this rung; team play arrives with it"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Neutral",
                    "no Teams service in this rung; team play arrives with it"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "ReplicationFocus",
                    "deferred by owner decision 5; replication focus is not scriptable in this rung"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "AddReplicationFocus",
                    "deferred by owner decision 5; replication focus is not scriptable in this rung"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "RemoveReplicationFocus",
                    "deferred by owner decision 5; replication focus is not scriptable in this rung"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetMouse",
                    "the legacy Mouse object is not implemented; read "
                    + "UserInputService:GetMouseLocation() and UserInputService.InputBegan instead"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "Chatted",
                    "chat is TextChatService, a non-goal of this rung"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetNetworkPing",
                    "network telemetry ships with the MVP11 transport"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "RespawnLocation",
                    "choosing a respawn point needs the SpawnLocation class, which no rung has "
                    + "claimed; move the character with PivotTo in Player.CharacterAdded instead"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "CameraMode",
                    "camera modes are host-scene configuration in this rung"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "CanLoadCharacterAppearance",
                    "avatar appearance fetch is a platform backend concern, not planned; "
                    + "the spawned character always has the default look"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "CharacterAppearanceId",
                    "avatar appearance fetch is a platform backend concern, not planned; "
                    + "the spawned character always has the default look"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "StarterGear",
                    "per-player StarterGear lands with character contents (MVP10/MVP14); use Backpack meanwhile"));
            // WHY these stay loud: each needs a character rig CoreAI does not model (seats,
            // ragdoll, swimming, climbing, accessories, animation). A silent no-op would let a
            // script believe it sat a player down; the loud stub names the rung instead.
            catalog.RegisterKnownUnimplementedMembers("Humanoid",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Sit", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "SeatPart", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "PlatformStand", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AutoRotate", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "HipHeight", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "MaxSlopeAngle", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CameraOffset", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "RigType", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Seated", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Climbing", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Swimming", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Touched", "connect BasePart.Touched on the character's parts instead"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "SetStateEnabled", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetStateEnabled", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ApplyDescription", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetAppliedDescription", humanoidRigWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "LoadAnimation", "animation playback lands with the animation slice"),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod(
                    "Move", "MVP10",
                    "drive movement with Humanoid:MoveTo until input-driven Move lands"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "EquipTool", "tools land with the Backpack contents slice"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "UnequipTools", "tools land with the Backpack contents slice"));
            RegisterCoreKnownUnimplementedMembers(catalog, physicsWorkaround, collisionWorkaround);
            RegisterKnownUnimplementedCreatableClasses(catalog, physicsWorkaround);
            catalog.EnsureKnownUnimplementedMembersFlattened();
            return catalog;
        }

        /// <summary>
        /// Real, script-visible members of the core classes (Instance, Model, WorldRoot, Camera,
        /// DataModel, ServiceProvider, BasePart) that no binding answers yet (M1-05).
        /// </summary>
        /// <remarks>
        /// WHY hand-listed from the mirror yaml instead of generated: the mirror is not shipped with
        /// the package. The rules the list follows: only non-deprecated, scriptable members, plus a
        /// few deprecated ones scripts still use daily (SetPrimaryPartCFrame, FindPartOnRay), which
        /// are Unsupported and name their modern replacement; never a member a binding answers (a
        /// bound member is dispatched before the catalog, but a stale entry still misleads every
        /// reader of this table); never a name the standard DataModel bootstraps as a child —
        /// DataModel.Workspace and DataModel.RunService stay unlisted because the catalog is asked
        /// before the child lookup, and <c>game.Workspace</c> must keep resolving to the child.
        /// </remarks>
        private static void RegisterCoreKnownUnimplementedMembers(ClassCatalog catalog,
            string physicsWorkaround, string collisionWorkaround)
        {
            const string streamingWorkaround =
                "instance streaming is not modeled; every instance is present on every peer";
            const string sandboxWorkaround =
                "script capabilities are granted per mod through CoreAI's Lua capabilities, "
                + "not per instance";
            const string stylingWorkaround =
                "UI styling is not implemented; read and write the property directly";
            const string overlapWorkaround =
                "cast with workspace:Raycast, or track overlaps with BasePart.Touched/TouchEnded";
            const string shapeCastWorkaround =
                "cast several workspace:Raycast rays across the shape's extent until shape casts land";
            const string fieldOfViewWorkaround =
                "the host camera rig owns the field of view; drive the view with Camera.CFrame and "
                + "Camera.CameraType";
            const string screenSpaceWorkaround =
                "screen-space camera queries are not implemented; use a ClickDetector for clicks "
                + "on parts and place 3D content in world space";
            const string vrWorkaround = "VR camera control is not supported";
            const string placeIdentityWorkaround =
                "CoreAI worlds are not Roblox places and carry no platform identity; key saved "
                + "data on Player.UserId";
            const string massWorkaround =
                "mass is owned by host physics and is not readable from Lua yet; keep parts "
                + "Anchored and animate CFrame for scripted motion";
            const string jointWorkaround =
                "joints and constraints are not implemented; group parts in a Model and move it "
                + "with PivotTo";
            const string solidModelingWorkaround =
                "solid modeling is not implemented; compose shapes from separate parts";

            catalog.RegisterKnownUnimplementedMembers("Instance",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "FindFirstDescendant", "use FindFirstChild(name, true) for a recursive search"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "QueryDescendants",
                    "use GetDescendants() and filter by ClassName, Name or HasTag"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetActor",
                    "Parallel Luau actors are a roadmap non-goal; every script runs on one thread"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "IsPropertyModified", "compare the property with its known default value"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ResetPropertyToDefault", "assign the property's default value explicitly"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetStyled", stylingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetStyledPropertyChangedSignal", stylingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "StyledPropertiesChanged", stylingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Capabilities", sandboxWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "Sandboxed", sandboxWorkaround));
            catalog.RegisterKnownUnimplementedMembers("Model",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "MoveTo",
                    "use model:PivotTo(CFrame.new(position)); MoveTo's collision-aware "
                    + "placement is not implemented"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "TranslateBy", "use model:PivotTo(model:GetPivot() + offset)"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetBoundingBox",
                    "compute the bounds from model:GetPivot() and each part's CFrame and Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetExtentsSize",
                    "compute the extents from each part's CFrame and Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ScaleTo",
                    "scale each part's Size and its offset from model:GetPivot() explicitly"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetScale", "keep the scale factor in an attribute"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "ModelStreamingMode", streamingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "AddPersistentPlayer", streamingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "RemovePersistentPlayer", streamingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetPersistentPlayers", streamingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "SetPrimaryPartCFrame", "deprecated in Roblox; use model:PivotTo(cframe)"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "GetPrimaryPartCFrame", "deprecated in Roblox; use model:GetPivot()"));
            catalog.RegisterKnownUnimplementedMembers("WorldRoot",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetPartBoundsInBox", overlapWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetPartBoundsInRadius", overlapWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetPartsInPart", overlapWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ArePartsTouchingOthers", overlapWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "Blockcast", shapeCastWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "Spherecast", shapeCastWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "Shapecast", shapeCastWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "BulkMoveTo",
                    "assign each part's CFrame, or move a Model with PivotTo"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "RegisterCollisionGroup", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "UnregisterCollisionGroup", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "RenameCollisionGroup", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "IsCollisionGroupRegistered", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetRegisteredCollisionGroups", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "CollisionGroupSetCollidable", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "CollisionGroupsAreCollidable", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetMaxCollisionGroups", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "FindPartOnRay",
                    "deprecated in Roblox; use workspace:Raycast(origin, direction, raycastParams)"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "FindPartOnRayWithIgnoreList",
                    "deprecated in Roblox; use workspace:Raycast with RaycastParams "
                    + "(FilterType Exclude)"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "FindPartOnRayWithWhitelist",
                    "deprecated in Roblox; use workspace:Raycast with RaycastParams "
                    + "(FilterType Include)"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod(
                    "FindPartsInRegion3",
                    "deprecated in Roblox; " + overlapWorkaround));
            catalog.RegisterKnownUnimplementedMembers("Camera",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "FieldOfView", fieldOfViewWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "FieldOfViewMode", fieldOfViewWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "DiagonalFieldOfView", fieldOfViewWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "MaxAxisFieldOfView", fieldOfViewWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Focus", "aim Camera.CFrame at the target with CFrame.lookAt"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ViewportSize", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "NearPlaneZ", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ScreenPointToRay", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ViewportPointToRay", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "WorldToScreenPoint", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "WorldToViewportPoint", screenSpaceWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetPartsObscuringTarget",
                    "cast workspace:Raycast from Camera.CFrame.Position to the target"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetRenderCFrame", "read Camera.CFrame"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ZoomToExtents", "set Camera.CFrame to frame the model explicitly"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "HeadLocked", vrWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "HeadScale", vrWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "VRTiltAndRollEnabled", vrWorkaround));
            catalog.RegisterKnownUnimplementedMembers("ServiceProvider",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ServiceAdded",
                    "services exist from world bootstrap on; resolve them with game:GetService"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ServiceRemoving",
                    "services live for the whole world; resolve them with game:GetService"));
            catalog.RegisterKnownUnimplementedMembers("DataModel",
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "PlaceId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "PlaceVersion", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "GameId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "JobId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "CreatorId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "CreatorType", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "PrivateServerId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "PrivateServerOwnerId", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "MatchmakingType", placeIdentityWorkaround),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "GraphicsQualityChangeRequest",
                    "graphics quality is host configuration, not scriptable"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty(
                    "ServerRestartScheduled",
                    "server restarts are managed by the host, not announced to scripts"));
            catalog.RegisterKnownUnimplementedMembers("BasePart",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "BrickColor", "use Color = Color3.fromRGB(r, g, b)"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Reflectance",
                    "use Material (for example Enum.Material.Glass or Metal) for a reflective look"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CastShadow", "shadows follow the host renderer's settings"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Locked", "Locked only guards Studio selection; leave it out"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "PivotOffset",
                    "keep the offset in an attribute and apply it with PivotTo"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ExtentsCFrame", "read CFrame"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ExtentsSize", "read Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ResizeableFaces", "resize the part by assigning Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "ResizeIncrement", "resize the part by assigning Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "Mass", massWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AssemblyMass", massWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AssemblyCenterOfMass", massWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CenterOfMass", massWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AssemblyRootPart", jointWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "RootPriority", jointWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "CurrentPhysicalProperties", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "EnableFluidForces", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty(
                    "AudioCanCollide", "positional audio is not implemented"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ApplyImpulse", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ApplyAngularImpulse", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "ApplyImpulseAtPosition", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetMass", massWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetVelocityAtPosition", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "AngularAccelerationToTorque", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "TorqueToAngularAcceleration", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "IsGrounded", physicsWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetClosestPointOnSurface",
                    "compute the point from the part's CFrame and Size"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetTouchingParts",
                    "track contacts with BasePart.Touched and BasePart.TouchEnded"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "CanCollideWith", collisionWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetConnectedParts", jointWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetJoints", jointWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "GetNoCollisionConstraints", jointWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "Resize", "assign Size and adjust CFrame to keep the anchored face in place"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "UnionAsync", solidModelingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "SubtractAsync", solidModelingWorkaround),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod(
                    "IntersectAsync", solidModelingWorkaround));
        }

        /// <summary>
        /// Real, script-creatable Roblox classes CoreAI does not implement yet (M1-05): Instance.new
        /// raises their loud stub. Planned rungs follow the roadmap's own class lists (MVP14 GUI
        /// subset, MVP15 Sound/ParticleEmitter); everything else is backlog or deliberately
        /// unsupported. Services and NotCreatable classes are not listed: Instance.new refuses them
        /// with the Roblox "Unable to create" error, exactly as Roblox does.
        /// </summary>
        private static void RegisterKnownUnimplementedCreatableClasses(ClassCatalog catalog,
            string physicsWorkaround)
        {
            const string partShapeWorkaround =
                "create a Part and set Shape (for example Enum.PartType.Wedge) and Size instead";
            const string jointWorkaround =
                "group the parts in a Model and move it with PivotTo, or keep them Anchored";
            const string guiWorkaround = "build HUD content in 3D space until the GUI subset lands";
            const string guiBacklogWorkaround =
                "use the GUI subset classes (ScreenGui, Frame, TextLabel, TextButton, ImageLabel, "
                + "TextBox, UICorner, UIListLayout) once they land";
            const string effectsWorkaround =
                "express the effect with parts, Transparency and Color animated by TweenService";
            const string lightingWorkaround =
                "configure the host scene lighting outside Lua until the lighting slice is scheduled";
            const string avatarWorkaround =
                "avatar appearance is a platform backend concern, not planned; the character "
                + "always has the default look";
            const string legacyMoverWorkaround =
                "deprecated in Roblox; keep the part Anchored and animate CFrame with TweenService";
            const string eventWorkaround =
                "call the function directly, or share it across mods with mods_export/mods_call";

            catalog.RegisterKnownUnimplementedClasses(
                RbxKnownUnimplementedClassDescriptor.Backlog("WedgePart", partShapeWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("CornerWedgePart", partShapeWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("TrussPart", partShapeWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("MeshPart",
                    "create a Part; mesh assets are not loaded, and rbxassetid content is never "
                    + "fetched"),
                RbxKnownUnimplementedClassDescriptor.Backlog("SpecialMesh", partShapeWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SpawnLocation",
                    "place a Part where players should appear and move the character there in "
                    + "Player.CharacterAdded"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Seat",
                    "seats need the character rig CoreAI does not model; use a plain Part"),
                RbxKnownUnimplementedClassDescriptor.Backlog("VehicleSeat",
                    "seats need the character rig CoreAI does not model; use a plain Part"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Weld", jointWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("WeldConstraint", jointWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Motor6D", jointWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Attachment",
                    "keep the offset as a CFrame relative to the part and apply it with "
                    + "part.CFrame * offset"),
                RbxKnownUnimplementedClassDescriptor.Backlog("NoCollisionConstraint",
                    "set CanCollide = false on one of the two parts; it then passes through every "
                    + "part, and still fires Touched and is hit by raycasts"),
                RbxKnownUnimplementedClassDescriptor.Backlog("HingeConstraint", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("RopeConstraint", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("RodConstraint", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SpringConstraint", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BallSocketConstraint",
                    physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("PrismaticConstraint",
                    physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("AlignPosition", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("AlignOrientation", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("LinearVelocity", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("AngularVelocity", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("VectorForce", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Torque", physicsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyVelocity",
                    legacyMoverWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyPosition",
                    legacyMoverWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyGyro", legacyMoverWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyForce",
                    legacyMoverWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyAngularVelocity",
                    legacyMoverWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BindableEvent", eventWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BindableFunction", eventWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Configuration",
                    "use a Folder of value objects or attributes"),
                RbxKnownUnimplementedClassDescriptor.Backlog("BrickColorValue",
                    "use a Color3Value"),
                RbxKnownUnimplementedClassDescriptor.Backlog("RayValue",
                    "store the origin and direction in two Vector3Value objects"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Tool",
                    "tools land with the Backpack contents slice; drive the action from a "
                    + "ClickDetector or UserInputService meanwhile"),
                RbxKnownUnimplementedClassDescriptor.Backlog("ProximityPrompt",
                    "use a ClickDetector on the part for interaction"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Team",
                    "no Teams service in this rung; track sides with an attribute on each Player"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Decal",
                    "use Color and Material; image content is never fetched"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Texture",
                    "use Color and Material, or a MaterialVariant for a tiled texture"),
                RbxKnownUnimplementedClassDescriptor.Backlog("SurfaceAppearance",
                    "use a MaterialVariant for a custom surface"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Highlight", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SelectionBox", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("Sound", "MVP15",
                    "play no audio from Lua until the audio rung lands"),
                RbxKnownUnimplementedClassDescriptor.Planned("ParticleEmitter", "MVP15",
                    effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Beam", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Trail", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Fire", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Smoke", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Sparkles", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Explosion", effectsWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("PointLight", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SpotLight", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SurfaceLight", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Sky", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Atmosphere", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Clouds", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BloomEffect", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BlurEffect", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("ColorCorrectionEffect",
                    lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SunRaysEffect", lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("DepthOfFieldEffect",
                    lightingWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("Animation",
                    "animation playback lands with the animation slice"),
                RbxKnownUnimplementedClassDescriptor.Backlog("AnimationController",
                    "animation playback lands with the animation slice"),
                RbxKnownUnimplementedClassDescriptor.Backlog("Animator",
                    "animation playback lands with the animation slice"),
                RbxKnownUnimplementedClassDescriptor.Unsupported("Accessory", avatarWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("Shirt", avatarWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("Pants", avatarWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("BodyColors", avatarWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("HumanoidDescription",
                    avatarWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("ScreenGui", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("Frame", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("TextLabel", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("TextButton", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("ImageLabel", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("TextBox", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("UICorner", "MVP14", guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Planned("UIListLayout", "MVP14",
                    guiWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("ImageButton", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("ScrollingFrame",
                    guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("BillboardGui", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("SurfaceGui", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("ViewportFrame",
                    guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIGridLayout", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIPadding", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIStroke", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIGradient", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIScale", guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UIAspectRatioConstraint",
                    guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Backlog("UISizeConstraint",
                    guiBacklogWorkaround),
                RbxKnownUnimplementedClassDescriptor.Unsupported("PathfindingModifier",
                    "PathfindingService is not planned; steer with scripted waypoints"),
                RbxKnownUnimplementedClassDescriptor.Unsupported("PathfindingLink",
                    "PathfindingService is not planned; steer with scripted waypoints"),
                RbxKnownUnimplementedClassDescriptor.Unsupported("Actor",
                    "Parallel Luau actors are a roadmap non-goal; use a Model"));
        }
    }
}
