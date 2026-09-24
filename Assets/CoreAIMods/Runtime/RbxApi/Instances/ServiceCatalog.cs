using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>A deferred service placeholder that raises its loud stub when Lua first
    /// accesses a member: the roadmap rung for a planned service, the backlog or unsupported
    /// verdict otherwise, and for the fallback of an implemented service the missing attachment.</summary>
    public sealed class RbxStubService : RbxInstance
    {
        internal RbxStubService(string serviceName, string plannedMvp, string workaroundHint)
            : this(serviceName, RbxKnownUnimplementedMemberStatus.Planned, plannedMvp,
                workaroundHint, false)
        {
        }

        internal RbxStubService(string serviceName, RbxKnownUnimplementedMemberStatus status,
            string plannedMvp, string workaroundHint, bool isImplementedFallback)
            : base(new ClassDescriptor(serviceName, "Instance", false, false, true))
        {
            Status = status;
            PlannedMvp = plannedMvp;
            WorkaroundHint = workaroundHint;
            IsImplementedFallback = isImplementedFallback;
        }

        /// <summary>Roadmap rung of a planned stub; null for every other kind of stub.</summary>
        public string PlannedMvp { get; }

        public string WorkaroundHint { get; }

        /// <summary>Why the service is a placeholder; meaningless when
        /// <see cref="IsImplementedFallback"/> is true.</summary>
        public RbxKnownUnimplementedMemberStatus Status { get; }

        /// <summary>
        /// True when the service IS implemented and this placeholder only stands in for a DataModel
        /// that was never given the real tree-backed instance.
        /// </summary>
        public bool IsImplementedFallback { get; }

        internal RbxError MemberAccessError(string memberName)
        {
            string feature = ClassName + ":" + memberName;
            if (IsImplementedFallback)
            {
                return RbxError.BadArgument(
                    feature + " is unavailable: " + ClassName
                    + " is implemented but is not attached to this DataModel",
                    "bootstrap the standard DataModel tree (DataModelBootstrap.CreateGame) before "
                    + "resolving services");
            }

            return RbxKnownUnimplementedErrors.ForMember(
                feature, Status, PlannedMvp, WorkaroundHint);
        }
    }

    /// <summary>Runtime registry for implemented and planned Rbx services.</summary>
    public sealed class ServiceCatalog
    {
        private sealed class Registration
        {
            public Registration()
            {
            }

            public Registration(RbxInstance service)
            {
                Service = service;
            }

            public Registration(Func<RbxStubService> stubFactory)
            {
                StubFactory = stubFactory;
            }

            public RbxInstance Service { get; set; }

            public Func<RbxStubService> StubFactory { get; }

            public bool IsStub => StubFactory != null;

            public bool IsTreeBacked => Service == null && StubFactory == null;
        }

        private readonly Dictionary<string, Registration> _byName =
            new(StringComparer.Ordinal);

        /// <summary>Every registered service name: implementations, tree-backed and stubs.</summary>
        public IEnumerable<string> ServiceNames => _byName.Keys;

        public void Register(string serviceName, RbxInstance service)
        {
            ValidateServiceName(serviceName);
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (!service.IsService || !string.Equals(
                    service.ClassName, serviceName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Registered service must be an Rbx service whose ClassName matches serviceName",
                    nameof(service));
            }

            if (_byName.TryGetValue(serviceName, out Registration registration))
            {
                if (ReferenceEquals(registration.Service, service))
                {
                    return;
                }

                if (!registration.IsStub && !registration.IsTreeBacked)
                {
                    throw new InvalidOperationException(
                        "Service already registered: " + serviceName);
                }
            }

            _byName[serviceName] = new Registration(service);
        }

        /// <summary>Registers a planned service whose members raise the stub naming its rung.</summary>
        public void RegisterStub(string serviceName, string plannedMvp, string workaroundHint)
        {
            ValidateServiceName(serviceName);
            if (string.IsNullOrWhiteSpace(plannedMvp))
            {
                throw new ArgumentException(
                    "Stub services require a roadmap rung", nameof(plannedMvp));
            }

            AddStub(serviceName, RbxKnownUnimplementedMemberStatus.Planned, plannedMvp,
                workaroundHint, false);
        }

        /// <summary>Registers a real Rbx service that no roadmap rung has claimed yet.</summary>
        public void RegisterBacklogStub(string serviceName, string workaroundHint)
        {
            AddStub(serviceName, RbxKnownUnimplementedMemberStatus.Backlog, null, workaroundHint,
                false);
        }

        /// <summary>Registers a real Rbx service CoreAI deliberately does not support.</summary>
        public void RegisterUnsupportedStub(string serviceName, string workaroundHint)
        {
            AddStub(serviceName, RbxKnownUnimplementedMemberStatus.Unsupported, null,
                workaroundHint, false);
        }

        /// <summary>
        /// Registers the placeholder for an implemented, tree-backed service: GetService still
        /// resolves it on a DataModel that lacks the real instance, so a top-of-file lookup does not
        /// abort the script, and the first member access names the missing attachment rather than
        /// a roadmap rung the service has already reached.
        /// </summary>
        public void RegisterImplementedFallback(string serviceName)
        {
            AddStub(serviceName, RbxKnownUnimplementedMemberStatus.Backlog, null, "", true);
        }

        /// <summary>Registers a service implemented by the DataModel tree before that tree is attached.</summary>
        public void RegisterTreeBacked(string serviceName)
        {
            ValidateServiceName(serviceName);
            if (_byName.ContainsKey(serviceName))
            {
                throw new InvalidOperationException(
                    "Service already registered: " + serviceName);
            }

            _byName.Add(serviceName, new Registration());
        }

        /// <summary>Returns a registered implementation or lazily creates its registered stub.</summary>
        public RbxInstance GetService(string serviceName)
        {
            Registration registration = ResolveRegistration(serviceName);
            if (registration.Service == null)
            {
                if (registration.StubFactory == null)
                {
                    throw RbxError.BadArgument(
                        serviceName + " is implemented but is not attached to this DataModel",
                        "bootstrap the standard DataModel tree before resolving services");
                }

                registration.Service = registration.StubFactory();
            }

            return registration.Service;
        }

        /// <summary>Returns an already-created service, null for a registered absent service,
        /// and raises for an unknown name.</summary>
        public RbxInstance FindService(string serviceName)
        {
            return ResolveRegistration(serviceName).Service;
        }

        public static ServiceCatalog CreateMvp2()
        {
            ServiceCatalog catalog = new();
            string implementedServiceHint =
                "use implemented services until this service lands";
            catalog.RegisterImplementedFallback("RunService");
            catalog.RegisterTreeBacked("HttpService");
            catalog.RegisterTreeBacked("Players");
            catalog.RegisterTreeBacked("Debris");
            catalog.RegisterTreeBacked("TweenService");
            catalog.RegisterTreeBacked("CollectionService");
            catalog.RegisterTreeBacked("ScriptContext");
            catalog.RegisterStub("DataStoreService", "MVP9", implementedServiceHint);
            catalog.RegisterImplementedFallback("UserInputService");
            catalog.RegisterStub("ContextActionService", "MVP10", implementedServiceHint);
            catalog.RegisterStub("SoundService", "MVP15", implementedServiceHint);
            catalog.RegisterStub("AIService", "a future MVP (reserved)",
                "CoreAI agent/chat access from Lua is planned; not yet scriptable");
            catalog.RegisterUnsupportedStub("PathfindingService",
                "use host navigation or scripted waypoints; this service is not planned");
            catalog.RegisterUnsupportedStub("MarketplaceService",
                "handle purchases outside Lua; this service is a roadmap non-goal");
            RegisterKnownRobloxServices(catalog);
            return catalog;
        }

        /// <summary>
        /// Real Roblox services game scripts commonly resolve (M1-05): each answers GetService with
        /// a loud stub, because "X is not a valid Service name" is false for them and sends the
        /// self-repair loop after a typo that is not there.
        /// </summary>
        private static void RegisterKnownRobloxServices(ServiceCatalog catalog)
        {
            const string platformWorkaround =
                "this is a Roblox platform backend service; CoreAI worlds have no such backend";
            const string chatWorkaround = "chat is TextChatService, which CoreAI does not support";

            catalog.RegisterStub("StarterGui", "MVP14",
                "build HUD content in 3D space until the GUI subset lands");
            catalog.RegisterBacklogStub("StarterPack",
                "per-player tools land with the Backpack contents slice; give items from "
                + "Players.PlayerAdded meanwhile");
            catalog.RegisterBacklogStub("ReplicatedFirst",
                "keep shared content in ReplicatedStorage; there is no early client loading phase");
            catalog.RegisterBacklogStub("Teams",
                "track sides with an attribute on each Player");
            // WHY not "use CanCollide": a CanCollide = false part still fires Touched and is still
            // hit by raycasts (Roblox semantics), so it replaces none of what collision groups do.
            catalog.RegisterBacklogStub("PhysicsService",
                "set CanCollide = false where bodies should pass through (the part still fires "
                + "Touched and is hit by raycasts), and filter raycasts with RaycastParams until "
                + "collision groups land");
            catalog.RegisterBacklogStub("ProximityPromptService",
                "use a ClickDetector on the part for interaction");
            catalog.RegisterBacklogStub("ContentProvider",
                "assets load on demand; drop the PreloadAsync call");
            catalog.RegisterBacklogStub("TextService",
                "size text by a fixed estimate until the GUI subset lands");
            catalog.RegisterBacklogStub("GuiService",
                "GUI selection and insets are not implemented");
            catalog.RegisterBacklogStub("LogService",
                "print and warn already reach the mod log; read it from the host");
            catalog.RegisterBacklogStub("Stats",
                "performance counters are host diagnostics, not scriptable yet");
            catalog.RegisterBacklogStub("LocalizationService",
                "keep translated strings in a table keyed by locale");
            catalog.RegisterBacklogStub("VRService", "VR input is not supported yet");
            catalog.RegisterUnsupportedStub("TextChatService", chatWorkaround);
            catalog.RegisterUnsupportedStub("Chat", chatWorkaround);
            catalog.RegisterUnsupportedStub("TeleportService",
                "a CoreAI world is one place; move the character with PivotTo instead");
            catalog.RegisterUnsupportedStub("BadgeService", platformWorkaround);
            catalog.RegisterUnsupportedStub("GamePassService", platformWorkaround);
            catalog.RegisterUnsupportedStub("GroupService", platformWorkaround);
            catalog.RegisterUnsupportedStub("SocialService", platformWorkaround);
            catalog.RegisterUnsupportedStub("PolicyService", platformWorkaround);
            catalog.RegisterUnsupportedStub("MessagingService", platformWorkaround);
            catalog.RegisterUnsupportedStub("MemoryStoreService", platformWorkaround);
            catalog.RegisterUnsupportedStub("AvatarEditorService", platformWorkaround);
            catalog.RegisterUnsupportedStub("AssetService", platformWorkaround);
            catalog.RegisterUnsupportedStub("InsertService", platformWorkaround);
            catalog.RegisterUnsupportedStub("AnalyticsService", platformWorkaround);
            catalog.RegisterUnsupportedStub("VoiceChatService", platformWorkaround);
        }

        private void AddStub(string serviceName, RbxKnownUnimplementedMemberStatus status,
            string plannedMvp, string workaroundHint, bool isImplementedFallback)
        {
            ValidateServiceName(serviceName);
            if (workaroundHint == null)
            {
                throw new ArgumentNullException(nameof(workaroundHint));
            }

            if (!isImplementedFallback)
            {
                RbxKnownUnimplementedMemberDescriptor.ValidateStatusPhase(status, plannedMvp);
            }

            if (_byName.ContainsKey(serviceName))
            {
                throw new InvalidOperationException(
                    "Service already registered: " + serviceName);
            }

            _byName.Add(serviceName, new Registration(
                () => new RbxStubService(serviceName, status, plannedMvp, workaroundHint,
                    isImplementedFallback)));
        }

        /// <summary>
        /// The registration for a lookup name. A blank name is an unknown service, answered with the
        /// same Roblox text as any other: <c>game:GetService("")</c> raises
        /// " is not a valid Service name", never a bare .NET argument exception (M1-25).
        /// </summary>
        private Registration ResolveRegistration(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)
                || !_byName.TryGetValue(serviceName, out Registration registration))
            {
                throw RbxError.UnknownService(serviceName ?? "");
            }

            return registration;
        }

        private static void ValidateServiceName(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException(
                    "Service name cannot be null or whitespace", nameof(serviceName));
            }
        }
    }
}
