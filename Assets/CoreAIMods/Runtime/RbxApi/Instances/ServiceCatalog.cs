using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>A deferred service placeholder that raises its roadmap rung when Lua first
    /// accesses a member.</summary>
    public sealed class RbxStubService : RbxInstance
    {
        internal RbxStubService(string serviceName, string plannedMvp, string workaroundHint)
            : base(new ClassDescriptor(serviceName, "Instance", false, false, true))
        {
            PlannedMvp = plannedMvp;
            WorkaroundHint = workaroundHint;
        }

        public string PlannedMvp { get; }

        public string WorkaroundHint { get; }

        internal RbxError MemberAccessError(string memberName)
        {
            return RbxError.NotImplemented(
                ClassName + ":" + memberName, PlannedMvp, WorkaroundHint);
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

        public void RegisterStub(string serviceName, string plannedMvp, string workaroundHint)
        {
            ValidateServiceName(serviceName);
            if (string.IsNullOrWhiteSpace(plannedMvp))
            {
                throw new ArgumentException(
                    "Stub services require a roadmap rung", nameof(plannedMvp));
            }

            if (workaroundHint == null)
            {
                throw new ArgumentNullException(nameof(workaroundHint));
            }

            if (_byName.ContainsKey(serviceName))
            {
                throw new InvalidOperationException(
                    "Service already registered: " + serviceName);
            }

            _byName.Add(serviceName, new Registration(
                () => new RbxStubService(serviceName, plannedMvp, workaroundHint)));
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
            ValidateServiceName(serviceName);
            if (!_byName.TryGetValue(serviceName, out Registration registration))
            {
                throw RbxError.UnknownService(serviceName);
            }

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
            ValidateServiceName(serviceName);
            if (!_byName.TryGetValue(serviceName, out Registration registration))
            {
                throw RbxError.UnknownService(serviceName);
            }

            return registration.Service;
        }

        public static ServiceCatalog CreateMvp2()
        {
            ServiceCatalog catalog = new();
            string implementedServiceHint =
                "use implemented services until this service lands";
            catalog.RegisterStub("RunService", "MVP2", implementedServiceHint);
            catalog.RegisterTreeBacked("HttpService");
            catalog.RegisterTreeBacked("Players");
            catalog.RegisterTreeBacked("Debris");
            catalog.RegisterTreeBacked("TweenService");
            catalog.RegisterTreeBacked("CollectionService");
            catalog.RegisterStub("DataStoreService", "MVP9", implementedServiceHint);
            catalog.RegisterStub("UserInputService", "MVP10", implementedServiceHint);
            catalog.RegisterStub("ContextActionService", "MVP10", implementedServiceHint);
            catalog.RegisterStub("SoundService", "MVP15", implementedServiceHint);
            catalog.RegisterStub("AIService", "a future MVP (reserved)",
                "CoreAI agent/chat access from Lua is planned; not yet scriptable");
            catalog.RegisterStub("PathfindingService", "no planned MVP (not planned)",
                "use host navigation or scripted waypoints; this service is not planned");
            catalog.RegisterStub("MarketplaceService", "no planned MVP (not planned)",
                "handle purchases outside Lua; this service is not planned");
            return catalog;
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
